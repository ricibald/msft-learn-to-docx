using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Downloads documentation from a GitHub-backed docs site (e.g., vscode-docs, azure-devops-docs).
/// Recursively walks directories, uses toc.yml for page ordering when available,
/// downloads images (with Git LFS support), and converts DFM to standard Markdown.
/// </summary>
public sealed partial class DocsDownloader
{
    private readonly GitHubRawClient _github;
    private readonly DfmConverter _dfmConverter;
    private readonly IDeserializer _yaml;

    public DocsDownloader(GitHubRawClient github, DfmConverter dfmConverter)
    {
        _github = github;
        _dfmConverter = dfmConverter;
        _yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Downloads all documentation content from the given repo path recursively.
    /// </summary>
    public async Task<DownloadedContent> DownloadAsync(DocsRepoInfo repo, string outputDir)
    {
        var repoPath = repo.RepoContentPath;
        Console.WriteLine($"Downloading docs from {repo.FullRepo}: {repoPath}");

        // Collect ordered page list (using toc.yml if available)
        var pages = await CollectPagesAsync(repo, repoPath);
        Console.WriteLine($"  Found {pages.Count} pages to download");

        var content = new DownloadedContent
        {
            Title = DeriveTitleFromPath(repo.ContentPath),
            IsPath = false,
            Type = ContentType.DocsSite
        };

        var module = new DownloadedModule
        {
            Title = content.Title,
            Uid = $"docs.{repo.Repo}.{repo.ContentPath.Replace('/', '.')}"
        };

        // Track all image paths for downloading
        var allImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pages.Count; i++)
        {
            var (pagePath, pageTitle) = pages[i];
            Console.Write($"  [{i + 1}/{pages.Count}] {pagePath}...");

            var markdown = await _github.TryDownloadStringAsync(repo, pagePath);
            if (markdown is null)
            {
                Console.WriteLine(" (not found)");
                continue;
            }

            // Strip YAML frontmatter from individual pages
            markdown = StripFrontmatter(markdown);

            // Extract title from first H1 if not provided by toc.yml
            var title = pageTitle ?? ExtractFirstHeading(markdown) ?? Path.GetFileNameWithoutExtension(pagePath);

            var pageDir = Path.GetDirectoryName(pagePath)?.Replace('\\', '/') ?? "";

            // Resolve [!INCLUDE] references by downloading the referenced files
            markdown = await ResolveIncludesAsync(markdown, pageDir, repo);

            // Convert DFM to standard markdown (alerts, zones, :::image:::, etc.)
            // Must run BEFORE RemapImagePaths so :::image::: paths are captured
            markdown = _dfmConverter.Convert(markdown);

            // Strip HTML blocks (video tags, div tags common in vscode-docs)
            markdown = StripHtmlBlocks(markdown);

            // Collect and remap image paths (after DFM conversion so :::image::: paths are included)
            markdown = RemapImagePaths(markdown, pageDir, repo.RepoContentPath, allImagePaths);

            module.Units.Add(new DownloadedUnit
            {
                Title = title,
                Uid = pagePath,
                MarkdownContent = markdown
            });

            Console.WriteLine(" OK");
        }

        content.Modules.Add(module);

        // Download all images
        if (allImagePaths.Count > 0)
        {
            Console.WriteLine($"  Downloading {allImagePaths.Count} images...");
            var mediaDir = Path.Combine(outputDir, "media");
            Directory.CreateDirectory(mediaDir);

            if (repo.UsesLfs)
            {
                // Batch download: single LFS Batch API call for all images
                var filesToDownload = allImagePaths
                    .Select(imagePath => (
                        RepoPath: imagePath,
                        LocalPath: Path.Combine(mediaDir, SanitizeImageFileName(imagePath, repo.RepoContentPath))))
                    .ToList();

                await _github.DownloadLfsFilesAsync(repo, filesToDownload);
            }
            else
            {
                var count = 0;
                foreach (var imagePath in allImagePaths)
                {
                    count++;
                    Console.Write($"\r  [{count}/{allImagePaths.Count}] Downloading images...");
                    var localFileName = SanitizeImageFileName(imagePath, repo.RepoContentPath);
                    var localPath = Path.Combine(mediaDir, localFileName);
                    await _github.DownloadFileAsync(repo, imagePath, localPath);
                }
                Console.WriteLine();
            }
        }

        // Summary statistics
        var downloadedPages = module.Units.Count;
        var downloadedImages = allImagePaths.Count > 0
            ? Directory.Exists(Path.Combine(outputDir, "media"))
                ? Directory.GetFiles(Path.Combine(outputDir, "media")).Length
                : 0
            : 0;
        Console.WriteLine($"  Summary: {downloadedPages} pages, {downloadedImages}/{allImagePaths.Count} images downloaded");

        return content;
    }

