using System.IO;
using System.Net;
using Differ.App.Services;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class ComparisonExportServiceTests
{
    [Fact]
    public async Task StructuralSpreadsheetEventsExportFullMappedCellDetailsAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "report.html");
            var document = CreateDocument("列新增", "B") with
            {
                FileKind = FileKind.Excel,
                TabularSections = [new TabularDiffSection
                {
                    Key = "配置", Columns = ["B"], Rows = [new TabularDiffRow
                    {
                        StableId = "row", Kind = DiffKind.Modified, OldIndex = 1, NewIndex = 1,
                        Cells = [new TabularDiffCell { ColumnKey = "logical", NewAddress = "B1", NewValue = "新列完整内容<safe>", Kind = DiffKind.Added }]
                    }]
                }]
            };
            await ComparisonExportService.ExportHtmlAsync(document, "结构明细", path, []);
            var html = await File.ReadAllTextAsync(path);
            Assert.Contains("新列完整内容<safe>", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            Assert.Contains("B1", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<safe>", html, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HtmlEncodesAllDocumentContentAndHasNoActiveOrExternalResourcesAsync()
    {
        var root = CreateRoot();
        try
        {
            const string malicious = "<script>alert('secret')</script>";
            var document = CreateDocument(malicious, "</pre><img src='https://example.invalid/exfil'>");
            var path = Path.Combine(root, "report.html");
            await ComparisonExportService.ExportHtmlAsync(document, malicious, path, []);
            var html = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
            Assert.Contains(malicious, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SourcesAndSnapshotsCannotBeOverwrittenIncludingCanonicalPathAliasesAsync()
    {
        var root = CreateRoot();
        try
        {
            var source = Path.Combine(root, "source.txt");
            var snapshot = Path.Combine(root, "snapshot.txt");
            await File.WriteAllTextAsync(source, "original source");
            await File.WriteAllTextAsync(snapshot, "original snapshot");
            foreach (var path in new[] { source, snapshot, Path.Combine(root, ".", "source.txt") })
            {
                await Assert.ThrowsAsync<IOException>(() => ComparisonExportService.ExportHtmlAsync(
                    CreateDocument("old", "new"), "report", path, [source, snapshot]));
            }
            Assert.Equal("original source", await File.ReadAllTextAsync(source));
            Assert.Equal("original snapshot", await File.ReadAllTextAsync(snapshot));
            Assert.Equal(2, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CanceledExportPreservesExistingReportAndTextExportContainsFullValuesAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "report.txt");
            await File.WriteAllTextAsync(path, "existing report");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComparisonExportService.ExportTextAsync(
                "A1", "old", "new", path, [], cancellation.Token));
            Assert.Equal("existing report", await File.ReadAllTextAsync(path));
            await ComparisonExportService.ExportTextAsync("A1", "完整旧内容\n第二行", "完整新内容\n第二行", path, []);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("完整旧内容\n第二行", text, StringComparison.Ordinal);
            Assert.Contains("完整新内容\n第二行", text, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static DiffDocument CreateDocument(string oldValue, string newValue)
    {
        var nodes = new[] { new DiffNode
        {
            StableId = "1", Path = "<path>", Kind = DiffKind.Modified,
            OldValue = oldValue, NewValue = newValue
        } };
        return new DiffDocument
        {
            RelativePath = "test",
            FileKind = FileKind.Text,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Warnings = ["<unsafe warning>"]
        };
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
