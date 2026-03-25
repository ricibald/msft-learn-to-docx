using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

[Trait("Category", "Unit")]
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
    public void Merge_DocsSite_PageTitleIsHeading_ContentShifted()
    {
        var content = CreateContent("Docs", ContentType.DocsSite, ("Page", "# Original H1\n## Original H2"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        // SectionDepth=0 → page title as H1, content shifted to start at H2
        Assert.Contains("# Page", result);
        Assert.Contains("## Original H1", result);
        Assert.Contains("### Original H2", result);
    }

    [Fact]
    public void Merge_DocsSite_DuplicateTitleRemoved()
    {
        // When page content starts with an H1 matching the TOC title, it should be removed
        var content = CreateContent("Docs", ContentType.DocsSite, ("My Page", "# My Page\n## Sub heading\nContent"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        // H1 "My Page" from content is removed, only the TOC-based heading remains
        var h1Count = System.Text.RegularExpressions.Regex.Matches(result, @"^# My Page\r?$", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        Assert.Equal(1, h1Count);
    }

    [Fact]
    public void Merge_DocsSite_SectionHeaders_CreateHeadingsWithoutContent()
    {
        var content = CreateDocsSiteContent("Docs",
            (Title: "Overview", Content: "", Depth: 0, IsSectionHeader: true),
            (Title: "Intro", Content: "Intro content", Depth: 1, IsSectionHeader: false),
            (Title: "Advanced", Content: "", Depth: 0, IsSectionHeader: true),
            (Title: "Deep Dive", Content: "Advanced content", Depth: 1, IsSectionHeader: false));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Overview", result);
        Assert.Contains("## Intro", result);
        Assert.Contains("# Advanced", result);
        Assert.Contains("## Deep Dive", result);
    }

    [Fact]
    public void Merge_DocsSite_DepthLevels_MappedToHeadingHierarchy()
    {
        var content = CreateDocsSiteContent("Docs",
            (Title: "Section", Content: "", Depth: 0, IsSectionHeader: true),
            (Title: "SubSection", Content: "", Depth: 1, IsSectionHeader: true),
            (Title: "Page", Content: "Content here", Depth: 2, IsSectionHeader: false));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Section", result);
        Assert.Contains("## SubSection", result);
        Assert.Contains("### Page", result);
        Assert.Contains("Content here", result);
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

    // --- Download summary ---

    [Fact]
    public void Merge_DownloadSummary_ShowsModuleCounts()
    {
        var content = CreateContent("Module", ContentType.LearnTraining, ("U1", "text"), ("U2", "![img](media/pic.png)"));
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("# Download Summary", result);
        Assert.Contains("**1/1** modules downloaded", result);
        Assert.Contains("**2** units processed", result);
        Assert.Contains("**1** images", result);
    }

    [Fact]
    public void Merge_DownloadSummary_UnavailableModulesListed()
    {
        var content = new DownloadedContent
        {
            Title = "Path",
            IsPath = true,
            Type = ContentType.LearnTraining,
            Modules =
            [
                new DownloadedModule
                {
                    Title = "OK Module",
                    Uid = "learn.ok",
                    Units = [new DownloadedUnit { Title = "U1", Uid = "learn.ok.u1", MarkdownContent = "text" }]
                },
                new DownloadedModule
                {
                    Title = "Missing Module",
                    Uid = "learn-bizapps.missing",
                    Units =
                    [
                        new DownloadedUnit
                        {
                            Title = "Content Unavailable",
                            Uid = "learn-bizapps.missing.unavailable",
                            MarkdownContent = "> warning"
                        }
                    ]
                }
            ]
        };
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("**1/2** modules downloaded", result);
        Assert.Contains("**1** units processed", result);
        Assert.Contains("1 module(s) could not be downloaded", result);
        Assert.Contains("Missing Module (`learn-bizapps.missing`)", result);
    }

    [Fact]
    public void Merge_DownloadSummary_IncludesQuizCount()
    {
        var content = new DownloadedContent
        {
            Title = "Module",
            Type = ContentType.LearnTraining,
            Modules =
            [
                new DownloadedModule
                {
                    Title = "Module",
                    Uid = "learn.mod",
                    Units =
                    [
                        new DownloadedUnit { Title = "Intro", Uid = "learn.mod.intro", MarkdownContent = "text" },
                        new DownloadedUnit { Title = "Quiz", Uid = "learn.mod.quiz", MarkdownContent = "Q1", IsQuiz = true }
                    ]
                }
            ]
        };
        var result = _merger.Merge([content], date: new DateTime(2025, 1, 1));
        Assert.Contains("**1** knowledge checks", result);
    }

    // --- RemoveLeadingDuplicateTitle ---

    [Fact]
    public void RemoveLeadingDuplicateTitle_MatchingH1_Removed()
    {
        var result = MarkdownMerger.RemoveLeadingDuplicateTitle("# My Title\nContent", "My Title");
        Assert.DoesNotContain("# My Title", result);
        Assert.Contains("Content", result);
    }

    [Fact]
    public void RemoveLeadingDuplicateTitle_DifferentH1_Preserved()
    {
        var result = MarkdownMerger.RemoveLeadingDuplicateTitle("# Other Title\nContent", "My Title");
        Assert.Contains("# Other Title", result);
    }

    [Fact]
    public void RemoveLeadingDuplicateTitle_NoH1_ReturnsUnchanged()
    {
        var result = MarkdownMerger.RemoveLeadingDuplicateTitle("Just content\nMore content", "Title");
        Assert.Equal("Just content\nMore content", result);
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

    private static DownloadedContent CreateDocsSiteContent(string title,
        params (string Title, string Content, int Depth, bool IsSectionHeader)[] units)
    {
        var moduleUnits = units.Select(u => new DownloadedUnit
        {
            Title = u.Title,
            MarkdownContent = u.Content,
            SectionDepth = u.Depth,
            IsSectionHeader = u.IsSectionHeader
        }).ToList();

        return new DownloadedContent
        {
            Title = title,
            Type = ContentType.DocsSite,
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
