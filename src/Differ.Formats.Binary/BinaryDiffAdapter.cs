using System.Globalization;
using System.Text;
using Differ.Core.Abstractions;
using Differ.Core.Algorithms;
using Differ.Core.Models;
using Differ.Formats.Binary.Resources;

namespace Differ.Formats.Binary;

/// <summary>
/// Byte-level fallback adapter for every format the structured adapters do
/// not cover. Registered last in the adapter chain with a catch-all
/// <see cref="CanHandle"/>, mirroring Beyond Compare's hex comparison mode.
/// </summary>
public sealed class BinaryDiffAdapter : IFileDiffAdapter
{
    private static readonly CompositeFormat FileTooLargeFormat =
        CompositeFormat.Parse(BinaryStrings.FileTooLargeFormat);

    public FileKind FileKind => FileKind.Binary;

    public bool CanHandle(string path)
    {
        return true;
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        var oldBytes = await ReadAllAsync(
                request.BaseFilePath,
                cancellationToken)
            .ConfigureAwait(false);
        var newBytes = await ReadAllAsync(
                request.CurrentFilePath,
                cancellationToken)
            .ConfigureAwait(false);

        // Identical bytes short-circuit before line materialization and
        // alignment, which is the common case for untouched snapshots.
        if (oldBytes.AsSpan().SequenceEqual(newBytes))
        {
            return new DiffDocument
            {
                RelativePath = request.RelativePath,
                FileKind = FileKind.Binary,
                BaseRevision = request.BaseRevision,
                Nodes = [],
                Summary = new DiffSummary(0, 0, 0, 0, 0, 0, 0),
                Warnings = []
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var matches = SequenceAligner.Align(
            SplitLines(oldBytes),
            SplitLines(newBytes),
            line => Convert.ToHexString(line));

        var nodes = new List<DiffNode>(matches.Count);
        foreach (var match in matches)
        {
            var kind = MapKind(match.Kind);
            if (!request.Options.IncludeUnchanged && kind == DiffKind.Unchanged)
            {
                continue;
            }

            nodes.Add(new DiffNode
            {
                StableId = $"bin:{match.OldIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}"
                    + $":{match.NewIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
                Path = $"字节行 {FormatOffset(match.NewIndex ?? match.OldIndex)}",
                Kind = kind,
                OldValue = match.OldItem is null ? null : FormatHexLine(match.OldItem),
                NewValue = match.NewItem is null ? null : FormatHexLine(match.NewItem),
                OldIndex = match.OldIndex * DiffPerformanceOptions.HexLineBytes,
                NewIndex = match.NewIndex * DiffPerformanceOptions.HexLineBytes
            });
        }

        return new DiffDocument
        {
            RelativePath = request.RelativePath,
            FileKind = FileKind.Binary,
            BaseRevision = request.BaseRevision,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Warnings = []
        };
    }

    private static async Task<byte[]> ReadAllAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > DiffPerformanceOptions.MaximumBinaryFileBytes)
        {
            throw new IOException(string.Format(
                CultureInfo.CurrentCulture,
                FileTooLargeFormat,
                stream.Length,
                DiffPerformanceOptions.MaximumBinaryFileBytes));
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static List<byte[]> SplitLines(byte[] bytes)
    {
        var lineLength = DiffPerformanceOptions.HexLineBytes;
        var lines = new List<byte[]>((bytes.Length + lineLength - 1) / lineLength);
        for (var offset = 0; offset < bytes.Length; offset += lineLength)
        {
            var length = Math.Min(lineLength, bytes.Length - offset);
            var line = new byte[length];
            Array.Copy(bytes, offset, line, 0, length);
            lines.Add(line);
        }

        return lines;
    }

    private static string FormatHexLine(byte[] line)
    {
        var builder = new StringBuilder(line.Length * 3);
        foreach (var value in line)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string FormatOffset(int? lineIndex)
    {
        return lineIndex is { } index
            ? (index * DiffPerformanceOptions.HexLineBytes)
                .ToString(CultureInfo.InvariantCulture)
            : "-";
    }

    private static DiffKind MapKind(SequenceMatchKind kind)
    {
        return kind switch
        {
            SequenceMatchKind.Equal => DiffKind.Unchanged,
            SequenceMatchKind.Added => DiffKind.Added,
            SequenceMatchKind.Deleted => DiffKind.Deleted,
            SequenceMatchKind.Modified => DiffKind.Modified,
            _ => DiffKind.Unsupported
        };
    }
}
