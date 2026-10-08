using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Anitools.Core.Settings;

/// <summary>Итог чтения настроек; <see cref="Error"/> — файл был, но прочитать его не удалось (взяты значения по умолчанию).</summary>
public sealed record SettingsLoadResult(AppSettings Settings, string? Error);

/// <summary>
/// settings.json: %APPDATA%\anitools на Windows, ~/.config/anitools на Linux. Пишется целиком через временный файл,
/// чтобы сбой посреди записи не оставил обрезанный файл. Испорченный файл не затирается молча: он сохраняется
/// рядом как settings.json.bad, а программа работает с настройками по умолчанию.
/// </summary>
public sealed class SettingsStore(string path)
{
    public const string FileName = "settings.json";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Не настройки, а состояние этого компьютера: в экспорт не попадают, при импорте остаются свои.</summary>
    private static readonly string[] LocalKeys = ["recentFolders", "lastUpdateCheck"];

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "anitools", FileName);

    public SettingsLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            return new SettingsLoadResult(new AppSettings(), null);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path, Encoding.UTF8), JsonOptions)
                ?? throw new JsonException("пустой файл");
            return new SettingsLoadResult(settings.Normalized(), null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var backup = Path + ".bad";
            try
            {
                File.Copy(Path, backup, overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                backup = null;
            }

            var saved = backup is null ? "" : $" Копия файла: {backup}.";
            return new SettingsLoadResult(new AppSettings(), $"Не удалось прочитать настройки ({ex.Message}), взяты значения по умолчанию.{saved}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new AppSettings(), $"Не удалось открыть {Path}: {ex.Message}");
        }
    }

    /// <exception cref="IOException">Не удалось записать.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на запись.</exception>
    public void Save(AppSettings settings) => Write(Path, JsonSerializer.Serialize(settings, JsonOptions));

    /// <summary>
    /// Все настройки в файл — перенести на другой компьютер или отложить про запас. Тот же формат, что settings.json,
    /// без недавних папок и времени проверки обновлений.
    /// </summary>
    /// <exception cref="IOException">Не удалось записать.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на запись.</exception>
    public static void Export(AppSettings settings, string path)
    {
        var json = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        foreach (var key in LocalKeys)
        {
            json.Remove(key);
        }

        Write(path, json.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// Настройки из файла (экспорт или чужой settings.json) поверх текущих: чего в файле нет — остаётся как сейчас,
    /// недавние папки и время проверки обновлений — свои. Не настройки anitools — null и понятная причина.
    /// </summary>
    public static (AppSettings? Settings, string? Error) Import(string path, AppSettings current)
    {
        JsonNode? file;
        try
        {
            file = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return (null, $"файл не читается как JSON ({ex.Message})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"не удалось открыть файл ({ex.Message})");
        }

        var known = JsonSerializer.SerializeToNode(new AppSettings(), JsonOptions)!.AsObject();
        if (file is not JsonObject imported || !imported.Any(p => known.ContainsKey(p.Key) && !LocalKeys.Contains(p.Key)))
        {
            return (null, "в файле нет настроек anitools");
        }

        var merged = JsonSerializer.SerializeToNode(current, JsonOptions)!.AsObject();
        Merge(merged, imported);
        try
        {
            var settings = merged.Deserialize<AppSettings>(JsonOptions) ?? throw new JsonException("пусто");
            return (settings.Normalized() with { RecentFolders = current.RecentFolders, LastUpdateCheck = current.LastUpdateCheck }, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, $"в настройках из файла ошибка ({ex.Message})");
        }
    }

    /// <summary>Значения из source поверх target; вложенные объекты — по полям, списки — целиком.</summary>
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (LocalKeys.Contains(key))
            {
                continue;
            }

            if (value is JsonObject inner && target[key] is JsonObject existing)
            {
                Merge(existing, inner);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>Через временный файл: сбой посреди записи не оставит обрезанный файл.</summary>
    private static void Write(string path, string json)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
