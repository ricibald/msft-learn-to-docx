using MsftLearnToDocx.Models;

namespace MsftLearnToDocx.Tests;

[Trait("Category", "Unit")]
public class TocEntryTests
{
    [Fact]
    public void EffectiveName_ReturnsDisplayNameWhenSet()
    {
        var entry = new TocEntry { Name = "Original", DisplayName = "Custom Display" };
        Assert.Equal("Custom Display", entry.EffectiveName);
    }

    [Fact]
    public void EffectiveName_ReturnsNameWhenNoDisplayName()
    {
        var entry = new TocEntry { Name = "Original" };
        Assert.Equal("Original", entry.EffectiveName);
    }

    [Fact]
    public void EffectiveName_ReturnsNameWhenDisplayNameEmpty()
    {
        var entry = new TocEntry { Name = "Original", DisplayName = "" };
        Assert.Equal("Original", entry.EffectiveName);
    }
}
