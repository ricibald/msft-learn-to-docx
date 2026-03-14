using System.Net;

namespace MsftLearnToDocx.Services;

/// <summary>
/// HTTP DelegatingHandler that retries transient failures with exponential backoff.
/// Handles HTTP 429 (Too Many Requests), 5xx, and network errors.
/// Respects Retry-After header from GitHub API.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private const int MaxRetries = 3;

    public RetryHandler() : base(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var currentRequest = attempt == 0 ? request : CloneRequest(request);

            try
            {
                response = await base.SendAsync(currentRequest, cancellationToken);

                // Success or 404 (expected for "file not found" checks) — return immediately
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                    return response;

                // Non-transient error — return immediately, no retry
                if (!IsTransient(response.StatusCode))
                    return response;

                // Transient error — retry if attempts remaining
                if (attempt < MaxRetries)
                {
                    var delay = GetDelay(attempt, response);
                    Console.WriteLine($"\n  [Retry {attempt + 1}/{MaxRetries}] HTTP {(int)response.StatusCode}, waiting {delay.TotalSeconds:F1}s...");
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                Console.WriteLine($"\n  [Retry {attempt + 1}/{MaxRetries}] {ex.Message}, waiting {delay.TotalSeconds:F1}s...");
                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                // Timeout (not user cancellation)
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                Console.WriteLine($"\n  [Retry {attempt + 1}/{MaxRetries}] Timeout: {ex.Message}, waiting {delay.TotalSeconds:F1}s...");
                await Task.Delay(delay, cancellationToken);
            }
        }

        return response ?? throw new HttpRequestException("All retry attempts exhausted");
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetDelay(int attempt, HttpResponseMessage response)
    {
        // Respect Retry-After header (GitHub API uses this for rate limiting)
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
            return retryAfter;

        if (response.Headers.RetryAfter?.Date is { } retryDate)
        {
            var delay = retryDate - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                return delay;
        }

        // Exponential backoff: 2s, 4s, 8s
        return TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
    }

    /// <summary>
    /// Clones an HTTP request message for retry (original can't be sent twice).
    /// Only supports requests without body (GET/HEAD).
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
