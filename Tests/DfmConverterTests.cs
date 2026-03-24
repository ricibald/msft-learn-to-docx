using MsftLearnToDocx.Services;

namespace MsftLearnToDocx.Tests;

public class DfmConverterTests
{
    private readonly DfmConverter _converter = new();

    // --- :::image::: conversion ---

    [Fact]
    public void Convert_ImageWithAltText_ConvertsToMarkdownImage()
    {
        var input = """:::image source="../media/screenshot.png" alt-text="Screenshot of the dashboard":::""";
        var result = _converter.Convert(input);
        Assert.Equal("![Screenshot of the dashboard](../media/screenshot.png)", result.Trim());
    }

    [Fact]
    public void Convert_ImageWithPathMapper_RemapsPath()
    {
        var input = """:::image source="../media/img.png" alt-text="Test":::""";
        var result = _converter.Convert(input, mediaPathMapper: p => "media/img.png");
        Assert.Equal("![Test](media/img.png)", result.Trim());
    }

    [Fact]
    public void Convert_ImageFromImagesDir_PathMapperReceivesOriginalDir()
    {
        var input = """:::image type="content" source="../images/load-balancer-types.png" alt-text="LB types" border="false":::""";
        var receivedPaths = new List<string>();
        var result = _converter.Convert(input, mediaPathMapper: p =>
        {
            receivedPaths.Add(p);
            return $"media/M1_{Path.GetFileName(p)}";
        });
        Assert.Single(receivedPaths);
        Assert.Equal("../images/load-balancer-types.png", receivedPaths[0]);
        Assert.Contains("media/M1_load-balancer-types.png", result);
    }

    [Fact]
    public void Convert_ImageWithoutSource_RemovesBlock()
    {
        var input = """:::image alt-text="no source":::""";
        var result = _converter.Convert(input);
        Assert.Equal("", result.Trim());
    }

    // --- Alert conversion ---

    [Theory]
    [InlineData("NOTE")]
    [InlineData("TIP")]
    [InlineData("WARNING")]
    [InlineData("IMPORTANT")]
    [InlineData("CAUTION")]
    public void Convert_Alerts_ConvertsToBlockquoteLabel(string alertType)
    {
        var input = $"> [!{alertType}]\n> Alert content here";
        var result = _converter.Convert(input);
        var expected = alertType[0] + alertType[1..].ToLowerInvariant();
        Assert.Contains($"> **{expected}:**", result);
        Assert.Contains("> Alert content here", result);
    }

    // --- [!div] removal ---

    [Fact]
    public void Convert_DivBlock_RemovesDivLine()
    {
        var input = "> [!div class=\"nextstepaction\"]\n> [Next step](url)";
        var result = _converter.Convert(input);
        Assert.DoesNotContain("[!div", result);
        Assert.Contains("> [Next step](url)", result);
    }

    // --- :::zone::: removal ---

    [Fact]
    public void Convert_ZoneMarkers_RemovesStartAndEnd()
    {
        var input = ":::zone target=\"docs\":::\nContent here\n:::zone-end:::";
        var result = _converter.Convert(input);
        Assert.DoesNotContain(":::zone", result);
        Assert.Contains("Content here", result);
    }

    [Fact]
    public void Convert_ZoneMarkers_WithSpaces_RemovesStartAndEnd()
    {
        var input = "::: zone pivot=\"programming-language-csharp\" :::\nC# content\n::: zone-end :::";
        var result = _converter.Convert(input);
        Assert.DoesNotContain("::: zone", result);
        Assert.DoesNotContain("zone-end", result);
        Assert.Contains("C# content", result);
    }

    [Fact]
    public void Convert_ZonePivot_KeepsAllPivotContent()
    {
        var input = "::: zone pivot=\"windows\" :::\nWindows content\n::: zone-end :::\n::: zone pivot=\"linux\" :::\nLinux content\n::: zone-end :::";
        var result = _converter.Convert(input);
        Assert.Contains("Windows content", result);
        Assert.Contains("Linux content", result);
        Assert.DoesNotContain("zone pivot", result);
    }

    // --- [!VIDEO] conversion ---

    [Fact]
    public void Convert_Video_ConvertsToLink()
    {
        var input = "[!VIDEO https://www.youtube.com/embed/abc123]";
        var result = _converter.Convert(input);
        Assert.Equal("[Video](https://www.youtube.com/embed/abc123)", result.Trim());
    }

    // --- :::code::: conversion ---

    [Fact]
    public void Convert_CodeReference_WithContent_EmbedsCode()
    {
        var input = """:::code source="sample.cs" language="csharp":::""";
        var codeContents = new Dictionary<string, string> { ["sample.cs"] = "Console.WriteLine(\"Hello\");" };
        var result = _converter.Convert(input, codeContents: codeContents);
        Assert.Contains("```csharp", result);
        Assert.Contains("Console.WriteLine(\"Hello\");", result);
    }

