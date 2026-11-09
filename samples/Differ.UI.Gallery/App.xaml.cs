using System.Windows;

namespace Differ.UI.Gallery;

public partial class App : Application
{
    internal void ApplyTheme(bool dark)
    {
        string palette = dark ? "Dark" : "Light";
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/Differ.UI;component/Themes/{palette}.xaml", UriKind.Relative),
        };
    }
}
