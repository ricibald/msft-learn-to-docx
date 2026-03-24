using System.Text;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Merges downloaded units into a single markdown document.
/// Heading hierarchy: Module = H1, Unit = H2, content headings = H3+.
/// </summary>
public sealed partial class MarkdownMerger
{
    /// <summary>
    /// Merges multiple <see cref="DownloadedContent"/> blocks into a single markdown string
    /// with YAML frontmatter (title, date, author, attribution metadata) for Word cover page.
    /// Heading hierarchy: Module = H1, Unit = H2, content headings = H3+.
    /// </summary>
    /// <param name="sourceUrls">Original learn.microsoft.com URLs — included in the CC BY 4.0 attribution.</param>
    public string Merge(IReadOnlyList<DownloadedContent> contents, string? documentTitle = null, DateTime? date = null, IReadOnlyList<string>? sourceUrls = null)
    {
        var sb = new StringBuilder();

        // YAML frontmatter for pandoc title block (renders as Word cover page + document properties)
        var title = documentTitle
            ?? (contents.Count == 1 ? contents[0].Title : string.Join(" / ", contents.Select(c => c.Title)));
        var dateStr = (date ?? DateTime.Now).ToString("yyyy-MM-dd");

        // keywords: module titles represent the topics covered
        var keywords = contents
            .SelectMany(c => c.Modules)
            .Select(m => m.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        // CC BY 4.0: identify creator, include copyright notice, reference license, include URI to source
        var sourceList = sourceUrls?.Count > 0
            ? string.Join(", ", sourceUrls)
            : "https://learn.microsoft.com";
        var attributionDescription = $"Content © Microsoft Corporation, licensed under CC BY 4.0 " +
            $"(https://creativecommons.org/licenses/by/4.0/). " +
            $"Source: {sourceList}. " +
            $"Adapted for offline use — original content unmodified except for format conversion.";

        // Determine subject based on content types
        var hasDocsSite = contents.Any(c => c.Type == ContentType.DocsSite);
        var hasTraining = contents.Any(c => c.Type == ContentType.LearnTraining);
        var subject = (hasTraining, hasDocsSite) switch
        {
            (true, true) => "Microsoft Learn & Documentation",
            (false, true) => "Microsoft Documentation",
            _ => "Microsoft Learn"
        };

        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        sb.AppendLine($"date: {dateStr}");
        if (keywords.Count > 0)
        {
            sb.AppendLine("keywords:");
            foreach (var kw in keywords)
                sb.AppendLine($"  - \"{EscapeYaml(kw)}\"");
        }
        sb.AppendLine($"subject: \"{subject}\"");
        sb.AppendLine($"description: \"{EscapeYaml(attributionDescription)}\"");
        sb.AppendLine("---");
        sb.AppendLine();

        // CC BY 4.0 attribution section — dedicated H1 paragraph (required by CC BY 4.0 Section 3(a))
        sb.AppendLine("# Attribution");
        sb.AppendLine();
        sb.AppendLine("Content originally published by [Microsoft](https://microsoft.com), " +
            "© Microsoft Corporation, licensed under " +
            "[Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/). " +
            "Adapted for offline use — original content unmodified except for format conversion.");
        sb.AppendLine();
        if (sourceUrls?.Count > 0)
        {
            sb.Append("**Source**:");
            if (sourceUrls.Count == 1)
            {
                sb.AppendLine($" <{sourceUrls[0]}>");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine();
                foreach (var url in sourceUrls)
                    sb.AppendLine($"- <{url}>");
            }
            sb.AppendLine();
        }

        // Download summary section
        var allModules = contents.SelectMany(c => c.Modules).ToList();
        var allUnits = allModules.SelectMany(m => m.Units)
            .Where(u => !u.Uid.EndsWith(".unavailable", StringComparison.Ordinal))
            .ToList();
        var unavailableModules = allModules
            .Where(m => m.Units.Any(u => u.Uid.EndsWith(".unavailable", StringComparison.Ordinal)))
            .ToList();
        var totalImages = allUnits
            .SelectMany(u => DfmConverter.ExtractMediaPaths(u.MarkdownContent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var totalQuizzes = allUnits.Count(u => u.IsQuiz);

        sb.AppendLine("# Download Summary");
        sb.AppendLine();
        sb.AppendLine($"- **{allModules.Count - unavailableModules.Count}/{allModules.Count}** modules downloaded");
        sb.AppendLine($"- **{allUnits.Count}** units processed");
        if (totalQuizzes > 0)
            sb.AppendLine($"- **{totalQuizzes}** knowledge checks");
        if (totalImages > 0)
            sb.AppendLine($"- **{totalImages}** images");
        sb.AppendLine($"- **{contents.Count}** source(s)");
        if (unavailableModules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"> **⚠ {unavailableModules.Count} module(s) could not be downloaded:**");
            sb.AppendLine(">");
            foreach (var m in unavailableModules)
                sb.AppendLine($"> - {m.Title} (`{m.Uid}`)");
        }
        sb.AppendLine();

        foreach (var content in contents)
        {
            if (content.Type == ContentType.DocsSite)
            {
                // Docs mode: use TOC hierarchy for headings when depth info is available
                foreach (var module in content.Modules)
                {
                    foreach (var unit in module.Units)
                    {
                        if (unit.IsSectionHeader)
                        {
                            // Section header: just a heading at appropriate depth
                            var headingLevel = Math.Min(unit.SectionDepth + 1, 6);
                            sb.AppendLine($"{new string('#', headingLevel)} {unit.Title}");
                            sb.AppendLine();
                        }
                        else if (!string.IsNullOrWhiteSpace(unit.MarkdownContent))
                        {
                            var headingLevel = Math.Min(unit.SectionDepth + 1, 6);

                            // Add page title as heading
                            sb.AppendLine($"{new string('#', headingLevel)} {unit.Title}");
                            sb.AppendLine();

                            // Remove the first H1 if it matches the unit title (avoid duplication)
                            var adjustedContent = RemoveLeadingDuplicateTitle(unit.MarkdownContent, unit.Title);

                            // Shift content headings so minimum becomes headingLevel + 1
                            adjustedContent = AdjustHeadingLevels(adjustedContent, headingLevel + 1);

                            sb.AppendLine(adjustedContent.Trim());
                            sb.AppendLine();
                            sb.AppendLine("---");
                            sb.AppendLine();
                        }
                    }
                }
            }
            else
            {
                // Learn training mode: Module = H1, Unit = H2, content = H3+
                foreach (var module in content.Modules)
                {
                    // Module title = H1
                    sb.AppendLine($"# {module.Title}");
                    sb.AppendLine();

                    foreach (var unit in module.Units)
                    {
                        // Unit title = H2
                        sb.AppendLine($"## {unit.Title}");
                        sb.AppendLine();

                        if (!string.IsNullOrWhiteSpace(unit.MarkdownContent))
                        {
                            // Shift content headings so minimum becomes H3
                            var adjustedContent = AdjustHeadingLevels(unit.MarkdownContent, 3);
                            sb.AppendLine(adjustedContent.Trim());
                            sb.AppendLine();
                        }
                    }
                }
            }
        }

        // Remove trailing horizontal rule if present (avoids empty --- at end of document)
        var result = sb.ToString();
        var trimmedEnd = result.TrimEnd();
        if (trimmedEnd.EndsWith("---", StringComparison.Ordinal))
            result = trimmedEnd[..^3].TrimEnd() + "\n";

        return result;
    }

    /// <summary>
    /// Single-content overload for backward compatibility.
    /// </summary>
    public string Merge(DownloadedContent content)
        => Merge([content]);

    private static string EscapeYaml(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Removes the first H1 heading from markdown if it matches the given title.
    /// Prevents duplication when the TOC title and content H1 are the same.
    /// </summary>
    internal static string RemoveLeadingDuplicateTitle(string markdown, string title)
    {
        var match = LeadingH1Regex().Match(markdown);
        if (!match.Success) return markdown;

        var h1Text = match.Groups[1].Value.Trim();
        if (h1Text.Equals(title, StringComparison.OrdinalIgnoreCase))
            return markdown[match.Length..].TrimStart('\r', '\n');

        return markdown;
    }

    /// <summary>
    /// Adjusts all heading levels in markdown so the minimum heading starts at baseLevel.
    /// For example, if baseLevel=4 and content has H2 and H3, they become H4 and H5.
    /// </summary>
    private static string AdjustHeadingLevels(string markdown, int baseLevel)
    {
        // Find the minimum heading level in the content
        var minLevel = 7; // Start higher than max heading
        foreach (Match m in HeadingRegex().Matches(markdown))
        {
            var level = m.Groups[1].Value.Length;
            if (level < minLevel) minLevel = level;
        }

        if (minLevel >= 7) return markdown; // No headings found

        var shift = baseLevel - minLevel;
        if (shift == 0) return markdown;

        return HeadingRegex().Replace(markdown, match =>
        {
            var currentLevel = match.Groups[1].Value.Length;
            var newLevel = Math.Min(currentLevel + shift, 6); // Cap at H6
            return $"{new string('#', newLevel)} {match.Groups[2].Value}";
        });
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    /// <summary>
    /// Matches the first H1 heading at the start of the content (with optional leading whitespace/blank lines).
    /// </summary>
    [GeneratedRegex(@"^\s*#\s+(.+?)[ \t]*\r?\n", RegexOptions.None)]
    private static partial Regex LeadingH1Regex();
}
