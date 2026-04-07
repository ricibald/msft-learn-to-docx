using System.Text.Json;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Client for the Microsoft Learn Catalog API.
/// No authentication required.
/// </summary>
public sealed class LearnCatalogClient
{
    private const string BaseUrl = "https://learn.microsoft.com/api/catalog/";

    private readonly HttpClient _http;

    public LearnCatalogClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Gets module metadata from the Catalog API.
    /// Returns the module URL (used to derive the GitHub directory name).
    /// </summary>
    public async Task<CatalogModule> GetModuleAsync(string uid)
    {
        var url = $"{BaseUrl}?uid={Uri.EscapeDataString(uid)}&type=modules";
        var json = await _http.GetStringAsync(url);

        var response = JsonSerializer.Deserialize<CatalogModuleResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (response?.Modules is not { Count: > 0 })
            throw new InvalidOperationException($"Module not found in Learn Catalog: {uid}");

        return response.Modules[0];
    }

    /// <summary>
    /// Gets learning path metadata from the Catalog API by exact UID.
    /// </summary>
    public async Task<CatalogLearningPath> GetLearningPathAsync(string uid)
    {
        var url = $"{BaseUrl}?uid={Uri.EscapeDataString(uid)}&type=learningPaths";
        var json = await _http.GetStringAsync(url);

        var response = JsonSerializer.Deserialize<CatalogLearningPathResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (response?.LearningPaths is not { Count: > 0 })
            throw new InvalidOperationException($"Learning path not found in Learn Catalog: {uid}");

        return response.LearningPaths[0];
    }

    // Known UID namespace prefixes for learning paths, ordered by frequency.
    // Some paths (e.g., learn.wwl.*) have no index.yml in the GitHub repo and
    // must be resolved entirely through the Catalog API.
    private static readonly string[] LearningPathUidPrefixes =
    [
        "learn.wwl.",
        "learn.",
        "learn.m365.",
        "learn.azure.",
        "learn.dotnet.",
        "learn.github.",
    ];

    /// <summary>
    /// Finds a learning path in the Catalog API by URL slug, trying known UID namespace prefixes.
    /// Throws if no matching UID is found.
    /// </summary>
    public async Task<CatalogLearningPath> FindLearningPathBySlugAsync(string pathSlug)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var prefix in LearningPathUidPrefixes)
        {
            var uid = $"{prefix}{pathSlug}";
            var url = $"{BaseUrl}?uid={Uri.EscapeDataString(uid)}&type=learningPaths";
            var json = await _http.GetStringAsync(url);
            var response = JsonSerializer.Deserialize<CatalogLearningPathResponse>(json, jsonOptions);
            if (response?.LearningPaths is { Count: > 0 })
            {
                Console.WriteLine($"  Resolved path UID: {uid}");
                return response.LearningPaths[0];
            }
        }

        throw new InvalidOperationException(
            $"Learning path '{pathSlug}' not found in Microsoft Learn Catalog. " +
            $"Tried UID prefixes: {string.Join(", ", LearningPathUidPrefixes.Select(p => p + pathSlug))}");
    }

    /// <summary>
    /// Extracts the directory name from a Learn module URL.
    /// Example: "https://learn.microsoft.com/en-us/training/modules/introduction-copilot-spaces/?WT.mc_id=..." → "introduction-copilot-spaces"
    /// </summary>
    public static string ExtractDirNameFromUrl(string moduleUrl)
    {
        var uri = new Uri(moduleUrl);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Find the segment after "modules"
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("modules", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }

        throw new InvalidOperationException($"Cannot extract module directory name from URL: {moduleUrl}");
    }
}
