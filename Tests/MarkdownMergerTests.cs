using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

public class MarkdownMergerTests
{
    private readonly MarkdownMerger _merger = new();

    // --- YAML frontmatter ---

    [Fact]
    public void Merge_GeneratesYamlFrontmatter()
    {
        var content = CreateContent("Test Module", ContentType.LearnTraining, ("Unit 1", "Hello"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 15));

        Assert.StartsWith("---", result);
        Assert.Contains("title: \"Test Module\"", result);
        Assert.Contains("date: 2025-01-15", result);
        Assert.Contains("subject: \"Microsoft Learn\"", result);
    }

    [Fact]
    public void Merge_CustomTitle_OverridesModuleTitle()
    {
        var content = CreateContent("Module", ContentType.LearnTraining, ("U1", "text"));
        var result = _merger.Merge([content], documentTitle: "Custom Title", date: new DateTime(2025, 1, 1));
        Assert.Contains("title: \"Custom Title\"", result);
    }

    [Fact]
    public void Merge_MultipleContents_JoinsTitles()
    {
        var c1 = CreateContent("First", ContentType.LearnTraining, ("U1", "text1"));
        var c2 = CreateContent("Second", ContentType.LearnTraining, ("U2", "text2"));
        var result = _merger.Merge([c1, c2], date: new DateTime(2025, 1, 1));
        Assert.Contains("title: \"First / Second\"", result);
    }

    // --- Subject field ---

    [Fact]
    public void Merge_DocsSiteOnly_SubjectIsMicrosoftDocumentation()
    {
        var content = CreateContent("Docs", ContentType.DocsSite, ("Page 1", "content"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("subject: \"Microsoft Documentation\"", result);
    }

    [Fact]
    public void Merge_MixedTypes_SubjectIsMicrosoftLearnAndDocumentation()
    {
        var c1 = CreateContent("Training", ContentType.LearnTraining, ("U1", "text"));
        var c2 = CreateContent("Docs", ContentType.DocsSite, ("P1", "text"));
        var result = _merger.Merge([c1, c2], date: new DateTime(2025, 1, 1));
        Assert.Contains("subject: \"Microsoft Learn & Documentation\"", result);
    }

    // --- CC BY 4.0 attribution ---

    [Fact]
    public void Merge_IncludesAttribution()
    {
        var content = CreateContent("M", ContentType.LearnTraining, ("U", "text"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Attribution", result);
        Assert.Contains("CC BY 4.0", result);
        Assert.Contains("Microsoft", result);
    }

    [Fact]
    public void Merge_SourceUrls_IncludedInAttribution()
    {
        var content = CreateContent("M", ContentType.LearnTraining, ("U", "text"));
        var urls = new[] { "https://learn.microsoft.com/training/paths/copilot/" };
        var result = _merger.Merge([content], sourceUrls: urls, date: new DateTime(2025, 1, 1));
        Assert.Contains("https://learn.microsoft.com/training/paths/copilot/", result);
    }

    // --- Learn training heading hierarchy ---

    [Fact]
    public void Merge_LearnTraining_ModuleTitleIsH1()
    {
        var content = CreateContent("My Module", ContentType.LearnTraining, ("My Unit", "## Heading\nText"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# My Module", result);
    }

    [Fact]
    public void Merge_LearnTraining_UnitTitleIsH2()
    {
        var content = CreateContent("Module", ContentType.LearnTraining, ("Unit Title", "text"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("## Unit Title", result);
    }

    [Fact]
    public void Merge_LearnTraining_ContentHeadingsShiftedToH3Plus()
    {
        var content = CreateContent("Module", ContentType.LearnTraining, ("Unit", "# Top\n## Sub"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        // Original H1 → H3, H2 → H4
        Assert.Contains("### Top", result);
        Assert.Contains("#### Sub", result);
    }

    // --- Docs site mode ---

    [Fact]
    public void Merge_DocsSite_PreservesOriginalHeadings()
    {
        var content = CreateContent("Docs", ContentType.DocsSite, ("Page", "# Original H1\n## Original H2"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Original H1", result);
        Assert.Contains("## Original H2", result);
    }

    [Fact]
    public void Merge_DocsSite_SeparatesPagesWithHorizontalRule()
    {
        var content = CreateContent("Docs", ContentType.DocsSite,
            ("Page 1", "Content 1"),
            ("Page 2", "Content 2"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("Content 1", result);
        Assert.Contains("---", result);
        Assert.Contains("Content 2", result);
    }

    // --- YAML escaping ---

    [Fact]
    public void Merge_TitleWithQuotes_Escaped()
    {
        var content = CreateContent("Module with \"quotes\"", ContentType.LearnTraining, ("U", "text"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("title: \"Module with \\\"quotes\\\"\"", result);
    }

    // --- Keywords ---

    [Fact]
    public void Merge_Keywords_IncludeModuleTitles()
    {
        var content = CreateContent("Module Alpha", ContentType.LearnTraining, ("Unit", "text"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("keywords:", result);
        Assert.Contains("\"Module Alpha\"", result);
    }

    // --- Placeholder modules ---

    [Fact]
    public void Merge_PlaceholderModule_RendersWarningBlockquote()
    {
        var content = new DownloadedContent
        {
            Title = "Test Path",
            IsPath = true,
            Type = ContentType.LearnTraining,
            Modules =
            [
                new DownloadedModule
                {
                    Title = "Azure Policy initiatives",
                    Uid = "learn-bizapps.sovereignty-policy-initiatives",
                    Units =
                    [
                        new DownloadedUnit
                        {
                            Title = "Content Unavailable",
                            Uid = "learn-bizapps.sovereignty-policy-initiatives.unavailable",
                            MarkdownContent = "> **⚠ Module not available for download**\n>\n> This module (*learn-bizapps.sovereignty-policy-initiatives*) could not be downloaded."
                        }
                    ]
                }
            ]
        };
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Azure Policy initiatives", result);
        Assert.Contains("## Content Unavailable", result);
        Assert.Contains("⚠ Module not available for download", result);
    }

    [Fact]
    public void Merge_MixedModules_PlaceholderAndNormal_BothRendered()
    {
        var content = new DownloadedContent
        {
            Title = "Test Path",
            IsPath = true,
            Type = ContentType.LearnTraining,
            Modules =
            [
                new DownloadedModule
                {
                    Title = "Real Module",
                    Uid = "learn.real-module",
                    Units = [new DownloadedUnit { Title = "Intro", MarkdownContent = "Real content here" }]
                },
                new DownloadedModule
                {
                    Title = "Unavailable Module",
                    Uid = "learn-bizapps.missing",
                    Units =
                    [
                        new DownloadedUnit
                        {
                            Title = "Content Unavailable",
                            Uid = "learn-bizapps.missing.unavailable",
                            MarkdownContent = "> **⚠ Module not available**"
                        }
                    ]
                }
            ]
        };
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Real Module", result);
        Assert.Contains("Real content here", result);
        Assert.Contains("# Unavailable Module", result);
        Assert.Contains("⚠ Module not available", result);
    }

    // --- Helper ---

    private static DownloadedContent CreateContent(string title, ContentType type, params (string Title, string Content)[] units)
    {
        var moduleUnits = units.Select(u => new DownloadedUnit
        {
            Title = u.Title,
            MarkdownContent = u.Content
        }).ToList();

        return new DownloadedContent
        {
            Title = title,
            Type = type,
            Modules =
            [
                new DownloadedModule
                {
                    Title = title,
                    Units = moduleUnits
                }
            ]
        };
    }
}
