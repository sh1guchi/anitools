namespace Anitools.Core.Platform;

/// <summary>Снятие «только для чтения» с выходных файлов и папок (clear_readonly, py:1047).</summary>
public static class ReadOnlyAttr
{
    /// <summary>Windows — снять атрибут «только чтение»; Linux/macOS — дать владельцу право записи. Ошибки глушатся.</summary>
    public static void Clear(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            else
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserWrite);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Сама папка и всё внутри (clear_readonly_tree).</summary>
    public static void ClearTree(string path)
    {
        Clear(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                Clear(entry);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
