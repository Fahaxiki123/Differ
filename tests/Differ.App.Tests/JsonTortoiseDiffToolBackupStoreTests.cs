using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Differ.App.Services;
using Differ.Svn.Models;

namespace Differ.App.Tests;

public sealed class JsonTortoiseDiffToolBackupStoreTests
{
    [Fact]
    public void StrictLoadRejectsCorruptionInsteadOfTreatingItAsMissing()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var backupPath = Path.Combine(testRoot, "backup.json");
            File.WriteAllText(backupPath, "{ damaged");
            var store = new JsonTortoiseDiffToolBackupStore(backupPath, throwOnInvalidBackup: true);

            Assert.Throws<JsonException>(() => store.Load());
            Assert.Equal("{ damaged", File.ReadAllText(backupPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"CreatedUtc\":\"2026-09-25T00:00:00Z\",\"InstalledCommand\":null,\"OriginalValues\":{}}")]
    [InlineData("{\"CreatedUtc\":\"2026-09-25T00:00:00Z\",\"InstalledCommand\":\"tool\",\"OriginalValues\":null}")]
    [InlineData("{\"CreatedUtc\":\"2026-09-25T00:00:00Z\",\"InstalledCommand\":\"tool\",\"OriginalValues\":{}}")]
    public void StrictLoadRejectsSemanticallyInvalidBackup(string json)
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var backupPath = Path.Combine(testRoot, "backup.json");
            File.WriteAllText(backupPath, json);
            var store = new JsonTortoiseDiffToolBackupStore(backupPath, throwOnInvalidBackup: true);

            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void StrictLoadReturnsNullForMissingBackup()
    {
        var store = new JsonTortoiseDiffToolBackupStore(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json"),
            throwOnInvalidBackup: true);
        Assert.Null(store.Load());
    }

    [Fact]
    public void StrictLoadDoesNotHideAnUnreadableBackupAsMissing()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var backupPath = Path.Combine(testRoot, "backup.json");
            File.WriteAllText(backupPath, "{}");
            using var lockedFile = File.Open(backupPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var store = new JsonTortoiseDiffToolBackupStore(backupPath, throwOnInvalidBackup: true);

            Assert.Throws<IOException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadReturnsNullWhenBackupFileIsCorrupted()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var backupPath = Path.Combine(testRoot, "backup.json");
            File.WriteAllText(backupPath, "{ this is not valid json");
            var store = CreateStore(backupPath);

            Assert.Null(store.Load());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoadRoundTripPreservesOriginalValues()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var backupPath = Path.Combine(testRoot, "backup.json");
            var store = CreateStore(backupPath);
            var backup = new TortoiseDiffToolBackup
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                InstalledCommand = @"""C:\Tools\Differ.App.exe"" --base %base --mine %mine",
                OriginalGlobalCommand = @"""C:\Old\tool.exe"" %base %mine",
                OriginalValues = new Dictionary<string, string?>
                {
                    [".xlsx"] = null,
                    [".docx"] = @"""C:\Old\word.exe"" %base %mine"
                }
            };

            store.Save(backup);
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(backup.InstalledCommand, loaded!.InstalledCommand);
            Assert.Equal(backup.OriginalGlobalCommand, loaded.OriginalGlobalCommand);
            Assert.Null(loaded.OriginalValues[".xlsx"]);
            Assert.Equal(
                backup.OriginalValues[".docx"],
                loaded.OriginalValues[".docx"]);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static JsonTortoiseDiffToolBackupStore CreateStore(string backupPath)
    {
        return new JsonTortoiseDiffToolBackupStore(backupPath);
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Differ.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
