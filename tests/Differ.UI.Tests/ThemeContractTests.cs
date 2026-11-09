using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.UI.Controls;

namespace Differ.UI.Tests;

public sealed class ThemeContractTests
{
    private static readonly Lazy<Dispatcher> WpfDispatcher = new(StartWpfDispatcher);
    private static readonly string[] Surfaces = ["Content", "Window", "Toolbar", "Tabs", "PaneHeader", "Status"];
    private static readonly string[] States = ["Hover", "Pressed", "Selected", "ReadOnly"];
    private static readonly string[] Actions = ["Accent", "Danger"];
    private static readonly string[] ActionStates = ["", ".Hover", ".Pressed"];
    private static readonly string[] Severities = ["Info", "Warning", "Error"];

    [Fact]
    public void LibraryHasNoProjectOrPackageDependencies()
    {
        var project = XDocument.Load(LibraryProjectPath());
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.DoesNotContain(typeof(AppIcon).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.StartsWith("Differ.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ThemesUseTheSameKeysAndTypesAndHaveLegibleRoles()
    {
        RunSta(() =>
        {
            var light = Load("Light");
            var dark = Load("Dark");
            var keys = light.Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(keys, dark.Keys.Cast<string>().Order(StringComparer.Ordinal));
            Assert.All(keys, key =>
            {
                Assert.StartsWith("Dui.", key, StringComparison.Ordinal);
                Assert.Equal(light[key].GetType(), dark[key].GetType());
            });

            foreach (var palette in new[] { light, dark })
            {
                foreach (var surface in Surfaces)
                {
                    AssertContrast(palette, "Dui.Text.Primary", $"Dui.Surface.{surface}", 4.5);
                    AssertContrast(palette, "Dui.Text.Secondary", $"Dui.Surface.{surface}", 4.5);
                }

                foreach (var surface in States)
                {
                    AssertContrast(palette, "Dui.Text.Primary", $"Dui.State.{surface}", 4.5);
                }

                foreach (var action in Actions)
                {
                    foreach (var state in ActionStates)
                    {
                        AssertContrast(palette, $"Dui.Text.On{action}", $"Dui.{action}{state}", 4.5);
                    }
                }

                foreach (var severity in Severities)
                {
                    AssertContrast(palette, $"Dui.Status.{severity}", $"Dui.Status.{severity}Surface", 4.5);
                }

                AssertContrast(palette, "Dui.Accent", "Dui.State.Selected", 4.5);
                AssertContrast(palette, "Dui.Border.Control", "Dui.Surface.Content", 3);
                AssertContrast(palette, "Dui.Border.Control", "Dui.Surface.Toolbar", 3);
                AssertContrast(palette, "Dui.Focus", "Dui.Surface.Toolbar", 3);
            }
        });
    }

    [Fact]
    public void WpfAppliesExplicitStylesAndReevaluatesDynamicResources()
    {
        RunSta(() =>
        {
            var light = Load("Light");
            var dark = Load("Dark");
            var styles = Load("Controls");
            var view = new StackPanel();
            view.Resources.MergedDictionaries.Add(light);
            view.Resources.MergedDictionaries.Add(styles);
            Assert.All(styles.Keys.Cast<object>(), key => Assert.IsType<string>(key));
            Assert.Equal(new GridLength(40), view.FindResource("Dui.Metrics.ToolbarRowHeight"));
            Assert.Equal(32d, view.FindResource("Dui.Metrics.ControlHeight"));
            Assert.IsType<Thickness>(view.FindResource("Dui.Metrics.PanePadding"));
            Assert.IsType<CornerRadius>(view.FindResource("Dui.Metrics.TabRadius"));

            var save = Attach(new Button { Content = "Save" }, "Dui.Button.Primary");
            var icon = new AppIcon { Kind = AppIconKind.Save, Size = 20 };
            Attach(new Button { Content = icon }, "Dui.Button.Icon");
            var input = Attach(new TextBox { Text = "sample" }, "Dui.Input.TextBox");
            var combo = Attach(new ComboBox { IsEditable = true, IsReadOnly = true }, "Dui.Input.ComboBox");
            var enabled = Attach(new CheckBox { IsChecked = true }, "Dui.CheckBox");
            var radio = Attach(new RadioButton { IsChecked = true }, "Dui.Radio.Segment");
            var tab = Attach(new TabItem { Header = "File" }, "Dui.Tab");
            Attach(new ToggleButton { IsChecked = true }, "Dui.Toggle.Switch");
            Attach(new ToolBar(), "Dui.ToolBar");
            Attach(new ProgressBar(), "Dui.ProgressBar");
            Attach(new ContentControl { Content = "Empty" }, "Dui.EmptyState");
            Attach(new GridSplitter(), "Dui.GridSplitter");
            Attach(new ScrollBar { Orientation = Orientation.Horizontal }, "Dui.ScrollBar");
            Attach(new ScrollBar { Orientation = Orientation.Vertical }, "Dui.ScrollBar");

            view.Measure(new Size(720, 720));
            view.Arrange(new Rect(new Size(720, 720)));
            Assert.NotNull(input.Template.FindName("PART_ContentHost", input));
            Assert.NotNull(combo.Template.FindName("PART_EditableTextBox", combo));
            Assert.True(enabled.IsChecked);
            Assert.True(radio.IsChecked);
            Assert.False(icon.Focusable);
            Assert.Equal(20, icon.DesiredSize.Width);
            Assert.Equal("File", tab.Header);

            Assert.Equal(Brush(light, "Dui.Accent").Color, Assert.IsType<SolidColorBrush>(save.Background).Color);
            view.Resources.MergedDictionaries[0] = dark;
            Assert.Equal(Brush(dark, "Dui.Accent").Color, Assert.IsType<SolidColorBrush>(save.Background).Color);
            save.IsEnabled = false;
            Assert.Equal(Brush(dark, "Dui.State.Disabled").Color, Assert.IsType<SolidColorBrush>(save.Background).Color);
            Assert.Equal(Brush(dark, "Dui.Text.Disabled").Color, Assert.IsType<SolidColorBrush>(save.Foreground).Color);

            T Attach<T>(T control, string styleKey) where T : Control
            {
                control.Style = Assert.IsType<Style>(view.FindResource(styleKey));
                view.Children.Add(control);
                control.ApplyTemplate();
                return control;
            }
        });
    }

    [Fact]
    public void NativeMenusOpenWithSeparatorsAndThemedSubmenus()
    {
        RunSta(() =>
        {
            var palette = Load("Dark");
            var controls = Load("Controls");
            var submenu = new MenuItem { Header = "_Advanced" };
            var nestedSeparator = new Separator();
            submenu.Items.Add(new MenuItem { Header = "First" });
            submenu.Items.Add(nestedSeparator);
            submenu.Items.Add(new MenuItem { Header = "Last" });

            var menu = new ContextMenu { Style = Assert.IsType<Style>(controls["Dui.ContextMenu"]) };
            var separator = new Separator();
            menu.Items.Add(new MenuItem { Header = "Save", InputGestureText = "Ctrl+S" });
            menu.Items.Add(separator);
            menu.Items.Add(submenu);

            var target = new Button { Content = "Menu", ContextMenu = menu };
            var window = new Window
            {
                Content = target,
                Width = 200,
                Height = 160,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Resources.MergedDictionaries.Add(palette);
            window.Resources.MergedDictionaries.Add(controls);

            try
            {
                window.Show();
                menu.PlacementTarget = target;
                menu.IsOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                Assert.True(menu.IsOpen);
                Assert.Same(separator, menu.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Equal(MenuItemRole.SubmenuHeader, submenu.Role);
                Assert.Same(controls["Dui.MenuItem"], submenu.Style.BasedOn);

                submenu.IsSubmenuOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                var popup = Assert.IsType<Popup>(submenu.Template.FindName("PART_Popup", submenu));
                Assert.True(popup.IsOpen);
                var popupFrame = Assert.IsType<Border>(popup.Child);
                Assert.Equal(Brush(palette, "Dui.Surface.Content").Color,
                    Assert.IsType<SolidColorBrush>(popupFrame.Background).Color);
                Assert.Same(nestedSeparator, submenu.ItemContainerGenerator.ContainerFromIndex(1));
            }
            finally
            {
                menu.IsOpen = false;
                window.Close();
            }
        });
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/Differ.UI;component/Themes/{name}.xaml", UriKind.Absolute)
    };

    private static SolidColorBrush Brush(ResourceDictionary palette, string key) =>
        Assert.IsType<SolidColorBrush>(palette[key]);

    private static void AssertContrast(ResourceDictionary palette, string foreground, string background, double minimum)
    {
        double first = Luminance(Brush(palette, foreground).Color);
        double second = Luminance(Brush(palette, background).Color);
        double ratio = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(ratio >= minimum, $"{foreground} on {background}: {ratio:F2}:1 < {minimum:F1}:1.");
    }

    private static double Luminance(Color colour) =>
        0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

    private static double Linear(byte component)
    {
        double value = component / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static string LibraryProjectPath([CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "src", "Differ.UI", "Differ.UI.csproj");

    private static void RunSta(Action action) => WpfDispatcher.Value.Invoke(action);

    private static Dispatcher StartWpfDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                if (!ready.TrySetException(exception)) throw;
            }
        })
        {
            IsBackground = true,
            Name = "Differ.UI theme test host"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