    [Fact]
    public void Convert_CodeReference_WithRange_FiltersLines()
    {
        var input = """:::code source="file.txt" language="text" range="2-3":::""";
        var codeContents = new Dictionary<string, string>
        {
            ["file.txt"] = "line1\nline2\nline3\nline4"
        };
        var result = _converter.Convert(input, codeContents: codeContents);
        Assert.Contains("line2", result);
        Assert.Contains("line3", result);
        Assert.DoesNotContain("line1", result);
        Assert.DoesNotContain("line4", result);
    }

    [Fact]
    public void Convert_CodeReference_NoContent_ShowsSourceComment()
    {
        var input = """:::code source="missing.cs" language="csharp":::""";
        var result = _converter.Convert(input);
        Assert.Contains("// Source: missing.cs", result);
    }

    // --- [!INCLUDE] cleanup ---

    [Fact]
    public void Convert_IncludeRef_RemovesUnresolved()
    {
        var input = "Before [!INCLUDE [title](path/to/include.md)] After";
        var result = _converter.Convert(input);
        Assert.Equal("Before  After", result.Trim());
    }

    // --- EnsureBlankLineBeforeLists ---

    [Fact]
    public void Convert_ListWithoutBlankLine_InsertsBlankLine()
    {
        var input = "Some text\n- item 1\n- item 2";
        var result = _converter.Convert(input);
        Assert.Contains("Some text\n\n- item 1", result);
    }

    [Fact]
    public void Convert_ListWithBlankLine_PreservesAsIs()
    {
        var input = "Some text\n\n- item 1\n- item 2";
        var result = _converter.Convert(input);
        Assert.Contains("Some text\n\n- item 1", result);
    }

    [Fact]
    public void Convert_NumberedListWithoutBlankLine_InsertsBlankLine()
    {
        var input = "Some text\n1. first\n2. second";
        var result = _converter.Convert(input);
        Assert.Contains("Some text\n\n1. first", result);
    }

    // --- Trailing whitespace cleanup ---

    [Fact]
    public void Convert_ExcessiveBlankLines_ReducesToTwo()
    {
        var input = "line1\n\n\n\n\nline2";
        var result = _converter.Convert(input);
        Assert.Equal("line1\n\nline2", result.Trim());
    }

    // --- Horizontal rule blank line enforcement ---

    [Fact]
    public void Convert_HrFollowedByHeading_InsertsBlankLine()
    {
        var input = "Content\n\n---\n## See also\nMore content";
        var result = _converter.Convert(input);
        Assert.Contains("---\n\n## See also", result);
    }

    [Fact]
    public void Convert_HrPrecededByContent_InsertsBlankLine()
    {
        var input = "Some text\n---\n\nMore content";
        var result = _converter.Convert(input);
        Assert.Contains("Some text\n\n---", result);
    }

    [Fact]
    public void Convert_HrWithBlankLines_PreservesAsIs()
    {
        var input = "Content\n\n---\n\nMore content";
        var result = _converter.Convert(input);
        Assert.Contains("Content\n\n---\n\nMore content", result);
    }

    // --- Triple-colon block removal ---

    [Fact]
    public void Convert_RowColumnBlocks_Removed()
    {
        var input = ":::row:::\n:::column:::\nContent\n:::column-end:::\n:::row-end:::";
        var result = _converter.Convert(input);
        Assert.DoesNotContain(":::row", result);
        Assert.DoesNotContain(":::column", result);
        Assert.Contains("Content", result);
    }

    // --- ExtractMediaPaths ---

    [Fact]
    public void ExtractMediaPaths_FindsBothImageTypes()
    {
        var markdown = """
            :::image source="../media/img1.png" alt-text="one":::
            ![alt](media/img2.png)
            ![external](https://example.com/img.png)
            """;
        var paths = DfmConverter.ExtractMediaPaths(markdown);
        Assert.Contains("../media/img1.png", paths);
        Assert.Contains("media/img2.png", paths);
        Assert.DoesNotContain("https://example.com/img.png", paths);
    }

    [Fact]
    public void ExtractMediaPaths_StripsOptionalTitleFromImagePath()
    {
        var markdown = """![User settings](../media/generate-token.png "User settings")""";
        var paths = DfmConverter.ExtractMediaPaths(markdown);
        Assert.Single(paths);
        Assert.Equal("../media/generate-token.png", paths[0]);
    }

    // --- ExtractCodeSourcePaths ---

    [Fact]
    public void ExtractCodeSourcePaths_ExtractsSourceAndLanguage()
    {
        var markdown = """:::code source="src/app.cs" language="csharp" range="1-5":::""";
        var refs = DfmConverter.ExtractCodeSourcePaths(markdown);
        Assert.Single(refs);
        Assert.Equal("src/app.cs", refs[0].Source);
        Assert.Equal("csharp", refs[0].Language);
        Assert.Equal("1-5", refs[0].Range);
    }
}
