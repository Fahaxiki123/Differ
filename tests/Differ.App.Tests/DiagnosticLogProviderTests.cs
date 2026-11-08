using System.IO;
using System.Text.Json;
using Differ.App.Services;
using Microsoft.Extensions.Logging;

namespace Differ.App.Tests;

public sealed class DiagnosticLogProviderTests
{
    [Fact]
    public async Task OnlyControlledMetadataIsPersistedAndFormatterIsNeverCalledAsync()
    {
        var root = CreateRoot();
        try
        {
            using var provider = new DiagnosticLogProvider(root);
            var logger = provider.CreateLogger(@"E:\secret\sensitive-document.docx");
            logger.Log(
                LogLevel.Error,
                new EventId(1234, "PRIVATE_EVENT_NAME"),
                "PRIVATE_DOCUMENT_CONTENT",
                new InvalidDataException(@"E:\secret\document.docx PRIVATE_EXCEPTION_MESSAGE"),
                (_, _) => throw new InvalidOperationException("The formatter must never be called."));
            await provider.CompleteAsync();

            var lines = await File.ReadAllLinesAsync(Path.Combine(root, "diagnostics.log"));
            using var document = JsonDocument.Parse(Assert.Single(lines));
            var entry = document.RootElement;
            Assert.Equal(1234, entry.GetProperty("EventId").GetInt32());
            Assert.Equal("Other", entry.GetProperty("Category").GetString());
            Assert.Equal("Error", entry.GetProperty("Level").GetString());
            Assert.Equal("InvalidDataException", entry.GetProperty("ExceptionType").GetString());
            Assert.Equal(5, entry.EnumerateObject().Count());
            Assert.DoesNotContain("PRIVATE", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("secret", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RotationBoundsTotalFilesAndIndividualFileSizesAsync()
    {
        var root = CreateRoot();
        try
        {
            using var provider = new DiagnosticLogProvider(root, maximumBytes: 512, retainedFiles: 3);
            var logger = provider.CreateLogger("Differ.App.App");
            for (var index = 0; index < 60; index++)
            {
                logger.Log(LogLevel.Information, new EventId(index), "ignored", null, (_, _) => "ignored");
            }

            await provider.CompleteAsync();

            var files = Directory.GetFiles(root, "diagnostics.log*");
            Assert.Equal(3, files.Length);
            Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 512));
            var current = await File.ReadAllTextAsync(Path.Combine(root, "diagnostics.log"));
            Assert.Contains("\"EventId\":59", current, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnwritableLogLocationDoesNotInterruptComparisonAsync()
    {
        var root = CreateRoot();
        try
        {
            var blockedPath = Path.Combine(root, "file-instead-of-directory");
            await File.WriteAllTextAsync(blockedPath, "unchanged");
            using var provider = new DiagnosticLogProvider(blockedPath);
            provider.CreateLogger("Differ.App.App").Log(
                LogLevel.Error, new EventId(1), "ignored", null, (_, _) => "ignored");

            await provider.CompleteAsync();

            Assert.Equal("unchanged", await File.ReadAllTextAsync(blockedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
