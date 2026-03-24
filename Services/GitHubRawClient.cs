using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Client for downloading raw content from GitHub and using the GitHub Contents API.
/// Supports both the default MicrosoftDocs/learn repo and arbitrary repos (for docs sites).
/// </summary>
public sealed class GitHubRawClient
{
    private const string RawBaseUrl = "https://raw.githubusercontent.com/MicrosoftDocs/learn/main/";
    private const string ApiBaseUrl = "https://api.github.com/repos/MicrosoftDocs/learn/contents/";

    private readonly HttpClient _http;
    private readonly HttpClient _lfsHttp;
    private readonly string? _lfsCacheDir;
    private List<string>? _parentDirsCache;

    public GitHubRawClient(HttpClient http, string? cacheDir = null)
    {
        _http = http;
        // Separate HttpClient for LFS Batch API — no default Authorization header
        // (GitHub's LFS endpoint may reject Bearer tokens or require different auth)
        _lfsHttp = new HttpClient();
        _lfsHttp.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("MsftLearnToDocx", "1.0"));
        _lfsHttp.Timeout = TimeSpan.FromSeconds(60);

        // LFS OID cache directory (lfs/ subfolder of HTTP cache dir)
        if (cacheDir is not null)
        {
            _lfsCacheDir = Path.Combine(cacheDir, "lfs");
            Directory.CreateDirectory(_lfsCacheDir);
        }
    }

    // ---- Default repo methods (MicrosoftDocs/learn) ----

    /// <summary>
    /// Downloads a text file from raw.githubusercontent.com (MicrosoftDocs/learn).
    /// Returns null if the file is not found (404).
    /// </summary>
    public async Task<string?> TryDownloadStringAsync(string repoPath)
    {
        var url = RawBaseUrl + repoPath;
        var response = await _http.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        EnsureGitHubSuccessStatusCode(response, url);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Downloads a text file from MicrosoftDocs/learn. Throws if not found.
    /// </summary>
    public async Task<string> DownloadStringAsync(string repoPath)
    {
        var content = await TryDownloadStringAsync(repoPath);
        if (content is null)
            throw new InvalidOperationException($"File not found on GitHub: {repoPath}");
        return content;
    }

    /// <summary>
    /// Downloads a binary file from MicrosoftDocs/learn and saves to local path.
    /// </summary>
    public async Task DownloadFileAsync(string repoPath, string localPath)
    {
        var url = RawBaseUrl + repoPath;
        var response = await _http.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            Console.WriteLine($"  [WARN] Media not found: {repoPath}");
            return;
        }

        response.EnsureSuccessStatusCode();
        var dir = Path.GetDirectoryName(localPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await using var fs = File.Create(localPath);
        await response.Content.CopyToAsync(fs);
    }

    /// <summary>
    /// Lists directory contents via the GitHub Contents API (MicrosoftDocs/learn).
    /// </summary>
    public async Task<List<GitHubContentItem>> ListDirectoryAsync(string repoPath)
    {
        var url = ApiBaseUrl + repoPath;
        return await ListDirectoryFromUrlAsync(url);
    }

    // ---- Arbitrary repo methods ----

    /// <summary>
    /// Downloads a text file from an arbitrary repo. Returns null if not found (404).
    /// </summary>
    public async Task<string?> TryDownloadStringAsync(DocsRepoInfo repo, string repoPath)
    {
        var url = $"https://raw.githubusercontent.com/{repo.FullRepo}/{repo.Branch}/{repoPath}";
        var response = await _http.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        EnsureGitHubSuccessStatusCode(response, url);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Downloads a text file from an arbitrary repo. Throws if not found.
    /// </summary>
    public async Task<string> DownloadStringAsync(DocsRepoInfo repo, string repoPath)
    {
        var content = await TryDownloadStringAsync(repo, repoPath);
        if (content is null)
            throw new InvalidOperationException($"File not found on GitHub ({repo.FullRepo}): {repoPath}");
        return content;
    }

    /// <summary>
    /// Downloads a binary file from an arbitrary repo and saves to local path.
    /// For LFS repos, detects LFS pointer files and resolves them via the Git LFS Batch API.
    /// </summary>
    public async Task DownloadFileAsync(DocsRepoInfo repo, string repoPath, string localPath)
    {
        var url = $"https://raw.githubusercontent.com/{repo.FullRepo}/{repo.Branch}/{repoPath}";
        var response = await _http.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            Console.WriteLine($"  [WARN] Media not found ({repo.FullRepo}): {repoPath}");
            return;
        }

        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync();

        // Check for LFS pointer and resolve via LFS Batch API
        if (repo.UsesLfs && IsLfsPointer(bytes))
        {
            var lfsInfo = ParseLfsPointer(bytes);
            if (lfsInfo is not null)
            {
                var lfsBytes = await DownloadLfsBlobAsync(repo, lfsInfo.Value.Oid, lfsInfo.Value.Size);
                if (lfsBytes is not null)
                    bytes = lfsBytes;
                else
                    Console.WriteLine($"  [WARN] LFS download failed ({repo.FullRepo}): {repoPath}");
            }
        }

        var dir = Path.GetDirectoryName(localPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(localPath, bytes);
    }

    /// <summary>
    /// Downloads an LFS blob via the Git LFS Batch API.
    /// POST https://github.com/{owner}/{repo}.git/info/lfs/objects/batch
    /// Returns the binary content or null on failure.
    /// </summary>
    private async Task<byte[]?> DownloadLfsBlobAsync(DocsRepoInfo repo, string oid, long size)
    {
        var results = await ResolveLfsBatchAsync(repo, [(oid, size)]);
        return results.GetValueOrDefault(oid);
    }

    /// <summary>
    /// Downloads multiple LFS files from an arbitrary repo using a single LFS Batch API call.
    /// For each file: downloads raw content, detects LFS pointer, batches all OIDs together,
    /// then downloads binaries from the resolved URLs. Saves each file to its local path.
    /// </summary>
    public async Task DownloadLfsFilesAsync(DocsRepoInfo repo, IReadOnlyList<(string RepoPath, string LocalPath)> files)
    {
        // Phase 1: Download all raw files and collect LFS pointers
        var nonLfsFiles = new List<(string LocalPath, byte[] Bytes)>();
        var lfsPointers = new List<(string RepoPath, string LocalPath, string Oid, long Size)>();

        var count = 0;
        foreach (var (repoPath, localPath) in files)
        {
            count++;
            Console.Write($"\r  [{count}/{files.Count}] Downloading images...");

            // Skip if file already exists with correct size (LFS cache hit)
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                continue;
            }

            var url = $"https://raw.githubusercontent.com/{repo.FullRepo}/{repo.Branch}/{repoPath}";
            var response = await _http.GetAsync(url);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"\n  [WARN] Media not found ({repo.FullRepo}): {repoPath}");
                continue;
            }

            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();

            if (repo.UsesLfs && IsLfsPointer(bytes))
            {
                var lfsInfo = ParseLfsPointer(bytes);
                if (lfsInfo is not null)
                {
                    lfsPointers.Add((repoPath, localPath, lfsInfo.Value.Oid, lfsInfo.Value.Size));
                    continue;
                }
            }

            nonLfsFiles.Add((localPath, bytes));
        }

        // Save non-LFS files immediately
        foreach (var (localPath, bytes) in nonLfsFiles)
        {
            var dir = Path.GetDirectoryName(localPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(localPath, bytes);
        }

        if (lfsPointers.Count == 0)
        {
            Console.WriteLine();
            return;
        }

        // Phase 2: Single batch request for all LFS OIDs
        Console.Write($"\r  Resolving {lfsPointers.Count} LFS objects via Batch API...");
        var oidToBytes = await ResolveLfsBatchAsync(repo,
            lfsPointers.Select(p => (p.Oid, p.Size)).ToList());

        // Phase 3: Save resolved LFS files
        var resolved = 0;
        foreach (var (repoPath, localPath, oid, _) in lfsPointers)
        {
            if (oidToBytes.TryGetValue(oid, out var lfsBytes))
            {
                var dir = Path.GetDirectoryName(localPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(localPath, lfsBytes);
                resolved++;
            }
            else
            {
                Console.WriteLine($"\n  [WARN] LFS download failed ({repo.FullRepo}): {repoPath}");
            }
        }

        Console.WriteLine($"\r  LFS: {resolved}/{lfsPointers.Count} objects resolved                    ");
    }

    /// <summary>
    /// Calls the Git LFS Batch API with multiple OIDs and downloads all resolved objects.
    /// Uses OID-based file cache to avoid re-downloading unchanged content.
    /// Returns a dictionary mapping OID → binary content.
    /// </summary>
    private async Task<Dictionary<string, byte[]>> ResolveLfsBatchAsync(
        DocsRepoInfo repo, IReadOnlyList<(string Oid, long Size)> objects)
    {
        var result = new Dictionary<string, byte[]>();

        // Check OID cache first
        var uncachedObjects = new List<(string Oid, long Size)>();
        foreach (var (oid, size) in objects)
        {
            var cached = TryReadLfsCache(oid);
            if (cached is not null)
                result[oid] = cached;
            else
                uncachedObjects.Add((oid, size));
        }

        if (result.Count > 0)
            Console.Write($" ({result.Count} cached)");

        if (uncachedObjects.Count == 0)
            return result;

        var batchUrl = $"https://github.com/{repo.FullRepo}.git/info/lfs/objects/batch";
        var requestBody = JsonSerializer.Serialize(new
        {
            operation = "download",
            transfers = new[] { "basic" },
            objects = uncachedObjects.Select(o => new { oid = o.Oid, size = o.Size }).ToArray()
        });

        var request = new HttpRequestMessage(HttpMethod.Post, batchUrl)
        {
            Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/vnd.git-lfs+json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.git-lfs+json"));

        var response = await _lfsHttp.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return result;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("objects", out var objectsArray))
            return result;

        foreach (var obj in objectsArray.EnumerateArray())
        {
            if (!obj.TryGetProperty("oid", out var oidProp))
                continue;
            var oid = oidProp.GetString();
            if (oid is null)
                continue;

            if (!obj.TryGetProperty("actions", out var actions) ||
                !actions.TryGetProperty("download", out var download) ||
                !download.TryGetProperty("href", out var href))
                continue;

            var downloadUrl = href.GetString();
            if (string.IsNullOrEmpty(downloadUrl))
                continue;

            var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            if (download.TryGetProperty("header", out var headers))
            {
                foreach (var header in headers.EnumerateObject())
                {
                    var headerValue = header.Value.GetString();
                    if (headerValue is not null)
                        downloadRequest.Headers.TryAddWithoutValidation(header.Name, headerValue);
                }
            }

            var downloadResponse = await _lfsHttp.SendAsync(downloadRequest);
            if (downloadResponse.IsSuccessStatusCode)
            {
                var bytes = await downloadResponse.Content.ReadAsByteArrayAsync();
                result[oid] = bytes;
                WriteLfsCache(oid, bytes);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses an LFS pointer file and extracts the OID (sha256 hash) and size.
    /// </summary>
    internal static (string Oid, long Size)? ParseLfsPointer(byte[] content)
    {
        var text = System.Text.Encoding.UTF8.GetString(content);
        string? oid = null;
        long size = 0;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
                oid = line["oid sha256:".Length..].Trim();
            else if (line.StartsWith("size ", StringComparison.Ordinal))
                long.TryParse(line["size ".Length..].Trim(), out size);
        }

        return oid is not null && size > 0 ? (oid, size) : null;
    }

    /// <summary>
    /// Reads a cached LFS object by OID. Returns null if not cached.
    /// </summary>
    private byte[]? TryReadLfsCache(string oid)
    {
        if (_lfsCacheDir is null) return null;
        var path = Path.Combine(_lfsCacheDir, oid);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Writes an LFS object to the OID cache.
    /// </summary>
    private void WriteLfsCache(string oid, byte[] content)
    {
        if (_lfsCacheDir is null) return;
        File.WriteAllBytes(Path.Combine(_lfsCacheDir, oid), content);
    }

    /// <summary>
    /// Lists directory contents for an arbitrary repo via the GitHub Contents API.
    /// Returns empty list if the directory is not found (404).
    /// </summary>
    public async Task<List<GitHubContentItem>> ListDirectoryAsync(DocsRepoInfo repo, string repoPath)
    {
        var url = $"https://api.github.com/repos/{repo.FullRepo}/contents/{repoPath}?ref={repo.Branch}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var response = await _http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        EnsureGitHubSuccessStatusCode(response, url);
        var json = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return items ?? [];
    }

    /// <summary>
    /// Recursively lists all files in a directory tree for an arbitrary repo.
    /// </summary>
    public async Task<List<GitHubContentItem>> ListDirectoryRecursiveAsync(DocsRepoInfo repo, string repoPath)
    {
        var allFiles = new List<GitHubContentItem>();
        var items = await ListDirectoryAsync(repo, repoPath);

        foreach (var item in items)
        {
            if (item.Type == "file")
            {
                allFiles.Add(item);
            }
            else if (item.Type == "dir")
            {
                var nested = await ListDirectoryRecursiveAsync(repo, item.Path);
                allFiles.AddRange(nested);
            }
        }

        return allFiles;
    }

    // ---- Shared helpers ----

    /// <summary>
    /// Returns all top-level subdirectories under learn-pr/ (cached).
    /// </summary>
    public async Task<List<string>> GetLearnPrParentDirsAsync()
    {
        if (_parentDirsCache is not null)
            return _parentDirsCache;

        Console.WriteLine("  Scanning learn-pr/ parent directories...");
        var items = await ListDirectoryAsync("learn-pr");
        _parentDirsCache = items
            .Where(i => i.Type == "dir" && i.Name != "paths" && i.Name != "achievements")
            .Select(i => i.Name)
            .ToList();

        Console.WriteLine($"  Found {_parentDirsCache.Count} parent directories");
        return _parentDirsCache;
    }

    private static void EnsureGitHubSuccessStatusCode(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException(
                $"GitHub API rate limit exceeded (HTTP 403). " +
                $"Set the GITHUB_TOKEN environment variable to raise the limit from 60 to 5000 req/h. " +
                $"URL: {url}");

        response.EnsureSuccessStatusCode();
    }

    private async Task<List<GitHubContentItem>> ListDirectoryFromUrlAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var response = await _http.SendAsync(request);
        EnsureGitHubSuccessStatusCode(response, url);

        var json = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return items ?? throw new InvalidOperationException($"Failed to parse directory listing for: {url}");
    }

    /// <summary>
    /// Detects Git LFS pointer files by checking for the LFS version header.
    /// </summary>
    internal static bool IsLfsPointer(byte[] content)
    {
        if (content.Length > 1024) return false; // LFS pointers are small
        var text = System.Text.Encoding.UTF8.GetString(content);
        return text.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal);
    }
}
