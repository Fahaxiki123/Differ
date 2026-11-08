using Differ.Core.Models;

namespace Differ.Formats.Word.Tests;

public sealed class DocDiffAdapterTests
{
    [Fact]
    public void CanHandleMatchesOnlyLegacyDocExtension()
    {
        var adapter = new DocDiffAdapter();

        Assert.True(adapter.CanHandle("report.DOC"));
        Assert.False(adapter.CanHandle("report.docx"));
        Assert.False(adapter.CanHandle("report.txt"));
    }

    [Fact]
    public async Task CompareIdenticalFilesReportsNoChangesWithScopeWarningAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var path = CopyFixture(testRoot, "SampleDoc.base.doc", "same.doc");
            var result = await CompareAsync(path, path);

            Assert.Equal(FileKind.Word, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
            Assert.Contains(result.Warnings, warning => warning.Contains(".doc", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareDetectsParagraphChangeAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var basePath = CopyFixture(testRoot, "SampleDoc.base.doc", "base.doc");
            var minePath = CopyFixture(testRoot, "SampleDoc.modified.doc", "working.doc");
            var result = await CompareAsync(basePath, minePath);

            Assert.Equal(1, result.Summary.Modified);
            Assert.Equal(0, result.Summary.Added);
            Assert.Equal(0, result.Summary.Deleted);
            var node = Assert.Single(
                result.Nodes,
                node => node.Kind == DiffKind.Modified);
            Assert.Equal("正文/段落 2", node.Path);
            Assert.Equal("This is page 1", node.OldValue);
            Assert.Equal("Revised page 1", node.NewValue);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static Task<DiffDocument> CompareAsync(string basePath, string minePath)
    {
        return new DocDiffAdapter().CompareAsync(
            new DiffRequest
            {
                RelativePath = "sample.doc",
                BaseFilePath = basePath,
                CurrentFilePath = minePath
            },
            CancellationToken.None);
    }

    private static string CopyFixture(string testRoot, string fixtureName, string targetName)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            fixtureName);
        Assert.True(File.Exists(fixturePath), $"缺少测试夹具：{fixturePath}");
        var targetPath = Path.Combine(testRoot, targetName);
        File.Copy(fixturePath, targetPath);
        return targetPath;
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
