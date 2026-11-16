using System.Security.Cryptography;
using System.Text;
using Differ.Core.Algorithms;
using Differ.Core.Security;
using Differ.Formats.Text.Resources;

namespace Differ.Formats.Text;

/// <summary>Owns merge decisions, bounded undo history, and the explicit output destination.</summary>
public sealed class TextMergeSession
{
    private sealed record State(string Text, TextMergeBlock[] Blocks);
    private static readonly HashSet<string> NonTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".doc", ".docx", ".docm", ".dotx", ".rtf", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".pdf", ".zip", ".7z", ".rar", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".exe", ".dll" };
    private readonly byte[] _mineBytes;
    private readonly string _lineEnding;
    private readonly bool _mixedNewlines;
    private readonly List<State> _undo = [];
    private readonly List<State> _redo = [];
    private readonly string[] _inputs;
    private readonly Dictionary<string, string?> _targetFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private string? _savedText;
    private bool _saving;
    private TextMergeBlock[] _blocks;

    private TextMergeSession(string[] inputs, string output, TextFileContent basis, TextFileContent mine,
        TextFileContent theirs, byte[] mineBytes, string? fingerprint, TextMergeResult merge)
    {
        _inputs = inputs;
        BasePath = inputs[0]; MinePath = inputs[1]; TheirsPath = inputs[2]; OutputPath = InitialOutputPath = output;
        BaseText = ThreeWayTextMerger.Normalize(basis.Text);
        MineText = ThreeWayTextMerger.Normalize(mine.Text);
        TheirsText = ThreeWayTextMerger.Normalize(theirs.Text);
        ResultText = merge.Text;
        _blocks = merge.Blocks.ToArray();
        _mineBytes = mineBytes;
        _targetFingerprints[output] = fingerprint;
        EncodingName = mine.EncodingName == "empty" ? "utf-8" : mine.EncodingName;
        HasBom = mine.HasBom;
        _lineEnding = mine.LineEnding == "CRLF" ? "\r\n" : mine.LineEnding == "CR" ? "\r" : "\n";
        _mixedNewlines = mine.LineEnding == "mixed";
        Warnings = basis.Warnings.Concat(mine.Warnings).Concat(theirs.Warnings).Concat(merge.Warnings)
            .Concat(_mixedNewlines ? [MergeTextStrings.MixedNewlines] : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
    }

    public string BasePath { get; }
    public string MinePath { get; }
    public string TheirsPath { get; }
    public string OutputPath { get; private set; }
    public string InitialOutputPath { get; }
    public string BaseText { get; }
    public string MineText { get; }
    public string TheirsText { get; }
    public string ResultText { get; private set; }
    public string EncodingName { get; }
    public bool HasBom { get; }
    public long Version { get; private set; } = 1;
    public IReadOnlyList<TextMergeBlock> Blocks => Array.AsReadOnly(_blocks);
    public int UnresolvedCount => _blocks.Count(block => block.IsConflict && !block.IsResolved);
    public bool HasSaved => _savedText is not null;
    public bool IsDirty => !HasSaved || !string.Equals(_savedText, ResultText, StringComparison.Ordinal) || UnresolvedCount > 0;
    public bool CanUndo => !_saving && _undo.Count > 0;
    public bool CanRedo => !_saving && _redo.Count > 0;
    public IReadOnlyList<string> Warnings { get; }

    public static async Task<TextMergeSession> OpenAsync(string basePath, string minePath, string theirsPath,
        string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new[] { basePath, minePath, theirsPath }.Select(MergePathSafety.NormalizeAndValidate).ToArray();
        var output = MergePathSafety.NormalizeAndValidate(outputPath);
        foreach (var input in inputs)
        {
            if (!File.Exists(input)) throw new FileNotFoundException(MergeTextStrings.MissingInput);
            ValidateFileType(input);
        }
        ValidateFileType(output);
        MergePathSafety.AssertOutputSafe(output, inputs);
        var fingerprint = File.Exists(output) ? await FingerprintAsync(output, cancellationToken).ConfigureAwait(false) : null;
        // Keep source reads coherent while encoding detection and raw-byte retention run.
        var locks = new List<FileStream>();
        try
        {
            foreach (var input in inputs.Distinct(StringComparer.OrdinalIgnoreCase))
                locks.Add(new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read));
            var reader = new TextFileReader();
            var basis = await reader.ReadAsync(inputs[0], cancellationToken).ConfigureAwait(false);
            var mine = await reader.ReadAsync(inputs[1], cancellationToken).ConfigureAwait(false);
            var theirs = await reader.ReadAsync(inputs[2], cancellationToken).ConfigureAwait(false);
            if (new[] { basis, mine, theirs }.Any(content => content.IsBinary || content.Text.Contains('\0')))
                throw new InvalidDataException(MergeTextStrings.UnsupportedFormat);
            var bytes = await File.ReadAllBytesAsync(inputs[1], cancellationToken).ConfigureAwait(false);
            var merge = ThreeWayTextMerger.Merge(basis.Text, mine.Text, theirs.Text, cancellationToken);
            return new TextMergeSession(inputs, output, basis, mine, theirs, bytes, fingerprint, merge);
        }
        finally { foreach (var stream in locks) await stream.DisposeAsync().ConfigureAwait(false); }
    }

    public bool Choose(int index, MergeChoice choice, long expectedVersion)
    {
        if (!CanMutate(expectedVersion) || !ValidIndex(index) || !_blocks[index].CanChoose) return false;
        var block = _blocks[index];
        var replacement = choice switch
        {
            MergeChoice.Mine => block.MineText,
            MergeChoice.Base => block.BaseText,
            MergeChoice.Theirs => block.TheirsText,
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
        if ((long)ResultText.Length - block.ResultLength + replacement.Length > DiffPerformanceOptions.MaximumTextCharacters)
            throw new ComparisonResourceLimitException(MergeTextStrings.TooLarge);
        Remember();
        ResultText = ResultText.Remove(block.ResultOffset, block.ResultLength).Insert(block.ResultOffset, replacement);
        var delta = replacement.Length - block.ResultLength;
        for (var i = index + 1; i < _blocks.Length; i++)
            _blocks[i] = _blocks[i] with { ResultOffset = _blocks[i].ResultOffset + delta };
        _blocks[index] = block with { ResultLength = replacement.Length, IsResolved = true, Choice = choice };
        Version++;
        return true;
    }

    public bool Confirm(int index, long expectedVersion)
    {
        if (!CanMutate(expectedVersion) || !ValidIndex(index) || !_blocks[index].IsConflict) return false;
        Remember();
        _blocks[index] = _blocks[index] with { IsResolved = true, Choice = MergeChoice.Manual };
        Version++;
        return true;
    }

    public bool UpdateResult(string text, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!CanMutate(expectedVersion)) return false;
        text = ThreeWayTextMerger.Normalize(text);
        if (text.Length > DiffPerformanceOptions.MaximumTextCharacters || text.Count(character => character == '\n') >= DiffPerformanceOptions.MaximumTextLines)
            throw new ComparisonResourceLimitException(MergeTextStrings.TooLarge);
        if (string.Equals(text, ResultText, StringComparison.Ordinal)) return true;
        var prefix = 0;
        while (prefix < ResultText.Length && prefix < text.Length && ResultText[prefix] == text[prefix]) prefix++;
        var oldEnd = ResultText.Length;
        var newEnd = text.Length;
        while (oldEnd > prefix && newEnd > prefix && ResultText[oldEnd - 1] == text[newEnd - 1]) { oldEnd--; newEnd--; }
        var delta = text.Length - ResultText.Length;
        var touched = _blocks.Where(block => block.ResultOffset <= oldEnd && block.ResultOffset + block.ResultLength >= prefix).ToArray();
        Remember();
        for (var i = 0; i < _blocks.Length; i++)
        {
            var block = _blocks[i];
            var start = block.ResultOffset;
            var end = start + block.ResultLength;
            if (end < prefix) continue;
            if (start > oldEnd) { _blocks[i] = block with { ResultOffset = start + delta }; continue; }
            var newStart = Math.Min(start, prefix);
            var mappedEnd = Math.Max(newEnd, end + delta);
            // Crossing a block boundary loses the independent replace range. Retain
            // the edited text for manual review; do not let a later Take erase context.
            var canChoose = block.CanChoose && touched.Length == 1 && prefix >= start && oldEnd <= end;
            _blocks[i] = block with
            {
                ResultOffset = newStart,
                ResultLength = Math.Max(0, mappedEnd - newStart),
                IsResolved = !block.IsConflict,
                Choice = block.IsConflict ? MergeChoice.Unresolved : MergeChoice.Manual,
                CanChoose = canChoose
            };
        }
        ResultText = text;
        Version++;
        return true;
    }

    public bool Undo(long expectedVersion)
    {
        if (!CanMutate(expectedVersion) || _undo.Count == 0) return false;
        _redo.Add(Capture());
        Restore(_undo[^1]); _undo.RemoveAt(_undo.Count - 1); Version++;
        return true;
    }

    public bool Redo(long expectedVersion)
    {
        if (!CanMutate(expectedVersion) || _redo.Count == 0) return false;
        _undo.Add(Capture());
        Restore(_redo[^1]); _redo.RemoveAt(_redo.Count - 1); Version++;
        return true;
    }

    public async Task SaveAsync(string? targetPath = null, CancellationToken cancellationToken = default)
    {
        if (_saving) throw new InvalidOperationException(MergeTextStrings.Saving);
        if (UnresolvedCount > 0) throw new InvalidOperationException(MergeTextStrings.Unresolved);
        cancellationToken.ThrowIfCancellationRequested();
        var target = MergePathSafety.NormalizeAndValidate(targetPath ?? OutputPath);
        ValidateFileType(target);
        MergePathSafety.AssertOutputSafe(target, _inputs);
        var textAtSave = ResultText;
        byte[] bytes;
        if (string.Equals(textAtSave, MineText, StringComparison.Ordinal)) bytes = _mineBytes;
        else
        {
            if (_mixedNewlines) throw new InvalidOperationException(MergeTextStrings.MixedNewlines);
            var encoding = Encoding.GetEncoding(EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var body = encoding.GetBytes(textAtSave.Replace("\n", _lineEnding, StringComparison.Ordinal));
            var preamble = HasBom ? Encoding.GetEncoding(EncodingName).GetPreamble() : [];
            bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0); body.CopyTo(bytes, preamble.Length);
        }
        _saving = true;
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".differ-merge-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!_targetFingerprints.TryGetValue(target, out var expected))
            {
                expected = File.Exists(target) ? await FingerprintAsync(target, cancellationToken).ConfigureAwait(false) : null;
                _targetFingerprints[target] = expected;
            }
            await AssertUnchangedAsync(target, expected, cancellationToken).ConfigureAwait(false);
            var newFingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.Equals(expected, newFingerprint, StringComparison.Ordinal))
            {
                _targetFingerprints[target] = newFingerprint;
                _savedText = textAtSave;
                OutputPath = target;
                return;
            }
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            MergePathSafety.AssertOutputSafe(target, _inputs);
            if (expected is null) File.Move(temporary, target);
            else
            {
                await using var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    65536, FileOptions.Asynchronous);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(locked, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new IOException(MergeTextStrings.ExternalChange);
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporary, target, null);
            }
            _targetFingerprints[target] = newFingerprint;
            _savedText = textAtSave;
            OutputPath = target;
        }
        finally
        {
            _saving = false;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private bool CanMutate(long version) => !_saving && version == Version;
    private bool ValidIndex(int index) => index >= 0 && index < _blocks.Length;
    private State Capture() => new(ResultText, _blocks.ToArray());
    private void Restore(State state) { ResultText = state.Text; _blocks = state.Blocks.ToArray(); }
    private void Remember()
    {
        _undo.Add(Capture()); _redo.Clear();
        var characters = _undo.Sum(state => (long)state.Text.Length + state.Blocks.Length * 64L);
        while (_undo.Count > 1 && (_undo.Count > DiffPerformanceOptions.MaximumMergeHistoryEntries
            || characters > DiffPerformanceOptions.MaximumMergeHistoryCharacters))
        { characters -= _undo[0].Text.Length + _undo[0].Blocks.Length * 64L; _undo.RemoveAt(0); }
    }

    public static void ValidateFileType(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (NonTextExtensions.Contains(Path.GetExtension(path))) throw new InvalidDataException(MergeTextStrings.UnsupportedFormat);
    }

    private static async Task AssertUnchangedAsync(string path, string? expected, CancellationToken token)
    {
        if (expected is null ? File.Exists(path) : !File.Exists(path)
            || !string.Equals(expected, await FingerprintAsync(path, token).ConfigureAwait(false), StringComparison.Ordinal))
            throw new IOException(MergeTextStrings.ExternalChange);
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
