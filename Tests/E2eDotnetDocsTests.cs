using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;
using Xunit.Abstractions;

namespace MsftLearnToDocx.Tests;

/// <summary>
/// E2E test for learn.microsoft.com/dotnet/core/extensions/dependency-injection.
/// Verifies the full pipeline for dotnet/docs repo.
/// </summary>
public sealed class E2eDotnetDocsTests : IDisposable
{
    private readonly string _outputDir;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly ITestOutputHelper _output;

    public E2eDotnetDocsTests(ITestOutputHelper output)
    {
        _output = output;
        _outputDir = Path.Combine(Path.GetTempPath(), $"e2e_dotnet_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task DownloadDotnetDependencyInjection_FullPipeline_NoActionableWarnings()
    {
        // --- Arrange ---
        var url = "https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection";
        var parsed = DocsUrlParser.Parse(url);
        Assert.IsType<DocsSiteUrl>(parsed);

        var docsSiteUrl = (DocsSiteUrl)parsed;
        var repo = docsSiteUrl.RepoInfo;

        Assert.Equal("dotnet", repo.Owner);
        Assert.Equal("docs", repo.Repo);
        Assert.Equal("docs", repo.DocsBasePath);

        var githubClient = new GitHubRawClient(_httpClient, _cacheDir);
        var dfmConverter = new DfmConverter();
        var docsDownloader = new DocsDownloader(githubClient, dfmConverter);
        var merger = new MarkdownMerger();

        // --- Act: Download ---
        var content = await docsDownloader.DownloadAsync(repo, _outputDir);

        // --- Act: Merge ---
        var mergedMarkdown = merger.Merge([content], documentTitle: null, sourceUrls: [url]);
        var mdPath = Path.Combine(_outputDir, "dependency-injection.md");
        await File.WriteAllTextAsync(mdPath, mergedMarkdown);

        // --- Assert: Downloads (file-based, no Console capture) ---
        var pageCount = content.Modules.SelectMany(m => m.Units).Count(u => !u.IsSectionHeader);
        Assert.True(pageCount > 0, "Should download at least 1 page");
        _output.WriteLine($"Pages: {pageCount}");

        var mediaDir = Path.Combine(_outputDir, "media");
        var downloadedImages = Directory.Exists(mediaDir) ? Directory.GetFiles(mediaDir).Length : 0;
        _output.WriteLine($"Images: {downloadedImages}");
        _output.WriteLine($"Output dir: {_outputDir}");

        // All images referenced in markdown should exist on disk
        var referencedFiles = Regex.Matches(mergedMarkdown, @"!\[[^\]]*\]\(media/([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var missingImages = referencedFiles
            .Where(f => !File.Exists(Path.Combine(_outputDir, "media", f)))
            .ToList();
        Assert.Empty(missingImages);

        // --- Act: Pandoc ---
        var docxPath = Path.Combine(_outputDir, "dependency-injection.docx");
        var pandocStderr = RunPandoc(mdPath, docxPath);

        Assert.True(File.Exists(docxPath), "DOCX not created");
        _output.WriteLine($"DOCX size: {new FileInfo(docxPath).Length:N0} bytes");

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
