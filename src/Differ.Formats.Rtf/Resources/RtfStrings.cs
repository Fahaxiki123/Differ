using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Formats.Rtf.Resources;

internal static class RtfStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Formats.Rtf.Resources.RtfStrings", typeof(RtfStrings).Assembly);

    public static string InvalidDocument => Get();
    public static string InvalidByteSequence => Get();

    private static string Get([CallerMemberName] string name = "") =>
        Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
