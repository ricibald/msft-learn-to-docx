using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;
using Xunit.Abstractions;

namespace MsftLearnToDocx.Tests;

/// <summary>
/// Diagnostic tests to investigate LFS batch download failures.
/// </summary>
public sealed class LfsBatchDiagnosticTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _lfsHttp;
    private readonly string _cacheDir;
    private readonly ITestOutputHelper _output;

    public LfsBatchDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        _cacheDir = Path.Combine(localAppData, "MsftLearnToDocx", "cache");

        var handler = new CachingHandler(new RetryHandler(), _cacheDir, TimeSpan.FromHours(24));
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MsftLearnToDocx", "1.0"));
        _httpClient.Timeout = TimeSpan.FromSeconds(60);

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(githubToken))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);

        // Separate LFS client (no auth)
        _lfsHttp = new HttpClient();
        _lfsHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MsftLearnToDocx", "1.0"));
        _lfsHttp.Timeout = TimeSpan.FromSeconds(60);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _lfsHttp.Dispose();
    }

    /// <summary>
    /// Downloads several LFS pointer files and calls the batch API.
    /// Logs every step to diagnose where the failure occurs.
    /// </summary>
    [Fact]
    public async Task DiagnosticReport_LfsBatchBehavior()
    {
        var repo = new DocsRepoInfo("microsoft", "vscode-docs", "main", "docs", "copilot", UsesLfs: true);

        // Test with a few known image paths from the copilot directory
        var testPaths = new[]
        {
            "docs/copilot/images/agents-overview/sessions-type-picker.png",
            "docs/copilot/images/copilot-smart-actions/generate-commit-message.png",
            "docs/copilot/images/getting-started/copilot-chat-view-welcome.png",
        };

        // Phase 1: Download raw files and extract LFS pointers
        var lfsPointers = new List<(string Path, string Oid, long Size)>();

        foreach (var path in testPaths)
        {
            var url = $"https://raw.githubusercontent.com/{repo.FullRepo}/{repo.Branch}/{path}";
            _output.WriteLine($"Downloading raw: {path}");

            var response = await _httpClient.GetAsync(url);
            _output.WriteLine($"  Status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                _output.WriteLine($"  FAILED: {response.StatusCode}");
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            _output.WriteLine($"  Content length: {bytes.Length} bytes");

            var isLfs = GitHubRawClient.IsLfsPointer(bytes);
            _output.WriteLine($"  Is LFS pointer: {isLfs}");

            if (isLfs)
            {
                var text = Encoding.UTF8.GetString(bytes);
                _output.WriteLine($"  Pointer content: {text.TrimEnd()}");

                var lfsInfo = GitHubRawClient.ParseLfsPointer(bytes);
                if (lfsInfo is not null)
                {
                    _output.WriteLine($"  Parsed OID: {lfsInfo.Value.Oid}");
                    _output.WriteLine($"  Parsed Size: {lfsInfo.Value.Size}");
                    lfsPointers.Add((path, lfsInfo.Value.Oid, lfsInfo.Value.Size));
                }
                else
                {
                    _output.WriteLine("  FAILED to parse LFS pointer!");
                }
            }
            else
            {
                _output.WriteLine($"  First 100 bytes: {Encoding.UTF8.GetString(bytes, 0, Math.Min(100, bytes.Length))}");
            }
        }

        Assert.NotEmpty(lfsPointers);
        _output.WriteLine($"\nCollected {lfsPointers.Count} LFS pointers");

        // Phase 2: Call LFS Batch API
        var batchUrl = $"https://github.com/{repo.FullRepo}.git/info/lfs/objects/batch";
        _output.WriteLine($"\nBatch API URL: {batchUrl}");

        var requestBody = JsonSerializer.Serialize(new
        {
            operation = "download",
            transfers = new[] { "basic" },
            objects = lfsPointers.Select(p => new { oid = p.Oid, size = p.Size }).ToArray()
        });
        _output.WriteLine($"Request body ({requestBody.Length} chars): {requestBody[..Math.Min(500, requestBody.Length)]}");

        var request = new HttpRequestMessage(HttpMethod.Post, batchUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/vnd.git-lfs+json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.git-lfs+json"));

        // Log the actual Content-Type that .NET sends
        _output.WriteLine($"Content-Type: {request.Content.Headers.ContentType}");

        var batchResponse = await _lfsHttp.SendAsync(request);
        _output.WriteLine($"\nBatch API response status: {batchResponse.StatusCode} ({(int)batchResponse.StatusCode})");

        var responseHeaders = string.Join("; ", batchResponse.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        _output.WriteLine($"Response headers: {responseHeaders}");

        var responseBody = await batchResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"Response body ({responseBody.Length} chars):");
        _output.WriteLine(responseBody.Length > 2000 ? responseBody[..2000] + "..." : responseBody);

        if (!batchResponse.IsSuccessStatusCode)
        {
            _output.WriteLine("\n*** BATCH API RETURNED NON-SUCCESS STATUS ***");
            _output.WriteLine("This is the likely root cause of 138 image failures!");
            return;
        }

        // Phase 3: Parse batch response
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("objects", out var objectsArray))
        {
            _output.WriteLine("\n*** BATCH RESPONSE HAS NO 'objects' PROPERTY ***");
            return;
        }

        var resolvedCount = 0;
        var errorCount = 0;
        foreach (var obj in objectsArray.EnumerateArray())
        {
            var oid = obj.GetProperty("oid").GetString();

            if (obj.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                _output.WriteLine($"  Object {oid?[..12]}... ERROR: {code} - {msg}");
                errorCount++;
                continue;
            }

            if (obj.TryGetProperty("actions", out var actions) &&
                actions.TryGetProperty("download", out var download) &&
                download.TryGetProperty("href", out var href))
            {
                var downloadUrl = href.GetString();
                _output.WriteLine($"  Object {oid?[..12]}... has download URL ({downloadUrl?.Length} chars)");

                // Try to actually download the binary
                var dlRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                if (download.TryGetProperty("header", out var headers))
                {
                    foreach (var header in headers.EnumerateObject())
                    {
                        var val = header.Value.GetString();
                        if (val is not null)
                            dlRequest.Headers.TryAddWithoutValidation(header.Name, val);
                    }
                }

                var dlResponse = await _lfsHttp.SendAsync(dlRequest);
                _output.WriteLine($"  Download response: {dlResponse.StatusCode} ({(int)dlResponse.StatusCode}), Content-Length: {dlResponse.Content.Headers.ContentLength}");

                if (dlResponse.IsSuccessStatusCode)
                    resolvedCount++;
            }
            else
            {
                _output.WriteLine($"  Object {oid?[..12]}... has no download action!");
            }
        }

        _output.WriteLine($"\nSummary: {resolvedCount} resolved, {errorCount} errors out of {lfsPointers.Count} requested");

        // Phase 4: Also check LFS OID cache state
        var lfsCacheDir = Path.Combine(_cacheDir, "lfs");
        if (Directory.Exists(lfsCacheDir))
        {
            var cachedFiles = Directory.GetFiles(lfsCacheDir);
            _output.WriteLine($"\nLFS OID cache: {cachedFiles.Length} files in {lfsCacheDir}");

            // Check if our test OIDs are cached
            foreach (var (path, oid, size) in lfsPointers)
            {
                var cachePath = Path.Combine(lfsCacheDir, oid);
                if (File.Exists(cachePath))
                {
                    var cachedSize = new FileInfo(cachePath).Length;
                    _output.WriteLine($"  Cached: {oid[..12]}... ({cachedSize} bytes, expected {size})");
                }
                else
                {
                    _output.WriteLine($"  NOT cached: {oid[..12]}...");
                }
            }
        }
        else
        {
            _output.WriteLine($"\nLFS OID cache directory does not exist: {lfsCacheDir}");
        }

        Assert.True(resolvedCount > 0, "Expected at least some LFS objects to be resolved");
    }
}
