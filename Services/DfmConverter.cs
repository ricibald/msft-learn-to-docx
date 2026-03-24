using System.Text.RegularExpressions;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Converts Docs-Flavored Markdown (DFM) to standard Markdown.
/// Handles :::image:::, [!NOTE], [!div], :::zone:::, :::code:::, [!VIDEO], etc.
/// </summary>
public sealed partial class DfmConverter
{
    /// <summary>
    /// Converts DFM content to standard markdown.
    /// mediaPathMapper: maps relative media paths (e.g., "../media/img.png") to local paths.
    /// codeContents: maps source paths to their downloaded content (for :::code:::).
    /// </summary>
    public string Convert(string dfm,
        Func<string, string>? mediaPathMapper = null,
        Dictionary<string, string>? codeContents = null)
    {
        var result = dfm;

        result = ConvertImages(result, mediaPathMapper);
        result = ConvertAlerts(result);
        result = RemoveDivBlocks(result);
        result = RemoveZoneMarkers(result);
        result = RemoveTripleColonBlocks(result);
        result = ConvertVideos(result);
        result = ConvertCodeReferences(result, codeContents);
        result = CleanupIncludeRefs(result);
        result = EnsureBlankLineBeforeLists(result);
        result = EnsureBlankLinesAroundHorizontalRules(result);
        result = CleanupTrailingWhitespace(result);

        return result;
    }

    /// <summary>
    /// Extracts all :::code source="..."::: paths from the raw DFM markdown.
    /// Returns list of (source path, language, range).
    /// </summary>
    public static List<(string Source, string Language, string? Range)> ExtractCodeSourcePaths(string markdown)
    {
        var refs = new List<(string, string, string?)>();
        foreach (Match m in CodeRefRegex().Matches(markdown))
        {
            var attrs = m.Groups[1].Value;
            var source = ExtractAttr(attrs, "source");
            if (source is null) continue;
            var lang = ExtractAttr(attrs, "language") ?? "";
            var range = ExtractAttr(attrs, "range");
            refs.Add((source, lang, range));
        }
        return refs;
    }

    /// <summary>
    /// Extracts all media paths referenced in the markdown (:::image source="...":::, ![](path)).
    /// </summary>
    public static List<string> ExtractMediaPaths(string markdown)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // :::image source="...":::
        foreach (Match m in ImageSourceRegex().Matches(markdown))
            paths.Add(m.Groups[1].Value);

        // Standard markdown images ![](path) - only local paths
        foreach (Match m in StandardImageRegex().Matches(markdown))
        {
            var path = m.Groups[1].Value;
            if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                paths.Add(path);
        }

