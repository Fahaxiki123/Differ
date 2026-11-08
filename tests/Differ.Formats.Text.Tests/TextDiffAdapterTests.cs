using Differ.Core.Models;

namespace Differ.Formats.Text.Tests;

public sealed class TextDiffAdapterTests
{
    [Fact]
    public async Task CompareReportsModifiedAndAddedLinesAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.cs");
            var newPath = Path.Combine(root, "new.cs");
            await File.WriteAllTextAsync(oldPath, "class Demo\n{\n}\n");
            await File.WriteAllTextAsync(
                newPath,
                "class Demo\n{\n    public int Value { get; set; }\n}\n");
            var adapter = new TextDiffAdapter(new TextFileReader());

            var result = await adapter.CompareAsync(
                new DiffRequest
                {
                    RelativePath = "Demo.cs",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    BaseRevision = "12"
                },
                CancellationToken.None);

            Assert.Equal(FileKind.Text, result.FileKind);
            Assert.True(result.Summary.Changed > 0);
            Assert.Contains(
                result.Nodes,
                node => node.Kind is DiffKind.Added or DiffKind.Modified);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompareReportsLineEndingChangeAsWarningAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.txt");
            var newPath = Path.Combine(root, "new.txt");
            await File.WriteAllTextAsync(oldPath, "one\r\ntwo\r\n");
            await File.WriteAllTextAsync(newPath, "one\ntwo\n");
            var adapter = new TextDiffAdapter(new TextFileReader());

            var result = await adapter.CompareAsync(
                new DiffRequest
                {
                    RelativePath = "sample.txt",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("行尾符", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample.csv", false)]
    [InlineData("sample.rtf", false)]
    [InlineData("sample.txt", true)]
    [InlineData("sample.cs", true)]
    public void CanHandleDefersStructuredFormatsToOtherAdapters(
        string path,
        bool expected)
    {
        Assert.Equal(expected, new TextDiffAdapter(new TextFileReader()).CanHandle(path));
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
