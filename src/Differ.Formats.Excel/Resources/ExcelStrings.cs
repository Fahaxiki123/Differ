using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text;

namespace Differ.Formats.Excel.Resources;

public static class ExcelStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Formats.Excel.Resources.ExcelStrings", typeof(ExcelStrings).Assembly);

    public static string AdapterWorkbookPartMissing => Get();
    public static string AdapterWorkbookEmpty => Get();
    public static string AdapterSheetUnreadableFormat => Get();
    public static string AdapterSheetEmptyFormat => Get();
    public static string AdapterFormulaWarning => Get();
    public static string AdapterFormulaDetailsFormat => Get();
    public static string AdapterGridLimit => Get();
    public static string AdapterInvalidAddress => Get();
    public static string AdapterSharedFormulaInvalid => Get();
    public static string AdapterSharedStringInvalid => Get();
    public static string AdapterEmptyRow => Get();
    public static string AdapterStyleInvalid => Get();
    public static string AdapterKeyColumnsInvalidFormat => Get();
    public static string AdapterKeyColumnMissingFormat => Get();
    public static string AdapterKeyEmptyFormat => Get();
    public static string AdapterKeyDuplicateFormat => Get();
    public static string AdapterBaseSide => Get();
    public static string AdapterCurrentSide => Get();

    public static string MappingInvalidRows => Get();
    public static string MappingInvalidOverrides => Get();
    public static string MappingManual => Get();
    public static string MappingPhysical => Get();
    public static string MappingUniqueField => Get();
    public static string MappingSample => Get();
    public static string MappingAmbiguousBasis => Get();
    public static string MappingOldColumn => Get();
    public static string MappingNewColumn => Get();
    public static string MappingAutomatic => Get();
    public static string MappingExplicit => Get();
    public static string MappingMissingHeader => Get();
    public static string MappingUniqueKey => Get();
    public static string MappingUniqueContent => Get();
    public static string MappingRepeatedContent => Get();
    public static string MappingLocalContent => Get();
    public static string MappingHeader => Get();
    public static string MappingDeletedRecord => Get();
    public static string MappingAddedRecord => Get();
    public static string MappingColumnLabel => Get();
    public static string MappingPendingColumnLabel => Get();
    public static string MappingColumnOrderLabel => Get();
    public static string MappingRecordOrderLabel => Get();
    public static string MappingHeaderRowLabel => Get();
    public static string MappingDataRowLabel => Get();
    public static string MappingFormulaAdjustmentLabel => Get();
    public static string MappingHiddenRowLabel => Get();
    public static CompositeFormat MappingHeaderLimitFormat { get; } = CompositeFormat.Parse(Get(nameof(MappingHeaderLimitFormat)));
    public static CompositeFormat MappingAmbiguousFormat { get; } = CompositeFormat.Parse(Get(nameof(MappingAmbiguousFormat)));
    public static CompositeFormat MappingDescriptionFormat { get; } = CompositeFormat.Parse(Get(nameof(MappingDescriptionFormat)));
    public static CompositeFormat MappingInvalidKeyFormat { get; } = CompositeFormat.Parse(Get(nameof(MappingInvalidKeyFormat)));
    public static CompositeFormat MappingAnomalyFormat { get; } = CompositeFormat.Parse(Get(nameof(MappingAnomalyFormat)));
    public static CompositeFormat MappingUnsupportedFormulaFormat { get; } = CompositeFormat.Parse(Get(nameof(MappingUnsupportedFormulaFormat)));

    public static CompositeFormat MappingFormatDetails { get; } = CompositeFormat.Parse(Get(nameof(MappingFormatDetails)));
    public static CompositeFormat MappingTypeDetails { get; } = CompositeFormat.Parse(Get(nameof(MappingTypeDetails)));
    public static CompositeFormat MappingCommentDetails { get; } = CompositeFormat.Parse(Get(nameof(MappingCommentDetails)));

    private static string Get([CallerMemberName] string name = "")
    {
        return Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
    }
}
