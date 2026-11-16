using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.App.Resources;

public static class FileOpenStrings
{
    private static readonly ResourceManager Manager = new("Differ.App.Resources.FileOpenStrings", typeof(FileOpenStrings).Assembly);
    public static string OpenCompare => Get();
    public static string OpenMerge => Get();
    public static string CompareTitle => Get();
    public static string MergeTitle => Get();
    public static string Base => Get();
    public static string Mine => Get();
    public static string Theirs => Get();
    public static string Output => Get();
    public static string Browse => Get();
    public static string Open => Get();
    public static string Cancel => Get();
    public static string SelectInput => Get();
    public static string SelectOutput => Get();
    public static string AllFiles => Get();
    public static string MergeHint => Get();
    public static string CompareHint => Get();
    public static string MissingPath => Get();
    public static string InputMissing => Get();
    public static string LaunchFailed => Get();
    public static string MergeLaunchFailed => Get();
    public static string UnsupportedMerge => Get();
    public static string MergeTemplateTitle => Get();
    public static string MergeTemplateHint => Get();
    public static string MergeArgumentError => Get();
    public static string MergeErrorTitle => Get();
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
