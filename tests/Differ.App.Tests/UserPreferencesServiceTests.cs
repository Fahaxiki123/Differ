using System.IO;
using System.Text.Json;
using Differ.App.Services;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class UserPreferencesServiceTests
{
    [Fact]
    public async Task SpreadsheetLayoutAndConfirmedColumnMappingsSurvivePreferenceReloadAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            var rule = new SpreadsheetRule
            {
                FilePattern = "W-*.xlsx",
                SheetName = "配置",
                HeaderRow = 4,
                DataStartRow = 7,
                AuxiliaryHeaderRows = [3, 5, 6],
                KeyColumns = ["A", "faction_id"],
                ColumnOverrides = [new("H", "H"), new(null, "I"), new("I", "J")],
                FrozenColumns = 2
            };
            using (var preferences = new UserPreferencesService(path))
                await preferences.SaveRulesAsync("excel", new DiffOptions { SpreadsheetRules = [rule] });
            using var loaded = new UserPreferencesService(path);
            var saved = Assert.Single((await loaded.LoadRulesAsync("excel"))!.SpreadsheetRules);
            Assert.Equal(JsonSerializer.Serialize(rule), JsonSerializer.Serialize(saved));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":1,\"Layout\":null,\"Rules\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Layout\":{\"ReadingScale\":1},\"Rules\":{}}")]
    public async Task MissingOrInvalidSettingsUseSafeDefaultsAsync(string json)
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            await File.WriteAllTextAsync(path, json);
            using var preferences = new UserPreferencesService(path);
            await preferences.LoadAsync();
            Assert.Equal(1, preferences.ReadingScale);
            Assert.Equal(1500, preferences.WindowWidth);
            Assert.True(double.IsNaN(preferences.WindowLeft));
            Assert.Equal(124, preferences.TextDetailHeight);
            Assert.Equal(104, preferences.SpreadsheetDetailHeight);
            Assert.False(preferences.TextDetailsExpanded);
            Assert.True(preferences.SpreadsheetDetailsExpanded);
            Assert.False(preferences.MergeShowBase);
            Assert.Null(await preferences.LoadRulesAsync("text"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AtomicLayoutAndPerFormatRulesPreserveEachOtherAndClampUnsafeGeometryAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var preferences = new UserPreferencesService(path)
            {
                ReadingScale = 50,
                WindowWidth = -100,
                TextDetailHeight = double.PositiveInfinity,
                SpreadsheetDetailHeight = double.NaN,
                WindowLeft = 42,
                WindowTop = 64
            };
            await Task.WhenAll(
                preferences.SaveRulesAsync("text", new DiffOptions { IgnoreCase = true, TextEncodingName = "utf-8" }),
                preferences.SaveRulesAsync("excel", new DiffOptions { KeyColumns = ["a", "C"], CompareFormatting = true }),
                preferences.SaveAsync());
            using var loaded = new UserPreferencesService(path);
            await loaded.LoadAsync();
            Assert.Equal(1.6, loaded.ReadingScale);
            Assert.Equal(720, loaded.WindowWidth);
            Assert.Equal(124, loaded.TextDetailHeight);
            Assert.Equal(104, loaded.SpreadsheetDetailHeight);
            Assert.Equal(42, loaded.WindowLeft);
            Assert.True((await loaded.LoadRulesAsync("text"))!.IgnoreCase);
            Assert.Equal(["A", "C"], (await loaded.LoadRulesAsync("excel"))!.KeyColumns);
            await loaded.RestoreRulesAsync("text");
            Assert.Null(await loaded.LoadRulesAsync("text"));
            Assert.NotNull(await loaded.LoadRulesAsync("excel"));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(72, 72)]
    [InlineData(124, 124)]
    [InlineData(72, 180)]
    public async Task LegacyHeightsArePreservedAndExpansionStatesUsePerFormatDefaultsAsync(double textHeight, double spreadsheetHeight)
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            var legacyJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Layout = new { TextDetailHeight = textHeight, SpreadsheetDetailHeight = spreadsheetHeight },
                Rules = new Dictionary<string, object>()
            });
            await File.WriteAllTextAsync(path, legacyJson);
            using var preferences = new UserPreferencesService(path);
            await preferences.LoadAsync();
            Assert.Equal(textHeight, preferences.TextDetailHeight);
            Assert.Equal(spreadsheetHeight, preferences.SpreadsheetDetailHeight);
            Assert.False(preferences.TextDetailsExpanded);
            Assert.True(preferences.SpreadsheetDetailsExpanded);

            preferences.TextDetailsExpanded = true;
            preferences.SpreadsheetDetailsExpanded = false;
            await preferences.SaveAsync();
            using var restored = new UserPreferencesService(path);
            await restored.LoadAsync();
            Assert.True(restored.TextDetailsExpanded);
            Assert.False(restored.SpreadsheetDetailsExpanded);
            Assert.Equal(textHeight, restored.TextDetailHeight);
            Assert.Equal(spreadsheetHeight, restored.SpreadsheetDetailHeight);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TemporarilyUnreadableSettingsAreNotReplacedWithDefaultRulesAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var preferences = new UserPreferencesService(path);
            await preferences.SaveRulesAsync("text", new DiffOptions { IgnoreCase = true });
            var original = await File.ReadAllTextAsync(path);
            using (var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => preferences.SaveAsync());
            }
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ArbitraryStringsCannotEnterSavedRulesAndCanceledSavePreservesPreviousFileAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var preferences = new UserPreferencesService(path);
            await preferences.SaveRulesAsync("text", new DiffOptions
            {
                TextEncodingName = @"E:\SECRET\private.txt",
                KeyColumns = ["DOCUMENT_CONTENT", @"E:\SECRET\private.txt", "A"]
            });
            var original = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SECRET", original, StringComparison.Ordinal);
            Assert.DoesNotContain("DOCUMENT_CONTENT", original, StringComparison.Ordinal);
            Assert.Null((await preferences.LoadRulesAsync("text"))!.TextEncodingName);
            await Assert.ThrowsAsync<ArgumentException>(() => preferences.SaveRulesAsync(@"E:\path", new DiffOptions()));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SaveAsync(cancellation.Token));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MergeVisibilityDefaultsHiddenAndPersistsWithoutChangingLayoutOrRulesAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var preferences = new UserPreferencesService(path);
            await preferences.LoadAsync();
            Assert.False(preferences.MergeShowBase);
            preferences.WindowWidth = 1234;
            preferences.SpreadsheetDetailHeight = 180;
            await preferences.SaveAsync();
            await preferences.SaveRulesAsync("text", new DiffOptions { IgnoreCase = true });
            await preferences.SetMergeShowBaseAsync(true);
            Assert.True(preferences.MergeShowBase);

            using var reopened = new UserPreferencesService(path);
            await reopened.LoadAsync();
            Assert.True(reopened.MergeShowBase);
            Assert.Equal(1234, reopened.WindowWidth);
            Assert.Equal(180, reopened.SpreadsheetDetailHeight);
            Assert.True((await reopened.LoadRulesAsync("text"))!.IgnoreCase);
            await reopened.SetMergeShowBaseAsync(false);
            using var hidden = new UserPreferencesService(path);
            await hidden.LoadAsync();
            Assert.False(hidden.MergeShowBase);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StaleDiffAndMergeInstancesOnlyWriteTheirOwnPreferenceFieldsAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var diff = new UserPreferencesService(path);
            using var merge = new UserPreferencesService(path);
            await diff.LoadAsync();
            await merge.LoadAsync();
            await merge.SetMergeShowBaseAsync(true);
            Assert.False(diff.MergeShowBase); // This process deliberately still has the old view.
            diff.WindowWidth = 1111;
            await diff.SaveAsync();
            await diff.SaveRulesAsync("excel", new DiffOptions { KeyColumns = ["B"] });
            using (var loaded = new UserPreferencesService(path))
            {
                await loaded.LoadAsync();
                Assert.True(loaded.MergeShowBase);
                Assert.Equal(1111, loaded.WindowWidth);
            }
            // A merge process's stale layout must not overwrite the later diff layout.
            await merge.SetMergeShowBaseAsync(false);
            using var final = new UserPreferencesService(path);
            await final.LoadAsync();
            Assert.False(final.MergeShowBase);
            Assert.Equal(1111, final.WindowWidth);
            Assert.Equal(["B"], (await final.LoadRulesAsync("excel"))!.KeyColumns);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentIndependentWritersPreserveMergeFlagLayoutAndRulesAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var diff = new UserPreferencesService(path);
            using var merge = new UserPreferencesService(path);
            using var rules = new UserPreferencesService(path);
            await Task.WhenAll(diff.LoadAsync(), merge.LoadAsync(), rules.LoadAsync());
            for (var index = 0; index < 12; index++)
            {
                var showBase = index % 2 == 0;
                diff.WindowWidth = 1000 + index;
                await Task.WhenAll(diff.SaveAsync(), merge.SetMergeShowBaseAsync(showBase),
                    rules.SaveRulesAsync("text", new DiffOptions { IgnoreCase = showBase }));
                using var latest = new UserPreferencesService(path);
                await latest.LoadAsync();
                Assert.Equal(showBase, latest.MergeShowBase);
                Assert.Equal(1000 + index, latest.WindowWidth);
                Assert.Equal(showBase, (await latest.LoadRulesAsync("text"))!.IgnoreCase);
                Assert.Single(Directory.GetFiles(root));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CanceledMergePreferenceWritePreservesFileAndInMemoryChoiceAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var preferences = new UserPreferencesService(path);
            await preferences.SetMergeShowBaseAsync(true);
            var original = await File.ReadAllTextAsync(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SetMergeShowBaseAsync(false, cancellation.Token));
            Assert.True(preferences.MergeShowBase);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellationWhileAnotherWriterHoldsLockDoesNotResetSettingsAsync()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            using var preferences = new UserPreferencesService(path);
            await preferences.SetMergeShowBaseAsync(true);
            var original = await File.ReadAllTextAsync(path);
            using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SetMergeShowBaseAsync(false, cancellation.Token));
            }
            Assert.True(preferences.MergeShowBase);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
