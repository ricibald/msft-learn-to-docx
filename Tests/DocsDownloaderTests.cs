using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

[Trait("Category", "Unit")]
public class DocsDownloaderTests
{
    // --- StripFrontmatter ---

    [Fact]
    public void StripFrontmatter_RemovesYamlBlock()
    {
        var markdown = "---\ntitle: Test\nauthor: Someone\n---\n# Content\nText here";
        Assert.Equal("# Content\nText here", DocsDownloader.StripFrontmatter(markdown));
    }

    [Fact]
    public void StripFrontmatter_NoFrontmatter_ReturnsUnchanged()
    {
        var markdown = "# No frontmatter\nJust content";
        Assert.Equal(markdown, DocsDownloader.StripFrontmatter(markdown));
    }

    [Fact]
    public void StripFrontmatter_FrontmatterOnly_ReturnsEmpty()
    {
        var markdown = "---\ntitle: Only meta\n---\n";
        Assert.Equal("", DocsDownloader.StripFrontmatter(markdown));
    }

    // --- StripHtmlBlocks ---

    [Fact]
    public void StripHtmlBlocks_RemovesVideoTags()
    {
        var markdown = "Before\n<video src=\"test.mp4\"></video>\nAfter";
        var result = DocsDownloader.StripHtmlBlocks(markdown);
        Assert.DoesNotContain("<video", result);
        Assert.Contains("Before", result);
        Assert.Contains("After", result);
    }

    [Fact]
    public void StripHtmlBlocks_RemovesDivBlocks()
    {
        var markdown = "Before\n<div class=\"docs-action\">\n<a href=\"link\">Action</a>\n</div>\nAfter";
        var result = DocsDownloader.StripHtmlBlocks(markdown);
        Assert.DoesNotContain("<div", result);
        Assert.DoesNotContain("</div>", result);
        Assert.Contains("Before", result);
        Assert.Contains("After", result);
    }

    // --- SanitizeImageFileName ---

    [Fact]
    public void SanitizeImageFileName_StripsBasePath()
    {
        Assert.Equal("copilot_media_screenshot.png",
            DocsDownloader.SanitizeImageFileName("docs/copilot/media/screenshot.png", "docs"));
    }

    [Fact]
    public void SanitizeImageFileName_ReplacesSlashes()
    {
        Assert.Equal("functions_media_img.png",
            DocsDownloader.SanitizeImageFileName("articles/functions/media/img.png", "articles"));
    }

    [Fact]
    public void SanitizeImageFileName_NoMatchingBasePath()
    {
        Assert.Equal("other_path_media_img.png",
            DocsDownloader.SanitizeImageFileName("other/path/media/img.png", "docs"));
    }

    // --- ResolveRelativePath ---

    [Fact]
    public void ResolveRelativePath_ParentDir_ResolvesCorrectly()
    {
        Assert.Equal("docs/media/img.png",
            DocsDownloader.ResolveRelativePath("docs/copilot", "../media/img.png"));
    }

    [Fact]
    public void ResolveRelativePath_SameDir_JoinsCorrectly()
    {
        Assert.Equal("docs/copilot/media/img.png",
            DocsDownloader.ResolveRelativePath("docs/copilot", "media/img.png"));
    }

    [Fact]
    public void ResolveRelativePath_EmptyBase_ReturnsRelative()
    {
        Assert.Equal("media/img.png",
            DocsDownloader.ResolveRelativePath("", "media/img.png"));
    }

    [Fact]
    public void ResolveRelativePath_DotSegments_Cleaned()
    {
        Assert.Equal("a/d/file.md",
            DocsDownloader.ResolveRelativePath("a/b/c", "../../d/file.md"));
    }

    [Fact]
    public void ResolveRelativePath_TildePrefix_ResolvesAsRepoRoot()
    {
        // ~/... is DocFX syntax for repo root — should NOT prepend baseDir
        Assert.Equal("reusable-content/ce-skilling/azure/media/storage/img.png",
            DocsDownloader.ResolveRelativePath("articles/storage/common", "~/reusable-content/ce-skilling/azure/media/storage/img.png"));
    }

    [Fact]
    public void ResolveRelativePath_TildePrefixWithEmptyBase_ResolvesAsRepoRoot()
    {
        Assert.Equal("some/path/file.md",
            DocsDownloader.ResolveRelativePath("", "~/some/path/file.md"));
    }

    // --- DeriveTitleFromPath ---

    [Fact]
    public void DeriveTitleFromPath_KebabCase_ConvertedToTitleCase()
    {
        Assert.Equal("Getting Started",
            DocsDownloader.DeriveTitleFromPath("copilot/getting-started"));
    }

    [Fact]
    public void DeriveTitleFromPath_Empty_ReturnsDocumentation()
    {
        Assert.Equal("Documentation",
            DocsDownloader.DeriveTitleFromPath(""));
    }

    // --- Merge trailing HR removal ---

