namespace Anitools.App.Startup;

/// <summary>С какой папкой открыться: полный путь или ошибка для показа в окне. Оба null — аргумента не было.</summary>
public sealed record StartupRequest(string? Folder, string? Error)
{
    public static StartupRequest None { get; } = new(null, null);
}

/// <summary>
/// Аргумент командной строки — рабочая папка (docs/PLAN.md §4.12): <c>Anitools.exe "D:\anime\X"</c>, его передаёт ani.bat.
/// Относительный путь — от текущей папки; файл — его папка; путь без кавычек с пробелами (ani D:\anime\Sousou no
/// Frieren) приходит несколькими аргументами — они склеиваются.
/// </summary>
public static class StartupArguments
{
    public static StartupRequest Parse(IReadOnlyList<string> args, string currentDirectory) =>
        Parse(args, currentDirectory, Directory.Exists, File.Exists);

    public static StartupRequest Parse(IReadOnlyList<string> args, string currentDirectory, Func<string, bool> directoryExists, Func<string, bool> fileExists)
    {
        var candidates = args.Count switch
        {
            0 => [],
            1 => [args[0]],
            _ => new[] { string.Join(' ', args), args[0] },
        };
        string? first = null;
        foreach (var candidate in candidates)
        {
            var cleaned = Clean(candidate);
            if (cleaned.Length == 0)
            {
                continue;
            }

            string full;
            try
            {
                full = Path.IsPathFullyQualified(cleaned) ? Path.GetFullPath(cleaned) : Path.GetFullPath(cleaned, currentDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                first ??= cleaned;
                continue;
            }

            if (directoryExists(full))
            {
                return new StartupRequest(TrimSeparator(full), null);
            }

            if (fileExists(full) && Path.GetDirectoryName(full) is { Length: > 0 } parent)
            {
                return new StartupRequest(TrimSeparator(parent), null);
            }

            first ??= full;
        }

        return first is null ? StartupRequest.None : new StartupRequest(null, $"Папка не найдена: {first}");
    }

    /// <summary>
    /// Пробелы и кавычки по краям прочь. <c>"D:\"</c> в командной строке превращается в <c>D:"</c> (обратная косая
    /// экранирует кавычку) — хвостовая кавычка срезается, а «D:» без косой — это корень диска, а не текущая папка на нём.
    /// </summary>
    public static string Clean(string arg)
    {
        var s = arg.Trim().Trim('"').Trim();
        return s.Length == 2 && char.IsAsciiLetter(s[0]) && s[1] == ':' ? s + "\\" : s;
    }

    /// <summary>Без хвостовой косой, кроме корня («D:\», «/»).</summary>
    private static string TrimSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
