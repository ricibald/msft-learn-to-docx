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

        // subtitle: source attribution reference (short, renders well as Word subtitle)
        var subtitle = sourceUrls?.Count > 0
            ? $"Source: {string.Join(" | ", sourceUrls)}"
            : "Source: https://learn.microsoft.com";

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

        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        sb.AppendLine($"subtitle: \"{EscapeYaml(subtitle)}\"");
        sb.AppendLine("author:");
        sb.AppendLine("  - \"Microsoft Corporation\"");
        sb.AppendLine($"date: {dateStr}");
        if (keywords.Count > 0)
        {
            sb.AppendLine("keywords:");
            foreach (var kw in keywords)
                sb.AppendLine($"  - \"{EscapeYaml(kw)}\"");
        }
        sb.AppendLine("subject: \"Microsoft Learn\"");
        sb.AppendLine($"description: \"{EscapeYaml(attributionDescription)}\"");
        sb.AppendLine("---");
        sb.AppendLine();

        // Visible attribution notice in document body (required by CC BY 4.0 Section 3(a))
        sb.AppendLine("> **Attribution**: Content originally published on [Microsoft Learn](https://learn.microsoft.com), " +
            "© Microsoft Corporation, licensed under " +
            "[Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/). " +
            "Adapted for offline use — original content unmodified except for format conversion.");
        if (sourceUrls?.Count > 0)
        {
            if (sourceUrls.Count == 1)
            {
                sb.AppendLine(">");
                sb.AppendLine($"> **Source**: <{sourceUrls[0]}>");
            }
            else
            {
                sb.AppendLine(">");
                sb.AppendLine("> **Source**:");
                sb.AppendLine(">");
                foreach (var url in sourceUrls)
                    sb.AppendLine($"> - <{url}>");
            }
        }
        sb.AppendLine();

        foreach (var content in contents)
        {
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

        return sb.ToString();
    }

    /// <summary>
    /// Single-content overload for backward compatibility.
    /// </summary>
    public string Merge(DownloadedContent content)
        => Merge([content]);

    private static string EscapeYaml(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
}
