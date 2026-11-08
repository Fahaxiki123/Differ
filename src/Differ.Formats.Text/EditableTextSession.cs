using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Differ.Core.Models;
using Differ.Formats.Text.Resources;

namespace Differ.Formats.Text;

public enum TextInputOrigin { Unknown, LocalFile, SvnWorking, HistoricalFile, SessionSnapshot }

public sealed record TextHunk(int Index, long Version, int OldStartLine, int OldEndLine,
    int NewStartLine, int NewEndLine, int OldOffset, int OldLength, int NewOffset, int NewLength,
    DiffKind Kind);

public sealed record EditorLineChange(int OriginalStartLineNumber, int OriginalEndLineNumber,
    int ModifiedStartLineNumber, int ModifiedEndLineNumber);

public sealed record EditorTextSnapshot(string OriginalText, string Text, long Version)
{
    public IReadOnlyList<string> SourceWarnings { get; init; } = [];
    public string? OriginalEncodingName { get; init; }
    public string? CurrentEncodingName { get; init; }
    public string? OriginalLineEnding { get; init; }
    public string? CurrentLineEnding { get; init; }
}
public sealed record EditorComparisonSnapshot(string OriginalText, string Text, long Version, IReadOnlyList<TextHunk> Hunks)
{
    public bool IgnoreFinalNewline { get; init; }
    public int ApproximateRegionCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ExternalTextChangeException : IOException
{
    public ExternalTextChangeException() : base(TextStrings.EditorExternalChange) { }
}

/// <summary>The disk source and editable document are separate. Editor ranges are accepted only for the current version.</summary>
public sealed class EditableTextSession
{
    private static readonly CompositeFormat CoarseRegionFormat = CompositeFormat.Parse(TextStrings.EditorCoarseRegionFormat);
    private static readonly IReadOnlyDictionary<string, string> EditorMetadata = new Dictionary<string, string> { ["editorAuthority"] = "diffplex" };
    private readonly string _basePath;
    private readonly string _snapshotPath;
    private readonly string? _snapshotDirectory;
    private string _savedText;
    private string? _savedFingerprint;
    private string? _inputPath;
    private string _inputFingerprint;
    private bool _saving;
    private readonly IReadOnlyList<string> _sourceWarnings;
    private readonly string _baseEncodingName;
    private readonly string _baseLineEnding;
    private EditorComparisonSnapshot? _acceptedComparison;
    private readonly HashSet<string> _protectedPaths = new(StringComparer.OrdinalIgnoreCase);

    private EditableTextSession(string basePath, string snapshotPath, string? snapshotDirectory,
        TextFileContent original, TextFileContent modified, string fingerprint)
    {
        _basePath = Path.GetFullPath(basePath);
        _snapshotPath = Path.GetFullPath(snapshotPath);
        _snapshotDirectory = snapshotDirectory is null ? null : Path.GetFullPath(snapshotDirectory);
        OriginalText = Normalize(original.Text);
        Text = _savedText = Normalize(modified.Text);
        EncodingName = modified.EncodingName == "empty" ? "utf-8" : modified.EncodingName;
        HasBom = modified.HasBom;
        OriginalLineEnding = modified.LineEnding;
        LineEnding = modified.LineEnding == "LF" ? "\n" : modified.LineEnding == "CR" ? "\r" : "\r\n";
        HasMixedLineEndings = modified.LineEnding == "mixed";
        _baseEncodingName = original.EncodingName;
        _baseLineEnding = original.LineEnding;
        _sourceWarnings = original.Warnings.Concat(modified.Warnings).Distinct(StringComparer.Ordinal).ToArray();
        _savedFingerprint = fingerprint;
        _inputFingerprint = fingerprint;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string OriginalText { get; }
    public string Text { get; private set; }
    public long Version { get; private set; } = 1;
    public long ResultVersion { get; private set; }
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);
    public bool ResultsCurrent => ResultVersion == Version;
    public string EncodingName { get; }
    public bool HasBom { get; }
    public string OriginalLineEnding { get; }
    public string LineEnding { get; private set; }
    public bool HasMixedLineEndings { get; private set; }
    public string? SaveTarget { get; private set; }
    public TextInputOrigin Origin { get; private set; } = TextInputOrigin.Unknown;
    public IReadOnlyList<TextHunk> Hunks { get; private set; } = [];

