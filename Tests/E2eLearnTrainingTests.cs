using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;
using Xunit.Abstractions;

namespace MsftLearnToDocx.Tests;

/// <summary>
/// E2E integration tests for Learn training URLs (Catalog API + unit YAML + DFM flow).
/// Covers both single-module and learning-path downloads.
/// </summary>
public sealed class E2eLearnTrainingTests : IDisposable
{
    private readonly string _outputDir;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly ITestOutputHelper _output;

    public E2eLearnTrainingTests(ITestOutputHelper output)
    {
        _output = output;
        _outputDir = Path.Combine(Path.GetTempPath(), $"e2e_learn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        _cacheDir = Path.Combine(localAppData, "MsftLearnToDocx", "cache");

        var handler = new CachingHandler(new RetryHandler(), _cacheDir, TimeSpan.FromHours(24));
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MsftLearnToDocx", "1.0"));
        _httpClient.Timeout = TimeSpan.FromSeconds(60);

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(githubToken))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Single module E2E: introduction-to-github-copilot (small module, ~5 units).
    /// </summary>
    [Fact]
    public async Task DownloadLearnModule_IntroToGitHubCopilot_FullPipeline()
    {
        // --- Arrange ---
        var url = "https://learn.microsoft.com/training/modules/introduction-to-github-copilot";
        var parsed = DocsUrlParser.Parse(url);
        Assert.IsType<LearnTrainingUrl>(parsed);

        var training = (LearnTrainingUrl)parsed;
        Assert.Equal("modules", training.Type);
        Assert.Equal("introduction-to-github-copilot", training.Slug);

        var githubClient = new GitHubRawClient(_httpClient, _cacheDir);
        var catalogClient = new LearnCatalogClient(_httpClient);
        var resolver = new ModuleResolver(catalogClient, githubClient);
        var dfmConverter = new DfmConverter();
        var downloader = new ContentDownloader(githubClient, resolver, catalogClient, dfmConverter);
        var merger = new MarkdownMerger();

        // --- Act: Download ---
        var content = await downloader.DownloadModuleBySlugAsync(training.Slug, _outputDir);

        // --- Assert: Content structure ---
        Assert.Equal(ContentType.LearnTraining, content.Type);
        Assert.False(content.IsPath, "Single module should not be IsPath");
        Assert.Single(content.Modules);
        Assert.NotEmpty(content.Title);
        _output.WriteLine($"Title: {content.Title}");

        var module = content.Modules[0];
        Assert.NotEmpty(module.Title);
        Assert.NotEmpty(module.Uid);
        Assert.True(module.Units.Count >= 3, $"Expected at least 3 units, got {module.Units.Count}");
        _output.WriteLine($"Module: {module.Title} ({module.Uid})");
        _output.WriteLine($"Units: {module.Units.Count}");

        foreach (var unit in module.Units)
        {
            var hasContent = !string.IsNullOrWhiteSpace(unit.MarkdownContent);
            var tag = unit.IsQuiz ? " [quiz]" : hasContent ? "" : " [empty]";
            _output.WriteLine($"  - {unit.Title}{tag}");
        }

        // At least some units should have markdown content
        var unitsWithContent = module.Units.Count(u => !string.IsNullOrWhiteSpace(u.MarkdownContent));
        Assert.True(unitsWithContent >= 2, $"Expected at least 2 units with content, got {unitsWithContent}");

        // --- Act: Merge ---
        var mergedMarkdown = merger.Merge([content], documentTitle: null, sourceUrls: [url]);
        var mdPath = Path.Combine(_outputDir, "introduction-to-github-copilot.md");
        await File.WriteAllTextAsync(mdPath, mergedMarkdown);

        Assert.True(File.Exists(mdPath), "Markdown not created");
        var mdSize = new FileInfo(mdPath).Length;
        Assert.True(mdSize > 0, "Markdown is empty");
        _output.WriteLine($"Markdown: {mdSize:N0} bytes");

        // --- Assert: Images on disk ---
        var mediaDir = Path.Combine(_outputDir, "media");
        var imagesOnDisk = Directory.Exists(mediaDir) ? Directory.GetFiles(mediaDir).Length : 0;
        _output.WriteLine($"Images on disk: {imagesOnDisk}");

        // All images referenced in markdown should exist on disk
        var referencedFiles = Regex.Matches(mergedMarkdown, @"!\[[^\]]*\]\(media/([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var missingImages = referencedFiles
            .Where(f => !File.Exists(Path.Combine(_outputDir, "media", f)))
            .ToList();
        foreach (var img in missingImages)
            _output.WriteLine($"  Missing: {img}");
        Assert.Empty(missingImages);

        // --- Act: Pandoc ---
        var docxPath = Path.Combine(_outputDir, "introduction-to-github-copilot.docx");
        var pandocStderr = RunPandoc(mdPath, docxPath);

        Assert.True(File.Exists(docxPath), "DOCX not created");
        _output.WriteLine($"DOCX: {new FileInfo(docxPath).Length:N0} bytes");

        // Only fail on actionable warnings
        var warnings = SplitPandocWarnings(pandocStderr);
        var actionable = warnings
            .Where(w => !w.Contains("rsvg-convert", StringComparison.OrdinalIgnoreCase)
                     && !w.Contains("Could not convert TeX math", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Pandoc: {warnings.Count} total, {actionable.Count} actionable");
        foreach (var w in actionable)
            _output.WriteLine($"  {w}");

        Assert.Empty(actionable);
    }

    /// <summary>
    /// Learning path E2E: GitHub Copilot fundamentals (small path, 2-3 modules).
    /// </summary>
    [Fact]
    public async Task DownloadLearnPath_GitHubCopilotFundamentals_FullPipeline()
    {
        // --- Arrange ---
        var url = "https://learn.microsoft.com/training/paths/copilot";
        var parsed = DocsUrlParser.Parse(url);
        Assert.IsType<LearnTrainingUrl>(parsed);

        var training = (LearnTrainingUrl)parsed;
        Assert.Equal("paths", training.Type);
        Assert.Equal("copilot", training.Slug);

        var githubClient = new GitHubRawClient(_httpClient, _cacheDir);
        var catalogClient = new LearnCatalogClient(_httpClient);
        var resolver = new ModuleResolver(catalogClient, githubClient);
        var dfmConverter = new DfmConverter();
        var downloader = new ContentDownloader(githubClient, resolver, catalogClient, dfmConverter);
        var merger = new MarkdownMerger();

        // --- Act: Download ---
        var content = await downloader.DownloadPathAsync(training.Slug, _outputDir);

        // --- Assert: Content structure ---
        Assert.Equal(ContentType.LearnTraining, content.Type);
        Assert.True(content.IsPath, "Path download should have IsPath=true");
        Assert.True(content.Modules.Count >= 2, $"Expected at least 2 modules, got {content.Modules.Count}");
        Assert.NotEmpty(content.Title);
        _output.WriteLine($"Title: {content.Title}");
        _output.WriteLine($"Modules: {content.Modules.Count}");

        var totalUnits = 0;
        foreach (var module in content.Modules)
        {
            var unavailable = module.Units.Any(u => u.Uid.EndsWith(".unavailable", StringComparison.Ordinal));
            var tag = unavailable ? " [unavailable]" : "";
            _output.WriteLine($"  [{module.Units.Count} units] {module.Title}{tag}");
            totalUnits += module.Units.Count;
        }
        _output.WriteLine($"Total units: {totalUnits}");

        // --- Act: Merge ---
        var mergedMarkdown = merger.Merge([content], documentTitle: null, sourceUrls: [url]);
        var mdPath = Path.Combine(_outputDir, "copilot.md");
        await File.WriteAllTextAsync(mdPath, mergedMarkdown);

        Assert.True(File.Exists(mdPath), "Markdown not created");
        _output.WriteLine($"Markdown: {new FileInfo(mdPath).Length:N0} bytes");

        // --- Assert: Images on disk ---
        var mediaDir = Path.Combine(_outputDir, "media");
        var imagesOnDisk = Directory.Exists(mediaDir) ? Directory.GetFiles(mediaDir).Length : 0;
        _output.WriteLine($"Images on disk: {imagesOnDisk}");

        var referencedFiles = Regex.Matches(mergedMarkdown, @"!\[[^\]]*\]\(media/([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var missingImages = referencedFiles
            .Where(f => !File.Exists(Path.Combine(_outputDir, "media", f)))
            .ToList();
        foreach (var img in missingImages)
            _output.WriteLine($"  Missing: {img}");
        Assert.Empty(missingImages);

        // --- Act: Pandoc ---
        var docxPath = Path.Combine(_outputDir, "copilot.docx");
        var pandocStderr = RunPandoc(mdPath, docxPath);

        Assert.True(File.Exists(docxPath), "DOCX not created");
        _output.WriteLine($"DOCX: {new FileInfo(docxPath).Length:N0} bytes");

        var warnings = SplitPandocWarnings(pandocStderr);
        var actionable = warnings
            .Where(w => !w.Contains("rsvg-convert", StringComparison.OrdinalIgnoreCase)
                     && !w.Contains("Could not convert TeX math", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Pandoc: {warnings.Count} total, {actionable.Count} actionable");
        foreach (var w in actionable)
            _output.WriteLine($"  {w}");

        Assert.Empty(actionable);
    }

    private static string RunPandoc(string mdPath, string docxPath)
    {
        var resourcePath = Path.GetDirectoryName(mdPath) ?? ".";
        var args = $"\"{mdPath}\" -o \"{docxPath}\" --from=markdown --to=docx --wrap=none --toc --toc-depth=2 --resource-path=\"{resourcePath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "pandoc",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = resourcePath
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pandoc");

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pandoc failed (exit {process.ExitCode}):\n{stderr}");

        return stderr;
    }

    private static List<string> SplitPandocWarnings(string stderr)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in stderr.Split('\n'))
        {
            if (line.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Length > 0)
                    blocks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0)
            blocks.Add(current.ToString().Trim());

        return blocks.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
    }
}
