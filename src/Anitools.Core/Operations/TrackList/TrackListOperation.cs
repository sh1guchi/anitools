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
}

/// <summary>«Дорожки файла» (mka_muxer.py, режим 2): таблица аудиодорожек и список тайтлов для копирования.</summary>
public static class TrackListOperation
{
    public static IReadOnlyList<string> Extensions { get; } = [".mka", ".mkv", ".mp4", ".mov", ".m4a", ".webm"];

    /// <summary>Файлы папки по sorted(os.listdir()) оригинала — по кодам символов.</summary>
    public static IReadOnlyList<string> ListFiles(string folder) =>
        [.. MediaFiles.List(folder, Extensions).OrderBy(p => Path.GetFileName(p), PyText.CodePointComparer)];

    /// <summary>Аудиодорожки (probe_tracks): каналы — раскладка («5.1(side)»), иначе «N ch».</summary>
    public static IReadOnlyList<TrackRow> Rows(MediaInfo info) =>
        [.. info.AudioStreams.Select((s, i) => new TrackRow(
            i + 1,
            s.Title,
            s.Language,
            NonEmpty(s.CodecName),
            NonEmpty(s.ChannelLayout) ?? (s.Channels is > 0 and var ch ? $"{ch} ch" : null),
            s.IsDefault))];

    /// <summary>Список для копирования (print_copy_block): без тайтла — «Дорожка N».</summary>
    public static string CopyList(IReadOnlyList<TrackRow> rows, CopyListStyle style)
    {
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

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
