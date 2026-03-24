using MsftLearnToDocx.Models;
using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

public class DocsUrlParserTests
{
    // --- Learn training URLs ---

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/training/paths/copilot/", "paths", "copilot")]
    [InlineData("https://learn.microsoft.com/training/modules/introduction-to-github-copilot/", "modules", "introduction-to-github-copilot")]
    [InlineData("https://learn.microsoft.com/fr-fr/training/paths/some-path/", "paths", "some-path")]
    public void Parse_LearnTrainingUrl_ExtractsTypeAndSlug(string url, string expectedType, string expectedSlug)
    {
        var result = DocsUrlParser.Parse(url);
        var training = Assert.IsType<LearnTrainingUrl>(result);
        Assert.Equal(expectedType, training.Type);
        Assert.Equal(expectedSlug, training.Slug);
    }

    // --- VS Code docs URLs ---

    [Fact]
    public void Parse_VsCodeDocsUrl_MapsToVscodeDocsRepo()
    {
        var result = DocsUrlParser.Parse("https://code.visualstudio.com/docs/copilot/getting-started");
        var docs = Assert.IsType<DocsSiteUrl>(result);
        Assert.Equal("microsoft", docs.RepoInfo.Owner);
        Assert.Equal("vscode-docs", docs.RepoInfo.Repo);
        Assert.Equal("main", docs.RepoInfo.Branch);
        Assert.Equal("docs", docs.RepoInfo.DocsBasePath);
        Assert.Equal("copilot/getting-started", docs.RepoInfo.ContentPath);
        Assert.True(docs.RepoInfo.UsesLfs);
        Assert.Equal("docs/copilot/getting-started", docs.RepoInfo.RepoContentPath);
    }

    [Fact]
    public void Parse_VsCodeDocsDirectory_PreservesTrailingPath()
    {
        var result = DocsUrlParser.Parse("https://code.visualstudio.com/docs/copilot/");
        var docs = Assert.IsType<DocsSiteUrl>(result);
        Assert.Equal("copilot", docs.RepoInfo.ContentPath);
        Assert.Equal("docs/copilot", docs.RepoInfo.RepoContentPath);
    }

    // --- Azure DevOps docs URLs ---

    [Fact]
    public void Parse_AzureDevOpsUrl_MapsCorrectly()
    {
        var result = DocsUrlParser.Parse("https://learn.microsoft.com/en-us/azure/devops/repos/get-started");
        var docs = Assert.IsType<DocsSiteUrl>(result);
        Assert.Equal("MicrosoftDocs", docs.RepoInfo.Owner);
        Assert.Equal("azure-devops-docs", docs.RepoInfo.Repo);
        Assert.Equal("docs", docs.RepoInfo.DocsBasePath);
        Assert.Equal("repos/get-started", docs.RepoInfo.ContentPath);
        Assert.False(docs.RepoInfo.UsesLfs);
    }

    [Fact]
    public void Parse_AzureDevOpsUrl_StripsLocale()
    {
        var result1 = DocsUrlParser.Parse("https://learn.microsoft.com/en-us/azure/devops/repos/");
        var result2 = DocsUrlParser.Parse("https://learn.microsoft.com/azure/devops/repos/");

        var docs1 = Assert.IsType<DocsSiteUrl>(result1);
        var docs2 = Assert.IsType<DocsSiteUrl>(result2);
        Assert.Equal(docs1.RepoInfo.ContentPath, docs2.RepoInfo.ContentPath);
    }

    // --- .NET docs ---

    [Fact]
    public void Parse_DotnetDocsUrl_MapsCorrectly()
    {
        var result = DocsUrlParser.Parse("https://learn.microsoft.com/en-us/dotnet/core/introduction");
        var docs = Assert.IsType<DocsSiteUrl>(result);
        Assert.Equal("dotnet", docs.RepoInfo.Owner);
        Assert.Equal("docs", docs.RepoInfo.Repo);
        Assert.Equal("core/introduction", docs.RepoInfo.ContentPath);
    }

    // --- Azure docs ---

    [Fact]
    public void Parse_AzureDocsUrl_MapsToAzureDocs()
    {
        var result = DocsUrlParser.Parse("https://learn.microsoft.com/en-us/azure/functions/overview");
        var docs = Assert.IsType<DocsSiteUrl>(result);
        Assert.Equal("MicrosoftDocs", docs.RepoInfo.Owner);
        Assert.Equal("azure-docs", docs.RepoInfo.Repo);
        Assert.Equal("articles", docs.RepoInfo.DocsBasePath);
        Assert.Equal("functions/overview", docs.RepoInfo.ContentPath);
    }

    // --- Azure DevOps takes precedence over Azure ---

    [Fact]
    public void Parse_AzureDevOps_TakesPrecedenceOverAzure()
    {
        var result = DocsUrlParser.Parse("https://learn.microsoft.com/en-us/azure/devops/pipelines/");
        var docs = Assert.IsType<DocsSiteUrl>(result);
        Assert.Equal("azure-devops-docs", docs.RepoInfo.Repo);
    }

    // --- DocsRepoInfo computed properties ---

    [Fact]
    public void DocsRepoInfo_FullRepo_CombinesOwnerAndRepo()
    {
        var info = new DocsRepoInfo("microsoft", "vscode-docs", "main", "docs", "copilot");
        Assert.Equal("microsoft/vscode-docs", info.FullRepo);
    }

    [Fact]
    public void DocsRepoInfo_RepoContentPath_CombinesDocsBaseAndContentPath()
    {
        var info = new DocsRepoInfo("microsoft", "vscode-docs", "main", "docs", "copilot/agents");
        Assert.Equal("docs/copilot/agents", info.RepoContentPath);
    }

    [Fact]
    public void DocsRepoInfo_RepoContentPath_EmptyContentPath()
    {
        var info = new DocsRepoInfo("microsoft", "vscode-docs", "main", "docs", "");
        Assert.Equal("docs", info.RepoContentPath);
    }

    // --- Error cases ---

    [Fact]
    public void Parse_UnknownUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DocsUrlParser.Parse("https://example.com/unknown/path"));
    }

    [Fact]
    public void Parse_InvalidUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DocsUrlParser.Parse("not-a-url"));
    }
}
