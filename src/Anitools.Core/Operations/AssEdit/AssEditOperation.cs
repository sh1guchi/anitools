using System.Globalization;
using System.Text;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.AssEdit;

/// <summary>По какому полю строки Dialogue фильтровать.</summary>
public enum AssField
{
    /// <summary>Стиль (поле 3).</summary>
    Style = 3,

    /// <summary>Актёр, Name (поле 4).</summary>
    Actor = 4,
}

/// <summary>Значение поля и файл, где оно встретилось первым (пустое — «(пусто)»).</summary>
public sealed record AssFieldValue(string Value, string ExampleFile);

/// <summary>Что есть в .ass папки: файлы, значения поля по порядку кодов символов, нечитаемые файлы.</summary>
public sealed record AssEditInspection(string Folder, AssField Field, IReadOnlyList<string> Files, IReadOnlyList<AssFieldValue> Values, IReadOnlyList<(string File, string Error)> Unreadable);

/// <summary>Итог правки: сколько строк убрано в каждом файле и куда сохранены оригиналы.</summary>
public sealed record AssEditResult(IReadOnlyList<(string File, int Removed)> Files, string BackupFolder, IReadOnlyList<(string File, string Error)> Failed)
{
    public int TotalRemoved => Files.Sum(f => f.Removed);
}

/// <summary>
/// Чистка .ass по стилю или актёру: оставить выбранные значения или удалить выбранные. Строки Dialogue
/// делятся просто по запятой. Файлы правятся на месте (UTF-8 с BOM), но сначала оригиналы копируются
/// в ass_backup_&lt;дата&gt;; переводы строк сохраняются как в файле.
/// </summary>
public static class AssEditOperation
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    /// <summary>Все .ass папки (без учёта регистра расширения) и уникальные значения поля.</summary>
    public static AssEditInspection Inspect(string folder, AssField field)
    {
        var files = MediaFiles.List(folder, [".ass"]);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new List<(string, string)>();
        var readable = new List<string>();
        foreach (var file in files)
        {
            if (!TryRead(file, out var text, out var error))
            {
                unreadable.Add((file, error));
                continue;
            }

            readable.Add(file);
            foreach (var line in Lines(text))
            {
                if (FieldValue(line.Text, field) is { } value)
                {
                    examples.TryAdd(value, Path.GetFileName(file));
                }
            }
        }

        var values = examples.Keys.Order(TextUtils.CodePointComparer).Select(v => new AssFieldValue(v, examples[v])).ToList();
        return new AssEditInspection(folder, field, readable, values, unreadable);
    }

    /// <summary>Значения, которые уйдут: при «оставить» — все, кроме выбранных; при «удалить» — выбранные.</summary>
    public static IReadOnlySet<string> ValuesToRemove(AssEditInspection inspection, IEnumerable<string> selected, bool keepSelected)
    {
        var chosen = selected.ToHashSet(StringComparer.Ordinal);
        return keepSelected
            ? inspection.Values.Select(v => v.Value).Where(v => !chosen.Contains(v)).ToHashSet(StringComparer.Ordinal)
            : inspection.Values.Select(v => v.Value).Where(chosen.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Текст без строк Dialogue, у которых значение поля — среди удаляемых; сколько строк убрано.</summary>
    public static (string Text, int Removed) RemoveLines(string text, AssField field, IReadOnlySet<string> remove)
    {
        var result = new StringBuilder(text.Length);
        var removed = 0;
        foreach (var line in Lines(text))
        {
            if (FieldValue(line.Text, field) is { } value && remove.Contains(value))
            {
                removed++;
                continue;
            }

            result.Append(line.Text).Append(line.Ending);
        }

        return (result.ToString(), removed);
    }

    /// <summary>Копирует оригиналы в ass_backup_&lt;дата&gt; и правит файлы на месте.</summary>
    public static AssEditResult Execute(AssEditInspection inspection, IReadOnlySet<string> remove, DateTime? now = null)
    {
        var backup = Path.Combine(inspection.Folder, "ass_backup_" + (now ?? DateTime.Now).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
        for (var n = 2; Directory.Exists(backup); n++)
        {
            backup = Path.Combine(inspection.Folder, $"ass_backup_{(now ?? DateTime.Now).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}_{n}");
        }

        Directory.CreateDirectory(backup);
        foreach (var file in inspection.Files)
        {
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
        }

        var done = new List<(string, int)>();
        var failed = new List<(string, string)>();
        foreach (var file in inspection.Files)
        {
            if (!TryRead(file, out var text, out var error))
            {
                failed.Add((file, error));
                continue;
            }

            var (edited, removed) = RemoveLines(text, inspection.Field, remove);
            try
            {
                File.WriteAllText(file, edited, StrictUtf8);
                done.Add((file, removed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((file, ex.Message));
            }
        }

        return new AssEditResult(done, backup, failed);
    }

    /// <summary>Значение поля строки «Dialogue:» (наивное деление по запятой) или null.</summary>
    private static string? FieldValue(string line, AssField field)
    {
        if (!line.StartsWith("Dialogue:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line.Split(',');
        return parts.Length > (int)field ? TextUtils.Strip(parts[(int)field]) : null;
    }

    /// <summary>Строки с их переводами («\r\n», «\n», «\r» или пусто у последней).</summary>
    private static IEnumerable<(string Text, string Ending)> Lines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
            {
                yield return (text[start..], "");
                yield break;
            }

            var length = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            yield return (text[start..end], text.Substring(end, length));
            start = end + length;
        }
    }

    private static bool TryRead(string file, out string text, out string error)
    {
        try
        {
            // UTF-8 с необязательным BOM: BOM снимается, остальное — строго UTF-8 (UTF-16 и cp1251 не трогаем)
            var bytes = File.ReadAllBytes(file);
            var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            text = StrictUtf8.GetString(bytes, start, bytes.Length - start);
            error = "";
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            error = "файл не в UTF-8";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = "";
            error = ex.Message;
            return false;
        }
    }
}
