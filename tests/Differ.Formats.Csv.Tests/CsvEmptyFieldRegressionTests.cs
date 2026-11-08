using Differ.Core.Algorithms;
using Differ.Core.Models;
using Differ.Core.Security;
using Differ.Formats.Text;

namespace Differ.Formats.Csv.Tests;

public sealed class CsvEmptyFieldRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddedTrailingEmptyFieldsAreRealColumnsAsync()
    {
        var result = await CompareAsync("id,value\r\n1,2\r\n", "id,value,\r\n1,2,\r\n");
        Assert.Equal(3, Assert.Single(result.TabularSections).Columns.Count);
        Assert.True(result.Summary.Changed > 0);
        Assert.Equal(2, result.Summary.Added);
    }

    [Theory]
    [InlineData(",\r\n")]
    [InlineData("\r\n")]
    [InlineData("\"\"\r\n")]
    public async Task AddedEmptyRecordRemainsVisibleAsync(string record)
    {
        var result = await CompareAsync("1,2\r\n", "1,2\r\n" + record);
        var sheet = Assert.Single(result.TabularSections);
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal(DiffKind.Added, sheet.Rows[1].Kind);
        Assert.True(result.Summary.Changed > 0);
    }

    [Fact]
    public async Task AllEmptySheetPreservesBothDimensionsAsync()
    {
        var result = await CompareAsync(",,\n,,\n", ",,\n,,\n");
        var sheet = Assert.Single(result.TabularSections);
        Assert.Equal(3, sheet.Columns.Count);
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal(0, result.Summary.Changed);
    }

    [Fact]
    public async Task EmptyRecordInsertionDoesNotModifyFollowingRecordsAsync()
    {
        var result = await CompareAsync("A,1\nB,2\n", "A,1\n,\nB,2\n");
        var sheet = Assert.Single(result.TabularSections);
        Assert.Equal(1, sheet.Rows.Count(row => row.Kind == DiffKind.Added));
        Assert.Equal(2, sheet.Rows.Count(row => row.Kind == DiffKind.Unchanged));
        Assert.DoesNotContain(sheet.Rows, row => row.Kind == DiffKind.Modified);
    }

    [Fact]
    public async Task RecordTerminatingNewlineDoesNotCreateEmptyRecordAsync()
    {
        var result = await CompareAsync("A,1", "A,1\r\n");
        Assert.Equal(0, result.Summary.Changed);
        Assert.Single(Assert.Single(result.TabularSections).Rows);
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("un\"quoted")]
    [InlineData("\"quoted\"tail")]
    public async Task MalformedCsvIsNotSilentlyNormalizedAsync(string content)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => CompareAsync(content, content));
    }

    [Fact]
    public async Task ExcessiveEmptyColumnsStopBeforeGridAllocationAsync()
    {
        var tooWide = new string(',', DiffPerformanceOptions.MaximumTabularColumns);
        await Assert.ThrowsAsync<ComparisonResourceLimitException>(() => CompareAsync(tooWide, tooWide));
    }

    private async Task<DiffDocument> CompareAsync(string before, string after)
    {
        Directory.CreateDirectory(_root);
        var oldPath = Path.Combine(_root, "old.csv");
        var newPath = Path.Combine(_root, "new.csv");
        await File.WriteAllTextAsync(oldPath, before);
        await File.WriteAllTextAsync(newPath, after);
        return await new CsvDiffAdapter(new TextFileReader()).CompareAsync(new DiffRequest
        { RelativePath = "data.csv", BaseFilePath = oldPath, CurrentFilePath = newPath }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
