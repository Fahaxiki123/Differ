using Differ.Svn.Models;

namespace Differ.Svn.Abstractions;

public interface ITortoiseDiffToolBackupStore
{
    TortoiseDiffToolBackup? Load();

    void Save(TortoiseDiffToolBackup backup);

    void Delete();
}
