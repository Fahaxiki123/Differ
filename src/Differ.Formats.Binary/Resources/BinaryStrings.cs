using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Formats.Binary.Resources;

public static class BinaryStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Formats.Binary.Resources.BinaryStrings", typeof(BinaryStrings).Assembly);

    public static string FileTooLargeFormat => Get();

    private static string Get([CallerMemberName] string name = "")
    {
        return Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
    }
}
