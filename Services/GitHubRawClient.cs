using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Client for downloading raw content from GitHub and using the GitHub Contents API.
/// </summary>
public sealed class GitHubRawClient
{
    private const string RawBaseUrl = "https://raw.githubusercontent.com/MicrosoftDocs/learn/main/";
    private const string ApiBaseUrl = "https://api.github.com/repos/MicrosoftDocs/learn/contents/";

    private readonly HttpClient _http;
    private List<string>? _parentDirsCache;

    public GitHubRawClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Downloads a text file from raw.githubusercontent.com.
    /// Returns null if the file is not found (404).
    /// Throws on other HTTP errors.
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
    /// Downloads a text file. Throws if not found.
    /// </summary>
    public async Task<string> DownloadStringAsync(string repoPath)
    {
        var content = await TryDownloadStringAsync(repoPath);
        if (content is null)
            throw new InvalidOperationException($"File not found on GitHub: {repoPath}");
        return content;
    }

    /// <summary>
    /// Downloads a binary file (e.g., media) and saves to local path.
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

    /// <summary>
    /// Lists directory contents via the GitHub Contents API.
    /// </summary>
    public async Task<List<GitHubContentItem>> ListDirectoryAsync(string repoPath)
    {
        var url = ApiBaseUrl + repoPath;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var response = await _http.SendAsync(request);
        EnsureGitHubSuccessStatusCode(response, url);

        var json = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return items ?? throw new InvalidOperationException($"Failed to parse directory listing for: {repoPath}");
    }

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
}
