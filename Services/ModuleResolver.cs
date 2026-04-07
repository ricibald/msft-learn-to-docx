using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// The result of resolving a module UID to a GitHub location.
/// When AltRepo is null the path is within the default MicrosoftDocs/learn repository.
/// When AltRepo is non-null the path is within that alternate repository.
/// </summary>
public record ResolvedModulePath(string Path, DocsRepoInfo? AltRepo);

/// <summary>
/// Resolves a module UID to its full path on GitHub.
/// Primary repo: MicrosoftDocs/learn (learn-pr/ tree).
/// Fallback repo: MicrosoftDocs/learn-m365-pr (learn-m365-pr/wwl/ tree, branch live).
/// Strategy: Catalog API for directory name + GitHub scan for parent directory.
/// </summary>
public sealed class ModuleResolver
{
    private readonly LearnCatalogClient _catalog;
    private readonly GitHubRawClient _github;

    // M365 wwl modules live in a separate semi-public repo, branch live
    private static readonly DocsRepoInfo LearnM365Repo = new(
        "MicrosoftDocs", "learn-m365-pr", "live",
        "learn-m365-pr", "",
        UsesLfs: false, LiveSiteHost: null, LiveSitePathPrefix: null);

    // Cache: module UID prefix (e.g., "github") → parent dir (e.g., "github")
    private readonly Dictionary<string, string> _parentDirCache = new(StringComparer.OrdinalIgnoreCase);

    public ModuleResolver(LearnCatalogClient catalog, GitHubRawClient github)
    {
        _catalog = catalog;
        _github = github;
    }

    /// <summary>
    /// Resolves a module UID to a <see cref="ResolvedModulePath"/>.
    /// The path is relative to the repository root (e.g., "learn-pr/github/intro-to-copilot").
    /// </summary>
    public async Task<ResolvedModulePath> ResolveModulePathAsync(string moduleUid)
    {
        Console.WriteLine($"  Resolving module: {moduleUid}");

        // Step 1: Get directory name from Catalog API
        var catalogModule = await _catalog.GetModuleAsync(moduleUid);
        var dirName = LearnCatalogClient.ExtractDirNameFromUrl(catalogModule.Url);
        Console.WriteLine($"    Directory name: {dirName}");

        // Step 2: Try heuristic parent dirs first (within MicrosoftDocs/learn)
        var hintParents = GetHeuristicParentDirs(moduleUid);
        foreach (var parent in hintParents)
        {
            var candidatePath = $"learn-pr/{parent}/{dirName}";
            var content = await _github.TryDownloadStringAsync($"{candidatePath}/index.yml");
            if (content is not null)
            {
                Console.WriteLine($"    Found at: {candidatePath}");
                CacheParentDir(moduleUid, parent);
                return new ResolvedModulePath(candidatePath, AltRepo: null);
            }
        }

        // Step 3: Fallback — scan all parent directories within MicrosoftDocs/learn
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
                return new ResolvedModulePath(candidatePath, AltRepo: null);
            }
        }

        // Step 4: Fallback — try MicrosoftDocs/learn-m365-pr (branch live, path learn-m365-pr/wwl/)
        // M365 wwl modules (e.g., learn.wwl.explore-microsoft-365-*) live there.
        var m365CandidatePath = $"learn-m365-pr/wwl/{dirName}";
        var m365Content = await _github.TryDownloadStringAsync(LearnM365Repo, $"{m365CandidatePath}/index.yml");
        if (m365Content is not null)
        {
            Console.WriteLine($"    Found in MicrosoftDocs/learn-m365-pr at: {m365CandidatePath}");
            return new ResolvedModulePath(m365CandidatePath, AltRepo: LearnM365Repo);
        }

        throw new InvalidOperationException(
            $"Could not find module '{moduleUid}' (dir: '{dirName}') in any learn-pr/ subdirectory " +
            $"or in MicrosoftDocs/learn-m365-pr");
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
