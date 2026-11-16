using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Differ.Core.Models;

namespace Differ.App.Services;

public sealed class UserPreferencesService : ObservableObject, IDisposable
{
    private const long MaximumSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> FormatKeys = new(StringComparer.Ordinal)
        { "text", "word", "excel", "csv", "rtf", "binary" };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private double _readingScale = 1;
    private double _windowWidth = 1500;
    private double _windowHeight = 860;
    private double _windowLeft = double.NaN;
    private double _windowTop = double.NaN;
    private bool _isMaximized;
    private double _textDetailHeight = 124;
    private double _spreadsheetDetailHeight = 104;
    private bool _textDetailsExpanded;
    private bool _spreadsheetDetailsExpanded = true;
    private bool _mergeShowBase;

    public UserPreferencesService(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            AppRuntimeProfile.DataRoot, "Preferences.json"));
    }

    public double ReadingScale { get => _readingScale; set => SetProperty(ref _readingScale, Clamp(value, 0.8, 1.6, 1)); }
    public double WindowWidth { get => _windowWidth; set => SetProperty(ref _windowWidth, Clamp(value, 720, 10000, 1500)); }
    public double WindowHeight { get => _windowHeight; set => SetProperty(ref _windowHeight, Clamp(value, 480, 10000, 860)); }
    public double WindowLeft { get => _windowLeft; set => SetProperty(ref _windowLeft, Coordinate(value)); }
    public double WindowTop { get => _windowTop; set => SetProperty(ref _windowTop, Coordinate(value)); }
    public bool IsMaximized { get => _isMaximized; set => SetProperty(ref _isMaximized, value); }
    // Preserve the historical setting's meaning: the complete expanded area,
    // including its header, rather than adding a new header to saved heights.
    public double TextDetailHeight { get => _textDetailHeight; set => SetProperty(ref _textDetailHeight, Clamp(value, 72, 1200, 124)); }
    public double SpreadsheetDetailHeight { get => _spreadsheetDetailHeight; set => SetProperty(ref _spreadsheetDetailHeight, Clamp(value, 72, 1200, 104)); }
    public bool TextDetailsExpanded { get => _textDetailsExpanded; set => SetProperty(ref _textDetailsExpanded, value); }
    public bool SpreadsheetDetailsExpanded { get => _spreadsheetDetailsExpanded; set => SetProperty(ref _spreadsheetDetailsExpanded, value); }
    public bool MergeShowBase { get => _mergeShowBase; private set => SetProperty(ref _mergeShowBase, value); }

    public void Dispose() => _gate.Dispose();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            var data = await ReadAsync(cancellationToken);
            ReadingScale = data.Layout.ReadingScale;
            WindowWidth = data.Layout.WindowWidth;
            WindowHeight = data.Layout.WindowHeight;
            WindowLeft = data.Layout.WindowLeft ?? double.NaN;
            WindowTop = data.Layout.WindowTop ?? double.NaN;
            IsMaximized = data.Layout.IsMaximized;
            TextDetailHeight = data.Layout.TextDetailHeight;
            SpreadsheetDetailHeight = data.Layout.SpreadsheetDetailHeight;
            TextDetailsExpanded = data.Layout.TextDetailsExpanded;
            SpreadsheetDetailsExpanded = data.Layout.SpreadsheetDetailsExpanded;
            MergeShowBase = data.MergeShowBase;
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var layout = new LayoutData
        {
            ReadingScale = ReadingScale,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            WindowLeft = double.IsFinite(WindowLeft) ? WindowLeft : null,
            WindowTop = double.IsFinite(WindowTop) ? WindowTop : null,
            IsMaximized = IsMaximized,
            TextDetailHeight = TextDetailHeight,
            SpreadsheetDetailHeight = SpreadsheetDetailHeight,
            TextDetailsExpanded = TextDetailsExpanded,
            SpreadsheetDetailsExpanded = SpreadsheetDetailsExpanded
        };
        return UpdateAsync(data => data with { Layout = layout }, cancellationToken);
    }

    public async Task SetMergeShowBaseAsync(bool value, CancellationToken cancellationToken = default)
    {
        // Merge windows run independently of the ordinary comparison process. Only
        // update this field; the latest disk layout and rules belong to their writers.
        await UpdateAsync(data => data with { MergeShowBase = value }, cancellationToken);
        MergeShowBase = value;
    }

    public async Task<DiffOptions?> LoadRulesAsync(string formatKey, CancellationToken cancellationToken = default)
    {
        ValidateFormatKey(formatKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return data.Rules.TryGetValue(formatKey, out var rules) ? SanitizeRules(rules) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveRulesAsync(string formatKey, DiffOptions rules, CancellationToken cancellationToken = default)
    {
        ValidateFormatKey(formatKey);
        ArgumentNullException.ThrowIfNull(rules);
        return UpdateAsync(data =>
        {
            data.Rules[formatKey] = SanitizeRules(rules);
            return data;
        }, cancellationToken);
    }

    public Task RestoreRulesAsync(string formatKey, CancellationToken cancellationToken = default)
    {
        ValidateFormatKey(formatKey);
        return UpdateAsync(data =>
        {
            data.Rules.Remove(formatKey);
            return data;
        }, cancellationToken);
    }

    private async Task UpdateAsync(Func<PreferencesData, PreferencesData> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            // The instance semaphore alone cannot protect separate merge/diff processes.
            // Keep this transient lock across the entire read/merge/replace transaction.
            using var writeLock = await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
            var data = update(await ReadAsync(cancellationToken, forUpdate: true).ConfigureAwait(false));
            var temporary = Path.Combine(directory, $".preferences-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data, JsonOptions), cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // DeleteOnClose also removes the lock after exceptions or process exit.
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33
                && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<PreferencesData> ReadAsync(CancellationToken cancellationToken, bool forUpdate = false)
    {
        try
        {
            var json = await Task.Run(async () =>
            {
                if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumSettingsBytes) return null;
                return await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            if (json is null) return new PreferencesData();
            var data = JsonSerializer.Deserialize<PreferencesData>(json, JsonOptions);
            if (data is null || data.Layout is null || data.Rules is null || data.SchemaVersion != 1)
            {
                return new PreferencesData();
            }

            return data with
            {
                Rules = data.Rules.Where(pair => FormatKeys.Contains(pair.Key) && pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => SanitizeRules(pair.Value), StringComparer.Ordinal)
            };
        }
        catch (JsonException)
        {
            return new PreferencesData();
        }
        catch (Exception exception) when (!forUpdate && exception is IOException or UnauthorizedAccessException)
        {
            return new PreferencesData();
        }
    }

    private static DiffOptions SanitizeRules(DiffOptions rules)
    {
        var encoding = rules.TextEncodingName?.ToLowerInvariant();
        if (encoding is not ("utf-8" or "utf-16" or "utf-16be" or "gb18030"))
        {
            encoding = null;
        }

        var columns = (rules.KeyColumns ?? []).Where(column => !string.IsNullOrEmpty(column)
                && column.Length <= 3 && column.All(char.IsAsciiLetter))
            .Select(column => column.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(16384).ToArray();
        return new DiffOptions
        {
            TextEncodingName = encoding,
            IgnoreWhitespace = rules.IgnoreWhitespace,
            IgnoreCase = rules.IgnoreCase,
            IgnoreLineEndings = rules.IgnoreLineEndings,
            CompareFormatting = rules.CompareFormatting,
            KeyColumns = columns,
            SpreadsheetRules = (rules.SpreadsheetRules ?? []).Where(rule => rule is not null
                && rule.HeaderRow >= 0 && rule.DataStartRow > rule.HeaderRow && rule.DataStartRow <= 100000
                && !string.IsNullOrWhiteSpace(rule.FilePattern) && !string.IsNullOrWhiteSpace(rule.SheetName))
                .Take(100).Select(rule => rule with
                {
                    AuxiliaryHeaderRows = (rule.AuxiliaryHeaderRows ?? []).Where(row => row > 0 && row < rule.DataStartRow).Take(100).ToArray(),
                    KeyColumns = (rule.KeyColumns ?? []).Take(64).ToArray(),
                    ColumnOverrides = (rule.ColumnOverrides ?? []).Where(item => item is not null).Take(16384).ToArray(),
                    FrozenColumns = Math.Clamp(rule.FrozenColumns, 0, 16384)
                }).ToArray(),
            IncludeUnchanged = true
        };
    }

    private static void ValidateFormatKey(string formatKey)
    {
        if (!FormatKeys.Contains(formatKey))
        {
            throw new ArgumentException("Unknown comparison rule format.", nameof(formatKey));
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static double Coordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 100000
        ? value : double.NaN;

    private sealed record PreferencesData
    {
        public int SchemaVersion { get; init; } = 1;
        public LayoutData Layout { get; init; } = new();
        public Dictionary<string, DiffOptions> Rules { get; init; } = new(StringComparer.Ordinal);
        public bool MergeShowBase { get; init; }
    }

    private sealed record LayoutData
    {
        public double ReadingScale { get; init; } = 1;
        public double WindowWidth { get; init; } = 1500;
        public double WindowHeight { get; init; } = 860;
        public double? WindowLeft { get; init; }
        public double? WindowTop { get; init; }
        public bool IsMaximized { get; init; }
        public double TextDetailHeight { get; init; } = 124;
        public double SpreadsheetDetailHeight { get; init; } = 104;
        public bool TextDetailsExpanded { get; init; }
        public bool SpreadsheetDetailsExpanded { get; init; } = true;
    }
}
