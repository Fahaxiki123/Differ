using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Differ.App.Controls;

namespace Differ.App.Tests;

public sealed class PersistentContentHostTests
{
    [Fact]
    public void SwitchingItemsReusesPresenterAndRemovalReleasesCache()
    {
        RunSta(() =>
        {
            var first = new object();
            var second = new object();
            var items = new ObservableCollection<object> { first, second };
            var host = new PersistentContentHost
            {
                ItemsSource = items,
                SelectedItem = first
            };
            var firstPresenter = Assert.IsType<ContentPresenter>(
                Assert.Single(host.Children));

            host.SelectedItem = second;
            Assert.Equal(2, host.CachedContentCount);
            host.SelectedItem = first;

            Assert.Same(firstPresenter, host.Children[0]);
            Assert.Equal(2, host.CachedContentCount);

            items.Remove(first);
            Assert.Equal(1, host.CachedContentCount);
            Assert.DoesNotContain(firstPresenter, host.Children.Cast<object>());
            Assert.Null(firstPresenter.Content);
        });
    }

    [Fact]
    public void PreservedWorksheetLayoutIsLimitedToTwoInactiveVisitedItems()
    {
        RunSta(() =>
        {
            var items = new ObservableCollection<object>(Enumerable.Range(0, 5).Select(_ => new object()));
            var host = new PersistentContentHost
            {
                PreserveRecentLayout = true,
                ItemsSource = items,
                SelectedItem = items[0]
            };

            Assert.Equal(1, host.CachedContentCount);
            for (var index = 1; index < 4; index++) host.SelectedItem = items[index];

            var visited = host.Children.Cast<ContentPresenter>().ToArray();
            Assert.Equal(4, visited.Length);
            Assert.Equal(Visibility.Collapsed, visited[0].Visibility);
            Assert.Equal(Visibility.Hidden, visited[1].Visibility);
            Assert.Equal(Visibility.Hidden, visited[2].Visibility);
            Assert.Equal(Visibility.Visible, visited[3].Visibility);

            host.SelectedItem = items[1];
            Assert.Equal(Visibility.Visible, visited[1].Visibility);
            Assert.Equal(Visibility.Hidden, visited[3].Visibility);
            items.RemoveAt(3);
            Assert.Null(visited[3].Content);
            host.SelectedItem = null;
            Assert.All(host.Children.Cast<ContentPresenter>(), child => Assert.Equal(Visibility.Collapsed, child.Visibility));
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
