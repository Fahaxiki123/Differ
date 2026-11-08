using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Differ.Formats.Word;

internal static class WordFormattingExtractor
{
    public static Dictionary<string, string> BuildParagraphMetadata(
        Paragraph paragraph,
        WordStyleCatalog styles,
        WordNumberingCatalog numbering,
        Dictionary<(int NumberId, int Level), int> numberingState)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["word.blockKind"] = "paragraph"
        };
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(styleId))
        {
            metadata["word.styleId"] = styleId;
            styles.Apply(styleId, metadata);
        }

        var numberingProperties = paragraph.ParagraphProperties?.NumberingProperties;
        if (numberingProperties?.NumberingId?.Val?.Value is > 0 and var numberId)
        {
            var level = numberingProperties.NumberingLevelReference?.Val?.Value ?? 0;
            numbering.ApplyIndentation(numberId, level, metadata);
            var marker = numbering.GetMarker(numberId, level, numberingState);
            if (!string.IsNullOrWhiteSpace(marker))
            {
                metadata["word.listMarker"] = marker + " ";
            }

            metadata["word.listLevel"] = level.ToString(
                CultureInfo.InvariantCulture);
            metadata["word.numberingId"] = numberId.ToString(
                CultureInfo.InvariantCulture);
        }

        // A numbering level supplies paragraph indentation even when the
        // paragraph itself only contains numPr. Explicit paragraph properties
        // take precedence over numbering and style defaults.
        ApplyParagraphProperties(paragraph.ParagraphProperties, metadata);
        ApplyFirstRunProperties(paragraph, metadata);
        return metadata;
    }

    public static Dictionary<string, string> BuildTableCellMetadata(
        TableCell cell,
        string tableKey,
        int rowIndex,
        int columnIndex,
        int columnCount,
        WordStyleCatalog styles,
        WordNumberingCatalog numbering,
        Dictionary<(int NumberId, int Level), int> numberingState)
    {
        var metadata = BuildParagraphMetadata(
            cell.Elements<Paragraph>().FirstOrDefault()
                ?? new Paragraph(),
            styles,
            numbering,
            numberingState);
        metadata["word.blockKind"] = "table-cell";
        metadata["word.tableKey"] = tableKey;
        metadata["word.tableRow"] = rowIndex.ToString(
            CultureInfo.InvariantCulture);
        metadata["word.tableColumn"] = columnIndex.ToString(
            CultureInfo.InvariantCulture);
        metadata["word.tableColumnCount"] = columnCount.ToString(
            CultureInfo.InvariantCulture);

        var gridSpan = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
        metadata["word.tableColumnSpan"] = gridSpan.ToString(
            CultureInfo.InvariantCulture);

        var cellWidth = cell.TableCellProperties?.TableCellWidth?.Width?.Value;
        if (!string.IsNullOrWhiteSpace(cellWidth))
        {
            metadata["word.cellWidth"] = cellWidth;
        }

        return metadata;
    }

    // The typed OpenXml accessors below replace per-paragraph
    // XDocument.Parse(OuterXml) round trips, which dominated extraction time
    // for large documents because every block re-parsed its own properties.
    private static void ApplyParagraphProperties(
        ParagraphProperties? properties,
        Dictionary<string, string> metadata)
    {
        if (properties is null)
        {
            return;
        }

        var indent = properties.Indentation;
        CopyString(indent?.Left, "word.leftTwips", metadata);
        CopyString(indent?.Right, "word.rightTwips", metadata);
        if (indent?.Hanging?.Value is not null)
        {
            metadata.Remove("word.firstLineTwips");
            CopyString(indent.Hanging, "word.hangingTwips", metadata);
        }
        else if (indent?.FirstLine?.Value is not null)
        {
            metadata.Remove("word.hangingTwips");
            CopyString(indent.FirstLine, "word.firstLineTwips", metadata);
        }

        var spacing = properties.SpacingBetweenLines;
        CopyString(spacing?.Before, "word.beforeTwips", metadata);
        CopyString(spacing?.After, "word.afterTwips", metadata);
        CopyString(spacing?.Line, "word.lineTwips", metadata);

        var alignment = GetAlignmentToken(properties.Justification);
        if (!string.IsNullOrWhiteSpace(alignment))
        {
            metadata["word.alignment"] = MapAlignment(alignment);
        }
    }

    private static void ApplyFirstRunProperties(
        Paragraph paragraph,
        Dictionary<string, string> metadata)
    {
        var runProperties = paragraph
            .Elements<Run>()
            .Select(run => run.RunProperties)
            .FirstOrDefault(properties => properties is not null);
        if (runProperties is null)
        {
            return;
        }

        var size = runProperties.FontSize?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(size))
        {
            metadata["word.fontSizeHalfPoints"] = size;
        }

        var fonts = runProperties.RunFonts;
        var fontFamily = fonts?.EastAsia?.Value
            ?? fonts?.Ascii?.Value
            ?? fonts?.HighAnsi?.Value;
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            metadata["word.fontFamily"] = fontFamily;
        }

        metadata["word.bold"] = ReadOnOff(runProperties.Bold)
            .ToString(CultureInfo.InvariantCulture);
        metadata["word.italic"] = ReadOnOff(runProperties.Italic)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static string MapAlignment(string alignment)
    {
        return alignment switch
        {
            "center" => "Center",
            "right" => "Right",
            "both" or "distribute" => "Justify",
            _ => "Left"
        };
    }

    private static void CopyString(
        StringValue? value,
        string metadataKey,
        Dictionary<string, string> metadata)
    {
        var text = value?.Value;
        if (!string.IsNullOrWhiteSpace(text))
        {
            metadata[metadataKey] = text;
        }
    }

    private static string? GetAlignmentToken(Justification? justification)
    {
        var value = justification?.Val?.Value;
        return value is IEnumValue enumValue ? enumValue.Value : null;
    }

    private static bool ReadOnOff(OnOffType? onOff)
    {
        // A present element without an explicit value means "on".
        return onOff?.Val?.Value ?? onOff is not null;
    }
}

