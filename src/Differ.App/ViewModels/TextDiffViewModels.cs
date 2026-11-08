using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed record TextDiffRowViewModel(
    string StableId,
    DiffKind Kind,
    int? OldIndex,
    int? NewIndex,
    string OldText,
    string NewText,
    IReadOnlyList<InlineDiffSegment> OldSegments,
    IReadOnlyList<InlineDiffSegment> NewSegments)
{
    public bool IsCollapsed { get; init; }

    public int HiddenCount { get; init; }

    public IRelayCommand? ExpandCommand { get; init; }

    public string CollapsedLabel => IsCollapsed ? OldText : string.Empty;

    public string Path { get; init; } = string.Empty;

    internal Lazy<InlineDiffResult>? InlineComparison { get; init; }

    public string InlineDiagnostic => InlineComparison?.Value.IsApproximate == true
        ? Differ.App.Resources.WordReviewStrings.ApproximateInline + Environment.NewLine + Differ.App.Resources.WordReviewStrings.SelectCharactersHint
        : Differ.App.Resources.WordReviewStrings.SelectCharactersHint;

    public string ChangeGroupKey => GetValue("word.changeGroup") ?? string.Empty;

    public bool IsSplitMergeGroup => GetValue("word.groupCorrespondence") == "split-merge";

    public int Depth { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string KindLabel => DiffRowViewModel.Localize(Kind);

    public string OldLineNumberText => OldIndex?.ToString(
        System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    public string NewLineNumberText => NewIndex?.ToString(
        System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    public string ParagraphMarkerText => (NewIndex ?? OldIndex)?.ToString(
        System.Globalization.CultureInfo.InvariantCulture) is { } index
        ? $"P{index}"
        : "P—";

    public bool HasDiffMarker => Kind != DiffKind.Unchanged;

    public bool IsHeading => BaseStyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
        || MineStyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
        || IsHeadingText(OldText) || IsHeadingText(NewText);

    public bool IsTableCell => !string.IsNullOrWhiteSpace(TableKey);

    public string TableKey => GetValue("word.alignedTableKey") ?? GetSideValue("word.tableKey")
        ?? string.Empty;

    public string TableRowKey => IsTableCell
        ? $"{TableKey}:{GetValue("word.alignedTableRow") ?? GetSideValue("word.tableRow") ?? string.Empty}"
        : string.Empty;

    public int TableColumnIndex => ParseInt(
        GetSideValue("word.tableColumn"),
        defaultValue: 1);

    public int TableColumnCount => Math.Max(
        TableColumnIndex,
        ParseInt(GetSideValue("word.tableColumnCount"), defaultValue: 1));

    public string BaseListMarker => GetValue("old.word.listMarker") ?? string.Empty;

    public string MineListMarker => GetValue("new.word.listMarker") ?? string.Empty;

    public double BaseLeftInset => GetIndent("old");

    public double MineLeftInset => GetIndent("new");

    public double BaseFirstLineInset => GetFirstLineIndent("old");

    public double MineFirstLineInset => GetFirstLineIndent("new");

    public double BaseTextLeftInset => BaseLeftInset + (BaseListMarker.Length == 0 ? Math.Min(0, BaseFirstLineInset) : 0);
    public double MineTextLeftInset => MineLeftInset + (MineListMarker.Length == 0 ? Math.Min(0, MineFirstLineInset) : 0);
    public double BaseParagraphFirstLine => BaseListMarker.Length == 0 ? BaseFirstLineInset : 0;
    public double MineParagraphFirstLine => MineListMarker.Length == 0 ? MineFirstLineInset : 0;
    public double BaseListMarkerInset => Math.Max(0, BaseLeftInset + BaseFirstLineInset);
    public double MineListMarkerInset => Math.Max(0, MineLeftInset + MineFirstLineInset);

    public double BaseBeforeSpacing => GetTwips("old.word.beforeTwips");

    public double MineBeforeSpacing => GetTwips("new.word.beforeTwips");

    public double BaseAfterSpacing => GetTwips("old.word.afterTwips");

    public double MineAfterSpacing => GetTwips("new.word.afterTwips");

    public double BaseFontSize => GetFontSize("old");

    public double MineFontSize => GetFontSize("new");

    public string BaseFontWeight => GetBool("old.word.bold") ? "Bold" : "Normal";

    public string MineFontWeight => GetBool("new.word.bold") ? "Bold" : "Normal";

    public string BaseFontStyle => GetBool("old.word.italic") ? "Italic" : "Normal";

    public string MineFontStyle => GetBool("new.word.italic") ? "Italic" : "Normal";

    public string BaseFontFamily => GetValue("old.word.fontFamily")
        ?? "Microsoft YaHei UI, Segoe UI";

    public string MineFontFamily => GetValue("new.word.fontFamily")
        ?? "Microsoft YaHei UI, Segoe UI";

    public string BaseTextAlignment => GetValue("old.word.alignment") ?? "Left";

    public string MineTextAlignment => GetValue("new.word.alignment") ?? "Left";

    public string BaseStyleId => GetValue("old.word.styleId") ?? string.Empty;

    public string MineStyleId => GetValue("new.word.styleId") ?? string.Empty;

    private string? GetSideValue(string key)
    {
        return GetValue($"old.{key}") ?? GetValue($"new.{key}");
    }

    private string? GetValue(string key)
    {
        return Metadata.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private double GetIndent(string side)
    {
        var left = GetTwips($"{side}.word.leftTwips");
        // Left describes subsequent lines. Hanging is a negative first-line
        // offset, not an additional left margin on the entire paragraph.
        var level = ParseInt(GetValue($"{side}.word.listLevel"), 0);
        return Math.Clamp(left > 0 ? left : level * 18, 0, 120);
    }

    private double GetFirstLineIndent(string side)
    {
        var hanging = GetTwips($"{side}.word.hangingTwips");
        return Math.Clamp(hanging > 0 ? -hanging : GetTwips($"{side}.word.firstLineTwips"), -GetIndent(side), 96);
    }

    private double GetTwips(string key)
    {
        return ParseDouble(GetValue(key)) / 15d;
    }

    private double GetFontSize(string side)
    {
        var halfPoints = ParseDouble(GetValue($"{side}.word.fontSizeHalfPoints"));
        return halfPoints > 0 ? halfPoints * (2d / 3d) : 13.5;
    }

    private bool GetBool(string key)
    {
        return bool.TryParse(GetValue(key), out var value) && value;
    }

    private static int ParseInt(string? value, int defaultValue)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : defaultValue;
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : 0;
    }

    private static bool IsHeadingText(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > 72)
        {
            return false;
        }

        if (text.StartsWith('第')
            && text.Contains('章', StringComparison.Ordinal))
        {
            return true;
        }

        var firstSpace = text.IndexOfAny([' ', '\u3000']);
        return firstSpace > 0
            && firstSpace <= 8
            && char.IsDigit(text[0])
            && text.Count(character => character == '.') <= 2;
    }
}
