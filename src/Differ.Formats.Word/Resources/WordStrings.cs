using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Formats.Word.Resources;

public static class WordStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Formats.Word.Resources.WordStrings", typeof(WordStrings).Assembly);

    public static string AdapterMainPartMissing => Get();
    public static string AdapterMainDocumentEmpty => Get();
    public static string AdapterPendingRevisionsFormat => Get();
    public static string AdapterUnsupportedBlocksFormat => Get();
    public static string AdapterDocScopeOnly => Get();
    public static string AdapterDocTooOld => Get();
    public static string AdapterDocEncrypted => Get();
    public static string AdapterInvalidPackage => Get();
    public static string AdapterInvalidDoc => Get();
    public static string AdapterBoundedAlignment => Get();

    private static string Get([CallerMemberName] string name = "")
    {
        return Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
    }
}
