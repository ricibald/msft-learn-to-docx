using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Downloads all content for a learning path or module from GitHub.
/// </summary>
public sealed class ContentDownloader
{
    private readonly GitHubRawClient _github;
    private readonly ModuleResolver _resolver;
    private readonly DfmConverter _dfmConverter;
    private readonly IDeserializer _yaml;

    public ContentDownloader(GitHubRawClient github, ModuleResolver resolver, DfmConverter dfmConverter)
    {
        _github = github;
        _resolver = resolver;
        _dfmConverter = dfmConverter;
        _yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Downloads a complete learning path (all modules and units).
    /// </summary>
    public async Task<DownloadedContent> DownloadPathAsync(string pathSlug, string outputDir)
    {
        Console.WriteLine($"Downloading learning path: {pathSlug}");

        // Download path index.yml
        var pathYamlStr = await _github.DownloadStringAsync($"learn-pr/paths/{pathSlug}/index.yml");
        var pathYaml = _yaml.Deserialize<LearningPathYaml>(pathYamlStr);

        Console.WriteLine($"  Title: {pathYaml.Title}");
        Console.WriteLine($"  Modules: {pathYaml.Modules.Count}");

        var content = new DownloadedContent
        {
            Title = pathYaml.Title,
            IsPath = true
        };

        for (var i = 0; i < pathYaml.Modules.Count; i++)
        {
            var moduleUid = pathYaml.Modules[i];
            Console.WriteLine($"\n[{i + 1}/{pathYaml.Modules.Count}] Module: {moduleUid}");

            var module = await DownloadModuleByUidAsync(moduleUid, outputDir, i + 1);
            content.Modules.Add(module);
        }

        return content;
    }

    /// <summary>
    /// Downloads a single module by its URL slug.
    /// The slug is the directory name on learn.microsoft.com (e.g., "introduction-to-github-copilot").
    /// </summary>
    public async Task<DownloadedContent> DownloadModuleBySlugAsync(string moduleSlug, string outputDir)
    {
        Console.WriteLine($"Downloading module by slug: {moduleSlug}");

        // Use Catalog API to find the module UID from the slug
        // The slug IS the directory name on learn.microsoft.com, but we need the UID
        // Try to find it by fetching the Catalog API with a search
        // OR: we can search for it on GitHub since slug = directory name
        var modulePath = await FindModulePathBySlugAsync(moduleSlug);
        var moduleYamlStr = await _github.DownloadStringAsync($"{modulePath}/index.yml");
        var moduleYaml = _yaml.Deserialize<ModuleYaml>(moduleYamlStr);

        var content = new DownloadedContent
        {
            Title = moduleYaml.Title,
            IsPath = false
        };

        var module = await DownloadModuleInternalAsync(moduleYaml, modulePath, outputDir, 1);
        content.Modules.Add(module);

        return content;
    }

    private async Task<DownloadedModule> DownloadModuleByUidAsync(string moduleUid, string outputDir, int moduleIndex)
    {
        var modulePath = await _resolver.ResolveModulePathAsync(moduleUid);
        var moduleYamlStr = await _github.DownloadStringAsync($"{modulePath}/index.yml");
        var moduleYaml = _yaml.Deserialize<ModuleYaml>(moduleYamlStr);

        return await DownloadModuleInternalAsync(moduleYaml, modulePath, outputDir, moduleIndex);
    }

    private async Task<DownloadedModule> DownloadModuleInternalAsync(
        ModuleYaml moduleYaml, string modulePath, string outputDir, int moduleIndex)
    {
        Console.WriteLine($"  Module title: {moduleYaml.Title}");
        Console.WriteLine($"  Units: {moduleYaml.Units.Count}");

        var downloadedModule = new DownloadedModule
        {
            Title = moduleYaml.Title,
            Uid = moduleYaml.Uid
        };

        // List unit YAML files in the module directory
        var unitFiles = await GetUnitFilesAsync(modulePath);

        // Download each unit
        for (var i = 0; i < moduleYaml.Units.Count; i++)
        {
            var unitUid = moduleYaml.Units[i];
            var unitSlug = unitUid.Split('.').Last();

            // Find the matching unit YAML file
            var unitFileName = FindUnitFile(unitFiles, unitSlug, i + 1);
            if (unitFileName is null)
            {
                Console.WriteLine($"    [{i + 1}] SKIP: No file found for unit {unitUid}");
                continue;
            }

            Console.Write($"    [{i + 1}/{moduleYaml.Units.Count}] {unitSlug}...");

            var unitYamlStr = await _github.DownloadStringAsync($"{modulePath}/{unitFileName}");
            var unitYaml = _yaml.Deserialize<UnitYaml>(unitYamlStr);

            var downloadedUnit = await ProcessUnitAsync(unitYaml, modulePath, outputDir, moduleIndex);
            if (downloadedUnit is not null)
            {
                downloadedModule.Units.Add(downloadedUnit);
                Console.WriteLine(" OK");
            }
            else
            {
                Console.WriteLine(" (no content)");
            }
        }

        // Download media files
        await DownloadMediaAsync(modulePath, outputDir, moduleIndex, downloadedModule);

        return downloadedModule;
    }

    private async Task<DownloadedUnit?> ProcessUnitAsync(
        UnitYaml unitYaml, string modulePath, string outputDir, int moduleIndex)
    {
        var content = unitYaml.Content?.Trim() ?? "";

        // Case 1: Include reference — [!include[](includes/xxx.md)]
        var includeMatch = Regex.Match(content, @"\[!include\[\]\(([^)]+)\)\]", RegexOptions.IgnoreCase);
        if (includeMatch.Success)
        {
            var includePath = includeMatch.Groups[1].Value;
            var fullPath = $"{modulePath}/{includePath}";
            var markdown = await _github.DownloadStringAsync(fullPath);

            // Download :::code source files referenced in the markdown
            var codeContents = await DownloadCodeSourcesAsync(markdown, modulePath, includePath);

            // Convert DFM to standard markdown
            var converted = _dfmConverter.Convert(markdown,
                mediaPath => MapMediaPath(mediaPath, moduleIndex),
                codeContents);

            return new DownloadedUnit
            {
                Title = unitYaml.Title,
                Uid = unitYaml.Uid,
                MarkdownContent = converted
            };
        }

        // Case 2: Quiz content (parsed as root-level YAML key or embedded in content)
        if (unitYaml.Quiz is not null)
        {
            var quizMarkdown = ConvertQuizDataToMarkdown(unitYaml.Quiz);
            return new DownloadedUnit
            {
                Title = unitYaml.Title,
                Uid = unitYaml.Uid,
                MarkdownContent = quizMarkdown,
                IsQuiz = true
            };
        }

        if (content.TrimStart().StartsWith("quiz:", StringComparison.OrdinalIgnoreCase))
        {
            var quizMarkdown = ConvertQuizToMarkdown(content);
            return new DownloadedUnit
            {
                Title = unitYaml.Title,
                Uid = unitYaml.Uid,
                MarkdownContent = quizMarkdown,
                IsQuiz = true
            };
        }

        // Case 3: No meaningful content (sandbox units, etc.)
        if (string.IsNullOrWhiteSpace(content))
            return null;

        // Case 4: Other content (just include as-is after DFM conversion)
        var dfmConverted = _dfmConverter.Convert(content, mediaPath =>
            MapMediaPath(mediaPath, moduleIndex));

        return new DownloadedUnit
        {
            Title = unitYaml.Title,
            Uid = unitYaml.Uid,
            MarkdownContent = dfmConverted
        };
    }

    private async Task DownloadMediaAsync(
        string modulePath, string outputDir, int moduleIndex, DownloadedModule module)
    {
        // Collect all media paths from all units
        var mediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in module.Units)
        {
            var paths = DfmConverter.ExtractMediaPaths(unit.MarkdownContent);
            foreach (var p in paths)
                mediaPaths.Add(p);
        }

        if (mediaPaths.Count == 0) return;

        Console.WriteLine($"    Downloading {mediaPaths.Count} media files...");
        var mediaDir = Path.Combine(outputDir, "media");
        Directory.CreateDirectory(mediaDir);

        foreach (var mediaRelPath in mediaPaths)
        {
            // Media paths are already mapped to media/M{moduleIndex}_filename format
            var localFileName = Path.GetFileName(mediaRelPath);
            var localPath = Path.Combine(outputDir, mediaRelPath.Replace('/', Path.DirectorySeparatorChar));

            // Resolve the GitHub path: the media path in converted markdown is "media/M{idx}_file"
            // The original path in the markdown was "../media/file" (relative to includes/)
            // which corresponds to "{modulePath}/media/file" on GitHub
            var originalFileName = localFileName;
            var prefix = $"M{moduleIndex}_";
            if (originalFileName.StartsWith(prefix))
                originalFileName = originalFileName[prefix.Length..];

            var githubMediaPath = $"{modulePath}/media/{originalFileName}";
            await _github.DownloadFileAsync(githubMediaPath, localPath);
        }
    }

