using System.Globalization;
using System.Text.Json;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Rename;

/// <summary>Настройки п.5 — те же вопросы, что в оригинале.</summary>
public sealed record RenameOptions
{
    /// <summary>Базовое название как ввели или как пришло с Shikimori; к имени файла приводится само («:» → « - » и т.п.).</summary>
    public required string BaseName { get; init; }

    /// <summary>С какого номера начинается исходная нумерация (12 → серия 12 станет 01).</summary>
    public int NumberingStart { get; init; } = 1;

    /// <summary>Приписка через точку: «надписи» → «Тайтл - 01.надписи.ass»; ведущая точка убирается.</summary>
    public string? Suffix { get; init; }

    /// <summary>
    /// Номера, введённые вручную: файл → текст. Число — номер серии как есть (без сдвига нумерации, как в оригинале);
    /// пусто или не число — файл пропускается. Файлов нет в словаре — номер, найденный автоматически.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ManualNumbers { get; init; }
}

public enum RenameRowStatus
{
    Rename,

    /// <summary>Уже так называется.</summary>
    Unchanged,

    /// <summary>Номер не найден или пропущен вручную.</summary>
    NoNumber,

    /// <summary>Новое имя занято или совпадает у нескольких файлов.</summary>
    Conflict,
}

/// <summary>Строка таблицы переименования.</summary>
/// <param name="Episode">Номер серии (две цифры и больше) или null.</param>
public sealed record RenameRow(string File, string? Episode, string? NewName, RenameRowStatus Status, string? Reason = null);

/// <summary>Итог: что переименовано (старое → новое) и что не вышло (файл → причина).</summary>
public sealed record RenameResult(IReadOnlyList<(string Old, string New)> Renamed, IReadOnlyList<(string File, string Error)> Failed, string? JournalPath);

/// <summary>П.5 «Переименовать файлы» (rename_files_by_pattern, py:3199): «Название - 01.ext» по номеру серии.</summary>
public static class RenameOperation
{
    private static readonly string[] HintVideoExtensions = [".mkv", ".mp4", ".avi", ".mov", ".m2ts", ".ts", ".webm"];

    /// <summary>Все файлы папки, кроме .bat, по коду символа (как sort() в оригинале).</summary>
    public static IReadOnlyList<string> ListFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .Order(PyText.CodePointComparer)
            .ToList();

    /// <summary>Подсказка для поиска на Shikimori: тайтл из первого видео (если видео нет — из первого файла).</summary>
    public static string TitleHint(IReadOnlyList<string> files)
    {
        var videos = files.Where(f => HintVideoExtensions.Contains(MediaFiles.Suffix(f), StringComparer.OrdinalIgnoreCase)).ToList();
        var first = (videos.Count > 0 ? videos : files)[0];
        var title = TitleText.AnimeTitle(first);
        return title.Length > 0 ? title : PyText.Stem(first);
    }

