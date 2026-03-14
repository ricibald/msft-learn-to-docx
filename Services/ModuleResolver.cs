namespace MsftLearnToDocx.Services;

/// <summary>
/// Resolves a module UID to its full path on GitHub within the MicrosoftDocs/learn repo.
/// Strategy: Catalog API for directory name + GitHub scan for parent directory.
/// </summary>
public sealed class ModuleResolver
{
    private readonly LearnCatalogClient _catalog;
    private readonly GitHubRawClient _github;

    // Cache: module UID prefix (e.g., "github") → parent dir (e.g., "github")
    private readonly Dictionary<string, string> _parentDirCache = new(StringComparer.OrdinalIgnoreCase);

    public ModuleResolver(LearnCatalogClient catalog, GitHubRawClient github)
    {
        _catalog = catalog;
        _github = github;
    }

    /// <summary>
    /// Resolves a module UID to a GitHub path like "learn-pr/github/introduction-to-github-copilot".
    /// </summary>
    public async Task<string> ResolveModulePathAsync(string moduleUid)
    {
        Console.WriteLine($"  Resolving module: {moduleUid}");

        // Step 1: Get directory name from Catalog API
        var catalogModule = await _catalog.GetModuleAsync(moduleUid);
        var dirName = LearnCatalogClient.ExtractDirNameFromUrl(catalogModule.Url);
        Console.WriteLine($"    Directory name: {dirName}");

        // Step 2: Try heuristic parent dirs first
        var hintParents = GetHeuristicParentDirs(moduleUid);
        foreach (var parent in hintParents)
        {
            var candidatePath = $"learn-pr/{parent}/{dirName}";
            var content = await _github.TryDownloadStringAsync($"{candidatePath}/index.yml");
            if (content is not null)
            {
                Console.WriteLine($"    Found at: {candidatePath}");
                CacheParentDir(moduleUid, parent);
                return candidatePath;
            }
        }

        // Step 3: Fallback - scan all parent directories
        Console.WriteLine($"    Heuristic failed, scanning all parent directories...");
        var allParents = await _github.GetLearnPrParentDirsAsync();
        var remainingParents = allParents.Except(hintParents, StringComparer.OrdinalIgnoreCase);

        foreach (var parent in remainingParents)
        {
            var candidatePath = $"learn-pr/{parent}/{dirName}";
            var content = await _github.TryDownloadStringAsync($"{candidatePath}/index.yml");
            if (content is not null)
            {
                Console.WriteLine($"    Found at: {candidatePath}");
                CacheParentDir(moduleUid, parent);
                return candidatePath;
            }
        }

        throw new InvalidOperationException(
            $"Could not find module '{moduleUid}' (dir: '{dirName}') in any learn-pr/ subdirectory");
    }

    private List<string> GetHeuristicParentDirs(string moduleUid)
    {
        var parts = moduleUid.Split('.');
        // UID patterns: learn.github.xxx, learn.wwl.xxx, learn.xxx
        var hints = new List<string>();

        // Check cache for this prefix
        var prefix = parts.Length >= 3 ? parts[1] : "";
        if (!string.IsNullOrEmpty(prefix) && _parentDirCache.TryGetValue(prefix, out var cached))
        {
            hints.Add(cached);
            return hints;
        }

        // Known mappings
        if (prefix.Equals("github", StringComparison.OrdinalIgnoreCase))
            hints.Add("github");
        else if (prefix.Equals("wwl", StringComparison.OrdinalIgnoreCase))
            hints.Add("wwl-azure");
        else if (prefix.Equals("azure", StringComparison.OrdinalIgnoreCase))
            hints.Add("azure");
        else if (prefix.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            hints.Add("dotnet");

        // For 2-segment UIDs (learn.xxx), try common parents
        if (parts.Length == 2)
        {
            hints.Add("github");
            hints.Add("azure");
        }

        return hints;
    }

    private void CacheParentDir(string moduleUid, string parentDir)
    {
        var parts = moduleUid.Split('.');
        if (parts.Length >= 3)
            _parentDirCache[parts[1]] = parentDir;
    }
}
