using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Formats.Text.Resources;

public static class TextStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Formats.Text.Resources.TextStrings", typeof(TextStrings).Assembly);

    public static string ReaderBinaryData => Get();
    public static string ReaderEncodingProbeFailed => Get();
    public static string ReaderUtf16LeNoBom => Get();
    public static string ReaderUtf16BeNoBom => Get();
    public static string ReaderGb18030Fallback => Get();
    public static string ReaderFileTooLarge => Get();
    public static string AdapterLineEndingChangedFormat => Get();
    public static string AdapterFinalNewlineChanged => Get();
    public static string AdapterEncodingChangedFormat => Get();
    public static string AdapterBinaryReason => Get();
    public static string AdapterBinaryWarning => Get();

    public static string EditorExternalChange => Get();
    public static string EditorStaleBlock => Get();
    public static string EditorUnverifiedTarget => Get();
    public static string EditorAlreadySaving => Get();
    public static string EditorSaveAsRequired => Get();
    public static string EditorMixedNewlines => Get();
    public static string EditorResultsPending => Get();
    public static string EditorProtectedTarget => Get();
    public static string EditorCoarseRegionFormat => Get();
    public static string EditorRowLabel => Get();

    private static string Get([CallerMemberName] string name = "")
    {
        return Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
    }
}
