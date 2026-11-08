using System.Globalization;
using System.Text;
using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Formats.Text.Resources;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Differ.Formats.Text;

public sealed class TextDiffAdapter(ITextFileReader reader) : IFileDiffAdapter
{
    private static readonly CompositeFormat LineEndingChangedFormat =
        CompositeFormat.Parse(TextStrings.AdapterLineEndingChangedFormat);
    private static readonly CompositeFormat EncodingChangedFormat =
        CompositeFormat.Parse(TextStrings.AdapterEncodingChangedFormat);

    private static readonly HashSet<string> OfficeAndKnownBinaryExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx",
            ".docm",
            ".dotx",
            ".rtf",
            ".xls",
            ".xlsx",
            ".xlsm",
            ".xlsb",
            ".csv",
            ".pdf",
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".zip",
            ".7z",
            ".rar",
            ".exe",
            ".dll"
        };

    private readonly SideBySideDiffBuilder _diffBuilder = new(new DiffPlex.Differ());

    public FileKind FileKind => FileKind.Text;

    public bool CanHandle(string path)
    {
        var extension = Path.GetExtension(path);
        return !OfficeAndKnownBinaryExtensions.Contains(extension);
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        var oldContent = await reader.ReadWithEncodingAsync(
                request.BaseFilePath, request.Options.TextEncodingName, cancellationToken)
            .ConfigureAwait(false);
        var newContent = await reader.ReadWithEncodingAsync(
                request.CurrentFilePath, request.Options.TextEncodingName, cancellationToken)
            .ConfigureAwait(false);

        if (oldContent.IsBinary || newContent.IsBinary)
        {
            return CreateBinaryResult(request, oldContent, newContent);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var model = _diffBuilder.BuildDiffModel(
            NormalizeLineEndings(oldContent.Text),
            NormalizeLineEndings(newContent.Text),
            request.Options.IgnoreWhitespace,
            request.Options.IgnoreCase);

        var nodes = BuildNodes(model, request.Options.IncludeUnchanged);
        var warnings = oldContent.Warnings
            .Concat(newContent.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!request.Options.IgnoreLineEndings && !string.Equals(
            oldContent.LineEnding,
            newContent.LineEnding,
            StringComparison.Ordinal))
        {
            warnings.Add(string.Format(
                CultureInfo.CurrentCulture,
                LineEndingChangedFormat,
                oldContent.LineEnding,
                newContent.LineEnding));
        }

        if (!request.Options.IgnoreLineEndings
            && HasFinalNewline(oldContent.Text) != HasFinalNewline(newContent.Text))
        {
            warnings.Add(TextStrings.AdapterFinalNewlineChanged);
        }

        if (!string.Equals(
            oldContent.EncodingName,
            newContent.EncodingName,
            StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(string.Format(
                CultureInfo.CurrentCulture,
                EncodingChangedFormat,
                oldContent.EncodingName,
                newContent.EncodingName));
        }

        return new DiffDocument
        {
            RelativePath = request.RelativePath,
            FileKind = FileKind.Text,
            BaseRevision = request.BaseRevision,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Warnings = warnings
        };
    }

    private static bool HasFinalNewline(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    private static string NormalizeLineEndings(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
    }

    private static List<DiffNode> BuildNodes(
        SideBySideDiffModel model,
        bool includeUnchanged)
    {
        var nodes = new List<DiffNode>(model.OldText.Lines.Count);
        var count = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);

        for (var index = 0; index < count; index++)
        {
            var oldLine = index < model.OldText.Lines.Count
                ? model.OldText.Lines[index]
                : new DiffPiece(string.Empty, ChangeType.Imaginary);
            var newLine = index < model.NewText.Lines.Count
                ? model.NewText.Lines[index]
                : new DiffPiece(string.Empty, ChangeType.Imaginary);
            var kind = MapKind(oldLine.Type, newLine.Type);
            if (!includeUnchanged && kind == DiffKind.Unchanged)
            {
                continue;
            }

            nodes.Add(new DiffNode
            {
                StableId = $"text:{index}",
                Path = $"第 {index + 1} 个对齐行",
                Kind = kind,
                OldValue = oldLine.Type == ChangeType.Imaginary ? null : oldLine.Text,
                NewValue = newLine.Type == ChangeType.Imaginary ? null : newLine.Text,
                OldIndex = oldLine.Position,
                NewIndex = newLine.Position
            });
        }

        return nodes;
    }

    private static DiffKind MapKind(ChangeType oldType, ChangeType newType)
    {
        if (oldType == ChangeType.Imaginary)
        {
            return DiffKind.Added;
        }

        if (newType == ChangeType.Imaginary)
        {
            return DiffKind.Deleted;
        }

        if (oldType == ChangeType.Unchanged && newType == ChangeType.Unchanged)
        {
            return DiffKind.Unchanged;
        }

        return DiffKind.Modified;
    }

    private static DiffDocument CreateBinaryResult(
        DiffRequest request,
        TextFileContent oldContent,
        TextFileContent newContent)
    {
        var node = new DiffNode
        {
            StableId = $"binary:{request.RelativePath}",
            Path = request.RelativePath,
            Kind = DiffKind.Unsupported,
            OldValue = request.BaseFilePath,
            NewValue = request.CurrentFilePath,
            Metadata = new Dictionary<string, string>
            {
                ["oldEncoding"] = oldContent.EncodingName,
                ["newEncoding"] = newContent.EncodingName,
                ["reason"] = TextStrings.AdapterBinaryReason
            }
        };
        var nodes = new[] { node };
        return new DiffDocument
        {
            RelativePath = request.RelativePath,
            FileKind = FileKind.Binary,
            BaseRevision = request.BaseRevision,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Warnings = oldContent.Warnings.Concat(newContent.Warnings)
                .Append(TextStrings.AdapterBinaryWarning).Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}
