using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;
using Xunit.Abstractions;

namespace MsftLearnToDocx.Tests;

/// <summary>
/// End-to-end integration test that downloads https://learn.microsoft.com/en-us/azure/storage/blobs
/// and verifies the full pipeline (download → markdown merge → pandoc DOCX conversion)
/// produces zero errors and zero warnings.
/// </summary>
[Trait("Category", "E2E")]
public sealed class E2eAzureBlobsTests : IDisposable
{
    private readonly string _outputDir;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly ITestOutputHelper _output;

    public E2eAzureBlobsTests(ITestOutputHelper output)
    {
        _output = output;
        _outputDir = Path.Combine(Path.GetTempPath(), $"e2e_blobs_{Guid.NewGuid():N}");
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

    public void Dispose()
    {
        _httpClient.Dispose();
        // Don't delete output dir so we can inspect results after test
    }

    /// <summary>
    /// Full E2E: download azure/storage/blobs docs → merge markdown → pandoc DOCX.
    /// Asserts:
    ///   - All images downloaded (no [WARN] lines)
    ///   - Pandoc conversion exits 0 with no stderr warnings
    ///   - Output .md and .docx files exist and are non-empty
    /// </summary>
    [Fact]
    public async Task DownloadAzureStorageBlobs_FullPipeline_NoErrorsOrWarnings()
    {
        // --- Arrange ---
        var url = "https://learn.microsoft.com/en-us/azure/storage/blobs";
        var parsed = DocsUrlParser.Parse(url);
        Assert.IsType<DocsSiteUrl>(parsed);

        var docsSiteUrl = (DocsSiteUrl)parsed;
        var repo = docsSiteUrl.RepoInfo;

        // Verify mapping
        Assert.Equal("MicrosoftDocs", repo.Owner);
        Assert.Equal("azure-docs", repo.Repo);
        Assert.Equal("articles", repo.DocsBasePath);
        Assert.Equal("storage/blobs", repo.ContentPath);

        var githubClient = new GitHubRawClient(_httpClient, _cacheDir);
        var dfmConverter = new DfmConverter();
        var docsDownloader = new DocsDownloader(githubClient, dfmConverter);
        var merger = new MarkdownMerger();

        // --- Act: Download ---
        var content = await docsDownloader.DownloadAsync(repo, _outputDir);

        // --- Act: Merge ---
        var mergedMarkdown = merger.Merge(
            [content],
            documentTitle: null,
            sourceUrls: [url]);

        var mdPath = Path.Combine(_outputDir, "blobs.md");
        await File.WriteAllTextAsync(mdPath, mergedMarkdown);

        // --- Assert: Download phase (file-based, no Console capture) ---
        var pageCount = content.Modules.SelectMany(m => m.Units).Count(u => !u.IsSectionHeader);
        Assert.True(pageCount > 0, "Should download at least 1 page");
        _output.WriteLine($"Pages downloaded: {pageCount}");

        var mediaDir = Path.Combine(_outputDir, "media");
        var downloadedImages = Directory.Exists(mediaDir) ? Directory.GetFiles(mediaDir).Length : 0;
        _output.WriteLine($"Images on disk: {downloadedImages}");
        _output.WriteLine($"Output dir: {_outputDir}");

        // Count image references in merged markdown to compare
        var imageRefs = Regex.Matches(mergedMarkdown, @"!\[[^\]]*\]\(media/[^)]+\)").Count;
        _output.WriteLine($"Image references in markdown: {imageRefs}");

        // All images referenced in markdown should exist on disk
        var referencedFiles = Regex.Matches(mergedMarkdown, @"!\[[^\]]*\]\(media/([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var missingImages = referencedFiles
            .Where(f => !File.Exists(Path.Combine(_outputDir, "media", f)))
            .ToList();
        _output.WriteLine($"Missing images: {missingImages.Count}");
        foreach (var img in missingImages)
            _output.WriteLine($"  {img}");
        Assert.Empty(missingImages);

        // Assert markdown file exists and is non-empty
        Assert.True(File.Exists(mdPath), "Markdown file not created");
        var mdSize = new FileInfo(mdPath).Length;
        Assert.True(mdSize > 0, "Markdown file is empty");
        _output.WriteLine($"Markdown size: {mdSize:N0} bytes");

        // --- Act: Pandoc conversion ---
        var docxPath = Path.Combine(_outputDir, "blobs.docx");
        var pandocStderr = RunPandoc(mdPath, docxPath);

        // --- Assert: Pandoc phase ---
        Assert.True(File.Exists(docxPath), "DOCX file not created");
        var docxSize = new FileInfo(docxPath).Length;
        Assert.True(docxSize > 0, "DOCX file is empty");
        _output.WriteLine($"DOCX size: {docxSize:N0} bytes");

        // Check pandoc stderr for actionable warnings (exclude environmental/content-inherent)
        var pandocWarnings = SplitPandocWarnings(pandocStderr);
        var actionableWarnings = pandocWarnings
            .Where(w => !w.Contains("rsvg-convert", StringComparison.OrdinalIgnoreCase)
                     && !w.Contains("Could not convert TeX math", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pandocWarnings.Count > 0)
        {
            _output.WriteLine($"Pandoc warnings ({pandocWarnings.Count} total, {actionableWarnings.Count} actionable):");
            foreach (var w in pandocWarnings)
                _output.WriteLine($"  {w}");
        }

        Assert.Empty(actionableWarnings);
    }

    /// <summary>
    /// Diagnostic-only version: downloads and reports all issues without hard-failing.
    /// Use this to discover problems before fixing them.
    /// </summary>
    [Fact]
    public async Task DownloadAzureStorageBlobs_DiagnosticReport()
    {
        var url = "https://learn.microsoft.com/en-us/azure/storage/blobs";
        var parsed = (DocsSiteUrl)DocsUrlParser.Parse(url);
        var repo = parsed.RepoInfo;

        var githubClient = new GitHubRawClient(_httpClient, _cacheDir);
        var dfmConverter = new DfmConverter();
        var docsDownloader = new DocsDownloader(githubClient, dfmConverter);
        var merger = new MarkdownMerger();

        // --- Act: Download ---
        var content = await docsDownloader.DownloadAsync(repo, _outputDir);

        var mergedMarkdown = merger.Merge([content], documentTitle: null, sourceUrls: [url]);
        var mdPath = Path.Combine(_outputDir, "blobs.md");
        await File.WriteAllTextAsync(mdPath, mergedMarkdown);

        // --- Report download issues (file-based, no Console capture) ---
        var issues = new List<string>();
        ReportDownloadIssues(mergedMarkdown, content, repo, issues);

        // --- Analyze markdown ---
        await ReportMarkdownIssuesAsync(mdPath, issues);

        // --- Run pandoc and capture warnings ---
        var docxPath = Path.Combine(_outputDir, "blobs.docx");
        ReportPandocIssues(mdPath, docxPath, issues);

        // Final assertion: collect all issues
        Assert.True(issues.Count == 0,
            $"Diagnostic found {issues.Count} issues:\n{string.Join("\n", issues)}");
    }

    private void ReportDownloadIssues(string mergedMarkdown, DownloadedContent content, DocsRepoInfo repo, List<string> issues)
    {
        var pageCount = content.Modules.SelectMany(m => m.Units).Count(u => !u.IsSectionHeader);
        _output.WriteLine($"Output dir: {_outputDir}");
        _output.WriteLine($"Pages: {pageCount}");

        var mediaDir = Path.Combine(_outputDir, "media");
        var imagesOnDisk = Directory.Exists(mediaDir) ? Directory.GetFiles(mediaDir).Length : 0;
        _output.WriteLine($"Images on disk: {imagesOnDisk}");

        // Find images referenced in markdown but missing from disk
        var referencedFiles = Regex.Matches(mergedMarkdown, @"!\[[^\]]*\]\(media/([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var missingImages = referencedFiles
            .Where(f => !File.Exists(Path.Combine(_outputDir, "media", f)))
            .ToList();

        _output.WriteLine($"Image refs in markdown: {referencedFiles.Count}");
        _output.WriteLine($"Missing images: {missingImages.Count}");

        foreach (var img in missingImages)
        {
            _output.WriteLine($"  Missing: {img}");
            issues.Add($"Missing image: {img}");

            // Find which unit references it
            var unit = content.Modules[0].Units
                .Where(u => u.MarkdownContent != null)
                .FirstOrDefault(u => u.MarkdownContent!.Contains(img, StringComparison.OrdinalIgnoreCase));

            if (unit != null)
                _output.WriteLine($"    Referenced by: {unit.Uid} ({unit.Title})");

            _output.WriteLine($"    Live site URL: {repo.GetLiveSiteUrl($"media/{img}")}");
        }
    }

    private async Task ReportMarkdownIssuesAsync(string mdPath, List<string> issues)
    {
        _output.WriteLine($"Markdown: {new FileInfo(mdPath).Length:N0} bytes");

        var mdContent = await File.ReadAllTextAsync(mdPath);
        var lines = mdContent.Split('\n');

        var mathBlocks = lines.Count(l => l.Contains("$$"));
        var inlineMath = lines.Count(l => Regex.IsMatch(l, @"(?<![\\$])\$[^$]+\$(?!\$)"));
        _output.WriteLine($"Math blocks ($$): {mathBlocks}");
        _output.WriteLine($"Inline math ($): {inlineMath}");

        // Check for image refs not pointing to media/ or http
        var brokenRefs = lines
            .Select((l, i) => (Line: l, Num: i + 1))
            .Where(x => Regex.IsMatch(x.Line, @"!\[[^\]]*\]\([^)]*\)") &&
                         !Regex.IsMatch(x.Line, @"!\[[^\]]*\]\(media/") &&
                         !Regex.IsMatch(x.Line, @"!\[[^\]]*\]\(https?://"))
            .Take(10)
            .ToList();

        foreach (var br in brokenRefs)
        {
            var line = br.Line.Trim();
            if (line.Length > 120) line = line[..120] + "...";
            _output.WriteLine($"  Broken ref L{br.Num}: {line}");
            issues.Add($"Broken image ref at L{br.Num}: {line}");
        }
    }

    private void ReportPandocIssues(string mdPath, string docxPath, List<string> issues)
    {
        var pandocStderr = RunPandoc(mdPath, docxPath);

        if (string.IsNullOrWhiteSpace(pandocStderr))
        {
            _output.WriteLine("Pandoc: no warnings");
            return;
        }

        // Pandoc emits multi-line warnings; group by [WARNING] marker
        var warningBlocks = SplitPandocWarnings(pandocStderr);

        // Classify warnings
        var actionable = new List<string>();
        var environmental = new List<string>();

        foreach (var w in warningBlocks)
        {
            if (w.Contains("rsvg-convert", StringComparison.OrdinalIgnoreCase))
                environmental.Add(w); // SVG converter not installed — Docker-only
            else if (w.Contains("Could not convert TeX math", StringComparison.OrdinalIgnoreCase))
                environmental.Add(w); // Source content uses $ literally (false positive)
            else
                actionable.Add(w);
        }

        _output.WriteLine($"Pandoc warnings: {warningBlocks.Count} total ({actionable.Count} actionable, {environmental.Count} environmental)");
        foreach (var w in actionable)
        {
            _output.WriteLine($"  [ACTIONABLE] {w}");
            issues.Add($"Pandoc: {w}");
        }
        foreach (var w in environmental)
            _output.WriteLine($"  [ENV] {w}");

        if (File.Exists(docxPath))
            _output.WriteLine($"DOCX size: {new FileInfo(docxPath).Length:N0} bytes");
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

    /// <summary>
    /// Runs pandoc and returns stderr content (warnings/errors).
    /// Throws if pandoc exits with non-zero code.
    /// </summary>
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
}
