using System.Text;
using System.Text.Json;
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
    public void Save(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }
}
