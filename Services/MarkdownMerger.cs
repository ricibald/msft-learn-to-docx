using System.Text;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Merges downloaded units into a single markdown document with proper heading hierarchy.
/// </summary>
public sealed partial class MarkdownMerger
{
    /// <summary>
    /// Merges all downloaded content into a single markdown string.
    /// Heading hierarchy:
    /// - Path mode: H1=Path title, H2=Module title, H3=Unit title, content shifted to H4+
    /// - Module mode: H1=Module title, H2=Unit title, content shifted to H3+
    /// </summary>
    public string Merge(DownloadedContent content)
    {
        var sb = new StringBuilder();
        var unitHeadingBaseLevel = content.IsPath ? 4 : 3;
        var unitTitleLevel = content.IsPath ? 3 : 2;
        var moduleTitleLevel = 2;

        // Document title (H1)
        sb.AppendLine($"# {content.Title}");
        sb.AppendLine();

        foreach (var module in content.Modules)
        {
            // Module title (H2 for paths, skip for single module since H1 is the title)
            if (content.IsPath)
            {
                sb.AppendLine($"{new string('#', moduleTitleLevel)} {module.Title}");
                sb.AppendLine();
            }

            foreach (var unit in module.Units)
            {
                // Unit title
                sb.AppendLine($"{new string('#', unitTitleLevel)} {unit.Title}");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(unit.MarkdownContent))
                {
                    // Shift headings in content to proper level
                    var adjustedContent = AdjustHeadingLevels(unit.MarkdownContent, unitHeadingBaseLevel);
                    sb.AppendLine(adjustedContent.Trim());
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
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
}
