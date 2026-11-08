using System.Text;
using Differ.Core.Algorithms;
using Differ.Core.Models;
using Differ.Core.Security;

namespace Differ.Formats.Text.Tests;

public sealed class TextOptionsTests
{
    [Fact]
    public async Task IgnoreOptionsPreserveOriginalSideContentAsync()
    {
        var result = await CompareAsync("Alpha   value\r\n", "alpha value\n", new DiffOptions
        {
            IgnoreCase = true,
            IgnoreWhitespace = true,
            IgnoreLineEndings = true
        });
        Assert.Equal(0, result.Summary.Changed);
        Assert.Equal("Alpha   value", Assert.Single(result.Nodes).OldValue);
        Assert.Equal("alpha value", Assert.Single(result.Nodes).NewValue);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task TerminalNewlineIsMetadataAndCanBeIgnoredAsync()
    {
        var result = await CompareAsync("one\n", "one", new DiffOptions());
        Assert.Equal(0, result.Summary.Changed);
        Assert.Contains(result.Warnings, warning => warning.Contains("末尾换行", StringComparison.Ordinal));
        var ignored = await CompareAsync("one\n", "one", new DiffOptions { IgnoreLineEndings = true });
        Assert.Empty(ignored.Warnings);
    }

    [Fact]
    public async Task ExplicitEncodingOverridesAutomaticProbeAsync()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [0xC2, 0xA3]);
            var reader = new TextFileReader();
            var automatic = await reader.ReadAsync(path);
            var explicitContent = await reader.ReadWithEncodingAsync(path, "gb18030");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Assert.Equal("£", automatic.Text);
            Assert.Equal(Encoding.GetEncoding("gb18030").GetString([0xC2, 0xA3]), explicitContent.Text);
            Assert.NotEqual(automatic.Text, explicitContent.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OversizeTextFailsBeforeReadingItsContentsAsync()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = File.OpenWrite(path))
            {
                stream.SetLength(DiffPerformanceOptions.MaximumTextFileBytes + 1);
            }

            await Assert.ThrowsAsync<ComparisonResourceLimitException>(() => new TextFileReader().ReadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<DiffDocument> CompareAsync(string oldText, string newText, DiffOptions options)
    {
        var oldPath = Path.GetTempFileName();
        var newPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(oldPath, oldText);
            await File.WriteAllTextAsync(newPath, newText);
            return await new TextDiffAdapter(new TextFileReader()).CompareAsync(new DiffRequest
            {
                RelativePath = "sample.txt",
                BaseFilePath = oldPath,
                CurrentFilePath = newPath,
                Options = options
            }, CancellationToken.None);
        }
        finally
        {
            File.Delete(oldPath);
            File.Delete(newPath);
        }
    }
}
