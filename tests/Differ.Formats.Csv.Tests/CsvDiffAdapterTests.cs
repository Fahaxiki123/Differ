using Differ.Core.Models;
using Differ.Formats.Text;

namespace Differ.Formats.Csv.Tests;

public sealed class CsvDiffAdapterTests
{
    [Fact]
    public async Task QuotedFieldsWithEscapesAndEmbeddedNewlinesParseAsSingleCellsAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.csv");
            var newPath = Path.Combine(root, "new.csv");
            var csv = "名称,说明\n\"逗号,引号\"\"转义\"\"\",\"换行\n内容\",尾列\n";
            await File.WriteAllTextAsync(oldPath, csv);
            await File.WriteAllTextAsync(newPath, csv);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(FileKind.Excel, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
            var section = Assert.Single(result.TabularSections);
            Assert.Equal("CSV", section.Key);
            Assert.Equal(2, section.Rows.Count);
            Assert.Equal(3, section.Columns.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InsertedRowDoesNotAmplifyDifferencesAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.csv");
            var newPath = Path.Combine(root, "new.csv");
            var values = Enumerable.Range(1, 30)
                .Select(index => $"行{index},数值{index}")
                .ToList();
            await File.WriteAllTextAsync(oldPath, string.Join('\n', values));
            await File.WriteAllTextAsync(
                newPath,
                string.Join('\n', ["新增表头,列B", .. values]));

            var result = await CompareAsync(oldPath, newPath);

            // Summary counts are cell-level; the no-amplification guarantee is
            // asserted at row level instead: exactly one row is added and no
            // pre-existing row becomes modified or moved.
            var section = Assert.Single(result.TabularSections);
            Assert.Equal(
                1,
                section.Rows.Count(row => row.Kind == DiffKind.Added));
            Assert.DoesNotContain(
                section.Rows,
                row => row.Kind is DiffKind.Modified or DiffKind.Moved);
            Assert.Equal(
                values.Count,
                section.Rows.Count(row => row.Kind == DiffKind.Unchanged));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CellValueChangeIsDetectedAsync()
    {
        var root = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.csv");
            var newPath = Path.Combine(root, "new.csv");
            await File.WriteAllTextAsync(oldPath, "甲,100\n乙,200\n");
            await File.WriteAllTextAsync(newPath, "甲,100\n乙,999\n");

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(1, result.Summary.Modified);
            Assert.Contains(
                result.Nodes,
                node => string.Equals(node.NewValue, "999", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample.csv", true)]
    [InlineData("sample.txt", false)]
    [InlineData("sample.xlsx", false)]
    public void CanHandleOnlyAcceptsCsv(string path, bool expected)
    {
        Assert.Equal(expected, new CsvDiffAdapter(new TextFileReader()).CanHandle(path));
    }

    private static async Task<DiffDocument> CompareAsync(string oldPath, string newPath)
    {
        return await new CsvDiffAdapter(new TextFileReader()).CompareAsync(
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
