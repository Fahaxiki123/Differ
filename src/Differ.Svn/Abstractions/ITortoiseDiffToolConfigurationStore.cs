namespace Differ.Svn.Abstractions;

public interface ITortoiseDiffToolConfigurationStore
{
    string? ReadGlobal();

    void WriteGlobal(string command);

    void DeleteGlobal();

    string? Read(string extension);

    void Write(string extension, string command);

    void Delete(string extension);
}