internal sealed class WordStyleCatalog
{
    private readonly IReadOnlyDictionary<string, WordStyleInfo> _styles;
    private readonly Dictionary<string, WordStyleInfo> _resolved = new(
        StringComparer.Ordinal);

    private WordStyleCatalog(IReadOnlyDictionary<string, WordStyleInfo> styles)
    {
        _styles = styles;
    }

    public static WordStyleCatalog From(Styles? styles)
    {
        if (styles is null)
        {
            return new WordStyleCatalog(
                new Dictionary<string, WordStyleInfo>(StringComparer.Ordinal));
        }

        var result = new Dictionary<string, WordStyleInfo>(StringComparer.Ordinal);
        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var pPr = style.StyleParagraphProperties;
            var rPr = style.StyleRunProperties;
            var ind = pPr?.Indentation;
            var spacing = pPr?.SpacingBetweenLines;
            var fonts = rPr?.RunFonts;
            result[id] = new WordStyleInfo(
                style.StyleName?.Val?.Value,
                style.BasedOn?.Val?.Value,
                rPr?.FontSize?.Val?.Value,
                ReadNullableOnOff(rPr?.Bold),
                ReadNullableOnOff(rPr?.Italic),
                fonts?.EastAsia?.Value
                    ?? fonts?.Ascii?.Value
                    ?? fonts?.HighAnsi?.Value,
                ind?.Left?.Value,
                ind?.Right?.Value,
                ind?.FirstLine?.Value,
                ind?.Hanging?.Value,
                spacing?.Before?.Value,
                spacing?.After?.Value,
                spacing?.Line?.Value,
                GetAlignmentToken(pPr?.Justification));
        }

