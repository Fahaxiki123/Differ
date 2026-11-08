using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class SpreadsheetRuleEditorTests
{
    [Fact]
    public void DoneCommitsPendingLayoutEditsAndDeletingTheLastRuleStaysDeleted()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var dialog = new SpreadsheetRulesWindow([])
            {
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false
            };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                var fields = Find<TextBox>(dialog).ToArray();
                fields.Single(box => AutomationProperties.GetName(box).StartsWith("字段名行", StringComparison.Ordinal)).Text = "4";
                fields.Single(box => AutomationProperties.GetName(box) == "数据起始行").Text = "7";
                Find<Button>(dialog).Single(button => button.Content as string == "完成")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            Assert.True(dialog.ShowDialog());
            var saved = Assert.Single(dialog.Rules);
            Assert.Equal(4, saved.HeaderRow); Assert.Equal(7, saved.DataStartRow);

            var removal = new SpreadsheetRulesWindow([saved])
            {
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false
            };
            removal.Loaded += (_, _) => removal.Dispatcher.BeginInvoke(() =>
            {
                Find<Button>(removal).Single(button => button.Content as string == "删除这一条")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<Button>(removal).Single(button => button.Content as string == "完成")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            Assert.True(removal.ShowDialog());
            Assert.Empty(removal.Rules);
        });
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