    private async Task<List<string>> GetUnitFilesAsync(string modulePath)
    {
        var items = await _github.ListDirectoryAsync(modulePath);
        return items
            .Where(i => i.Type == "file"
                && i.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                && !i.Name.Equals("index.yml", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Name)
            .OrderBy(n => n)
            .ToList();
    }

    private static string? FindUnitFile(List<string> unitFiles, string unitSlug, int expectedIndex)
    {
        // Try exact match: N-slug.yml
        var exactMatch = unitFiles.FirstOrDefault(f =>
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(f);
            var dashIndex = nameWithoutExt.IndexOf('-');
            if (dashIndex < 0) return false;
            var slug = nameWithoutExt[(dashIndex + 1)..];
            return slug.Equals(unitSlug, StringComparison.OrdinalIgnoreCase);
        });

        if (exactMatch is not null) return exactMatch;

        // Try by index: N-xxx.yml where N matches expectedIndex
        var byIndex = unitFiles.FirstOrDefault(f =>
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(f);
            var dashIndex = nameWithoutExt.IndexOf('-');
            if (dashIndex < 0) return false;
            return int.TryParse(nameWithoutExt[..dashIndex], out var num) && num == expectedIndex;
        });

        return byIndex;
    }

    private static string MapMediaPath(string originalPath, int moduleIndex)
    {
        // Original: "../media/image.png" (relative to includes/)
        // Mapped: "media/M{moduleIndex}_image.png" (relative to output root)
        var fileName = Path.GetFileName(originalPath);
        return $"media/M{moduleIndex}_{fileName}";
    }

    /// <summary>
    /// Downloads all :::code source="..."::: referenced files from GitHub.
    /// Returns a dictionary: source path → file content.
    /// </summary>
    private async Task<Dictionary<string, string>?> DownloadCodeSourcesAsync(
        string markdown, string modulePath, string includeRelPath)
    {
        var codeRefs = DfmConverter.ExtractCodeSourcePaths(markdown);
        if (codeRefs.Count == 0) return null;

        // Resolve the base directory for source paths (relative to the markdown file in includes/)
        var includeDir = Path.GetDirectoryName(includeRelPath)?.Replace('\\', '/') ?? "";
        var codeContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (source, _, _) in codeRefs)
        {
            if (codeContents.ContainsKey(source)) continue;

            // Resolve relative path from the includes/ directory
            var resolvedPath = ResolveRelativePath($"{modulePath}/{includeDir}", source);
            var content = await _github.TryDownloadStringAsync(resolvedPath);
            if (content is not null)
            {
                codeContents[source] = content;
                Console.Write(" [code]");
            }
            else
            {
                Console.Write($" [code:miss:{source}]");
            }
        }

        return codeContents.Count > 0 ? codeContents : null;
    }

    /// <summary>
    /// Resolves a relative path like "../snippets/file.cs" from a base directory path.
    /// </summary>
    private static string ResolveRelativePath(string baseDir, string relativePath)
    {
        var combined = baseDir + "/" + relativePath;
        var parts = combined.Split('/').ToList();

        // Resolve .. segments
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

    private string ConvertQuizToMarkdown(string quizContent)
    {
        try
        {
            var quiz = _yaml.Deserialize<QuizWrapper>(quizContent);
            if (quiz?.Quiz is null) return "";
            return ConvertQuizDataToMarkdown(quiz.Quiz);
        }
        catch
        {
            return "*Quiz content could not be parsed.*\n";
        }
    }

    private static string ConvertQuizDataToMarkdown(QuizData quiz)
    {
        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < quiz.Questions.Count; i++)
        {
            var q = quiz.Questions[i];
            sb.AppendLine($"**Question {i + 1}:** {q.Content}");
            sb.AppendLine();

            foreach (var choice in q.Choices)
            {
                var marker = choice.IsCorrect ? "- [x]" : "- [ ]";
                sb.AppendLine($"{marker} {choice.Content}");
            }

            var correctChoice = q.Choices.FirstOrDefault(c => c.IsCorrect);
            if (correctChoice is not null && !string.IsNullOrWhiteSpace(correctChoice.Explanation))
            {
                sb.AppendLine();
                sb.AppendLine($"> **Explanation:** {correctChoice.Explanation}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> FindModulePathBySlugAsync(string slug)
    {
        // The slug is the directory name on learn.microsoft.com (same as GitHub dir name)
        // Search parent dirs to find it
        var hintParents = new[] { "github", "azure", "wwl-azure", "dotnet" };

        foreach (var parent in hintParents)
        {
            var candidatePath = $"learn-pr/{parent}/{slug}";
            var content = await _github.TryDownloadStringAsync($"{candidatePath}/index.yml");
            if (content is not null) return candidatePath;
        }

        // Scan all parent dirs
        var allParents = await _github.GetLearnPrParentDirsAsync();
        foreach (var parent in allParents.Except(hintParents, StringComparer.OrdinalIgnoreCase))
        {
            var candidatePath = $"learn-pr/{parent}/{slug}";
            var content = await _github.TryDownloadStringAsync($"{candidatePath}/index.yml");
            if (content is not null) return candidatePath;
        }

        throw new InvalidOperationException($"Could not find module directory '{slug}' in any learn-pr/ subdirectory");
    }
}