        return new WordStyleCatalog(result);
    }

    public void Apply(string styleId, Dictionary<string, string> metadata)
    {
        if (!TryResolve(styleId, out var style))
        {
            return;
        }

        SetIfNotEmpty(metadata, "word.fontSizeHalfPoints", style.FontSizeHalfPoints);
        SetIfNotEmpty(metadata, "word.fontFamily", style.FontFamily);
        SetIfNotEmpty(metadata, "word.leftTwips", style.LeftTwips);
        SetIfNotEmpty(metadata, "word.rightTwips", style.RightTwips);
        SetIfNotEmpty(metadata, "word.firstLineTwips", style.FirstLineTwips);
        SetIfNotEmpty(metadata, "word.hangingTwips", style.HangingTwips);
        SetIfNotEmpty(metadata, "word.beforeTwips", style.BeforeTwips);
        SetIfNotEmpty(metadata, "word.afterTwips", style.AfterTwips);
        SetIfNotEmpty(metadata, "word.lineTwips", style.LineTwips);
        if (!string.IsNullOrWhiteSpace(style.Alignment))
        {
            metadata["word.alignment"] = style.Alignment switch
            {
                "center" => "Center",
                "right" => "Right",
                "both" or "distribute" => "Justify",
                _ => "Left"
            };
        }

        if (style.Bold is { } bold)
        {
            metadata["word.bold"] = bold.ToString(CultureInfo.InvariantCulture);
        }

        if (style.Italic is { } italic)
        {
            metadata["word.italic"] = italic.ToString(CultureInfo.InvariantCulture);
        }
    }

    private bool TryResolve(string styleId, out WordStyleInfo style)
    {
        if (_resolved.TryGetValue(styleId, out style!))
        {
            return true;
        }

        if (!_styles.TryGetValue(styleId, out var current))
        {
            style = default!;
            return false;
        }

        var parent = !string.IsNullOrWhiteSpace(current.BasedOn)
            && !string.Equals(current.BasedOn, styleId, StringComparison.Ordinal)
            && TryResolve(current.BasedOn, out var parentStyle)
            ? parentStyle
            : null;
        style = WordStyleInfo.Merge(parent, current);
        _resolved[styleId] = style;
        return true;
    }

    private static void SetIfNotEmpty(
        Dictionary<string, string> metadata,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value;
        }
    }

    private static bool? ReadNullableOnOff(OnOffType? onOff)
    {
        return onOff is null ? null : onOff.Val?.Value ?? true;
    }

    private static string? GetAlignmentToken(Justification? justification)
    {
        var value = justification?.Val?.Value;
        return value is IEnumValue enumValue ? enumValue.Value : null;
    }
}

internal sealed record WordStyleInfo(
    string? Name,
    string? BasedOn,
    string? FontSizeHalfPoints,
    bool? Bold,
    bool? Italic,
    string? FontFamily,
    string? LeftTwips,
    string? RightTwips,
    string? FirstLineTwips,
    string? HangingTwips,
    string? BeforeTwips,
    string? AfterTwips,
    string? LineTwips,
    string? Alignment)
{
    public static WordStyleInfo Merge(
        WordStyleInfo? parent,
        WordStyleInfo current)
    {
        return new WordStyleInfo(
            current.Name ?? parent?.Name,
            current.BasedOn ?? parent?.BasedOn,
            current.FontSizeHalfPoints ?? parent?.FontSizeHalfPoints,
            current.Bold ?? parent?.Bold,
            current.Italic ?? parent?.Italic,
            current.FontFamily ?? parent?.FontFamily,
            current.LeftTwips ?? parent?.LeftTwips,
            current.RightTwips ?? parent?.RightTwips,
            current.FirstLineTwips ?? parent?.FirstLineTwips,
            current.HangingTwips ?? parent?.HangingTwips,
            current.BeforeTwips ?? parent?.BeforeTwips,
            current.AfterTwips ?? parent?.AfterTwips,
            current.LineTwips ?? parent?.LineTwips,
            current.Alignment ?? parent?.Alignment);
    }
}

internal sealed class WordNumberingCatalog
{
    private readonly IReadOnlyDictionary<int, int> _numberToAbstract;
    private readonly IReadOnlyDictionary<(int AbstractId, int Level), WordLevelInfo>
        _levels;

