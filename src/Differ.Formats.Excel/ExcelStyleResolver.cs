using System.Globalization;
using System.Xml.Linq;
using Differ.Core.Security;
using Differ.Formats.Excel.Resources;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NPOI.HSSF.UserModel;
using NPOI.OOXML.XSSF.UserModel;
using NPOI.SS.UserModel;

namespace Differ.Formats.Excel;

internal sealed class ExcelStyleResolver
{
    private readonly Stylesheet? stylesheet;
    private readonly OpenXmlElement[] themeColors;
    private readonly Dictionary<uint, string> signatures = [];

    public ExcelStyleResolver(WorkbookPart workbookPart)
    {
        stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        themeColors = workbookPart.ThemePart?.Theme?.ThemeElements?.ColorScheme?.ChildElements.ToArray() ?? [];
    }

    public string Resolve(uint? index)
    {
        var key = index ?? 0;
        if (signatures.TryGetValue(key, out var signature))
        {
            return signature;
        }

        var formats = stylesheet?.CellFormats;
        if (key >= (formats?.ChildElements.Count ?? 0) && (key != 0 || formats is not null))
        {
            throw new StructuralParseException(ExcelStrings.AdapterStyleInvalid);
        }

        var format = formats?.Elements<CellFormat>().ElementAtOrDefault((int)key);
        signature = format is null ? string.Empty : ResolveFormat(format);
        signatures.Add(key, signature);
        return signature;
    }

    private string ResolveFormat(CellFormat format)
    {
        var parent = stylesheet?.CellStyleFormats?.Elements<CellFormat>()
            .ElementAtOrDefault((int)(format.FormatId?.Value ?? 0));
        var fontId = format.ApplyFont?.Value == false ? parent?.FontId : format.FontId ?? parent?.FontId;
        var fillId = format.ApplyFill?.Value == false ? parent?.FillId : format.FillId ?? parent?.FillId;
        var borderId = format.ApplyBorder?.Value == false ? parent?.BorderId : format.BorderId ?? parent?.BorderId;
        var numberId = format.ApplyNumberFormat?.Value == false
            ? parent?.NumberFormatId : format.NumberFormatId ?? parent?.NumberFormatId;
        var numberFormat = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(item => item.NumberFormatId?.Value == (numberId?.Value ?? 0))?.FormatCode?.Value
            ?? BuiltinFormats.GetBuiltinFormat((int)(numberId?.Value ?? 0))
            ?? (numberId?.Value ?? 0).ToString(CultureInfo.InvariantCulture);

        return string.Join('\u001f',
            Canonical(stylesheet?.Fonts?.ChildElements.ElementAtOrDefault((int)(fontId?.Value ?? 0))),
            Canonical(stylesheet?.Fills?.ChildElements.ElementAtOrDefault((int)(fillId?.Value ?? 0))),
            Canonical(stylesheet?.Borders?.ChildElements.ElementAtOrDefault((int)(borderId?.Value ?? 0))),
            numberFormat,
            Canonical(format.ApplyAlignment?.Value == false ? parent?.Alignment : format.Alignment ?? parent?.Alignment),
            Canonical(format.ApplyProtection?.Value == false ? parent?.Protection : format.Protection ?? parent?.Protection));
    }

    private string Canonical(OpenXmlElement? element)
    {
        return element is null ? string.Empty : Canonical(XElement.Parse(element.OuterXml));
    }

    private string Canonical(XElement element)
    {
        if (element.Name.LocalName is "color" or "fgColor" or "bgColor")
        {
            NormalizeColor(element);
        }

        return element.Name.LocalName + "["
            + string.Join(';', element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value))
            + "]{" + string.Join(';', element.Elements()
                .OrderBy(child => child.Name.LocalName, StringComparer.Ordinal)
            .Select(Canonical)) + "}";
    }

    private void NormalizeColor(XElement element)
    {
        var rgb = element.Attribute("rgb")?.Value;
        if (uint.TryParse(element.Attribute("theme")?.Value, out var themeIndex))
        {
            // Spreadsheet theme indices begin Light1, Dark1, Light2, Dark2;
            // DrawingML stores those first four children in the reverse pairs.
            var position = themeIndex < 4 ? (int)themeIndex ^ 1 : (int)themeIndex;
            if (position >= 0 && position < themeColors.Length)
            {
                var color = themeColors[position].FirstChild;
                rgb = color?.GetAttribute(color.LocalName == "sysClr" ? "lastClr" : "val", string.Empty).Value;
            }
        }
        else if (int.TryParse(element.Attribute("indexed")?.Value, out var indexed))
        {
            rgb = stylesheet?.Colors?.IndexedColors?.Elements<RgbColor>().ElementAtOrDefault(indexed)?.Rgb?.Value;
            if (rgb is null && indexed is >= 0 and < 64)
            {
                var bytes = DefaultIndexedColorMap.GetDefaultRGB(indexed);
                rgb = bytes is null ? null : Convert.ToHexString(bytes);
            }
        }

        if (!string.IsNullOrEmpty(rgb))
        {
            element.Attribute("theme")?.Remove();
            element.Attribute("indexed")?.Remove();
            element.SetAttributeValue("rgb", (rgb.Length == 6 ? "FF" + rgb : rgb).ToUpperInvariant());
        }
    }

    public static string ResolveLegacy(ICellStyle style, IWorkbook workbook)
    {
        var font = workbook.GetFontAt(style.FontIndex);
        // Resolve the referenced font and format strings, never compare the
        // workbook-local style/font indices themselves.
        return string.Join('|',
            font.FontName, font.FontHeight.ToString(CultureInfo.InvariantCulture),
            font.IsBold, font.IsItalic, font.IsStrikeout, font.Underline, font.TypeOffset,
            font.Charset.ToString(CultureInfo.InvariantCulture), LegacyColor(font.Color, workbook),
            style.FillPattern, LegacyColor(style.FillForegroundColor, workbook),
            LegacyColor(style.FillBackgroundColor, workbook),
            style.BorderLeft, style.BorderRight, style.BorderTop, style.BorderBottom,
            LegacyColor(style.LeftBorderColor, workbook), LegacyColor(style.RightBorderColor, workbook),
            LegacyColor(style.TopBorderColor, workbook), LegacyColor(style.BottomBorderColor, workbook),
            style.Alignment, style.VerticalAlignment, style.WrapText, style.ShrinkToFit,
            style.Indention.ToString(CultureInfo.InvariantCulture),
            style.Rotation.ToString(CultureInfo.InvariantCulture), style.GetDataFormatString(),
            style.IsHidden, style.IsLocked);
    }

    private static string LegacyColor(short index, IWorkbook workbook)
    {
        var color = (workbook as HSSFWorkbook)?.GetCustomPalette().GetColor(index);
        return color is null ? index.ToString(CultureInfo.InvariantCulture)
            : string.Join(',', color.RGB);
    }
}
