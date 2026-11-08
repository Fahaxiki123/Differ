using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed class ComparisonOptionsViewModel : ObservableObject
{
    private bool _ignoreWhitespace;
    private bool _ignoreCase;
    private bool _ignoreLineEndings;
    private bool _compareFormatting;
    private string? _textEncodingName;
    private bool _supportsText;
    private bool _supportsEncoding;
    private bool _supportsFormatting;
    private bool _supportsKeyColumns;
    private string _keyColumnsText = string.Empty;
    private string _formatKey = "text";
    private bool _hasPendingChanges;
    private IReadOnlyList<SpreadsheetRule> _spreadsheetRules = [];

    public ComparisonOptionsViewModel()
    {
        SetEncodingCommand = new RelayCommand<string>(name =>
        {
            TextEncodingName = string.IsNullOrEmpty(name) ? null : name;
        });
    }

    public bool IgnoreWhitespace { get => _ignoreWhitespace; set => SetProperty(ref _ignoreWhitespace, value); }
    public bool IgnoreCase { get => _ignoreCase; set => SetProperty(ref _ignoreCase, value); }
    public bool IgnoreLineEndings { get => _ignoreLineEndings; set => SetProperty(ref _ignoreLineEndings, value); }
    public bool CompareFormatting { get => _compareFormatting; set => SetProperty(ref _compareFormatting, value); }
    public bool SupportsText { get => _supportsText; private set => SetProperty(ref _supportsText, value); }
    public bool SupportsEncoding { get => _supportsEncoding; private set => SetProperty(ref _supportsEncoding, value); }
    public bool SupportsFormatting { get => _supportsFormatting; private set => SetProperty(ref _supportsFormatting, value); }
    public bool SupportsKeyColumns { get => _supportsKeyColumns; private set => SetProperty(ref _supportsKeyColumns, value); }
    public string FormatKey { get => _formatKey; private set => SetProperty(ref _formatKey, value); }
    public string KeyColumnsText { get => _keyColumnsText; set => SetProperty(ref _keyColumnsText, value ?? string.Empty); }
    public bool HasPendingChanges { get => _hasPendingChanges; internal set => SetProperty(ref _hasPendingChanges, value); }
    public IRelayCommand<string> SetEncodingCommand { get; }
    public IReadOnlyList<SpreadsheetRule> SpreadsheetRules { get => _spreadsheetRules; set => SetProperty(ref _spreadsheetRules, value ?? []); }
    public bool SupportsSpreadsheetRules => FormatKey == "excel";

    public string? TextEncodingName
    {
        get => _textEncodingName;
        set
        {
            if (SetProperty(ref _textEncodingName, value))
            {
                OnPropertyChanged(nameof(EncodingLabel));
            }
        }
    }

    public string EncodingLabel => $"{Strings.TextEncoding}: {TextEncodingName ?? Strings.AutomaticEncoding}";

    public bool HasCustomRules => (SupportsEncoding && TextEncodingName is not null)
        || (SupportsText && (IgnoreWhitespace || IgnoreCase || IgnoreLineEndings))
        || (SupportsFormatting && CompareFormatting)
        || (SupportsKeyColumns && !string.IsNullOrWhiteSpace(KeyColumnsText))
        || (SupportsSpreadsheetRules && SpreadsheetRules.Count > 0);

    public string ToolbarLabel => HasPendingChanges
        ? Strings.ComparisonRulesPending
        : HasCustomRules ? Strings.ComparisonRulesAdjusted : Strings.ComparisonOptions;

    public void SetFormat(FileKind kind, string extension)
    {
        SupportsText = kind == FileKind.Text;
        SupportsEncoding = SupportsText || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
        SupportsFormatting = kind is FileKind.Word or FileKind.Excel
            && !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase);
        SupportsKeyColumns = kind == FileKind.Excel;
        FormatKey = extension.ToLowerInvariant() switch
        {
            ".csv" => "csv",
            ".rtf" => "rtf",
            _ => kind switch
            {
                FileKind.Word => "word",
                FileKind.Excel => "excel",
                FileKind.Binary => "binary",
                _ => "text"
            }
        };
    }

    public string ActiveRulesSummary
    {
        get
        {
            var parts = new List<string>();
            if (SupportsEncoding && TextEncodingName is not null) parts.Add(EncodingLabel);
            if (SupportsText && IgnoreWhitespace) parts.Add(Strings.IgnoreWhitespace);
            if (SupportsText && IgnoreCase) parts.Add(Strings.IgnoreCase);
            if (SupportsText && IgnoreLineEndings) parts.Add(Strings.IgnoreLineEndings);
            if (SupportsFormatting && CompareFormatting) parts.Add(Strings.CompareFormatting);
            if (SupportsKeyColumns && !string.IsNullOrWhiteSpace(KeyColumnsText)) parts.Add($"{Strings.KeyColumnsLabel}: {KeyColumnsText}");
            if (SupportsSpreadsheetRules && SpreadsheetRules.Count > 0) parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.SpreadsheetRuleCountFormat, SpreadsheetRules.Count));
            var summary = parts.Count == 0 ? Strings.RulesDefault : string.Join(" · ", parts);
            return HasPendingChanges ? $"{Strings.PendingComparisonOptions} · {summary}" : summary;
        }
    }

    public void Apply(DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TextEncodingName = options.TextEncodingName;
        IgnoreWhitespace = options.IgnoreWhitespace;
        IgnoreCase = options.IgnoreCase;
        IgnoreLineEndings = options.IgnoreLineEndings;
        CompareFormatting = options.CompareFormatting;
        KeyColumnsText = string.Join(",", options.KeyColumns);
        SpreadsheetRules = options.SpreadsheetRules;
    }

    public void ResetDefaults() => Apply(new DiffOptions());

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(ActiveRulesSummary))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ActiveRulesSummary)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasCustomRules)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ToolbarLabel)));
        }
    }

    public DiffOptions ToDiffOptions() => new()
    {
        IncludeUnchanged = true,
        IgnoreWhitespace = SupportsText && IgnoreWhitespace,
        IgnoreCase = SupportsText && IgnoreCase,
        IgnoreLineEndings = SupportsText && IgnoreLineEndings,
        TextEncodingName = SupportsEncoding ? TextEncodingName : null,
        CompareFormatting = SupportsFormatting && CompareFormatting,
        KeyColumns = SupportsKeyColumns ? KeyColumnsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => column.ToUpperInvariant()).ToArray() : [],
        SpreadsheetRules = SupportsSpreadsheetRules ? SpreadsheetRules : []
    };
}
