using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class ContextFoldTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FoldedGridRowOffersAnExecutableButton(bool spreadsheet)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var invoked = 0;
            var command = new RelayCommand(() => invoked++);
            var label = "expanded context";
            UserControl view = spreadsheet ? new SpreadsheetWorksheetDiffView() : new TextDiffView();
            var row = new DataGridRow
            {
                DataContext = new { IsCollapsed = true, CollapsedLabel = label, ExpandCommand = command },
                Style = Assert.IsType<Style>(view.Resources[spreadsheet ? "SpreadsheetRowStyle" : "TextDiffRowStyle"])
            };
            // Production's ordinary buttons have a 34-DIP minimum. Fold
            // controls must override it to fit the smaller virtualized row.
            row.Resources[typeof(Button)] = new Style(typeof(Button))
            {
                Setters = { new Setter(FrameworkElement.MinHeightProperty, 34d) }
            };
            row.ApplyTemplate();
            row.Measure(new Size(600, 40));
            row.Arrange(new Rect(0, 0, 600, row.DesiredSize.Height));
            row.UpdateLayout();

            var button = Assert.Single(Descendants<Button>(row));
            Assert.Same(command, button.Command);
            Assert.Equal(label, button.Content);
            button.Command.Execute(null);
            Assert.Equal(1, invoked);
            Assert.InRange(row.ActualHeight, 20, 32);
            Assert.True(button.ActualHeight <= row.ActualHeight);
        });
    }

    [Fact]
    public void FoldedWordBlockKeepsTheSameExpansionCommandAsItsAnchor()
    {
        var invoked = 0;
        var command = new RelayCommand(() => invoked++);
        var anchor = new TextDiffRowViewModel("fold", DiffKind.Unchanged, null, null, "fold", "fold",
            [new InlineDiffSegment("fold", false)], [new InlineDiffSegment("fold", false)])
        { IsCollapsed = true, HiddenCount = 12, ExpandCommand = command };
        var row = WordDisplayRowViewModel.CreateCollapsed(anchor);
        Assert.False(row.IsParagraph);
        Assert.False(row.IsTable);
        Assert.True(row.IsCollapsed);
        Assert.Equal(12, row.HiddenCount);
        Assert.Same(command, row.ExpandCommand);
        row.ExpandCommand!.Execute(null);
        Assert.Equal(1, invoked);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
