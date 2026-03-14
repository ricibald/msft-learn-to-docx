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

string? inputUrl = null;
string? templatePath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--template" or "-t" when i + 1 < args.Length:
            templatePath = args[++i];
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            if (inputUrl is null && !args[i].StartsWith('-'))
                inputUrl = args[i];
            break;
    }
}

if (string.IsNullOrWhiteSpace(inputUrl))
{
    Console.Error.WriteLine("Error: No URL provided.");
    PrintUsage();
    return 1;
}

// --- Parse URL ---
var (contentType, slug) = ParseLearnUrl(inputUrl);
Console.WriteLine($"Type: {contentType}, Slug: {slug}");

// --- Verify pandoc ---
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

// --- Create output directory (unique per run) ---
var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output", $"{slug}_{timestamp}");
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

// --- Download content ---
DownloadedContent content;

if (contentType == "paths")
{
    content = await downloader.DownloadPathAsync(slug, outputDir);
}
else
{
    content = await downloader.DownloadModuleBySlugAsync(slug, outputDir);
}

// --- Merge markdown ---
Console.WriteLine("\nMerging markdown...");
var mergedMarkdown = merger.Merge(content);
var mdPath = Path.Combine(outputDir, $"{slug}.md");
await File.WriteAllTextAsync(mdPath, mergedMarkdown);
Console.WriteLine($"  Markdown saved: {mdPath}");

// --- Convert to DOCX ---
var docxPath = Path.Combine(outputDir, $"{slug}.docx");
pandoc.Convert(mdPath, docxPath, templatePath);
Console.WriteLine($"\nDone! Output files:");
Console.WriteLine($"  Markdown: {mdPath}");
Console.WriteLine($"  Word:     {docxPath}");

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
    var client = new HttpClient(new RetryHandler());
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
          MsftLearnToDocx <url> [options]

        Arguments:
          <url>    Microsoft Learn URL for a path or module
                   Examples:
                     https://learn.microsoft.com/en-us/training/paths/copilot/
                     https://learn.microsoft.com/en-us/training/modules/introduction-to-github-copilot/

        Options:
          --template, -t <path>   Path to a DOCX template file for pandoc --reference-doc
                                  Default: Templates/template.docx (if exists)
          --help, -h              Show this help

        Environment Variables:
          GITHUB_TOKEN            Optional GitHub Personal Access Token for higher API rate limits

        Prerequisites:
          pandoc                  Must be installed and in PATH (https://pandoc.org/installing.html)
        """);
}
