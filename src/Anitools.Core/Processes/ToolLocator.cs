namespace Anitools.Core.Processes;

/// <summary>Внешние программы, которые нужны anitools.</summary>
public enum Tool
{
    Ffmpeg,
    Ffprobe,
    Mkvmerge,
    Mkvextract,
    Imdisk,
}

/// <summary>Окружение для поиска программ (в тестах подменяется).</summary>
public sealed class ToolEnvironment
{
    public required Func<string, string?> GetVariable { get; init; }

    public required bool IsWindows { get; init; }

    public static ToolEnvironment Current { get; } = new()
    {
        GetVariable = Environment.GetEnvironmentVariable,
        IsWindows = OperatingSystem.IsWindows(),
    };
}

/// <summary>
/// Поиск программ (docs/PLAN.md §3.5): путь из настроек → FFMPEG_PATH / FFPROBE_PATH → PATH → известные
/// папки (MKVToolNix в Program Files, ImDisk в System32). Шим Chocolatey (…\chocolatey\bin\ffmpeg.exe)
/// подменяется настоящим exe из …\chocolatey\lib\…\tools\…: иначе при отмене убивался бы только шим.
/// </summary>
public sealed class ToolLocator(ToolEnvironment environment)
{
    public ToolLocator()
        : this(ToolEnvironment.Current)
    {
    }

    /// <summary>Полный путь к программе или null, если её нигде нет.</summary>
    /// <param name="configured">Путь из настроек: файл или папка с программой; пусто — искать самим.</param>
    public string? Find(Tool tool, string? configured = null)
    {
        var fileName = FileName(tool);
        foreach (var candidate in Candidates(tool, fileName, configured))
        {
            if (File.Exists(candidate))
            {
                return ResolveChocolateyShim(Path.GetFullPath(candidate), fileName);
            }
        }

        return null;
    }

    /// <summary>Путь из настроек, если программа по нему есть (файл или папка с ней); иначе null.</summary>
    public string? ConfiguredPath(Tool tool, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var path = Directory.Exists(configured) ? Path.Combine(configured, FileName(tool)) : configured;
        return File.Exists(path) ? path : null;
    }

    private string FileName(Tool tool)
    {
        var name = tool switch
        {
            Tool.Ffmpeg => "ffmpeg",
            Tool.Ffprobe => "ffprobe",
            Tool.Mkvmerge => "mkvmerge",
            Tool.Mkvextract => "mkvextract",
            Tool.Imdisk => "imdisk",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
        return environment.IsWindows ? name + ".exe" : name;
    }

    private IEnumerable<string> Candidates(Tool tool, string fileName, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Directory.Exists(configured) ? Path.Combine(configured, fileName) : configured;
        }

        var variable = tool switch
        {
            Tool.Ffmpeg => "FFMPEG_PATH",
            Tool.Ffprobe => "FFPROBE_PATH",
            _ => null,
        };
        if (variable is not null && environment.GetVariable(variable) is { Length: > 0 } fromEnv)
        {
            // Полный путь или просто имя программы из PATH
            if (fromEnv.Contains('/', StringComparison.Ordinal) || fromEnv.Contains('\\', StringComparison.Ordinal))
            {
                yield return fromEnv;
            }
            else
            {
                foreach (var p in InPath(environment.IsWindows && !fromEnv.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fromEnv + ".exe" : fromEnv))
                {
                    yield return p;
                }
            }
        }

        foreach (var p in InPath(fileName))
        {
            yield return p;
        }

        if (!environment.IsWindows)
        {
            yield break;
        }

        if (tool is Tool.Mkvmerge or Tool.Mkvextract)
        {
            foreach (var root in new[] { environment.GetVariable("ProgramFiles"), environment.GetVariable("ProgramFiles(x86)") })
            {
                if (!string.IsNullOrEmpty(root))
                {
                    yield return Path.Combine(root, "MKVToolNix", fileName);
                }
            }
        }

        if (tool == Tool.Imdisk && environment.GetVariable("SystemRoot") is { Length: > 0 } systemRoot)
        {
            yield return Path.Combine(systemRoot, "System32", fileName);
        }
    }

    private IEnumerable<string> InPath(string fileName)
    {
        var path = environment.GetVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(dir.Trim('"'), fileName);
        }
    }

    /// <summary>…\chocolatey\bin\X.exe → …\chocolatey\lib\&lt;пакет&gt;\tools\…\X.exe, если он есть.</summary>
    private static string ResolveChocolateyShim(string path, string fileName)
    {
        var bin = Path.GetDirectoryName(path);
        var chocolatey = bin is null ? null : Path.GetDirectoryName(bin);
        if (bin is null || chocolatey is null
            || !string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(chocolatey), "chocolatey", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var lib = Path.Combine(chocolatey, "lib");
        if (!Directory.Exists(lib))
        {
            return path;
        }

        try
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var family = stem is "ffprobe" ? "ffmpeg" : stem; // ffprobe лежит в пакете ffmpeg
            var real = Directory.EnumerateDirectories(lib)
                .OrderByDescending(pkg => Path.GetFileName(pkg).StartsWith(family, StringComparison.OrdinalIgnoreCase))
                .ThenBy(pkg => Path.GetFileName(pkg), StringComparer.OrdinalIgnoreCase)
                .Select(pkg => Path.Combine(pkg, "tools"))
                .Where(Directory.Exists)
                .SelectMany(tools => Directory.EnumerateFiles(tools, fileName, SearchOption.AllDirectories))
                .FirstOrDefault();
            return real ?? path;
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }
    }
}

/// <summary>Найденные пути программ; null — программы нет.</summary>
public sealed record ToolPaths(string? Ffmpeg, string? Ffprobe, string? Mkvmerge, string? Mkvextract)
{
    public static ToolPaths Find(ToolLocator locator) => Find(locator, _ => null);

    /// <param name="configured">Путь из настроек для программы; пусто или null — искать самим.</param>
    public static ToolPaths Find(ToolLocator locator, Func<Tool, string?> configured) => new(
        locator.Find(Tool.Ffmpeg, configured(Tool.Ffmpeg)),
        locator.Find(Tool.Ffprobe, configured(Tool.Ffprobe)),
        locator.Find(Tool.Mkvmerge, configured(Tool.Mkvmerge)),
        locator.Find(Tool.Mkvextract, configured(Tool.Mkvextract)));
}