    private WordNumberingCatalog(
        IReadOnlyDictionary<int, int> numberToAbstract,
        IReadOnlyDictionary<(int AbstractId, int Level), WordLevelInfo> levels)
    {
        _numberToAbstract = numberToAbstract;
        _levels = levels;
    }

    public static WordNumberingCatalog From(Numbering? numbering)
    {
        if (numbering is null)
        {
            return new WordNumberingCatalog(
                new Dictionary<int, int>(),
                new Dictionary<(int AbstractId, int Level), WordLevelInfo>());
        }

        var numberToAbstract = new Dictionary<int, int>();
        foreach (var number in numbering.Elements<NumberingInstance>())
        {
            if (number.NumberID?.Value is { } numberId
                && number.AbstractNumId?.Val?.Value is { } abstractId)
            {
                numberToAbstract[numberId] = abstractId;
            }
        }

        var levels = new Dictionary<(int AbstractId, int Level), WordLevelInfo>();
        foreach (var abstractNum in numbering.Elements<AbstractNum>())
        {
            if (abstractNum.AbstractNumberId?.Value is not { } abstractId)
            {
                continue;
            }

            foreach (var level in abstractNum.Elements<Level>())
            {
                if (level.LevelIndex?.Value is not { } levelId)
                {
                    continue;
                }

                var start = level.StartNumberingValue?.Val?.Value ?? 1;
                var indent = level.PreviousParagraphProperties?.Indentation;
                levels[(abstractId, levelId)] = new WordLevelInfo(
                    level.NumberingFormat?.Val?.InnerText,
                    level.LevelText?.Val?.Value,
                    start, indent?.Left?.Value, indent?.Hanging?.Value, indent?.FirstLine?.Value);
            }
        }

        return new WordNumberingCatalog(numberToAbstract, levels);
    }

    public string? GetMarker(
        int numberId,
        int level,
        Dictionary<(int NumberId, int Level), int> state)
    {
        if (!_numberToAbstract.TryGetValue(numberId, out var abstractId)
            || !_levels.TryGetValue((abstractId, level), out var format))
        {
            return "•";
        }

        if (string.Equals(format.Format, "none", StringComparison.OrdinalIgnoreCase)) return null;

        if (string.Equals(format.Format, "bullet", StringComparison.OrdinalIgnoreCase))
        {
            return level switch
            {
                0 => "•",
                1 => "◦",
                _ => "▪"
            };
        }

        var currentKey = (numberId, level);
        var current = state.TryGetValue(currentKey, out var value)
            ? value + 1
            : format.Start;
        state[currentKey] = current;
        foreach (var key in state.Keys
                     .Where(key => key.NumberId == numberId && key.Level > level)
                     .ToArray())
        {
            state.Remove(key);
        }

        var marker = format.LevelText;
        if (string.IsNullOrWhiteSpace(marker))
        {
            return current.ToString(CultureInfo.InvariantCulture) + ".";
        }

        for (var index = 0; index <= level; index++)
        {
            var levelKey = (numberId, index);
            var number = state.TryGetValue(levelKey, out var levelValue)
                ? levelValue
                : format.Start;
            marker = marker.Replace(
                $"%{index + 1}",
                number.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        return marker;
    }

    public void ApplyIndentation(int numberId, int level, Dictionary<string, string> metadata)
    {
        if (!_numberToAbstract.TryGetValue(numberId, out var abstractId)
            || !_levels.TryGetValue((abstractId, level), out var format)) return;
        if (format.LeftTwips is not null) metadata["word.leftTwips"] = format.LeftTwips;
        if (format.HangingTwips is not null)
        {
            metadata.Remove("word.firstLineTwips");
            metadata["word.hangingTwips"] = format.HangingTwips;
        }
        else if (format.FirstLineTwips is not null)
        {
            metadata.Remove("word.hangingTwips");
            metadata["word.firstLineTwips"] = format.FirstLineTwips;
        }
    }
}

internal sealed record WordLevelInfo(
    string? Format,
    string? LevelText,
    int Start,
    string? LeftTwips,
    string? HangingTwips,
    string? FirstLineTwips);
