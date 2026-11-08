using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Controls;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class WordListLayoutRegressionTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ChangeGroupListsStaySeparatedAcrossReadingScalesAndFonts(double scale)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Row("12.345. ", 0, 142, 0, 30, "组内中文 English，窄窗换行仍然正确")
                with
            { Kind = DiffKind.Deleted, NewIndex = null, NewText = string.Empty, NewSegments = [] };
            var after = Row("• ", 0, 142, 0, 21, "更新后的中文内容")
                with
            { Kind = DiffKind.Added, OldIndex = null, OldText = string.Empty, OldSegments = [] };
            before = WithGroup(before, "宋体", true);
            after = WithGroup(after, "Calibri", false);
            var group = Assert.Single(WordDisplayRowViewModel.Build([before, after])).Group;
            Assert.NotNull(group);
            var view = new WordDiffView();
            var template = Assert.IsType<DataTemplate>(view.Resources["WordChangeGroupTemplate"]);
            var content = Assert.IsType<Grid>(template.LoadContent());
            content.DataContext = group;
            content.LayoutTransform = new ScaleTransform(scale, scale);
            var window = new Window
            {
                Content = content,
                Width = 720,
                Height = 560,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Light.xaml"));
            window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Metrics.xaml"));
            try
            {
                window.Show();
                Pump(window);
                var panels = Visuals<WordParagraphLayout>(content).ToArray();
                Assert.Equal(2, panels.Length);
                foreach (var panel in panels)
                {
                    var marker = Assert.IsType<TextBlock>(panel.Children[0]);
                    var host = Assert.IsType<WordParagraphHost>(panel.Children[1]);
                    var labelBounds = marker.TransformToAncestor(panel).TransformBounds(new Rect(marker.RenderSize));
                    var bodyBounds = host.TransformToAncestor(panel).TransformBounds(new Rect(host.RenderSize));
                    Assert.True(bodyBounds.Left >= labelBounds.Right + 3);
                    Assert.True(bodyBounds.Width > 20);
                    var plain = Assert.IsType<WordInlineDiffTextBlock>(host.Content);
                    Assert.Equal(marker.FontSize, plain.FontSize);
                    Assert.Equal(marker.FontFamily, plain.FontFamily);
                    Assert.Equal(marker.FontStyle, plain.FontStyle);
                }
            }
            finally { window.Close(); }
        });
    }

    private static TextDiffRowViewModel WithGroup(TextDiffRowViewModel row, string font, bool italic)
    {
        var metadata = row.Metadata.ToDictionary();
        metadata["word.changeGroup"] = "controlled-group";
        metadata["word.groupCorrespondence"] = "unresolved";
        foreach (var side in new[] { "old", "new" })
        {
            metadata[$"{side}.word.fontFamily"] = font;
            metadata[$"{side}.word.italic"] = italic.ToString();
        }
        return row with { Metadata = metadata };
    }

    [Theory]
    [InlineData("1. ", 0, 0, 0, 24, "中文标题 ABC")]
    [InlineData("• ", 0, 142, 0, 21, "请将阅读模式调整为【视图】——【Web版式】。")]
    [InlineData("12.345.678. ", 270, 0, 0, 30, "混合 English 与中文换行内容")]
    [InlineData("1. ", 720, 0, 360, 24, "已有正确悬挂缩进")]
    [InlineData("123.456.789. ", 1800, 1440, 0, 30, "极端缩进在窄窗保留可读正文")]
    public void ListMarkersAndBodyNeverShareTheSameHorizontalSpace(
        string marker, int left, int firstLine, int hanging, int halfPoints, string text)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var row = Row(marker, left, firstLine, hanging, halfPoints, text);
            var view = new WordDiffView();
            var template = Assert.IsType<DataTemplate>(view.Resources["WordDiffRowTemplate"]);
            var content = Assert.IsType<Grid>(template.LoadContent());
            content.DataContext = row;
            var window = new Window
            {
                Content = content,
                Width = 720,
                Height = 320,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Light.xaml"));
            window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Metrics.xaml"));
            try
            {
                window.Show();
                Pump(window);
                Check(content, marker);
                window.Width = 500;
                Pump(window);
                Check(content, marker);
                // Recycle the exact template into a longer marker and a larger font.
                content.DataContext = Row("123.456. ", 0, 142, 0, 36, "回收后的大字号中文 English");
                Pump(window);
                Check(content, "123.456. ");
            }
            finally { window.Close(); }
        });
    }

    private static void Check(Grid content, string marker)
    {
        foreach (var pane in content.Children.OfType<Border>().Where(pane => pane.Name is "BasePane" or "MinePane"))
        {
            var label = Assert.Single(Visuals<TextBlock>(pane), value => value is not WordInlineDiffTextBlock && value.Text == marker);
            var body = Assert.Single(Visuals<WordParagraphHost>(pane));
            var labelBounds = label.TransformToAncestor(pane).TransformBounds(new Rect(label.RenderSize));
            var bodyBounds = body.TransformToAncestor(pane).TransformBounds(new Rect(body.RenderSize));
            Assert.True(bodyBounds.Left >= labelBounds.Right + 3,
                $"List marker overlaps text: marker={labelBounds}; body={bodyBounds}; marker text={marker}");
            Assert.True(bodyBounds.Width > 20, $"Body has no readable width: {bodyBounds}");
            Assert.True(bodyBounds.Right <= pane.ActualWidth + 0.1);
        }
    }

    private static TextDiffRowViewModel Row(string marker, int left, int firstLine, int hanging, int halfPoints, string text)
    {
        var metadata = new Dictionary<string, string>();
        foreach (var side in new[] { "old", "new" })
        {
            metadata[$"{side}.word.listMarker"] = marker;
            metadata[$"{side}.word.leftTwips"] = left.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata[$"{side}.word.firstLineTwips"] = firstLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata[$"{side}.word.hangingTwips"] = hanging.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata[$"{side}.word.fontSizeHalfPoints"] = halfPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata[$"{side}.word.fontFamily"] = "Microsoft YaHei UI";
        }
        return new TextDiffRowViewModel("list", DiffKind.Unchanged, 1, 1, text, text,
            [new InlineDiffSegment(text, false)], [new InlineDiffSegment(text, false)])
        { Metadata = metadata };
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }

    private static void Pump(Window window)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
