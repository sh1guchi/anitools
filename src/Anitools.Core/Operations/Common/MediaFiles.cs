using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Common;

/// <summary>Файлы рабочей папки и имена выходных файлов.</summary>
public static class MediaFiles
{
    /// <summary>Видео для «Только видео», «Сборки аудио» и «Субтитров».</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [".mkv", ".mp4", ".hevc", ".avi", ".h264", ".m2ts", ".ogm", ".mpg", ".mov"];

    /// <summary>
    /// Файлы верхнего уровня с расширением из списка (без учёта регистра) — полные пути, по имени без учёта
    /// регистра (как их отдаёт NTFS). Папки не берутся.
    /// </summary>
    public static IReadOnlyList<string> List(string folder, IEnumerable<string> extensions)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(folder)
            .Where(path => wanted.Contains(Suffix(Path.GetFileName(path))))
            .Order(Comparer<string>.Create((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b))))
            .ToList();
    }

    /// <summary>Последний суффикс имени: «a.b.mkv» → «.mkv», у «.mkv» и «x.» — пусто.</summary>
    public static string Suffix(string name) => name[TextUtils.Stem(name).Length..];

    /// <summary>Имя без последнего расширения (всё до последней точки) — для выходов «Только аудио», «Сборки аудио» и «Субтитров».</summary>
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
