using System.Net;
using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

[Trait("Category", "Unit")]
public class ContentDownloaderTests
{
    [Fact]
    public async Task DownloadCourseAsync_ExportsPathHierarchyAndOnlineUnitIndexInOrder()
    {
        const string moduleUrl = "https://learn.microsoft.com/en-us/training/modules/renamed-module/";
        using var http = new HttpClient(new StubHandler(new Dictionary<string, string>
        {
            ["https://learn.microsoft.com/api/catalog/?uid=course.ab-731t00&type=courses"] = """
                {"courses":[{"uid":"course.ab-731t00","title":"Sample course","study_guide":[
                    {"type":"learningPath","uid":"learn.wwl.sample-path"},
                    {"type":"module","uid":"learn.wwl.original-module"},
                    {"type":"learningPath","uid":"learn.wwl.second-path"},
                    {"type":"module","uid":"learn.wwl.direct-module"}
                ]}]}
                """,
            ["https://learn.microsoft.com/api/catalog/?uid=learn.wwl.sample-path&type=learningPaths"] = """
                {"learningPaths":[{"title":"First path","modules":["learn.github.public-module","learn.wwl.original-module"]}]}
                """,
            ["https://learn.microsoft.com/api/catalog/?uid=learn.wwl.second-path&type=learningPaths"] = """
                {"learningPaths":[{"title":"Second path","modules":["learn.github.public-module"]}]}
                """,
            ["https://learn.microsoft.com/api/catalog/?uid=learn.github.public-module&type=modules"] = """
                {"modules":[{"url":"https://learn.microsoft.com/en-us/training/modules/public-module/"}]}
                """,
            ["https://learn.microsoft.com/api/catalog/?uid=learn.wwl.original-module&type=modules"] =
                $$"""{"modules":[{"title":"Renamed module","url":"{{moduleUrl}}","units":["learn.wwl.original-module.intro","learn.wwl.original-module.summary"]}]}""",
            ["https://learn.microsoft.com/api/catalog/?uid=learn.wwl.original-module.intro,learn.wwl.original-module.summary&type=units"] = """
                {"units":[
                    {"uid":"learn.wwl.original-module.summary","title":"Online summary"},
                    {"uid":"learn.wwl.original-module.intro","title":"Online introduction"}
                ]}
                """,
            ["https://learn.microsoft.com/api/catalog/?uid=learn.wwl.direct-module&type=modules"] = """
                {"modules":[{"title":"Direct module","url":"https://learn.microsoft.com/en-us/training/modules/direct-module/","units":["learn.wwl.direct-module.intro"]}]}
                """,
            ["https://raw.githubusercontent.com/MicrosoftDocs/learn/main/learn-pr/github/public-module/index.yml"] = """
                uid: learn.github.public-module
                title: Public module
                units: [learn.github.public-module.introduction]
                """,
            ["https://api.github.com/repos/MicrosoftDocs/learn/contents/learn-pr/github/public-module"] =
                """[{"type":"file","name":"1-introduction.yml"}]""",
            ["https://raw.githubusercontent.com/MicrosoftDocs/learn/main/learn-pr/github/public-module/1-introduction.yml"] = """
                uid: learn.github.public-module.introduction
                title: Introduction
                content: |
                  ## Lesson details
                  Sample lesson text.
                """,
            ["https://api.github.com/repos/MicrosoftDocs/learn/contents/learn-pr"] = "[]",
        }));

        var content = await CreateDownloader(http).DownloadCourseAsync("AB-731T00", Path.GetTempPath());
        var markdown = new MarkdownMerger().Merge([content]);

        Assert.Equal("Sample course", content.Title);
        Assert.Equal(new[] { "learn.github.public-module", "learn.wwl.original-module", "learn.wwl.direct-module" },
            content.Modules.Select(m => m.Uid));
        var lines = markdown.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["# First path", "## Public module", "## Renamed module", "# Second path", "## Public module", "# Direct module", "## Content Unavailable"],
            lines.Where(line => line.StartsWith("# ") || line.StartsWith("## "))
                .Where(line => line is not ("# Attribution" or "# Download Summary")));
        Assert.Contains("### Introduction", lines);
        Assert.Contains("#### Lesson details", lines);
        Assert.Contains("Sample lesson text.", markdown);
        Assert.Contains($"<{moduleUrl}>", markdown);
        Assert.Contains("<https://learn.microsoft.com/en-us/training/modules/direct-module/>", markdown);
        Assert.Equal(["- Online introduction", "- Online summary"], lines.Where(line => line.StartsWith("- Online ")));
        Assert.Contains("**2** units indexed (online only)", markdown);
        Assert.Contains("**1/3** modules downloaded", markdown);
    }

    [Theory]
    [InlineData("""{"courses":[]}""", "Course not found")]
    [InlineData("""{"courses":[{"uid":"course.ab-731t00","study_guide":[]}]}""", "No self-paced training modules")]
    public async Task DownloadCourseAsync_CannotExportCourse_ExplainsWhy(string response, string expectedMessage)
    {
        using var http = new HttpClient(new StubHandler(new Dictionary<string, string>
        {
            ["https://learn.microsoft.com/api/catalog/?uid=course.ab-731t00&type=courses"] = response,
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateDownloader(http).DownloadCourseAsync("ab-731t00", Path.GetTempPath()));
        Assert.Contains(expectedMessage, error.Message);
        Assert.Contains("ab-731t00", error.Message);
    }

    private static ContentDownloader CreateDownloader(HttpClient http)
    {
        var catalog = new LearnCatalogClient(http);
        var github = new GitHubRawClient(http);
        return new ContentDownloader(github, new ModuleResolver(catalog, github), catalog, new DfmConverter());
    }

    private sealed class StubHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
