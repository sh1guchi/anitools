using Anitools.Core.Media;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Hls;

/// <summary>
/// Аудиодорожка для озвучек HLS (_get_audio_track_ids_for_episode, py:3754): номер среди аудио (0:a:N),
/// тайтл (иначе язык, иначе «Track N»), язык (иначе und) и число каналов (0 — неизвестно).
/// </summary>
public sealed record HlsAudioTrack(int Index, string Title, string Language, int Channels)
{
    public static IReadOnlyList<HlsAudioTrack> FromStreams(IReadOnlyList<MediaStream> audioStreams) =>
        [.. audioStreams.Select((s, i) => new HlsAudioTrack(
            i,
            NonEmpty(s.Title) ?? NonEmpty(s.Language) ?? $"Track {i + 1}",
            s.Language ?? "und",
            s.Channels ?? 0))];

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>
/// Раскладка аудио файла (_audio_layout, py:3784): пары (тайтл без пробелов по краям в нижнем регистре, язык).
/// Файлы с одинаковой раскладкой делят одно назначение озвучек.
/// </summary>
public sealed class AudioLayout : IEquatable<AudioLayout>
{
    private AudioLayout(IReadOnlyList<(string Title, string Language)> tracks)
    {
        Tracks = tracks;
        Key = string.Join('\u001f', tracks.Select(t => t.Title + '\u001e' + t.Language));
    }

    public IReadOnlyList<(string Title, string Language)> Tracks { get; }

    /// <summary>Строковый ключ для словарей и сохранения выбора.</summary>
    public string Key { get; }

    public static AudioLayout Of(IReadOnlyList<HlsAudioTrack> tracks) =>
        new([.. tracks.Select(t => (PyText.Lower(PyText.Strip(t.Title)), PyText.Lower(t.Language)))]);

    public bool Equals(AudioLayout? other) => other is not null && Key == other.Key;

    public override bool Equals(object? obj) => Equals(obj as AudioLayout);

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// NVDEC декодирует кадр не больше определённого размера (_needs_cpu_decode, py:3910–3929).
/// H.264 — только до 4096 даже на RTX 40; кодек не из списка считаем поддерживаемым (упадёт — повтор на CPU).
/// </summary>
public static class NvdecLimits
{
    public static IReadOnlyDictionary<string, int> MaxSize { get; } = new Dictionary<string, int>
    {
        ["h264"] = 4096,
        ["mpeg2video"] = 4080,
        ["mpeg1video"] = 4080,
        ["mpeg4"] = 2048,
        ["vc1"] = 2048,
        ["hevc"] = 8192,
        ["av1"] = 8192,
        ["vp9"] = 8192,
        ["vp8"] = 4096,
    };

    /// <summary>Причина декодировать на процессоре или null, если видеокарта справится.</summary>
    public static string? CpuDecodeReason(string? codec, int? width, int? height)
    {
        int w = width ?? 0, h = height ?? 0;
        if (codec is not null && MaxSize.TryGetValue(codec, out var limit) && Math.Max(w, h) > limit)
        {
            return FormattableString.Invariant($"{codec.ToUpperInvariant()} {w}×{h} — видеокарта декодирует такой кодек только до {limit}px");
        }

        return null;
    }
}

/// <summary>Озвучки (_VOICE_OPTIONS, py:3670): список для выбора в п.3 и п.7.</summary>
public static class VoiceList
{
    public static IReadOnlyList<string> Default { get; } =
    [
        "AniLiberty (AniLibria)",
        "ТО Дубляжная",
        "Studio Band",
        "Оригинальная",
        "AniLibria.TV",
        "DEEP",
        "AniStar x DEEP",
        "AniLibria.TV x DEEP",
        "ТО Дубляжная x DEEP",
        "SHIZA Project",
        "AniDUB",
        "OnWave",
        "Reanimedia",
        "Dream Cast",
        "JAM",
        "AniPlague",
        "Animedia",
        "Shachiburi",
        "Ancord",
        "KANSAI Studio",
    ];
}

/// <summary>Выбор для одной дорожки: название озвучки или null — дорожку не брать (комментарии и т.п.).</summary>
public sealed record VoiceChoice(int TrackIndex, string? Name);

/// <summary>Назначение озвучек дорожкам (_select_audio_voices_multi_res, py:3789).</summary>
public static class VoiceAssignment
{
    /// <summary>Что предлагается при ручном вводе: тайтл дорожки, приведённый к имени папки, или «TrackN».</summary>
    public static string SuggestedName(HlsAudioTrack track) =>
        TitleText.SanitizeFolder(track.Title) is { Length: > 0 } name ? name : $"Track{track.Index + 1}";

    /// <summary>
    /// Озвучки по порядку дорожек. Имя папки — название без недопустимых символов (пусто → «TrackN»);
    /// повтор — «_2», «_3»… Сравнение без учёта регистра: на Windows «DEEP» и «deep» — одна папка.
    /// Дорожки без выбора и с Name = null не берутся.
    /// </summary>
    public static IReadOnlyList<HlsVoice> Build(IReadOnlyList<HlsAudioTrack> tracks, IEnumerable<VoiceChoice> choices)
    {
        var byTrack = new Dictionary<int, string?>();
        foreach (var choice in choices)
        {
            byTrack[choice.TrackIndex] = choice.Name;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var voices = new List<HlsVoice>();
        foreach (var track in tracks)
        {
            if (!byTrack.TryGetValue(track.Index, out var name) || name is null)
            {
                continue;
            }

            var folder = TitleText.SanitizeFolder(PyText.Strip(name));
            if (folder.Length == 0)
            {
                folder = $"Track{track.Index + 1}";
            }

            var unique = folder;
            for (var n = 2; used.Contains(unique); n++)
            {
                unique = $"{folder}_{n}";
            }

            used.Add(unique);
            voices.Add(new HlsVoice(track.Index, unique, track.Language));
        }

        return voices;
    }
}