        return [.. paths];
    }

    private static string ConvertImages(string markdown, Func<string, string>? pathMapper)
    {
        return ImageFullRegex().Replace(markdown, match =>
        {
            var attrs = match.Groups[1].Value;
            var source = ExtractAttr(attrs, "source");
            var altText = ExtractAttr(attrs, "alt-text") ?? ExtractAttr(attrs, "alt") ?? "";

            if (source is null) return "";

            var mappedPath = pathMapper?.Invoke(source) ?? source;
            return $"![{altText}]({mappedPath})";
        });
    }

    private static string ConvertAlerts(string markdown)
    {
        // > [!NOTE], > [!TIP], > [!WARNING], > [!IMPORTANT], > [!CAUTION]
        // Convert to pandoc-compatible blockquote with bold label
        return AlertRegex().Replace(markdown, match =>
        {
            var prefix = match.Groups[1].Value;
            var alertType = match.Groups[2].Value;
            var label = alertType[0] + alertType[1..].ToLowerInvariant();
            return $"{prefix}**{label}:**";
        });
    }

    private static string RemoveDivBlocks(string markdown)
    {
        // > [!div class="nextstepaction"] and > [!div class="checklist"] — just remove the div line
        return DivBlockRegex().Replace(markdown, "");
    }

    private static string RemoveZoneMarkers(string markdown)
    {
        var result = ZoneStartRegex().Replace(markdown, "");
        result = ZoneEndRegex().Replace(result, "");
        return result;
    }

    private static string RemoveTripleColonBlocks(string markdown)
    {
        return TripleColonBlockRegex().Replace(markdown, "");
    }

    private static string ConvertVideos(string markdown)
    {
        return VideoRegex().Replace(markdown, match =>
        {
            var url = match.Groups[1].Value;
            return $"[Video]({url})";
        });
    }

    private static string ConvertCodeReferences(string markdown, Dictionary<string, string>? codeContents)
    {
        return CodeRefRegex().Replace(markdown, match =>
        {
            var attrs = match.Groups[1].Value;
            var lang = ExtractAttr(attrs, "language") ?? "";
            var source = ExtractAttr(attrs, "source");
            var range = ExtractAttr(attrs, "range");

            if (source is not null && codeContents is not null && codeContents.TryGetValue(source, out var code))
            {
                var lines = code.Split('\n');
                if (range is not null)
                    lines = ApplyRange(lines, range);
                var filtered = string.Join('\n', lines).TrimEnd();
                return $"```{lang}\n{filtered}\n```";
            }

            return source is not null
                ? $"```{lang}\n// Source: {source}\n```"
                : "";
        });
    }

    /// <summary>
    /// Applies a range filter like "5-10" or "1-3,8-12" to an array of lines (1-indexed).
    /// </summary>
    private static string[] ApplyRange(string[] lines, string range)
    {
        var result = new List<string>();
        foreach (var part in range.Split(','))
        {
            var trimmed = part.Trim();
            var dashIdx = trimmed.IndexOf('-');
            if (dashIdx >= 0
                && int.TryParse(trimmed[..dashIdx], out var start)
                && int.TryParse(trimmed[(dashIdx + 1)..], out var end))
            {
                start = Math.Max(1, start);
                end = Math.Min(lines.Length, end);
                for (var i = start; i <= end; i++)
                    result.Add(lines[i - 1]);
            }
        }
        return result.Count > 0 ? [.. result] : lines;
    }

    private static string CleanupIncludeRefs(string markdown)
    {
        // Remove any remaining [!INCLUDE] / [!include] refs that weren't resolved
        return IncludeRegex().Replace(markdown, "");
    }

    /// <summary>
    /// Ensures a blank line precedes any bullet/numbered list item that directly follows
    /// a non-empty, non-list, non-blank line. Without this, pandoc may not recognize the
    /// list and renders it as inline paragraph text.
    /// </summary>
    private static string EnsureBlankLineBeforeLists(string markdown)
    {
        return ListWithoutPrecedingBlankLine().Replace(markdown, "$1\n\n$2");
    }

    /// <summary>
    /// Ensures blank lines exist before and after horizontal rules (---, ***, ___)
    /// to prevent pandoc from misinterpreting them as setext headings or YAML delimiters.
    /// </summary>
    private static string EnsureBlankLinesAroundHorizontalRules(string markdown)
    {
        // Ensure blank line after horizontal rule when followed by non-blank line
        var result = HrMissingBlankAfter().Replace(markdown, "$1\n\n");
        // Ensure blank line before horizontal rule when preceded by non-blank line
        result = HrMissingBlankBefore().Replace(result, "$1\n\n$2");
        return result;
    }

    private static string CleanupTrailingWhitespace(string markdown)
    {
        // Remove excessive blank lines (more than 2 consecutive)
        return ExcessiveBlankLines().Replace(markdown, "\n\n");
    }

    private static string? ExtractAttr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"{Regex.Escape(name)}=""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // --- Compiled Regex patterns ---

    [GeneratedRegex(@":::image\s+(.*?):::", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ImageFullRegex();

    [GeneratedRegex(@"source=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\(([^\s\)""]+)(?:\s+""[^""]*"")?\)", RegexOptions.IgnoreCase)]
    private static partial Regex StandardImageRegex();

    [GeneratedRegex(@"^(>\s*)\[!(NOTE|TIP|WARNING|IMPORTANT|CAUTION)\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AlertRegex();

    [GeneratedRegex(@">\s*\[!div\s+class=""[^""]*""\]\s*\r?\n?", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DivBlockRegex();

    [GeneratedRegex(@":::\s*zone\s+[^:]*?\s*:::\s*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneStartRegex();

    [GeneratedRegex(@":::\s*zone-end\s*:::\s*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneEndRegex();

    [GeneratedRegex(@":::(row|column|row-end|column-end)(?:\s+[^:]*)?:::\s*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex TripleColonBlockRegex();

    [GeneratedRegex(@"\[!VIDEO\s+(https?://[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex VideoRegex();

    [GeneratedRegex(@":::code\s+(.*?):::", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeRefRegex();

    [GeneratedRegex(@"\[!(?:i|I)(?:nclude|NCLUDE)\s*\[.*?\]\(.*?\)\]")]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveBlankLines();

    /// <summary>
    /// Matches a non-blank, non-list line (group 1) immediately followed (no blank line) by a
    /// bullet or numbered list line (group 2). Used to inject the missing blank separator.
    /// </summary>
    [GeneratedRegex(@"(^(?!\s*[-*+]|\s*\d+\.)(?!\s*$).+)\n([ \t]*(?:[-*+]|\d+\.)[ \t])", RegexOptions.Multiline)]
    private static partial Regex ListWithoutPrecedingBlankLine();

    /// <summary>
    /// Matches a horizontal rule (---) immediately followed by a non-blank line (no intervening blank line).
    /// </summary>
    [GeneratedRegex(@"^(---[ \t]*)\r?\n(?!\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex HrMissingBlankAfter();

    /// <summary>
    /// Matches a non-blank line immediately followed by a horizontal rule (---) without an intervening blank line.
    /// </summary>
    [GeneratedRegex(@"(\S[^\n]*)\r?\n(---[ \t]*)$", RegexOptions.Multiline)]
    private static partial Regex HrMissingBlankBefore();
}
