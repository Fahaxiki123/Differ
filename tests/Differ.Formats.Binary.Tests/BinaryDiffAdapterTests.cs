using Differ.Core.Abstractions;
using Differ.Core.Algorithms;
using Differ.Core.Models;
using Differ.Core.Services;
using Differ.Formats.Text;

namespace Differ.Formats.Binary.Tests;

public sealed class BinaryDiffAdapterTests
{
    [Fact]
    public async Task IdenticalFilesReportZeroChangesAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var content = CreateBytes(100, seed: 7);
            var oldPath = Path.Combine(testRoot, "base.bin");
            var newPath = Path.Combine(testRoot, "working.bin");
            await File.WriteAllBytesAsync(oldPath, content);
            await File.WriteAllBytesAsync(newPath, content);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(FileKind.Binary, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SingleByteChangeIsLocalizedToItsLineAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldContent = CreateBytes(64, seed: 11);
            var newContent = CreateBytes(64, seed: 11);
            newContent[35] = (byte)(newContent[35] ^ 0xFF);
            var oldPath = Path.Combine(testRoot, "base.bin");
            var newPath = Path.Combine(testRoot, "working.bin");
            await File.WriteAllBytesAsync(oldPath, oldContent);
            await File.WriteAllBytesAsync(newPath, newContent);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(1, result.Summary.Modified);
            Assert.Equal(1, result.Summary.Changed);
            Assert.Equal(3, result.Summary.Unchanged);
            var changed = Assert.Single(
                result.Nodes,
                node => node.Kind == DiffKind.Modified);
            Assert.Equal(32, changed.OldIndex);
            Assert.Equal(32, changed.NewIndex);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InsertedBlockKeepsFollowingLinesAlignedAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldContent = CreateBytes(48, seed: 13);
            var newContent = CreateBytes(16, seed: 99)
                .Concat(oldContent)
                .ToArray();
            var oldPath = Path.Combine(testRoot, "base.bin");
            var newPath = Path.Combine(testRoot, "working.bin");
            await File.WriteAllBytesAsync(oldPath, oldContent);
            await File.WriteAllBytesAsync(newPath, newContent);

            var result = await CompareAsync(oldPath, newPath);

            Assert.Equal(1, result.Summary.Added);
            Assert.Equal(0, result.Summary.Moved);
            Assert.Equal(3, result.Summary.Unchanged);
            Assert.Equal(1, result.Summary.Changed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedFileThrowsActionableErrorAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var size = (int)DiffPerformanceOptions.MaximumBinaryFileBytes + 1;
            var oldPath = Path.Combine(testRoot, "base.bin");
            var newPath = Path.Combine(testRoot, "working.bin");
            using (var stream = File.Create(oldPath))
            {
                stream.SetLength(size);
            }

            await File.WriteAllBytesAsync(newPath, [1, 2, 3]);

            var exception = await Assert.ThrowsAsync<IOException>(
                () => CompareAsync(oldPath, newPath));
            Assert.Contains("二进制文件过大", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorReroutesDetectedBinaryContentToBinaryAdapterAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            // Unknown extension plus NUL bytes: the text adapter detects
            // binary content and the coordinator must hand the request to
            // the byte-level fallback instead of refusing to compare.
            var oldContent = CreateBytes(40, seed: 21);
            var newContent = CreateBytes(40, seed: 21);
            newContent[5] = (byte)(newContent[5] + 1);
            var oldPath = Path.Combine(testRoot, "base.dat");
            var newPath = Path.Combine(testRoot, "working.dat");
            await File.WriteAllBytesAsync(oldPath, oldContent);
            await File.WriteAllBytesAsync(newPath, newContent);

            var coordinator = new DiffCoordinator(new IFileDiffAdapter[]
            {
                new TextDiffAdapter(new TextFileReader()),
                new BinaryDiffAdapter()
            });
            var result = await coordinator.CompareAsync(
                new DiffRequest
                {
                    RelativePath = "working.dat",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Equal(FileKind.Binary, result.FileKind);
            Assert.True(result.Summary.Changed > 0);
            Assert.Contains(
                result.Nodes,
                node => node.Kind == DiffKind.Modified
                    && node.OldValue?.Length > 0
                    && IsHexLine(node.OldValue));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<DiffDocument> CompareAsync(
        string oldPath,
        string newPath)
    {
        return await new BinaryDiffAdapter().CompareAsync(
            new DiffRequest
            {
                RelativePath = Path.GetFileName(newPath),
                BaseFilePath = oldPath,
                CurrentFilePath = newPath
            },
            CancellationToken.None);
    }

    private static byte[] CreateBytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * seed) % 251);
        }

        return bytes;
    }

    private static bool IsHexLine(string value)
    {
        return value.Split(' ').All(token =>
            token.Length == 2
            && token.All(character => Uri.IsHexDigit(character)));
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
