using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

public class GitHubRawClientTests
{
    [Theory]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:abc123\nsize 12345\n", true)]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:def456def456def456\nsize 999\n", true)]
    [InlineData("# Regular markdown content\nSome text here", false)]
    [InlineData("PNG binary content...", false)]
    public void IsLfsPointer_DetectsCorrectly(string content, bool expected)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        Assert.Equal(expected, GitHubRawClient.IsLfsPointer(bytes));
    }

    [Fact]
    public void IsLfsPointer_LargeFile_ReturnsFalse()
    {
        var bytes = new byte[2048];
        Assert.False(GitHubRawClient.IsLfsPointer(bytes));
    }

    [Fact]
    public void ParseLfsPointer_ValidPointer_ExtractsOidAndSize()
    {
        var pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393\nsize 12345\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(pointer);
        var result = GitHubRawClient.ParseLfsPointer(bytes);

        Assert.NotNull(result);
        Assert.Equal("4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393", result.Value.Oid);
        Assert.Equal(12345, result.Value.Size);
    }

    [Fact]
    public void ParseLfsPointer_InvalidPointer_ReturnsNull()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Not an LFS pointer");
        Assert.Null(GitHubRawClient.ParseLfsPointer(bytes));
    }
}