    /// <summary>
    /// Collects pages in order: uses toc.yml if present, otherwise falls back to alphabetical directory listing.
    /// If the path is a single file (not a directory), returns it directly.
    /// Returns a list of (repoPath, title?) tuples.
    /// </summary>
    private async Task<List<(string Path, string? Title)>> CollectPagesAsync(DocsRepoInfo repo, string repoPath)
    {
        // Try to download toc.yml
        var tocYaml = await _github.TryDownloadStringAsync(repo, $"{repoPath}/toc.yml");
        if (tocYaml is not null)
        {
            Console.WriteLine("  Using toc.yml for page ordering");
            var tocEntries = _yaml.Deserialize<List<TocEntry>>(tocYaml);
            if (tocEntries is not null)
                return await FlattenTocAsync(tocEntries, repoPath, repo);
        }

        // Try directory listing first
        var pages = await CollectPagesFromDirectoryAsync(repo, repoPath);
        if (pages.Count > 0)
            return pages;

        // If directory listing returned nothing, the path may be a single file — try appending .md
        if (!repoPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var singleFilePath = repoPath + ".md";
            var content = await _github.TryDownloadStringAsync(repo, singleFilePath);
            if (content is not null)
            {
                Console.WriteLine("  Path resolves to a single page");
                return [(singleFilePath, null)];
            }
        }

        return [];
    }

    /// <summary>
    /// Flattens a toc.yml structure into an ordered list of (path, title) tuples.
    /// Recursively resolves sub-TOC references (e.g., "get-started/toc.yml").
    /// Skips external links and cross-references.
    /// </summary>
    private async Task<List<(string Path, string? Title)>> FlattenTocAsync(
        List<TocEntry> entries, string baseDir, DocsRepoInfo repo)
    {
        var pages = new List<(string, string?)>();

        foreach (var entry in entries)
        {
            if (entry.Href is not null)
            {
                var href = entry.Href.Trim();

                // Skip external URLs
                if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip cross-references (absolute paths starting with /)
                if (href.StartsWith('/'))
                    continue;

                // Skip references that go too far outside the base directory
                if (href.StartsWith("../../"))
                    continue;

                // Recursively resolve sub-TOC references (e.g., "get-started/toc.yml")
                if (href.EndsWith("toc.yml", StringComparison.OrdinalIgnoreCase))
                {
                    var subTocPath = ResolveRelativePath(baseDir, href);
                    var subTocContent = await _github.TryDownloadStringAsync(repo, subTocPath);
                    if (subTocContent is not null)
                    {
                        var subTocDir = Path.GetDirectoryName(subTocPath)?.Replace('\\', '/') ?? baseDir;
                        var subEntries = _yaml.Deserialize<List<TocEntry>>(subTocContent);
                        if (subEntries is not null)
                        {
                            var subPages = await FlattenTocAsync(subEntries, subTocDir, repo);
                            pages.AddRange(subPages);
                        }
                    }
                    continue;
                }

                // Skip query string parameters and anchors
                var cleanHref = href.Split('?')[0].Split('#')[0].Trim();
                if (string.IsNullOrEmpty(cleanHref))
                    continue;

                // Only include .md files for content
                if (cleanHref.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    var fullPath = ResolveRelativePath(baseDir, cleanHref);
                    pages.Add((fullPath, entry.EffectiveName));
                }
            }

            // Recursively handle nested items
            if (entry.Items is { Count: > 0 })
            {
                var nested = await FlattenTocAsync(entry.Items, baseDir, repo);
                pages.AddRange(nested);
            }
        }

        return pages;
    }

