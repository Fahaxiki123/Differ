using System.Text;
using Differ.Core.Models;
using Differ.Formats.Text;

namespace Differ.Formats.Rtf.Tests;

public sealed class RtfDiffAdapterTests
{
    [Fact]
    public async Task ControlWordsAreStrippedAndUnicodeEscapesDecodedAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.rtf");
            var newPath = Path.Combine(root, "new.rtf");
            // \u20320 = 你, \u22909 = 好; \u19990 = 世, \u30028 = 界
            var rtf = @"{\rtf1\ansi{\fonttbl{\f0 SimSun;}}\uc1 "
                + @"\par \u20320?\u22909?\par \u19990?\u30028?\par }";
            await File.WriteAllTextAsync(oldPath, rtf, Encoding.ASCII);
            await File.WriteAllTextAsync(newPath, rtf, Encoding.ASCII);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(FileKind.Word, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
            Assert.Contains(
                result.Nodes,
                node => string.Equals(node.OldValue, "你好", StringComparison.Ordinal));
            Assert.Contains(
                result.Nodes,
                node => string.Equals(node.NewValue, "世界", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gb18030HexEscapesAreDecodedAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.rtf");
            var newPath = Path.Combine(root, "new.rtf");
            // \'c4\'e3\'ba\'c3 is GB18030/GBK for 你好.
            var rtf = @"{\rtf1\ansi\par \'c4\'e3\'ba\'c3\par }";
            await File.WriteAllTextAsync(oldPath, rtf, Encoding.ASCII);
            await File.WriteAllTextAsync(newPath, rtf, Encoding.ASCII);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(0, result.Summary.Changed);
            Assert.Contains(
                result.Nodes,
                node => string.Equals(node.OldValue, "你好", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParagraphChangeIsDetectedAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.rtf");
            var newPath = Path.Combine(root, "new.rtf");
            var oldRtf = @"{\rtf1\ansi\par \u20320?\u22909?\par }";
            var newRtf = @"{\rtf1\ansi\par \u20320?\u22909?\u21464?\u21270?\par }";
            await File.WriteAllTextAsync(oldPath, oldRtf, Encoding.ASCII);
            await File.WriteAllTextAsync(newPath, newRtf, Encoding.ASCII);

            var result = await CompareAsync(oldPath, newPath);

            Assert.True(result.Summary.Changed > 0);
            Assert.Contains(
                result.Nodes,
                node => node.NewValue?.Contains("变化", StringComparison.Ordinal) == true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DestinationGroupsAreSkippedAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.rtf");
            var newPath = Path.Combine(root, "new.rtf");
            var rtf = @"{\rtf1\ansi{\*\themedata skipped bytes here}"
                + @"{\colortbl;\red0\green0\blue0;}\par visible\par }";
            await File.WriteAllTextAsync(oldPath, rtf, Encoding.ASCII);
            await File.WriteAllTextAsync(newPath, rtf, Encoding.ASCII);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(0, result.Summary.Changed);
            var node = Assert.Single(
                result.Nodes,
                candidate => candidate.Kind == DiffKind.Unchanged);
            Assert.Equal("visible", node.OldValue);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample.rtf", true)]
    [InlineData("sample.docx", false)]
    [InlineData("sample.txt", false)]
    public void CanHandleOnlyAcceptsRtf(string path, bool expected)
    {
        Assert.Equal(expected, new RtfDiffAdapter(new TextFileReader()).CanHandle(path));
    }

    private static async Task<DiffDocument> CompareAsync(string oldPath, string newPath)
    {
        return await new RtfDiffAdapter(new TextFileReader()).CompareAsync(
            new DiffRequest
            {
                RelativePath = Path.GetFileName(newPath),
                BaseFilePath = oldPath,
                CurrentFilePath = newPath
            },
            CancellationToken.None);
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Differ.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
