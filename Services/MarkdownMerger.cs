using System.Text;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Merges downloaded units into a single markdown document.
/// Each unit starts at heading level 1; content headings are shifted to H2+.
/// </summary>
public sealed partial class MarkdownMerger
{
    /// <summary>
    /// Merges multiple <see cref="DownloadedContent"/> blocks into a single markdown string
    /// with an optional YAML frontmatter cover page.
    /// Every unit is a top-level section (H1). Content headings start at H2.
    /// </summary>
    public string Merge(IReadOnlyList<DownloadedContent> contents, string? documentTitle = null, DateTime? date = null)
    {
        var sb = new StringBuilder();

        // YAML frontmatter for pandoc title block (renders as Word cover page)
        var title = documentTitle
            ?? (contents.Count == 1 ? contents[0].Title : string.Join(" / ", contents.Select(c => c.Title)));
        var dateStr = (date ?? DateTime.Now).ToString("yyyy-MM-dd");

        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        sb.AppendLine($"date: {dateStr}");
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var content in contents)
        {
            foreach (var module in content.Modules)
            {
                foreach (var unit in module.Units)
                {
                    // Each unit is H1
                    sb.AppendLine($"# {unit.Title}");
                    sb.AppendLine();

                    if (!string.IsNullOrWhiteSpace(unit.MarkdownContent))
                    {
                        // Shift content headings so minimum becomes H2
                        var adjustedContent = AdjustHeadingLevels(unit.MarkdownContent, 2);
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
