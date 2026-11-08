using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
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