    [Fact]
    public void Merge_DocsSite_NoTrailingHorizontalRule()
    {
        var merger = new MarkdownMerger();
        var content = new MsftLearnToDocx.Models.DownloadedContent
        {
            Title = "Test",
            Type = MsftLearnToDocx.Models.ContentType.DocsSite,
            Modules =
            [
                new MsftLearnToDocx.Models.DownloadedModule
                {
                    Title = "Test",
                    Units =
                    [
                        new MsftLearnToDocx.Models.DownloadedUnit
                        {
                            Title = "Last Page",
                            MarkdownContent = "Final content"
                        }
                    ]
                }
            ]
        };
        var result = merger.Merge([content], date: new DateTime(2025, 1, 1));
        var trimmed = result.TrimEnd();
        Assert.False(trimmed.EndsWith("---"), "Merged output should not end with a trailing horizontal rule");
    }

    // --- InlineRefStyleImageLinks (via RemapImagePaths) ---

    [Fact]
    public void StripFrontmatter_RefStyleImageLinks_ConvertedToInline()
    {
        // Simulate what RemapImagePaths does with reference-style image links
        // by testing the overall flow: the ref-style link should be inlined
        var input = "Some text\n\n![Screenshot of blobs][0]\n\n[0]: media/blob.png\n";

        // We test the InlineRefStyleImageLinks indirectly through RemapImagePaths
        // by using reflection or by testing the result of the public workflow.
        // For simplicity, we test the InlineRefStyleImageLinks method via its effect
        // on the markdown. Since RemapImagePaths is private, we verify via the full page process output.

        // Use the internal helper method available via DocsDownloader
        var allImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var method = typeof(DocsDownloader).GetMethod("InlineRefStyleImageLinks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [input, "articles/storage", "articles", allImagePaths])!;

        // Reference-style should be converted to inline
        Assert.DoesNotContain("[0]:", result);
        Assert.Contains("![Screenshot of blobs](media/", result);
        // Image should be collected for download
        Assert.Contains(allImagePaths, p => p.Contains("blob.png"));
    }

    // --- ParseTocJsonSection ---

    [Fact]
    public void ParseTocJsonSection_ExtractsMatchingSection()
    {
        var json = """
            [
              {"name": "Setup", "area": "setup", "topics": [["Overview", "/docs/setup/overview"]]},
              {"name": "Copilot", "area": "copilot", "topics": [
                ["Overview", "/docs/copilot/overview"],
                ["Setup", "/docs/copilot/setup"],
                ["Getting Started", "/docs/copilot/getting-started"]
              ]}
            ]
            """;

        var result = DocsDownloader.ParseTocJsonSection(json, "copilot", "docs");

        Assert.Equal(3, result.Count);
        Assert.Equal("docs/copilot/overview.md", result[0].Path);
        Assert.Equal("Overview", result[0].Title);
        Assert.Equal("docs/copilot/setup.md", result[1].Path);
        Assert.Equal("docs/copilot/getting-started.md", result[2].Path);
    }

    [Fact]
    public void ParseTocJsonSection_HandlesNestedSections()
    {
        var json = """
            [
              {"name": "Copilot", "area": "copilot", "topics": [
                ["Overview", "/docs/copilot/overview"],
                ["", "", {"name": "Agents", "area": "copilot/agents", "topics": [
                  ["Agent Overview", "/docs/copilot/agents/overview"],
                  ["Tutorial", "/docs/copilot/agents/tutorial"]
                ]}]
              ]}
            ]
            """;

        var result = DocsDownloader.ParseTocJsonSection(json, "copilot", "docs");

        Assert.Equal(4, result.Count);
        Assert.Equal("docs/copilot/overview.md", result[0].Path);
        Assert.False(result[0].IsSectionHeader);
        Assert.Equal(0, result[0].Depth);

        Assert.Null(result[1].Path);
        Assert.Equal("Agents", result[1].Title);
        Assert.True(result[1].IsSectionHeader);
        Assert.Equal(0, result[1].Depth);

        Assert.Equal("docs/copilot/agents/overview.md", result[2].Path);
        Assert.Equal(1, result[2].Depth);

        Assert.Equal("docs/copilot/agents/tutorial.md", result[3].Path);
        Assert.Equal(1, result[3].Depth);
    }

    [Fact]
    public void ParseTocJsonSection_NoMatchingArea_ReturnsEmpty()
    {
        var json = """[{"name": "Setup", "area": "setup", "topics": [["Overview", "/docs/setup/overview"]]}]""";

        var result = DocsDownloader.ParseTocJsonSection(json, "copilot", "docs");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseTocJsonSection_FindsNestedSubArea()
    {
        var json = """
            [
              {"name": "Setup", "area": "setup", "topics": [["Overview", "/docs/setup/overview"]]},
              {"name": "Copilot", "area": "copilot", "topics": [
                ["Overview", "/docs/copilot/overview"],
                ["", "", {"name": "Agents", "area": "copilot/agents", "topics": [
                  ["Agent Overview", "/docs/copilot/agents/overview"],
                  ["Tutorial", "/docs/copilot/agents/tutorial"]
                ]}],
                ["", "", {"name": "Chat", "area": "copilot/chat", "topics": [
                  ["Chat Overview", "/docs/copilot/chat/overview"]
                ]}]
              ]}
            ]
            """;

        // Request only the "copilot/agents" sub-area → should return only agents topics
        var result = DocsDownloader.ParseTocJsonSection(json, "copilot/agents", "docs");

        Assert.Equal(2, result.Count);
        Assert.Equal("docs/copilot/agents/overview.md", result[0].Path);
        Assert.Equal("Agent Overview", result[0].Title);
        Assert.Equal(0, result[0].Depth);
        Assert.Equal("docs/copilot/agents/tutorial.md", result[1].Path);
        Assert.Equal("Tutorial", result[1].Title);
    }

    [Fact]
    public void ParseTocJsonSection_FindsDeeplyNestedSubArea()
    {
        var json = """
            [
              {"name": "Copilot", "area": "copilot", "topics": [
                ["Overview", "/docs/copilot/overview"],
                ["", "", {"name": "Reference", "area": "copilot/reference", "topics": [
                  ["Cheat Sheet", "/docs/copilot/reference/cheat-sheet"],
                  ["", "", {"name": "API", "area": "copilot/reference/api", "topics": [
                    ["Endpoints", "/docs/copilot/reference/api/endpoints"]
                  ]}]
                ]}]
              ]}
            ]
            """;

        // Request deeply nested area
        var result = DocsDownloader.ParseTocJsonSection(json, "copilot/reference/api", "docs");

        Assert.Single(result);
        Assert.Equal("docs/copilot/reference/api/endpoints.md", result[0].Path);
        Assert.Equal("Endpoints", result[0].Title);
    }

    [Fact]
    public void ParseTocJsonSection_EmptyArea_ReturnsAllSections()
    {
        var json = """
            [
              {"name": "Setup", "area": "setup", "topics": [
                ["Overview", "/docs/setup/overview"],
                ["Linux", "/docs/setup/linux"]
              ]},
              {"name": "Copilot", "area": "copilot", "topics": [
                ["Overview", "/docs/copilot/overview"],
                ["", "", {"name": "Agents", "area": "copilot/agents", "topics": [
                  ["Agent Overview", "/docs/copilot/agents/overview"]
                ]}]
              ]}
            ]
            """;

        // Empty area → returns all sections flattened with section headers
        var result = DocsDownloader.ParseTocJsonSection(json, "", "docs");

        // Section "Setup" (header) + 2 pages + Section "Copilot" (header) + 1 page + subsection "Agents" (header) + 1 page = 7
        Assert.Equal(7, result.Count);

        // Setup section header at depth 0
        Assert.True(result[0].IsSectionHeader);
        Assert.Equal("Setup", result[0].Title);
        Assert.Equal(0, result[0].Depth);

        // Setup pages at depth 1
        Assert.Equal("docs/setup/overview.md", result[1].Path);
        Assert.Equal(1, result[1].Depth);
        Assert.Equal("docs/setup/linux.md", result[2].Path);

        // Copilot section header at depth 0
        Assert.True(result[3].IsSectionHeader);
        Assert.Equal("Copilot", result[3].Title);
        Assert.Equal(0, result[3].Depth);

        // Copilot page at depth 1
        Assert.Equal("docs/copilot/overview.md", result[4].Path);
        Assert.Equal(1, result[4].Depth);

        // Agents subsection header at depth 1
        Assert.True(result[5].IsSectionHeader);
        Assert.Equal("Agents", result[5].Title);
        Assert.Equal(1, result[5].Depth);

        // Agents page at depth 2
        Assert.Equal("docs/copilot/agents/overview.md", result[6].Path);
        Assert.Equal(2, result[6].Depth);
    }

    // --- IsRedirectPage ---

    [Fact]
    public void IsRedirectPage_FrontmatterWithRedirectUrl_ReturnsTrue()
    {
        var raw = "---\nredirect_url: /azure/some-page\ntitle: Old Page\n---\n# Old Page\nSome content";
        var stripped = DocsDownloader.StripFrontmatter(raw);

        Assert.True(DocsDownloader.IsRedirectPage(raw, stripped));
    }

    [Fact]
    public void IsRedirectPage_ShortBodyWithRedirect_ReturnsTrue()
    {
        var raw = "---\ntitle: Redirect Page\n---\n# Review code\nThis page is redirected to https://example.com.";
        var stripped = DocsDownloader.StripFrontmatter(raw);

        Assert.True(DocsDownloader.IsRedirectPage(raw, stripped));
    }

    [Fact]
    public void IsRedirectPage_NormalPage_ReturnsFalse()
    {
        var raw = "---\ntitle: Normal Page\n---\n# Normal Page\n" + new string('x', 600);
        var stripped = DocsDownloader.StripFrontmatter(raw);

        Assert.False(DocsDownloader.IsRedirectPage(raw, stripped));
    }
}
