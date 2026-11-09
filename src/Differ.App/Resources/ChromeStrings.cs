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
    public static string ReadOnly => Get();
    public static string SaveOptions => Get();
    public static string SaveShortcut => Get();
    public static string UndoShortcut => Get();
    public static string RedoShortcut => Get();
    public static string AdvancedRules => Get();
    public static string FindContent => Get();
    public static string PreviewIntegrationReadOnly => Get();

    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, Strings.Culture ?? CultureInfo.CurrentUICulture) ?? name;
}
