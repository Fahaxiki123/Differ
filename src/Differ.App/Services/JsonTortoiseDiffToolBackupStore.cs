using System.IO;
using System.Text.Json;
using Differ.Svn.Abstractions;
using Differ.Svn.Models;

namespace Differ.App.Services;

public sealed class JsonTortoiseDiffToolBackupStore : ITortoiseDiffToolBackupStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _backupPath;
    private readonly bool _throwOnInvalidBackup;

    public JsonTortoiseDiffToolBackupStore()
        : this(throwOnInvalidBackup: false)
    {
    }

    public JsonTortoiseDiffToolBackupStore(bool throwOnInvalidBackup)
        : this(Path.Combine(
            AppRuntimeProfile.DataRoot,
            "TortoiseDiffToolsBackup.json"), throwOnInvalidBackup)
    {
    }

    internal JsonTortoiseDiffToolBackupStore(
        string backupPath,
        bool throwOnInvalidBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        _backupPath = Path.GetFullPath(backupPath);
        _throwOnInvalidBackup = throwOnInvalidBackup;
    }

    public TortoiseDiffToolBackup? Load()
    {
        try
        {
            var json = File.ReadAllText(_backupPath);
            var backup = JsonSerializer.Deserialize<TortoiseDiffToolBackup>(
                json,
                SerializerOptions)
                ?? throw new InvalidDataException(
                    "The TortoiseSVN diff-tool backup file is empty or invalid.");
            if (string.IsNullOrWhiteSpace(backup.InstalledCommand)
                || backup.OriginalValues is null
                || backup.SchemaVersion is < 1 or > 2)
            {
                throw new InvalidDataException("The TortoiseSVN backup is invalid.");
            }

            if (_throwOnInvalidBackup && backup.SchemaVersion >= 2)
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty(
                    nameof(TortoiseDiffToolBackup.OriginalGlobalCommand), out _))
                {
                    throw new InvalidDataException("The global tool backup is missing.");
                }
            }

            return backup;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (!_throwOnInvalidBackup
            && exception is JsonException or InvalidDataException)
        {
            // UI status tolerates corruption. Installer maintenance uses strict
            // reads so it cannot delete a still-bound application after an error.
            return null;
        }
    }

    public void Save(TortoiseDiffToolBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var directory = Path.GetDirectoryName(_backupPath)
            ?? throw new InvalidOperationException(
                "The TortoiseSVN backup directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_backupPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(backup, SerializerOptions));
            File.Move(temporaryPath, _backupPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_backupPath))
        {
            File.Delete(_backupPath);
        }
    }
}