    /// <summary>Номер с учётом начала нумерации: «13» при начале 12 → «02»; не больше нуля — null.</summary>
    public static string? Adjust(string? raw, int numberingStart)
    {
        if (raw is null)
        {
            return null;
        }

        try
        {
            var adjusted = PyText.ParseInt(raw) - (numberingStart - 1);
            return adjusted > 0 ? PyText.Pad2(adjusted) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Номер, найденный автоматически (как в режиме «автоматически»).</summary>
    public static string? AutoEpisode(string file, int numberingStart) => Adjust(EpisodeNumber.Extract(file), numberingStart);

    /// <summary>Номер, который ручной режим оригинала предлагает по Enter: имя без «.надписи.ass».</summary>
    public static string? ManualSuggestion(string file, int numberingStart) =>
        Adjust(EpisodeNumber.Extract(file.Replace(".надписи.ass", "", StringComparison.Ordinal)), numberingStart);

    public static IReadOnlyList<RenameRow> Plan(string folder, IReadOnlyList<string> files, RenameOptions options)
    {
        if (options.NumberingStart < 1)
        {
            throw new PlanException("Начало нумерации — положительное целое число.");
        }

        var baseName = TitleText.FileNameSafe(options.BaseName);
        if (baseName.Length == 0)
        {
            throw new PlanException("Базовое название не может быть пустым.");
        }

        var suffix = options.Suffix is { Length: > 0 } s && s.StartsWith('.') ? s[1..] : options.Suffix ?? "";
        var rows = new List<RenameRow>();
        foreach (var file in files)
        {
            string? episode;
            if (options.ManualNumbers is not null && options.ManualNumbers.TryGetValue(file, out var typed))
            {
                episode = ParseManual(typed);
            }
            else
            {
                episode = AutoEpisode(file, options.NumberingStart);
            }

            if (episode is null)
            {
                rows.Add(new RenameRow(file, null, null, RenameRowStatus.NoNumber, "номер серии не найден — файл пропускается"));
                continue;
            }

            var ext = PyText.SplitExt(file).Ext;
            var newName = suffix.Length > 0 ? $"{baseName} - {episode}.{suffix}{ext}" : $"{baseName} - {episode}{ext}";
            rows.Add(new RenameRow(file, episode, newName, newName == file ? RenameRowStatus.Unchanged : RenameRowStatus.Rename));
        }

        return MarkConflicts(folder, rows);
    }

    /// <summary>
    /// Переименовывает строки со статусом Rename по порядку. Новое имя занято другим файлом — ошибка этого файла
    /// (смена только регистра букв — не конфликт). Успешные пары пишутся в журнал для отката.
    /// </summary>
    public static RenameResult Execute(string folder, IReadOnlyList<RenameRow> rows, string journalDirectory, DateTime? now = null)
    {
        var renamed = new List<(string, string)>();
        var failed = new List<(string, string)>();
        foreach (var row in rows.Where(r => r.Status == RenameRowStatus.Rename))
        {
            var oldPath = Path.Combine(folder, row.File);
            var newPath = Path.Combine(folder, row.NewName!);
            try
            {
                if (Exists(newPath) && !SameFile(oldPath, newPath))
                {
                    failed.Add((row.File, $"Файл {row.NewName} уже существует"));
                    continue;
                }

                File.Move(oldPath, newPath);
                renamed.Add((row.File, row.NewName!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((row.File, ex.Message));
            }
        }

        var journal = renamed.Count > 0 ? RenameJournal.Write(journalDirectory, folder, renamed, now ?? DateTime.Now) : null;
        return new RenameResult(renamed, failed, journal);
    }

    private static string? ParseManual(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        try
        {
            return PyText.Pad2(PyText.ParseInt(typed));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Одинаковое новое имя у нескольких файлов или имя уже занято другим файлом папки — конфликт.</summary>
    private static List<RenameRow> MarkConflicts(string folder, List<RenameRow> rows)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var targets = rows.Where(r => r.NewName is not null).GroupBy(r => r.NewName!, comparer).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(comparer);
        var leaving = rows.Where(r => r.Status == RenameRowStatus.Rename).Select(r => r.File).ToHashSet(comparer);
        return rows.Select(r =>
        {
            if (r.Status != RenameRowStatus.Rename)
            {
                return r;
            }

            if (targets.Contains(r.NewName!))
            {
                return r with { Status = RenameRowStatus.Conflict, Reason = "такое же новое имя получается у другого файла" };
            }

            var target = Path.Combine(folder, r.NewName!);
            return Exists(target) && !SameFile(Path.Combine(folder, r.File), target) && !leaving.Contains(r.NewName!)
                ? r with { Status = RenameRowStatus.Conflict, Reason = $"файл {r.NewName} уже существует" }
                : r;
        }).ToList();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Журнал переименования «rename_&lt;дата&gt;.json» в папке логов — чтобы откатить последнее переименование.</summary>
public static class RenameJournal
{
    private sealed record Entry(string Old, string New);

    private sealed record Journal(string Folder, DateTime Time, IReadOnlyList<Entry> Renames);

    public static string Write(string directory, string folder, IReadOnlyList<(string Old, string New)> renames, DateTime now)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"rename_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"rename_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}_{n}.json");
        }

        var journal = new Journal(folder, now, renames.Select(r => new Entry(r.Old, r.New)).ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Самый свежий журнал (по имени файла) или null.</summary>
    public static string? Latest(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "rename_*.json").OrderBy(p => p, StringComparer.Ordinal).LastOrDefault()
            : null;

    /// <summary>Откат: новое → старое, в обратном порядке. Занятые имена не трогаются (ошибка по файлу).</summary>
    public static RenameResult Undo(string journalPath)
    {
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath))
            ?? throw new InvalidDataException("Пустой журнал переименования");
        var restored = new List<(string, string)>();
        var failed = new List<(string, string)>();
        foreach (var entry in journal.Renames.Reverse())
        {
            var from = Path.Combine(journal.Folder, entry.New);
            var to = Path.Combine(journal.Folder, entry.Old);
            try
            {
                if (!File.Exists(from))
                {
                    failed.Add((entry.New, "файла уже нет"));
                }
                else if (File.Exists(to) && !string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add((entry.New, $"имя {entry.Old} уже занято"));
                }
                else
                {
                    File.Move(from, to);
                    restored.Add((entry.New, entry.Old));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((entry.New, ex.Message));
            }
        }

        if (failed.Count == 0)
        {
            File.Delete(journalPath);
        }

        return new RenameResult(restored, failed, failed.Count == 0 ? null : journalPath);
    }
}
