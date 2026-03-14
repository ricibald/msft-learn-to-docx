using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;

// --- Parse arguments ---
if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var inputUrls = new List<string>();
string? templatePath = null;
string? title = null;
string? outputPath = null;
string format = "docx";
int tocDepth = 3;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--template" or "-t" when i + 1 < args.Length:
            templatePath = args[++i];
            break;
        case "--title" when i + 1 < args.Length:
            title = args[++i];
            break;
        case "--output" or "-o" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--format" or "-f" when i + 1 < args.Length:
            format = args[++i].ToLowerInvariant();
            if (format is not ("docx" or "md"))
                throw new ArgumentException($"Unsupported format '{format}'. Supported: docx, md");
            break;
        case "--toc-depth" when i + 1 < args.Length:
            tocDepth = int.Parse(args[++i]);
            if (tocDepth is < 1 or > 6)
                throw new ArgumentException("--toc-depth must be between 1 and 6");
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            if (!args[i].StartsWith('-'))
                inputUrls.Add(args[i]);
            break;
    }
}

if (inputUrls.Count == 0)
{
    Console.Error.WriteLine("Error: No URL provided.");
    PrintUsage();
    return 1;
}

// --- Parse all URLs ---
var parsedUrls = inputUrls.Select(ParseLearnUrl).ToList();
foreach (var (type, slug) in parsedUrls)
    Console.WriteLine($"  Input: {type}/{slug}");

// --- Verify pandoc (skip when markdown-only) ---
if (format == "docx")
    PandocRunner.FindPandoc();

// --- Create HTTP client ---
using var httpClient = CreateHttpClient();

// --- Create services ---
var catalogClient = new LearnCatalogClient(httpClient);
var githubClient = new GitHubRawClient(httpClient);
var resolver = new ModuleResolver(catalogClient, githubClient);
var dfmConverter = new DfmConverter();
var downloader = new ContentDownloader(githubClient, resolver, dfmConverter);
var merger = new MarkdownMerger();
var pandoc = new PandocRunner();

// --- Create output directory ---
var firstSlug = parsedUrls[0].slug;
var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var outputDir = outputPath ?? Path.Combine(Directory.GetCurrentDirectory(), "output", $"{firstSlug}_{timestamp}");
Directory.CreateDirectory(outputDir);

// --- Resolve template (default: Templates/template.docx) ---
if (string.IsNullOrWhiteSpace(templatePath))
{
    var defaultTemplate = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "template.docx");
    if (File.Exists(defaultTemplate))
    {
        templatePath = defaultTemplate;
        Console.WriteLine($"Using default template: {templatePath}");
    }
}
else
{
    Console.WriteLine($"Using template: {templatePath}");
}

// --- Download content for all URLs ---
var allContents = new List<DownloadedContent>();

foreach (var (contentType, slug) in parsedUrls)
{
    Console.WriteLine($"\n=== Downloading: {contentType}/{slug} ===");
    DownloadedContent content;

    if (contentType == "paths")
    {
        content = await downloader.DownloadPathAsync(slug, outputDir);
    }
    else
    {
        content = await downloader.DownloadModuleBySlugAsync(slug, outputDir);
    }

    allContents.Add(content);
}

// --- Merge markdown (all contents into a single document) ---
Console.WriteLine("\nMerging markdown...");
var mergedMarkdown = merger.Merge(allContents, title);
var mdPath = Path.Combine(outputDir, $"{firstSlug}.md");
await File.WriteAllTextAsync(mdPath, mergedMarkdown);
Console.WriteLine($"  Markdown saved: {mdPath}");

// --- Convert to DOCX (unless markdown-only) ---
Console.WriteLine($"\nDone! Output files:");
Console.WriteLine($"  Markdown: {mdPath}");

if (format == "docx")
{
    var docxPath = Path.Combine(outputDir, $"{firstSlug}.docx");
    pandoc.Convert(mdPath, docxPath, templatePath, tocDepth);
    Console.WriteLine($"  Word:     {docxPath}");
}

return 0;

// --- Helper methods ---

static (string type, string slug) ParseLearnUrl(string url)
{
    // Support formats:
    // https://learn.microsoft.com/en-us/training/paths/copilot/
    // https://learn.microsoft.com/en-us/training/modules/introduction-to-github-copilot/
    // https://learn.microsoft.com/training/paths/copilot
    // paths/copilot or modules/introduction-to-github-copilot (shorthand)
    var match = Regex.Match(url, @"(?:training/)?(paths|modules)/([^/?#]+)", RegexOptions.IgnoreCase);
    if (!match.Success)
        throw new ArgumentException(
            $"Invalid URL format. Expected: https://learn.microsoft.com/.../training/paths/{{slug}} or .../modules/{{slug}}\nGot: {url}");

    return (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value);
}

static HttpClient CreateHttpClient()
{
    // Handler pipeline: CachingHandler → RetryHandler → HttpClientHandler
    var cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MsftLearnToDocx", "cache");
    var handler = new CachingHandler(new RetryHandler(), cacheDir, TimeSpan.FromHours(24));

    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MsftLearnToDocx", "1.0"));
    client.Timeout = TimeSpan.FromSeconds(30);

    // Optional GitHub token for higher rate limits
    var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (!string.IsNullOrWhiteSpace(githubToken))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        Console.WriteLine("Using GITHUB_TOKEN for authenticated requests.");
    }
    else
    {
        Console.WriteLine("No GITHUB_TOKEN set. Using unauthenticated requests (60 req/h rate limit for GitHub API).");
    }

    return client;
}

static void PrintUsage()
{
    Console.WriteLine("""
        MsftLearnToDocx - Convert Microsoft Learn paths/modules to Markdown and DOCX

        Usage:
          MsftLearnToDocx <url> [<url2> ...] [options]

        Arguments:
          <url>    One or more Microsoft Learn URLs (paths or modules).
                   Multiple URLs are merged into a single output document.
                   Examples:
                     https://learn.microsoft.com/en-us/training/paths/copilot/
                     https://learn.microsoft.com/en-us/training/modules/introduction-to-github-copilot/

        Options:
          --title <text>          Custom document title (used in cover page).
                                  Default: auto-derived from the learning path/module title(s).
          --template, -t <path>   Path to a DOCX template file for pandoc --reference-doc.
                                  Default: Templates/template.docx (if exists).
          --output, -o <dir>      Output directory. Default: output/{slug}_{timestamp}/.
          --format, -f <fmt>      Output format: "docx" (default) or "md" (markdown only, no pandoc).
          --toc-depth <n>         Table of contents depth for DOCX (1-6). Default: 3.
          --help, -h              Show this help.

        Environment Variables:
          GITHUB_TOKEN            Optional GitHub Personal Access Token for higher API rate limits.

        Prerequisites:
          pandoc                  Required for DOCX output (https://pandoc.org/installing.html).

        Examples:
          # Single learning path
          MsftLearnToDocx "https://learn.microsoft.com/en-us/training/paths/copilot/"

          # Multiple modules merged into one document
          MsftLearnToDocx "https://learn.microsoft.com/.../modules/mod1/" "https://learn.microsoft.com/.../modules/mod2/"

          # Markdown only, custom output directory
          MsftLearnToDocx "https://learn.microsoft.com/.../paths/copilot/" -f md -o ./my-output

          # With custom title and template
          MsftLearnToDocx "https://learn.microsoft.com/.../paths/copilot/" --title "GitHub Copilot Guide" -t custom.docx
        """);
}
