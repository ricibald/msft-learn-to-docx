using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Result of parsing a documentation URL.
/// </summary>
public abstract record ParsedUrl;

/// <summary>
/// URL pointing to a Microsoft Learn training path or module.
/// </summary>
public record LearnTrainingUrl(string Type, string Slug) : ParsedUrl;

/// <summary>
/// URL pointing to a documentation site backed by a GitHub repo.
/// </summary>
public record DocsSiteUrl(DocsRepoInfo RepoInfo) : ParsedUrl;

/// <summary>
/// Parses documentation URLs and maps them to GitHub repo coordinates.
/// Supports:
///   - learn.microsoft.com training paths/modules (existing flow)
///   - code.visualstudio.com/docs/* → microsoft/vscode-docs
///   - learn.microsoft.com/{locale?}/{product}/* → auto-detected or known-mapped repos
/// </summary>
public static partial class DocsUrlParser
{
    /// <summary>
    /// Known mappings from URL path prefix → GitHub repo info.
    /// Ordered by prefix length descending so longer prefixes match first.
    /// </summary>
    private static readonly (string Host, string PathPrefix, string Owner, string Repo, string Branch, string DocsPath, bool UsesLfs)[] KnownMappings =
    [
        // code.visualstudio.com/docs/* → microsoft/vscode-docs (uses Git LFS for images)
        ("code.visualstudio.com", "docs", "microsoft", "vscode-docs", "main", "docs", true),

        // learn.microsoft.com/.../azure/devops/* → MicrosoftDocs/azure-devops-docs
        ("learn.microsoft.com", "azure/devops", "MicrosoftDocs", "azure-devops-docs", "main", "docs", false),

        // learn.microsoft.com/.../dotnet/* → dotnet/docs
        ("learn.microsoft.com", "dotnet", "dotnet", "docs", "main", "docs", false),

        // learn.microsoft.com/.../azure/* → MicrosoftDocs/azure-docs (articles/ base path)
        ("learn.microsoft.com", "azure", "MicrosoftDocs", "azure-docs", "main", "articles", false),

        // learn.microsoft.com/.../sql/* → MicrosoftDocs/sql-docs
        ("learn.microsoft.com", "sql", "MicrosoftDocs", "sql-docs", "main", "docs", false),

        // learn.microsoft.com/.../powershell/* → MicrosoftDocs/PowerShell-Docs
        ("learn.microsoft.com", "powershell", "MicrosoftDocs", "PowerShell-Docs", "main", "reference", false),

        // learn.microsoft.com/.../visualstudio/* → MicrosoftDocs/visualstudio-docs
        ("learn.microsoft.com", "visualstudio", "MicrosoftDocs", "visualstudio-docs", "main", "docs", false),

        // learn.microsoft.com/.../windows/* → MicrosoftDocs/windows-dev-docs
        ("learn.microsoft.com", "windows", "MicrosoftDocs", "windows-dev-docs", "main", "uwp", false),
    ];

    /// <summary>
    /// Parses a URL and returns either a LearnTrainingUrl or DocsSiteUrl.
    /// Throws if the URL format is not recognized.
    /// </summary>
    public static ParsedUrl Parse(string url)
    {
        // Try Learn training URL first
        var trainingMatch = TrainingUrlRegex().Match(url);
        if (trainingMatch.Success)
        {
            return new LearnTrainingUrl(
                trainingMatch.Groups[1].Value.ToLowerInvariant(),
                trainingMatch.Groups[2].Value);
        }

        // Parse as URI
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL: {url}");

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.Trim('/');

        // Strip locale from learn.microsoft.com paths (e.g., "en-us/azure/devops/...")
        if (host == "learn.microsoft.com")
            path = StripLocale(path);

        // Try known mappings (longest prefix first — array is pre-sorted)
        foreach (var mapping in KnownMappings)
        {
            if (!host.Equals(mapping.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            // For learn.microsoft.com, match against locale-stripped path
            // For other hosts, match against full path
            if (!path.StartsWith(mapping.PathPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Extract the content path after the prefix
            var contentPath = path.Length > mapping.PathPrefix.Length
                ? path[(mapping.PathPrefix.Length + 1)..].Trim('/')
                : "";

            return new DocsSiteUrl(new DocsRepoInfo(
                mapping.Owner,
                mapping.Repo,
                mapping.Branch,
                mapping.DocsPath,
                contentPath,
                mapping.UsesLfs));
        }

        throw new ArgumentException(
            $"Unrecognized URL format. Supported:\n" +
            $"  - https://learn.microsoft.com/.../training/paths/{{slug}}\n" +
            $"  - https://learn.microsoft.com/.../training/modules/{{slug}}\n" +
            $"  - https://code.visualstudio.com/docs/{{path}}\n" +
            $"  - https://learn.microsoft.com/.../azure/devops/{{path}}\n" +
            $"  - https://learn.microsoft.com/.../dotnet/{{path}}\n" +
            $"  - https://learn.microsoft.com/.../azure/{{path}}\n" +
            $"Got: {url}");
    }

    /// <summary>
    /// Checks if a URL is a docs site URL (not a Learn training URL).
    /// </summary>
    public static bool IsDocsSiteUrl(string url) => Parse(url) is DocsSiteUrl;

    /// <summary>
    /// Strips the locale segment from a learn.microsoft.com path.
    /// E.g., "en-us/azure/devops/repos" → "azure/devops/repos"
    /// </summary>
    private static string StripLocale(string path)
    {
        var match = LocaleRegex().Match(path);
        return match.Success ? path[(match.Length)..].TrimStart('/') : path;
    }

    // Matches training URLs: .../training/(paths|modules)/slug
    [GeneratedRegex(@"(?:training/)?(paths|modules)/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrainingUrlRegex();

    // Matches locale prefix: en-us, fr-fr, zh-cn, etc.
    [GeneratedRegex(@"^[a-z]{2}(-[a-z]{2,4})?/", RegexOptions.IgnoreCase)]
    private static partial Regex LocaleRegex();
}
