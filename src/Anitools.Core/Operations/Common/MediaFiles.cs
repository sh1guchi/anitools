using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Common;

/// <summary>Файлы рабочей папки и имена выходных файлов.</summary>
public static class MediaFiles
{
    /// <summary>Видео для п.1, п.3, п.4.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [".mkv", ".mp4", ".hevc", ".avi", ".h264", ".m2ts", ".ogm", ".mpg", ".mov"];

    /// <summary>
    /// Файлы верхнего уровня с расширением из списка (без учёта регистра) — полные пути, по имени без учёта
    /// регистра (как отдаёт их NTFS: оригинал брал порядок os.listdir). Папки не берутся.
    /// </summary>
    public static IReadOnlyList<string> List(string folder, IEnumerable<string> extensions)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(folder)
            .Where(path => wanted.Contains(Suffix(Path.GetFileName(path))))
            .Order(Comparer<string>.Create((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b))))
            .ToList();
    }

    /// <summary>Path(name).suffix из Python: «.mkv», у «.mkv» и «x.» — пусто.</summary>
    public static string Suffix(string name) => name[TextUtils.Stem(name).Length..];

    /// <summary>name.rsplit('.', 1)[0] — имя без последнего расширения, как в п.2–4.</summary>
    public static string WithoutLastExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>«Уже готово»: файл есть и не пустой.</summary>
    public static bool IsDone(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }
}