    public void ProtectPath(string path) => _protectedPaths.Add(Path.GetFullPath(path));
    public void RememberInputPath(string path) => _inputPath = Path.GetFullPath(path);

    public void SetReadOnlyOrigin(TextInputOrigin origin)
    {
        if (origin is TextInputOrigin.LocalFile or TextInputOrigin.SvnWorking) throw new ArgumentException("Use ConfirmSaveTarget for writable sources.", nameof(origin));
        Origin = origin;
        SaveTarget = null;
    }

    public static async Task<EditableTextSession?> OpenAsync(string basePath, string snapshotPath,
        string? snapshotDirectory, string? encodingName, CancellationToken cancellationToken = default)
    {
        var reader = new TextFileReader();
        var original = await reader.ReadWithEncodingAsync(basePath, encodingName, cancellationToken).ConfigureAwait(false);
        var modified = await reader.ReadWithEncodingAsync(snapshotPath, encodingName, cancellationToken).ConfigureAwait(false);
        if (original.IsBinary || modified.IsBinary) return null;
        return new EditableTextSession(basePath, snapshotPath, snapshotDirectory, original, modified,
            await FingerprintAsync(snapshotPath, cancellationToken).ConfigureAwait(false));
    }

    public bool Update(string text, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (expectedVersion != Version) return false;
        text = Normalize(text);
        Text = text;
        Version++;
        Hunks = [];
        ResultVersion = 0;
        _acceptedComparison = null;
        return true;
    }

    public void InvalidateResults()
    {
        ResultVersion = 0;
        Hunks = [];
        _acceptedComparison = null;
    }

    public bool AcceptChanges(long version, IReadOnlyList<EditorLineChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (version != Version) return false;
        Hunks = BuildHunks(new EditorTextSnapshot(OriginalText, Text, version), changes);
        _acceptedComparison = null;
        ResultVersion = version;
        return true;
    }

    public EditorTextSnapshot CaptureText() => new(OriginalText, Text, Version)
    {
        SourceWarnings = _sourceWarnings,
        OriginalEncodingName = _baseEncodingName,
        CurrentEncodingName = EncodingName,
        OriginalLineEnding = _baseLineEnding,
        CurrentLineEnding = Version == 1 ? OriginalLineEnding : !Text.Contains('\n') ? "none"
            : LineEnding == "\n" ? "LF" : LineEnding == "\r" ? "CR" : "CRLF"
    };
    public bool AcceptComparison(EditorComparisonSnapshot comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.Version != Version || !string.Equals(comparison.Text, Text, StringComparison.Ordinal)) return false;
        Hunks = comparison.Hunks;
        ResultVersion = comparison.Version;
        _acceptedComparison = comparison;
        return true;
    }

    public static IReadOnlyList<TextHunk> BuildHunks(EditorTextSnapshot snapshot, IReadOnlyList<EditorLineChange> changes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        var original = snapshot.OriginalText;
        var text = snapshot.Text;
        var oldLines = LineOffsets(original);
        var newLines = LineOffsets(text);
        var hunks = new List<TextHunk>(changes.Count);
        foreach (var change in changes)
        {
            var oldRange = GetRange(original, oldLines, change.OriginalStartLineNumber, change.OriginalEndLineNumber);
            var newRange = GetRange(text, newLines, change.ModifiedStartLineNumber, change.ModifiedEndLineNumber);
            // EOF changes include the separator before the last line. This makes empty EOF ranges reversible.
            if (oldRange.End == original.Length && newRange.End == text.Length)
            {
                if (oldRange.Start > 0 && original[oldRange.Start - 1] == '\n') oldRange.Start--;
                if (newRange.Start > 0 && text[newRange.Start - 1] == '\n') newRange.Start--;
            }
            var kind = change.OriginalEndLineNumber == 0 ? DiffKind.Added
                : change.ModifiedEndLineNumber == 0 ? DiffKind.Deleted : DiffKind.Modified;
            hunks.Add(new TextHunk(hunks.Count, snapshot.Version, change.OriginalStartLineNumber, change.OriginalEndLineNumber,
                change.ModifiedStartLineNumber, change.ModifiedEndLineNumber, oldRange.Start, oldRange.End - oldRange.Start,
                newRange.Start, newRange.End - newRange.Start, kind));
        }
        return hunks;
    }

