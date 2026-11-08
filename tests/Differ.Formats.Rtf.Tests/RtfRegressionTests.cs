using Differ.Core.Models;
using Differ.Formats.Text;

namespace Differ.Formats.Rtf.Tests;

public sealed class RtfRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FieldResultsParticipateWithoutEvaluatingInstructionsAsync()
    {
        var result = await CompareAsync(
            @"{\rtf1\ansi Invoice: {\field{\*\fldinst FORMULA 1+1}{\fldrslt 100}}\par}",
            @"{\rtf1\ansi Invoice: {\field{\*\fldinst FORMULA 1+1}{\fldrslt 200}}\par}");
        Assert.True(result.Summary.Changed > 0);
        Assert.Contains(result.Nodes, node => node.OldValue == "Invoice: 100" && node.NewValue == "Invoice: 200");
        Assert.DoesNotContain(result.Nodes, node => node.NewValue?.Contains("FORMULA", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task FieldInstructionChangesAloneDoNotChangeVisibleTextAsync()
    {
        var result = await CompareAsync(
            @"{\rtf1{\field{\*\fldinst HYPERLINK old}{\fldrslt visible}}}",
            @"{\rtf1{\field{\*\fldinst HYPERLINK new}{\fldrslt visible}}}");
        Assert.Equal(0, result.Summary.Changed);
        Assert.Equal("visible", Assert.Single(result.Nodes).NewValue);
    }

    [Theory]
    [InlineData(@"{\rtf1\uc1 \u233e}", "é")]
    [InlineData(@"{\rtf1\uc1 \u233\'65}", "é")]
    [InlineData(@"{\rtf1\uc0 \u233?}", "é?")]
    [InlineData(@"{\rtf1\uc2 \u20320\'c4\'e3!}", "你!")]
    [InlineData(@"{\rtf1\uc1 \u233?{\uc0 \u233!}\u233e}", "éé!é")]
    [InlineData(@"{\rtf1\uc1 \u233\{!}", "é!")]
    [InlineData(@"{\rtf1\uc1 \u-10179?\u-8704?}", "😀")]
    public async Task UnicodeFallbackRespectsCharactersAndGroupScopeAsync(string rtf, string expected)
    {
        var result = await CompareAsync(rtf, rtf);
        Assert.Equal(expected, Assert.Single(result.Nodes).NewValue);
        Assert.Equal(0, result.Summary.Changed);
    }

    [Fact]
    public async Task EquivalentUnicodeFallbackDoesNotProduceFalseDifferenceAsync()
    {
        var result = await CompareAsync(@"{\rtf1\uc1 \u233e}", @"{\rtf1\uc1 \u233?}");
        Assert.Equal(0, result.Summary.Changed);
    }

    [Theory]
    [InlineData(@"{\rtf1 text")]
    [InlineData(@"{\rtf1\u999999999999999999999?}")]
    [InlineData(@"{\rtf1\'ff}")]
    public async Task InvalidRtfNeverBecomesAnAccurateEmptyDocumentAsync(string content)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => CompareAsync(content, content));
    }

    private async Task<DiffDocument> CompareAsync(string before, string after)
    {
        Directory.CreateDirectory(_root);
        var oldPath = Path.Combine(_root, "old.rtf");
        var newPath = Path.Combine(_root, "new.rtf");
        await File.WriteAllTextAsync(oldPath, before);
        await File.WriteAllTextAsync(newPath, after);
        return await new RtfDiffAdapter(new TextFileReader()).CompareAsync(new DiffRequest
        { RelativePath = "document.rtf", BaseFilePath = oldPath, CurrentFilePath = newPath }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
