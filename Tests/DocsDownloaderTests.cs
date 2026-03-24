using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

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
}