    /// <summary>
    /// Collects all .md files from a directory recursively, alphabetically ordered.
    /// </summary>
    private async Task<List<(string Path, string? Title)>> CollectPagesFromDirectoryAsync(
        DocsRepoInfo repo, string repoPath)
    {
        var pages = new List<(string, string?)>();
        var items = await _github.ListDirectoryAsync(repo, repoPath);

        // Process .md files first (sorted: index.md/overview.md first, then alphabetical)
        var mdFiles = items
            .Where(i => i.Type == "file" && i.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name.Equals("index.md", StringComparison.OrdinalIgnoreCase) ? 0 :
                          i.Name.Equals("overview.md", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i.Name)
            .ToList();

        foreach (var file in mdFiles)
            pages.Add((file.Path, null));

        // Then recurse into subdirectories (skip images/media dirs)
        var dirs = items
            .Where(i => i.Type == "dir" &&
                        !i.Name.Equals("images", StringComparison.OrdinalIgnoreCase) &&
                        !i.Name.Equals("media", StringComparison.OrdinalIgnoreCase) &&
                        !i.Name.Equals("includes", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name)
            .ToList();

        foreach (var dir in dirs)
        {
            var nested = await CollectPagesFromDirectoryAsync(repo, dir.Path);
            pages.AddRange(nested);
        }

        return pages;
    }

    /// <summary>
    /// Remaps image paths in markdown to media/ output directory and collects all image paths for downloading.
    /// </summary>
    private static string RemapImagePaths(string markdown, string pageDir, string contentBasePath,
        HashSet<string> allImagePaths)
    {
        // Standard markdown images: ![alt](path)
        markdown = StandardImageRegex().Replace(markdown, match =>
        {
            var alt = match.Groups[1].Value;
            var imagePath = match.Groups[2].Value;

            // Skip external URLs
            if (imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            // Resolve relative path to repo-absolute path
            var resolvedPath = ResolveRelativePath(pageDir, imagePath);
            allImagePaths.Add(resolvedPath);

            // Map to local path: media/{sanitized_filename}
            var localName = SanitizeImageFileName(resolvedPath, contentBasePath);
            return $"![{alt}](media/{localName})";
        });

        // HTML img tags: <img src="path" ...>
        markdown = HtmlImgRegex().Replace(markdown, match =>
        {
            var imagePath = match.Groups[1].Value;
            if (imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            var resolvedPath = ResolveRelativePath(pageDir, imagePath);
            allImagePaths.Add(resolvedPath);
            var localName = SanitizeImageFileName(resolvedPath, contentBasePath);

            // Replace only the src attribute value
            return match.Value.Replace(match.Groups[1].Value, $"media/{localName}");
        });

        return markdown;
    }

    /// <summary>
    /// Creates a unique, filesystem-safe filename from a repo-relative image path.
    /// Strips the content base path and replaces path separators with underscores.
    /// </summary>
    internal static string SanitizeImageFileName(string repoPath, string contentBasePath)
    {
        // Strip the base content path to get a relative name
        var relative = repoPath;
        if (relative.StartsWith(contentBasePath, StringComparison.OrdinalIgnoreCase))
            relative = relative[(contentBasePath.Length)..].TrimStart('/');

        // Replace path separators and other problematic chars
        return relative.Replace('/', '_').Replace('\\', '_');
    }

    /// <summary>
    /// Strips YAML frontmatter (--- ... ---) from the beginning of a markdown document.
    /// </summary>
    internal static string StripFrontmatter(string markdown)
    {
        var match = FrontmatterRegex().Match(markdown);
        return match.Success ? markdown[match.Length..].TrimStart('\n', '\r') : markdown;
    }

    /// <summary>
    /// Extracts the text of the first H1 heading from markdown.
    /// </summary>
    private static string? ExtractFirstHeading(string markdown)
    {
        var match = FirstH1Regex().Match(markdown);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Strips HTML block elements (<video>, <div class="...">) common in docs.
    /// </summary>
    internal static string StripHtmlBlocks(string markdown)
    {
        // Remove <video> tags entirely
        markdown = VideoTagRegex().Replace(markdown, "");

        // Remove <div> ... </div> blocks (like docs-action)
        markdown = DivBlockRegex().Replace(markdown, "");

        return markdown;
    }

    /// <summary>
    /// Derives a human-readable title from the content path.
    /// </summary>
    internal static string DeriveTitleFromPath(string contentPath)
    {
        if (string.IsNullOrEmpty(contentPath))
            return "Documentation";

        var lastSegment = contentPath.Split('/').Last(s => !string.IsNullOrEmpty(s));
        // Convert kebab-case to Title Case
        return string.Join(' ', lastSegment.Split('-')
            .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }

    /// <summary>
    /// Resolves a relative path from a base directory.
    /// </summary>
    internal static string ResolveRelativePath(string baseDir, string relativePath)
    {
        var combined = string.IsNullOrEmpty(baseDir) ? relativePath : $"{baseDir}/{relativePath}";
        var parts = combined.Split('/').ToList();

        var resolved = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".." && resolved.Count > 0)
                resolved.RemoveAt(resolved.Count - 1);
            else if (part != "." && part != "")
                resolved.Add(part);
        }

        return string.Join('/', resolved);
    }

    /// <summary>
    /// Resolves [!INCLUDE [title](path)] references by downloading the included file.
    /// Replaces each include reference with the actual content of the referenced file.
    /// </summary>
    private async Task<string> ResolveIncludesAsync(string markdown, string pageDir, DocsRepoInfo repo)
    {
        var matches = IncludeRefRegex().Matches(markdown);
        if (matches.Count == 0)
            return markdown;

        // Process matches in reverse order to preserve positions during replacement
        var result = markdown;
        foreach (var match in matches.Cast<Match>().Reverse())
        {
            var includePath = match.Groups[2].Value.Trim();

            // Skip external/absolute refs
            if (includePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                includePath.StartsWith('/'))
            {
                // Remove the include ref (can't resolve it)
                result = result.Remove(match.Index, match.Length);
                continue;
            }

            var resolvedPath = ResolveRelativePath(pageDir, includePath);
            var includeContent = await _github.TryDownloadStringAsync(repo, resolvedPath);

            if (includeContent is not null)
            {
                includeContent = StripFrontmatter(includeContent).Trim();
                result = result.Remove(match.Index, match.Length).Insert(match.Index, includeContent);
            }
            else
            {
                // Remove unresolvable include ref
                result = result.Remove(match.Index, match.Length);
            }
        }

        return result;
    }

    // Regex patterns

    [GeneratedRegex(@"\[!(?:i|I)(?:nclude|NCLUDE)\[([^\]]*)\]\(([^)]+)\)\]")]
    private static partial Regex IncludeRefRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex StandardImageRegex();

    [GeneratedRegex(@"<img\s[^>]*src\s*=\s*""([^""]+)""[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlImgRegex();

    [GeneratedRegex(@"^---\s*\n[\s\S]*?\n---\s*\n", RegexOptions.Compiled)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex FirstH1Regex();

    [GeneratedRegex(@"<video\b[^>]*>.*?</video>|<video\b[^>]*/?>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VideoTagRegex();

    [GeneratedRegex(@"<div\b[^>]*>[\s\S]*?</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DivBlockRegex();
}
