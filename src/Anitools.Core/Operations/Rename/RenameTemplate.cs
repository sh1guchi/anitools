using Anitools.Core.Templates;

namespace Anitools.Core.Operations.Rename;

/// <summary>
/// Шаблон имени для «Переименовать» (без расширения — оно остаётся своё): переменные, готовые условия для меню
/// и проверки для имён файлов. Стандартный шаблон: «Название - 01[.суффикс]».
/// </summary>
public static class RenameTemplate
{
    public const string Title = "название";
    public const string Episode = "серия";
    public const string Season = "сезон";
    public const string Suffix = "суффикс";
    public const string Group = "группа";
    public const string Quality = "качество";
    public const string Name = "имя";

    // Из самого видео (ffprobe) — для имён как у релизов: …1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Group
    public const string Resolution = "разрешение";
    public const string VideoCodec = "видео";
    public const string AudioCodec = "аудио";
    public const string Channels = "каналы";
    public const string Multi = "мульти";

    /// <summary>Стандартный: «Название - 01.ext», с суффиксом — «Название - 01.надписи.ext».</summary>
    public const string Default = "{название} - {серия}{?суффикс}.{суффикс}{/}";

    /// <summary>Символы, которых не бывает в именах файлов Windows.</summary>
    public const string ForbiddenChars = "\\/:*?\"<>|";

    public static IReadOnlyList<TemplateVariable> Variables { get; } =
    [
        new(Title, "название тайтла"),
        new(Episode, "номер серии: 01, 02…"),
        new(Season, "сезон — поле «Сезон»"),
        new(Suffix, "суффикс — поле «Суффикс»"),
        new(Group, "релиз-группа из имени файла"),
        new(Quality, "качество из имени файла: 1080p"),
        new(Name, "имя файла сейчас, без расширения"),
        new(Resolution, "разрешение видео: 1080p"),
        new(VideoCodec, "кодек видео: AVC, HEVC"),
        new(AudioCodec, "кодек звука: FLAC, AAC"),
        new(Channels, "каналы звука: 2.0, 5.1"),
        new(Multi, "MULTi, если звук на нескольких языках"),
    ];

    /// <summary>Переменные, для которых видео читается через ffprobe.</summary>
    public static IReadOnlyList<string> MediaVariables { get; } = [Resolution, VideoCodec, AudioCodec, Channels, Multi];

    /// <summary>Переменные с преобразованием — для меню «+ Переменная»: имена файлов как у релизов.</summary>
    public static IReadOnlyList<TemplateVariable> Formatted { get; } =
    [
        new(Title + ":" + TextTemplate.Dots, "название через точки: To.Be.Hero.X"),
        new(Season + ":00", "сезон двумя цифрами: 01"),
        new(Episode + ":000", "серия тремя цифрами: 001"),
    ];

    /// <summary>Встроенные пресеты: их не удалить, свои — в настройках (<see cref="Settings.RenameSettings.Presets"/>).</summary>
    public static IReadOnlyList<TemplatePreset> BuiltInPresets { get; } =
    [
        new("Стандартный", Default),
        new("Сезон и серия", "{название} S{сезон:00}E{серия}"),
        new("Как у релизов", "{название:точки}.S{сезон:00}E{серия}.{разрешение}.BluRay.Remux.{видео}{?мульти}.{мульти}{/}.{аудио}.{каналы}-Group"),
    ];

    /// <summary>Условия для меню «Условие ▾» — как templateVariables() в core.js: сравнение с сезоном и «если есть …».</summary>
    public static IReadOnlyList<TemplateCondition> Conditions { get; } =
    [
        new(Suffix, "если есть суффикс"),
        new(Season + "≠1", "если сезон не 1"),
        new(Season, "если есть сезон"),
        new(Group, "если есть группа"),
        new(Quality, "если есть качество"),
        new(Multi, "если звук на нескольких языках"),
    ];

    private static readonly HashSet<string> VariableNames = [.. Variables.Select(v => v.Name)];

    /// <summary>
    /// Ошибки и предупреждения по порядку: синтаксис и неизвестные переменные, символы, которых не бывает в именах
    /// файлов, и предупреждение, если без {серия} и {имя} у всех файлов выйдет одно имя.
    /// </summary>
    public static IReadOnlyList<TemplateIssue> Check(string? template)
    {
        var src = template ?? "";
        var issues = TextTemplate.Validate(src, VariableNames).ToList();
        var syntax = issues.ToList();
        foreach (var token in TextTemplate.Tokenize(src).Tokens.Where(t => t.Kind == TemplateTokenKind.Text))
        {
            for (var i = token.Start; i < token.End; i++)
            {
                var c = src[i];
                if ((ForbiddenChars.Contains(c) || char.IsControl(c)) && !syntax.Any(e => e.Start <= i && i < e.End))
                {
                    issues.Add(new TemplateIssue(i, i + 1, char.IsControl(c)
                        ? "Управляющий символ в имени файла"
                        : $"Символ «{c}» в имени файла нельзя"));
                }
            }
        }

        if (src.Trim().Length == 0)
        {
            issues.Add(new TemplateIssue(0, 0, "Шаблон пустой — впишите хотя бы {название} - {серия}"));
        }
        else if (!TextTemplate.Uses(src, Episode) && !TextTemplate.Uses(src, Name))
        {
            issues.Add(new TemplateIssue(src.Length, src.Length, "Нет {серия} — у всех файлов получится одно имя", IsWarning: true));
        }

        return [.. issues.OrderBy(e => e.Start).ThenBy(e => e.End)];
    }

    /// <summary>Нужно ли читать видео (ffprobe): в шаблоне есть {разрешение}, {видео}, {аудио}, {каналы} или {мульти}.</summary>
    public static bool UsesMedia(string? template) => MediaVariables.Any(v => TextTemplate.Uses(template, v));

    /// <summary>Есть ли ошибки (предупреждения не мешают переименовать).</summary>
    public static bool HasErrors(IReadOnlyList<TemplateIssue> issues) => issues.Any(i => !i.IsWarning);
}