    public (int Offset, int Length, string Text) GetLeftReplacement(int index, long version)
    {
        if (!ResultsCurrent || version != Version || index < 0 || index >= Hunks.Count)
            throw new InvalidOperationException(TextStrings.EditorStaleBlock);
        var hunk = Hunks[index];
        return (hunk.NewOffset, hunk.NewLength, OriginalText.Substring(hunk.OldOffset, hunk.OldLength));
    }

    public void ConfirmSaveTarget(string target, TextInputOrigin origin)
    {
        if (origin is not (TextInputOrigin.LocalFile or TextInputOrigin.SvnWorking))
            throw new ArgumentException(TextStrings.EditorUnverifiedTarget, nameof(origin));
        ValidateTarget(target);
        SaveTarget = Path.GetFullPath(target);
        Origin = origin;
    }

    public void ChooseUniformLineEnding(string lineEnding)
    {
        if (lineEnding is not ("\n" or "\r\n" or "\r")) throw new ArgumentException("Invalid line ending.", nameof(lineEnding));
        LineEnding = lineEnding;
        HasMixedLineEndings = false;
    }

    public async Task SaveAsync(string? saveAsPath = null, CancellationToken cancellationToken = default)
    {
        if (_saving) throw new InvalidOperationException(TextStrings.EditorAlreadySaving);
        var target = saveAsPath is null ? SaveTarget : Path.GetFullPath(saveAsPath);
        if (target is null) throw new InvalidOperationException(TextStrings.EditorSaveAsRequired);
        ValidateTarget(target);
        var sameTarget = string.Equals(target, SaveTarget, StringComparison.OrdinalIgnoreCase);
        if (sameTarget && !IsDirty) return; // No edit means no byte rewrite, including mixed line endings.
        if (!IsDirty && Version == 1 && string.Equals(target, _inputPath, StringComparison.OrdinalIgnoreCase))
        {
            await AssertUnchangedAsync(target, _inputFingerprint, cancellationToken).ConfigureAwait(false);
            SaveTarget = target;
            Origin = TextInputOrigin.LocalFile;
            return;
        }
        if (HasMixedLineEndings && (IsDirty || Version != 1)) throw new InvalidOperationException(TextStrings.EditorMixedNewlines);
        var textAtSave = Text;
        byte[] bytes;
        if (!IsDirty && Version == 1)
        {
            bytes = await File.ReadAllBytesAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != _inputFingerprint) throw new ExternalTextChangeException();
        }
        else
        {
            var encoding = Encoding.GetEncoding(EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var content = encoding.GetBytes(textAtSave.Replace("\n", LineEnding, StringComparison.Ordinal));
            var preamble = HasBom ? Encoding.GetEncoding(EncodingName).GetPreamble() : [];
            bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);
        }
        _saving = true;
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".differ-save-{Guid.NewGuid():N}.tmp");
        try
        {
            var expected = sameTarget ? _savedFingerprint
                : string.Equals(target, _inputPath, StringComparison.OrdinalIgnoreCase) ? _inputFingerprint
                : File.Exists(target) ? await FingerprintAsync(target, cancellationToken).ConfigureAwait(false) : null;
            await AssertUnchangedAsync(target, expected, cancellationToken).ConfigureAwait(false);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (expected is null) File.Move(temporary, target);
            else
            {
                // Retain a deny-write handle from the final fingerprint check through atomic replacement.
                await using var lockedTarget = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    65536, FileOptions.Asynchronous);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(lockedTarget, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new ExternalTextChangeException();
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporary, target, null);
            }
            _savedFingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.Equals(target, _inputPath, StringComparison.OrdinalIgnoreCase)) _inputFingerprint = _savedFingerprint;
            _savedText = textAtSave;
            SaveTarget = target;
            if (!sameTarget) Origin = TextInputOrigin.LocalFile;
        }
        finally
        {
            _saving = false;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public DiffDocument CreateDocument(string relativePath, string? revision)
    {
        if (!ResultsCurrent) throw new InvalidOperationException(TextStrings.EditorResultsPending);
        return CreateDocument(_acceptedComparison ?? new EditorComparisonSnapshot(OriginalText, Text, Version, Hunks)
        { Warnings = EditorDiffEngine.CreateWarnings(CaptureText(), new DiffOptions()) }, relativePath, revision);
    }

    public static DiffDocument CreateDocument(EditorComparisonSnapshot comparison, string relativePath, string? revision)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var oldText = comparison.IgnoreFinalNewline && comparison.OriginalText.EndsWith('\n') ? comparison.OriginalText[..^1] : comparison.OriginalText;
        var newText = comparison.IgnoreFinalNewline && comparison.Text.EndsWith('\n') ? comparison.Text[..^1] : comparison.Text;
        var oldLines = oldText.Length == 0 ? [] : oldText.Split('\n');
        var newLines = newText.Length == 0 ? [] : newText.Split('\n');
        var nodes = new List<DiffNode>(Math.Max(oldLines.Length, newLines.Length));
        var oldIndex = 0;
        var newIndex = 0;
        foreach (var hunk in comparison.Hunks)
        {
            var oldStart = hunk.OldEndLine == 0 ? hunk.OldStartLine : hunk.OldStartLine - 1;
            var newStart = hunk.NewEndLine == 0 ? hunk.NewStartLine : hunk.NewStartLine - 1;
            while (oldIndex < oldStart && newIndex < newStart) Add(DiffKind.Unchanged, oldIndex++, newIndex++);
            var oldEnd = hunk.OldEndLine == 0 ? oldStart : hunk.OldEndLine;
            var newEnd = hunk.NewEndLine == 0 ? newStart : hunk.NewEndLine;
            while (oldIndex < oldEnd || newIndex < newEnd)
            {
                var oldRow = oldIndex < oldEnd ? oldIndex++ : (int?)null;
                var newRow = newIndex < newEnd ? newIndex++ : (int?)null;
                Add(oldRow is null ? DiffKind.Added : newRow is null ? DiffKind.Deleted : DiffKind.Modified, oldRow, newRow);
            }
        }
        while (oldIndex < oldLines.Length && newIndex < newLines.Length) Add(DiffKind.Unchanged, oldIndex++, newIndex++);
        while (oldIndex < oldLines.Length) Add(DiffKind.Deleted, oldIndex++, null);
        while (newIndex < newLines.Length) Add(DiffKind.Added, null, newIndex++);
        var warnings = comparison.ApproximateRegionCount == 0 ? comparison.Warnings
            : [.. comparison.Warnings, string.Format(CultureInfo.CurrentCulture, CoarseRegionFormat, comparison.ApproximateRegionCount)];
        return new DiffDocument
        {
            RelativePath = relativePath,
            BaseRevision = revision,
            FileKind = FileKind.Text,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Warnings = warnings
        };

        void Add(DiffKind kind, int? oldRow, int? newRow) => nodes.Add(new DiffNode
        {
            StableId = $"editor:{comparison.Version}:{nodes.Count}",
            Path = $"{TextStrings.EditorRowLabel} {oldRow + 1} → {newRow + 1}",
            Kind = kind,
            Metadata = EditorMetadata,
            OldIndex = oldRow + 1,
            NewIndex = newRow + 1,
            OldValue = oldRow is { } o && o < oldLines.Length ? oldLines[o] : null,
            NewValue = newRow is { } n && n < newLines.Length ? newLines[n] : null
        });
    }

    private void ValidateTarget(string target)
    {
        var path = Path.GetFullPath(target);
        if (_protectedPaths.Contains(path) || string.Equals(path, _basePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, _snapshotPath, StringComparison.OrdinalIgnoreCase)
            || (_snapshotDirectory is not null && path.StartsWith(_snapshotDirectory.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ", "Sessions")
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException(TextStrings.EditorProtectedTarget);
    }

    private static async Task AssertUnchangedAsync(string path, string? expected, CancellationToken token)
    {
        if (expected is null ? File.Exists(path) : !File.Exists(path)
            || !string.Equals(expected, await FingerprintAsync(path, token).ConfigureAwait(false), StringComparison.Ordinal))
            throw new ExternalTextChangeException();
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static int[] LineOffsets(string text)
    {
        var offsets = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') offsets.Add(i + 1);
        return offsets.ToArray();
    }
    private static (int Start, int End) GetRange(string text, int[] offsets, int start, int end)
    {
        if (start < 0 || end < 0 || start > offsets.Length || end > offsets.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (end == 0) { var at = start < offsets.Length ? offsets[start] : text.Length; return (at, at); }
        if (start == 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
        return (offsets[start - 1], end < offsets.Length ? offsets[end] : text.Length);
    }
}
