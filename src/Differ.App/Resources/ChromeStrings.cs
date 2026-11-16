using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.App.Resources;

public static class ChromeStrings
{
    private static readonly ResourceManager Manager = new("Differ.App.Resources.ChromeStrings", typeof(ChromeStrings).Assembly);

    public static string DisplayAll => Get();
    public static string DisplayDifferences => Get();
    public static string DisplayContext => Get();
    public static string EditMode => Get();
    public static string EditModeHint => Get();
    public static string EnableEditing => Get();
    public static string EditingActive => Get();
    public static string ReadOnly => Get();
    public static string SaveOptions => Get();
    public static string SaveShortcut => Get();
    public static string UndoShortcut => Get();
    public static string RedoShortcut => Get();
    public static string AdvancedRules => Get();
    public static string FindContent => Get();
    public static string WorkspaceFiles => Get();
    public static string WorkspaceView => Get();
    public static string WorkspaceFind => Get();
    public static string WorkspaceGoTo => Get();
    public static string PreviousDifferenceShort => Get();
    public static string NextDifferenceShort => Get();
    public static string FilePicker => Get();
    public static string FileOverflowFormat => Get();
    public static string ComparisonRailHint => Get();
    public static string SpreadsheetSummaryDetailsFormat => Get();
    public static string PreviewIntegrationReadOnly => Get();
    public static string MinimizeWindow => Get();
    public static string MaximizeWindow => Get();
    public static string RestoreWindow => Get();
    public static string CloseWindow => Get();

    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, Strings.Culture ?? CultureInfo.CurrentUICulture) ?? name;
}
