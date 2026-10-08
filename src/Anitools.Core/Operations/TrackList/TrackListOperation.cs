using System.Text;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.TrackList;

/// <summary>Аудиодорожка готового файла для таблицы: № (с 1), тайтл, язык, кодек, каналы, по умолчанию ли.</summary>
public sealed record TrackRow(int Number, string? Title, string? Language, string? Codec, string? Channels, bool IsDefault);

/// <summary>Вид списка «для копирования» (в Telegram).</summary>
public enum CopyListStyle
{
    /// <summary>«• тайтл».</summary>
    Bullets,

    /// <summary>«1. тайтл».</summary>
    Numbers,

    /// <summary>Просто тайтлы.</summary>
    Plain,

    /// <summary>Одной строкой через запятую: русские озвучки, потом «English», потом «Original» (японская).</summary>
    Comma,
}

/// <summary>«Дорожки файла»: таблица аудиодорожек и список тайтлов для копирования.</summary>
public static class TrackListOperation
{
    public static IReadOnlyList<string> Extensions { get; } = [".mka", ".mkv", ".mp4", ".mov", ".m4a", ".webm"];

    /// <summary>Файлы папки по имени, по кодам символов.</summary>
    public static IReadOnlyList<string> ListFiles(string folder) =>
        [.. MediaFiles.List(folder, Extensions).OrderBy(p => Path.GetFileName(p), TextUtils.CodePointComparer)];

    /// <summary>Аудиодорожки: каналы — раскладка («5.1(side)»), иначе «N ch».</summary>
    public static IReadOnlyList<TrackRow> Rows(MediaInfo info) =>
        [.. info.AudioStreams.Select((s, i) => new TrackRow(
            i + 1,
            s.Title,
            s.Language,
            NonEmpty(s.CodecName),
            NonEmpty(s.ChannelLayout) ?? (s.Channels is > 0 and var ch ? $"{ch} ch" : null),
            s.IsDefault))];

    /// <summary>Список для копирования: без тайтла — «Дорожка N».</summary>
    public static string CopyList(IReadOnlyList<TrackRow> rows, CopyListStyle style)
    {
        if (style == CopyListStyle.Comma)
        {
            return CommaList(rows);
        }

        var text = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            var title = string.IsNullOrEmpty(rows[i].Title) ? $"Дорожка {rows[i].Number}" : rows[i].Title;
            text.Append(style switch
            {
                CopyListStyle.Numbers => $"{i + 1}. {title}",
                CopyListStyle.Plain => title,
                _ => $"• {title}",
            }).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// «AniLibria.TV, DEEP, English, Original»: сначала русские (и прочие) озвучки по порядку дорожек, потом английская
    /// как «English», потом японская как «Original». Английская и японская — по языку дорожки (eng / jpn), иначе
    /// по тайтлу («English», «Оригинальная», «JP»…). Одинаковые подписи — один раз.
    /// </summary>
    public static string CommaList(IReadOnlyList<TrackRow> rows)
    {
        var labels = rows
            .Select(row => (Row: row, Group: Group(row)))
            .OrderBy(x => x.Group) // устойчивая сортировка: внутри группы — порядок дорожек
            .Select(x => x.Group switch
            {
                VoiceGroup.English => "English",
                VoiceGroup.Original => "Original",
                _ => string.IsNullOrEmpty(x.Row.Title) ? $"Дорожка {x.Row.Number}" : x.Row.Title,
            })
            .Distinct(StringComparer.Ordinal);
        return string.Join(", ", labels);
    }

    /// <summary>Чья озвучка: язык дорожки, а если он не английский и не японский — догадка по тайтлу.</summary>
    public static VoiceGroup Group(TrackRow row)
    {
        var language = TextUtils.Lower(row.Language ?? "");
        var guessed = language switch
        {
            "eng" or "en" => "eng",
            "jpn" or "ja" or "jp" => "jpn",
            _ => LanguageGuess.Detect(row.Title),
        };
        return guessed switch
        {
            "eng" => VoiceGroup.English,
            "jpn" => VoiceGroup.Original,
            _ => VoiceGroup.Dub,
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Порядок в списке через запятую: озвучки, английская, оригинальная.</summary>
public enum VoiceGroup
{
    Dub,
    English,
    Original,
}
