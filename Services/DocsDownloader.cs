using System.Text.Json;
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

        // Collect ordered page list (using toc.yml if available, with hierarchy info)
        var tocItems = await CollectPagesAsync(repo, repoPath);
        var pageCount = tocItems.Count(t => !t.IsSectionHeader);
        Console.WriteLine($"  Found {pageCount} pages to download ({tocItems.Count(t => t.IsSectionHeader)} sections)");

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

        var pageIndex = 0;
        for (var i = 0; i < tocItems.Count; i++)
        {
            var item = tocItems[i];

            // Section headers: add as title-only units (no content download)
            if (item.IsSectionHeader)
            {
                module.Units.Add(new DownloadedUnit
                {
                    Title = item.Title ?? "Section",
                    Uid = $"section.{i}",
                    SectionDepth = item.Depth,
                    IsSectionHeader = true
                });
                continue;
            }

            pageIndex++;
            Console.Write($"  [{pageIndex}/{pageCount}] {item.Path}...");

            var markdown = await _github.TryDownloadStringAsync(repo, item.Path!);
            if (markdown is null)
            {
                Console.WriteLine(" (not found)");
                continue;
            }

            // Strip YAML frontmatter from individual pages
            var rawMarkdown = markdown;
            markdown = StripFrontmatter(markdown);

            // Skip redirect stubs (pages that only redirect to another URL)
            if (IsRedirectPage(rawMarkdown, markdown))
            {
                Console.WriteLine(" (redirect, skipped)");
                continue;
            }

            // Extract title from first H1 if not provided by toc.yml
            var title = item.Title ?? ExtractFirstHeading(markdown) ?? Path.GetFileNameWithoutExtension(item.Path);

            var pageDir = Path.GetDirectoryName(item.Path)?.Replace('\\', '/') ?? "";

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
                Uid = item.Path!,
                MarkdownContent = markdown,
                SectionDepth = item.Depth,
                IsSectionHeader = false
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

                var failedPaths = await _github.DownloadLfsFilesAsync(repo, filesToDownload);

                // Try live site fallback for failed images
                if (failedPaths.Count > 0)
                {
                    var liveSiteFallbacks = 0;
                    foreach (var failedPath in failedPaths)
                    {
                        var localFileName = SanitizeImageFileName(failedPath, repo.RepoContentPath);
                        var localPath = Path.Combine(mediaDir, localFileName);
                        if (await TryDownloadFromLiveSiteAsync(repo, failedPath, localPath))
                            liveSiteFallbacks++;
                        else
                            Console.WriteLine($"  [WARN] Media not found: {failedPath}");
                    }
                    if (liveSiteFallbacks > 0)
                        Console.WriteLine($"  {liveSiteFallbacks} image(s) downloaded from live site (not in GitHub/LFS)");
                }
            }
            else
            {
                var count = 0;
                var liveSiteFallbacks = 0;
                foreach (var imagePath in allImagePaths)
                {
                    count++;
                    Console.Write($"\r  [{count}/{allImagePaths.Count}] Downloading images...");
                    var localFileName = SanitizeImageFileName(imagePath, repo.RepoContentPath);
                    var localPath = Path.Combine(mediaDir, localFileName);

                    // Try GitHub first, then fall back to live site
                    var downloaded = await _github.TryDownloadFileAsync(repo, imagePath, localPath);
                    if (!downloaded)
                    {
                        downloaded = await TryDownloadFromLiveSiteAsync(repo, imagePath, localPath);
                        if (downloaded)
                            liveSiteFallbacks++;
                        else
                            Console.WriteLine($"\n  [WARN] Media not found: {imagePath}");
                    }
                }
                Console.WriteLine();
                if (liveSiteFallbacks > 0)
                    Console.WriteLine($"  {liveSiteFallbacks} image(s) downloaded from live site (not in GitHub repo)");
            }
        }

        // Summary statistics
        var downloadedPages = module.Units.Count(u => !u.IsSectionHeader);
        var downloadedImages = allImagePaths.Count > 0
            ? Directory.Exists(Path.Combine(outputDir, "media"))
                ? Directory.GetFiles(Path.Combine(outputDir, "media")).Length
                : 0
            : 0;
        Console.WriteLine($"  Summary: {downloadedPages} pages, {downloadedImages}/{allImagePaths.Count} images downloaded");

        return content;
    }

    /// <summary>
    /// Tries to download an image from the live site (e.g., learn.microsoft.com) as a fallback
    /// when the image is not found in the GitHub repo.
    /// </summary>
    private async Task<bool> TryDownloadFromLiveSiteAsync(DocsRepoInfo repo, string repoPath, string localPath)
    {
        var liveUrl = repo.GetLiveSiteUrl(repoPath);
        if (liveUrl is null) return false;
        return await _github.TryDownloadFromUrlAsync(liveUrl, localPath);
    }

    /// <summary>
    /// Represents a flattened TOC item with depth and section-header information.
    /// </summary>
    internal sealed record TocFlatItem(string? Path, string? Title, int Depth, bool IsSectionHeader);

    /// <summary>
    /// Deserializes toc.yml content which can be either a direct sequence (list of TocEntry)
    /// or a mapping with an "items" key (e.g., Azure docs: {items: [...]}).
    /// </summary>
    private List<TocEntry>? DeserializeTocEntries(string tocYaml)
    {
        // Try as a direct list first (most common format)
        try
        {
            return _yaml.Deserialize<List<TocEntry>>(tocYaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Fall back to root mapping with "items" key
            var root = _yaml.Deserialize<TocEntry>(tocYaml);
            return root?.Items;
        }
    }

    /// <summary>
    /// Collects pages in order: uses toc.yml if present (with hierarchy),
    /// then tries toc.json from docs root (vscode-docs format),
    /// otherwise falls back to alphabetical listing.
    /// </summary>
    private async Task<List<TocFlatItem>> CollectPagesAsync(DocsRepoInfo repo, string repoPath)
    {
        // Try to download toc.yml (case-insensitive: some repos use TOC.yml)
        string? tocYaml = null;
        foreach (var tocName in new[] { "toc.yml", "TOC.yml" })
        {
            tocYaml = await _github.TryDownloadStringAsync(repo, $"{repoPath}/{tocName}");
            if (tocYaml is not null) break;
        }

        if (tocYaml is not null)
        {
            Console.WriteLine("  Using toc.yml for page ordering");
            var tocEntries = DeserializeTocEntries(tocYaml);
            if (tocEntries is not null)
                return await FlattenTocWithDepthAsync(tocEntries, repoPath, repo, 0);
        }

        // Try toc.json from the docs root (used by vscode-docs)
        var tocJsonPath = $"{repo.DocsBasePath}/toc.json";
        var tocJson = await _github.TryDownloadStringAsync(repo, tocJsonPath);
        if (tocJson is not null)
        {
            // Find the section matching the content path area (e.g., "copilot" from "docs/copilot")
            var area = repo.ContentPath;
            var pages = ParseTocJsonSection(tocJson, area, repo.DocsBasePath);
            if (pages.Count > 0)
            {
                Console.WriteLine("  Using toc.json for page ordering");
                return pages;
            }
        }

        // Try directory listing first (flat, depth 0)
        var dirPages = await CollectPagesFromDirectoryAsync(repo, repoPath);
        if (dirPages.Count > 0)
            return dirPages;

        // If directory listing returned nothing, the path may be a single file — try appending .md
        if (!repoPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var singleFilePath = repoPath + ".md";
            var fileContent = await _github.TryDownloadStringAsync(repo, singleFilePath);
            if (fileContent is not null)
            {
                Console.WriteLine("  Path resolves to a single page");
                return [new TocFlatItem(singleFilePath, null, 0, false)];
            }
        }

        return [];
    }

    /// <summary>
    /// Flattens a toc.yml structure into an ordered list with depth and section header info.
    /// Recursively resolves sub-TOC references and preserves the hierarchical structure.
    /// </summary>
    private async Task<List<TocFlatItem>> FlattenTocWithDepthAsync(
        List<TocEntry> entries, string baseDir, DocsRepoInfo repo, int depth)
    {
        var result = new List<TocFlatItem>();

        foreach (var entry in entries)
        {
            var hasHref = entry.Href is not null;
            var hasItems = entry.Items is { Count: > 0 };

            if (hasHref)
            {
                var href = entry.Href!.Trim();

                // Sub-TOC references replace both href and children processing entirely
                if (href.EndsWith("toc.yml", StringComparison.OrdinalIgnoreCase))
                {
                    var subTocPath = ResolveRelativePath(baseDir, href);

                    // Try the original path first, then alternate case
                    var subTocContent = await _github.TryDownloadStringAsync(repo, subTocPath);
                    if (subTocContent is null)
                    {
                        var altCase = subTocPath.EndsWith("toc.yml", StringComparison.Ordinal)
                            ? subTocPath[..^7] + "TOC.yml"
                            : subTocPath[..^7] + "toc.yml";
                        subTocContent = await _github.TryDownloadStringAsync(repo, altCase);
                    }

                    if (subTocContent is not null)
                    {
                        var subTocDir = Path.GetDirectoryName(subTocPath)?.Replace('\\', '/') ?? baseDir;
                        var subEntries = DeserializeTocEntries(subTocContent);
                        if (subEntries is not null)
                            result.AddRange(await FlattenTocWithDepthAsync(subEntries, subTocDir, repo, depth));
                    }
                    continue;
                }

                // Skip external URLs, cross-references, and deeply nested parent refs
                // but still process children below
                if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    !href.StartsWith('/') &&
                    !href.StartsWith("../../"))
                {
                    var cleanHref = href.Split('?')[0].Split('#')[0].Trim();
                    if (!string.IsNullOrEmpty(cleanHref) &&
                        cleanHref.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        var fullPath = ResolveRelativePath(baseDir, cleanHref);
                        result.Add(new TocFlatItem(fullPath, entry.EffectiveName, depth, false));
                    }
                }
            }
            else if (hasItems)
            {
                // No href, has children → this is a section header
                result.Add(new TocFlatItem(null, entry.EffectiveName, depth, true));
            }

            // Recursively handle nested items (children go one level deeper)
            if (hasItems)
            {
                var nested = await FlattenTocWithDepthAsync(entry.Items!, baseDir, repo, depth + 1);
                result.AddRange(nested);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a toc.json file (vscode-docs format) and extracts the section matching the given area.
    /// Format: array of [title, path] or [_, _, {name, area, topics:[...]}].
    /// </summary>
    internal static List<TocFlatItem> ParseTocJsonSection(string json, string area, string docsBasePath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return [];

        // Find the top-level section matching the area
        foreach (var section in root.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object)
                continue;

            if (!section.TryGetProperty("area", out var areaProp) ||
                !area.Equals(areaProp.GetString(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (!section.TryGetProperty("topics", out var topics))
                continue;

            return FlattenTocJsonTopics(topics, docsBasePath, 0);
        }

        return [];
    }

    /// <summary>
    /// Recursively flattens toc.json topics into TocFlatItems.
    /// Each topic is either ["Title", "/docs/path"] or ["", "", {name, topics}].
    /// </summary>
    private static List<TocFlatItem> FlattenTocJsonTopics(JsonElement topics, string docsBasePath, int depth)
    {
        var result = new List<TocFlatItem>();

        if (topics.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in topics.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array)
                continue;

            var arrayLen = entry.GetArrayLength();

            // Check if this is a nested section: ["", "", {name, topics}]
            if (arrayLen >= 3)
            {
                var third = entry[2];
                if (third.ValueKind == JsonValueKind.Object &&
                    third.TryGetProperty("name", out var nameProp) &&
                    third.TryGetProperty("topics", out var subTopics))
                {
                    var sectionName = nameProp.GetString();
                    if (!string.IsNullOrEmpty(sectionName))
                        result.Add(new TocFlatItem(null, sectionName, depth, true));

                    result.AddRange(FlattenTocJsonTopics(subTopics, docsBasePath, depth + 1));
                    continue;
                }
            }

            // Leaf page: ["Title", "/docs/copilot/page"]
            if (arrayLen >= 2)
            {
                var title = entry[0].GetString();
                var path = entry[1].GetString();

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(path))
                    continue;

                // Convert URL path to repo path: /docs/copilot/page → docs/copilot/page.md
                var repoPath = path.TrimStart('/');
                if (!repoPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    repoPath += ".md";

                result.Add(new TocFlatItem(repoPath, title, depth, false));
            }
        }

        return result;
    }

    /// <summary>
    /// Collects all .md files from a directory recursively, alphabetically ordered (flat, depth 0).
    /// </summary>
    private async Task<List<TocFlatItem>> CollectPagesFromDirectoryAsync(
        DocsRepoInfo repo, string repoPath)
    {
        var pages = new List<TocFlatItem>();
        var items = await _github.ListDirectoryAsync(repo, repoPath);

        // Process .md files first (sorted: index.md/overview.md first, then alphabetical)
        var mdFiles = items
            .Where(i => i.Type == "file" && i.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name.Equals("index.md", StringComparison.OrdinalIgnoreCase) ? 0 :
                          i.Name.Equals("overview.md", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i.Name)
            .ToList();

        foreach (var file in mdFiles)
            pages.Add(new TocFlatItem(file.Path, null, 0, false));

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

        // Reference-style image links: ![alt][ref] + [ref]: path
        // Convert to inline format to avoid duplicates when merging pages
        markdown = InlineRefStyleImageLinks(markdown, pageDir, contentBasePath, allImagePaths);

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
    /// Detects redirect pages: either YAML frontmatter contains redirect_url,
    /// or the stripped content body is short and mentions "redirect".
    /// </summary>
    internal static bool IsRedirectPage(string rawMarkdown, string strippedMarkdown)
    {
        // Check frontmatter for redirect_url (used by MicrosoftDocs repos)
        var frontmatter = FrontmatterRegex().Match(rawMarkdown);
        if (frontmatter.Success &&
            frontmatter.Value.Contains("redirect_url", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check for short body that mentions "redirect" (used by vscode-docs)
        if (strippedMarkdown.Length < 500 &&
            strippedMarkdown.Contains("redirect", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
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
        // DocFX ~/... means repo root — don't prepend baseDir
        if (relativePath.StartsWith("~/", StringComparison.Ordinal))
            return relativePath[2..];

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

    /// <summary>
    /// Converts reference-style image links (![alt][ref] + [ref]: path) to inline format.
    /// This prevents duplicate reference IDs when multiple pages are merged and ensures
    /// image paths are properly remapped and collected for download.
    /// </summary>
    private static string InlineRefStyleImageLinks(string markdown, string pageDir,
        string contentBasePath, HashSet<string> allImagePaths)
    {
        // Collect reference definitions: [ref]: path
        var refDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RefLinkDefRegex().Matches(markdown))
        {
            var refId = m.Groups[1].Value;
            var path = m.Groups[2].Value.Trim();
            refDefs[refId] = path;
        }

        if (refDefs.Count == 0)
            return markdown;

        // Identify which refs are used as images: ![alt][ref]
        var imageRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RefStyleImageRegex().Matches(markdown))
            imageRefs.Add(m.Groups[2].Value);

        // Replace ![alt][ref] with inline ![alt](media/remapped) for image refs with local paths
        markdown = RefStyleImageRegex().Replace(markdown, match =>
        {
            var alt = match.Groups[1].Value;
            var refId = match.Groups[2].Value;

            if (!refDefs.TryGetValue(refId, out var imagePath))
                return match.Value;

            if (imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return $"![{alt}]({imagePath})";

            var resolvedPath = ResolveRelativePath(pageDir, imagePath);
            allImagePaths.Add(resolvedPath);
            var localName = SanitizeImageFileName(resolvedPath, contentBasePath);
            return $"![{alt}](media/{localName})";
        });

        // Remove reference definitions that were used as images
        markdown = RefLinkDefRegex().Replace(markdown, match =>
        {
            var refId = match.Groups[1].Value;
            return imageRefs.Contains(refId) ? "" : match.Value;
        });

        return markdown;
    }

    // Regex patterns

    [GeneratedRegex(@"\[!(?:i|I)(?:nclude|NCLUDE)\[([^\]]*)\]\(([^)]+)\)\]")]
    private static partial Regex IncludeRefRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^\s\)""]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled)]
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

    [GeneratedRegex(@"^\[([^\]]+)\]:\s*(\S+)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex RefLinkDefRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\[([^\]]+)\]", RegexOptions.Compiled)]
    private static partial Regex RefStyleImageRegex();
}
