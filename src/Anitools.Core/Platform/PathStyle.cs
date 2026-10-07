namespace Anitools.Core.Platform;

/// <summary>
/// Склейка путей для аргументов команд по правилам Windows или POSIX — как pathlib в оригинале.
/// Для работы всегда <see cref="Current"/>; другая нужна тестам, чтобы сверять команды с эталонами обеих ОС.
/// </summary>
public sealed class PathStyle
{
    private PathStyle(char separator) => Separator = separator;

    public static PathStyle Windows { get; } = new('\\');

    public static PathStyle Posix { get; } = new('/');

    public static PathStyle Current => OperatingSystem.IsWindows() ? Windows : Posix;

    public char Separator { get; }

    /// <summary>dir / name / … (имена — без разделителей внутри).</summary>
    public string Join(string directory, params string[] names)
    {
        var path = directory;
        foreach (var name in names)
        {
            path = IsSeparator(path.Length > 0 ? path[^1] : '\0') ? path + name : path + Separator + name;
        }

        return path;
    }

    /// <summary>Path.as_posix(): у Windows обратные слеши → прямые, у POSIX — как есть.</summary>
    public string AsPosix(string path) => Separator == '\\' ? path.Replace('\\', '/') : path;

    private bool IsSeparator(char c) => c == '/' || (Separator == '\\' && c == '\\');
}
