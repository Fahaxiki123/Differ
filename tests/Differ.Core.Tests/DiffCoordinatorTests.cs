using System.Xml;
using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Core.Security;
using Differ.Core.Services;

namespace Differ.Core.Tests;

public sealed class DiffCoordinatorTests
{
    [Fact]
    public async Task TruncatedZipDirectoryWithInternalSeekFailureStillFallsBackAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var broken = Path.Combine(root, "truncated.xlsx");
            await File.WriteAllBytesAsync(broken, [0x50, 0x4B, 0x03, 0x04, 0x41, 0x42, 0x43, 0x00]);
            var error = Assert.Throws<StructuralParseException>(() => OfficePackageGuard.Validate(broken));
            Assert.IsType<InvalidDataException>(error.InnerException);
            Assert.IsType<IOException>(error.InnerException.InnerException);
            var structural = new CapturingAdapter(FileKind.Excel, _ => true, error);
            var binary = new CapturingAdapter(FileKind.Binary, _ => true);

            var result = await new DiffCoordinator([structural, binary]).CompareAsync(CreateRequest());

            Assert.True(binary.WasCalled);
            Assert.Equal(FileKind.Binary, result.FileKind);
            Assert.Contains(result.Warnings, warning => warning.Contains("字节级", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContentDetectionFallbackPreservesEncodingAndBinaryWarningsWithoutDuplicatesAsync()
    {
        var text = new CapturingAdapter(
            FileKind.Text,
            _ => true,
            warnings: ["encoding unavailable", "shared warning"],
            resultKind: FileKind.Binary);
        var binary = new CapturingAdapter(
            FileKind.Binary,
            _ => true,
            warnings: ["shared warning", "byte comparison warning"]);

        var result = await new DiffCoordinator([text, binary]).CompareAsync(CreateRequest());

        Assert.Equal(FileKind.Binary, result.FileKind);
        Assert.Equal(
            ["encoding unavailable", "shared warning", "byte comparison warning"],
            result.Warnings);
    }

    [Fact]
    public async Task KnownStructureFailuresFallBackWithAnExplicitWarningAsync()
    {
        Exception[] errors =
        [
            new InvalidDataException("broken package"),
            new XmlException("broken XML"),
            new FormatException("invalid cell encoding"),
            new StructuralParseException("unsupported document structure")
        ];
        foreach (var error in errors)
        {
            var structural = new CapturingAdapter(FileKind.Word, _ => true, error);
            var binary = new CapturingAdapter(FileKind.Binary, _ => true);
            var coordinator = new DiffCoordinator([structural, binary]);

            var result = await coordinator.CompareAsync(CreateRequest());

            Assert.True(binary.WasCalled);
            Assert.Equal(FileKind.Binary, result.FileKind);
            Assert.Contains(result.Warnings, warning => warning.Contains("字节级", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AccessCancellationResourceAndUnexpectedFailuresNeverFallBackAsync()
    {
        Exception[] errors =
        [
            new OperationCanceledException(),
            new FileNotFoundException(),
            new DirectoryNotFoundException(),
            new UnauthorizedAccessException(),
            new IOException("sharing violation"),
            new ComparisonResourceLimitException("budget exceeded"),
            new InvalidOperationException("adapter bug"),
            new StructuralParseException("wrapped access failure", new UnauthorizedAccessException()),
            new StructuralParseException("wrapped sharing violation", new IOException()),
            new InvalidDataException("wrapped cancellation", new OperationCanceledException()),
            new StructuralParseException("wrapped budget", new ComparisonResourceLimitException())
        ];
        foreach (var error in errors)
        {
            var structural = new CapturingAdapter(FileKind.Word, _ => true, error);
            var binary = new CapturingAdapter(FileKind.Binary, _ => true);
            var coordinator = new DiffCoordinator([structural, binary]);

            var actual = await Assert.ThrowsAsync(
                error.GetType(), () => coordinator.CompareAsync(CreateRequest()));

            Assert.Same(error, actual);
            Assert.False(binary.WasCalled);
        }
    }

    [Fact]
    public async Task BinaryFallbackStillEnforcesItsOwnSafetyLimitsAsync()
    {
        var structural = new CapturingAdapter(FileKind.Word, _ => true, new InvalidDataException());
        var binary = new CapturingAdapter(
            FileKind.Binary, _ => true, new ComparisonResourceLimitException());
        var coordinator = new DiffCoordinator([structural, binary]);

        await Assert.ThrowsAsync<ComparisonResourceLimitException>(
            () => coordinator.CompareAsync(CreateRequest()));
        Assert.True(binary.WasCalled);
    }

    [Fact]
    public async Task CancellationRequestedDuringParseFailurePreventsFallbackAsync()
    {
        var structural = new CapturingAdapter(FileKind.Word, _ => true, new InvalidDataException());
        var binary = new CapturingAdapter(FileKind.Binary, _ => true);
        var coordinator = new DiffCoordinator([structural, binary]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CompareAsync(CreateRequest(), cancellation.Token));
        Assert.False(binary.WasCalled);
    }

    [Fact]
    public async Task CompareUsesOriginalFileNameBeforeTemporaryInputExtensionAsync()
    {
        var excelAdapter = new CapturingAdapter(
            FileKind.Excel,
            path => string.Equals(
                Path.GetExtension(path),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase));
        var fallbackAdapter = new CapturingAdapter(FileKind.Text, _ => true);
        var coordinator = new DiffCoordinator([excelAdapter, fallbackAdapter]);

        var result = await coordinator.CompareAsync(new DiffRequest
        {
            RelativePath = "W-物品表.xlsx",
            BaseFilePath = @"C:\Temp\base.tmp",
            CurrentFilePath = @"C:\Temp\mine.tmp"
        });

        Assert.Equal(FileKind.Excel, result.FileKind);
        Assert.True(excelAdapter.WasCalled);
        Assert.False(fallbackAdapter.WasCalled);
    }

    private sealed class CapturingAdapter(
        FileKind fileKind,
        Func<string, bool> canHandle,
        Exception? error = null,
        IReadOnlyList<string>? warnings = null,
        FileKind? resultKind = null) : IFileDiffAdapter
    {
        public FileKind FileKind { get; } = fileKind;

        public bool WasCalled { get; private set; }

        public bool CanHandle(string path)
        {
            return canHandle(path);
        }

        public Task<DiffDocument> CompareAsync(
            DiffRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            if (error is not null)
            {
                return Task.FromException<DiffDocument>(error);
            }

            return Task.FromResult(new DiffDocument
            {
                RelativePath = request.RelativePath,
                FileKind = resultKind ?? FileKind,
                Nodes = [],
                Summary = DiffSummary.FromNodes([]),
                Warnings = warnings ?? []
            });
        }
    }

    private static DiffRequest CreateRequest()
    {
        return new DiffRequest
        {
            RelativePath = "test.docx",
            BaseFilePath = "base.docx",
            CurrentFilePath = "mine.docx"
        };
    }
}
