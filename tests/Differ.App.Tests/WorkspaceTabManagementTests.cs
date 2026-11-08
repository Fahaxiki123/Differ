using System.IO;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class WorkspaceTabManagementTests
{
    [Fact]
    public async Task FilteringCyclingAndClosingManageOnlyOwnedSessionsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseline = Path.Combine(root, "base.txt");
            await File.WriteAllTextAsync(baseline, "base");
            var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
            using var scheduler = new ComparisonLoadScheduler();
            using var preferences = new UserPreferencesService(Path.Combine(root, "preferences.json"));
            using var workspace = new ComparisonWorkspaceViewModel(
                new DiffCoordinator([new SessionActionsTests.ControlledAdapter()]), scheduler,
                store, NullLoggerFactory.Instance, preferences);
            var sessions = new List<ExternalDiffSession>();
            foreach (var name in new[] { "alpha.txt", "beta.txt", "gamma.txt" })
            {
                var mine = Path.Combine(root, name);
                await File.WriteAllTextAsync(mine, name);
                var session = await store.CreateAsync(new ExternalDiffRequest
                {
                    BaseFilePath = baseline,
                    MineFilePath = mine
                }, CancellationToken.None);
                sessions.Add(session);
                workspace.AddSession(session);
            }

            Assert.Equal(3, workspace.VisibleTabs.Count);
            workspace.NextTabCommand.Execute(null);
            Assert.Equal("alpha.txt", workspace.SelectedTab!.FileName);
            workspace.PreviousTabCommand.Execute(null);
            Assert.Equal("gamma.txt", workspace.SelectedTab!.FileName);
            workspace.FilterText = "BETA";
            Assert.Single(workspace.VisibleTabs);
            Assert.Equal("beta.txt", workspace.SelectedTab!.FileName);
            Assert.Equal(3, workspace.Tabs.Count);
            workspace.CloseOtherTabsCommand.Execute(null);
            Assert.Single(workspace.Tabs);
            Assert.False(Directory.Exists(sessions[0].SessionDirectoryPath));
            Assert.True(Directory.Exists(sessions[1].SessionDirectoryPath));
            Assert.False(Directory.Exists(sessions[2].SessionDirectoryPath));
            workspace.CloseAllTabsCommand.Execute(null);
            Assert.Empty(workspace.Tabs);
            Assert.Null(workspace.SelectedTab);
            Assert.Empty(Directory.GetDirectories(store.RootPath));
            Assert.Equal("base", await File.ReadAllTextAsync(baseline));
            Assert.Equal("beta.txt", await File.ReadAllTextAsync(Path.Combine(root, "beta.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
