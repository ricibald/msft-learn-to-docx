using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MsftLearnToDocx.Services;

/// <summary>
/// HTTP DelegatingHandler that caches successful (200 OK) responses to disk.
/// Cache entries expire after a configurable TTL (default: 24 hours).
/// 404 and error responses are never cached.
/// </summary>
public sealed class CachingHandler : DelegatingHandler
{
    private readonly string _cacheDir;
    private readonly TimeSpan _ttl;

    public CachingHandler(DelegatingHandler innerHandler, string cacheDir, TimeSpan ttl)
        : base(innerHandler)
    {
        _cacheDir = cacheDir;
        _ttl = ttl;
        Directory.CreateDirectory(_cacheDir);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only cache GET requests
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken);

        var cacheKey = ComputeCacheKey(request.RequestUri.AbsoluteUri);
        var cachePath = Path.Combine(_cacheDir, cacheKey);
        var metaPath = cachePath + ".meta";

        // Check cache hit
        if (File.Exists(cachePath) && File.Exists(metaPath))
        {
            var writeTime = File.GetLastWriteTimeUtc(cachePath);
            if (DateTime.UtcNow - writeTime < _ttl)
            {
                var contentType = await File.ReadAllTextAsync(metaPath, cancellationToken);
                var cachedBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(cachedBytes)
                };
                if (!string.IsNullOrEmpty(contentType))
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                return response;
            }
        }

        // Cache miss — send request, with retry for body-read failures.
        // RetryHandler handles transient HTTP errors, but once it returns a 200 response
        // the body stream may still fail mid-read (socket reset). We retry the full request
        // here because only CachingHandler owns the post-response buffering step.
        const int maxBodyRetries = 3;
        HttpResponseMessage? liveResponse = null;
        byte[]? bytes = null;

        for (var attempt = 0; attempt <= maxBodyRetries; attempt++)
        {
            // Clone request on retry (HttpRequestMessage cannot be resent)
            var currentRequest = attempt == 0
                ? request
                : CloneGetRequest(request);

            liveResponse = await base.SendAsync(currentRequest, cancellationToken);

            if (liveResponse.StatusCode != HttpStatusCode.OK)
                return liveResponse;

            try
            {
                bytes = await liveResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested && attempt < maxBodyRetries)
            {
                liveResponse.Dispose();
                liveResponse = null;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                Console.WriteLine($"\n  [Cache retry {attempt + 1}/{maxBodyRetries}] Body read failed, retrying in {delay.TotalSeconds:F0}s...");
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (liveResponse is null || bytes is null)
            throw new HttpRequestException("Failed to read HTTP response body after retries");

        var ct = liveResponse.Content.Headers.ContentType?.ToString() ?? "";

        await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
        await File.WriteAllTextAsync(metaPath, ct, cancellationToken);

        // Reconstruct response with the bytes we already read
        var freshContent = new ByteArrayContent(bytes);
        if (!string.IsNullOrEmpty(ct))
            freshContent.Headers.TryAddWithoutValidation("Content-Type", ct);

        liveResponse.Content.Dispose();
        liveResponse.Content = freshContent;

        return liveResponse;
    }

    private static string ComputeCacheKey(string url)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static HttpRequestMessage CloneGetRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(HttpMethod.Get, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
