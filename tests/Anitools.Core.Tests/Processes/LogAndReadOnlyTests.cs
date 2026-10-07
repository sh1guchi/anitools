using Anitools.Core.Logging;
using Anitools.Core.Platform;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Processes;

public sealed class LogAndReadOnlyTests
{
    [Fact]
    public void Error_log_has_command_code_and_output()
    {
        using var dir = new TempDir();
        var writer = new ErrorLogWriter(dir.Path, () => new DateTime(2026, 10, 7, 20, 15, 0, 123));
        var path = writer.WriteProcessError(
            "Frieren - 01: Заголовок?", "  строка 1\nстрока 2  \n", ["ffmpeg", "-i", "a b.mkv"], 1);

        Assert.NotNull(path);
        Assert.Equal("Frieren_-_01__Заголовок__ffmpeg_error_2026-10-07_20-15-00.log", Path.GetFileName(path));
        Assert.Equal(
            "Time: 2026-10-07T20:15:00.123000\nCommand: ffmpeg -i \"a b.mkv\"\nReturn code: 1\n\n"
            + "--- output (stdout+stderr, tail) ---\nстрока 1\nстрока 2\n",
            File.ReadAllText(path));

        // в ту же секунду — второй файл рядом, а не поверх
        var second = writer.WriteProcessError("Frieren - 01: Заголовок?", "", null, null);
        Assert.EndsWith("_2.log", second);
        Assert.Contains("(процесс не вывел ничего", File.ReadAllText(second!));
    }

    [Fact]
    public void ReadOnly_is_cleared_on_file_and_tree()
    {
        using var dir = new TempDir();
        var file = dir.File("out/a.mka", "x");
        var nested = dir.File("out/sub/b.mka", "y");
        MakeReadOnly(file);
        MakeReadOnly(nested);

        ReadOnlyAttr.Clear(file);
        Assert.False(IsReadOnly(file));

        ReadOnlyAttr.ClearTree(dir.Combine("out"));
        Assert.False(IsReadOnly(nested));

        ReadOnlyAttr.Clear(dir.Combine("missing")); // без исключения
    }

    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        }
    }

    private static bool IsReadOnly(string path) =>
        OperatingSystem.IsWindows()
            ? File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)
            : !File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserWrite);
}
