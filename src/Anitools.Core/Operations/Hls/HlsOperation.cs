using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Hls;

/// <summary>Видео для HLS и его аудиодорожки; <see cref="Error"/> — ffprobe не смог прочитать файл.</summary>
public sealed record HlsSourceFile(string Path, IReadOnlyList<HlsAudioTrack> Tracks, string? Error = null)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public AudioLayout Layout => AudioLayout.Of(Tracks);
}

/// <summary>Набор аудиодорожек, общий для части файлов группы: озвучки назначаются один раз на раскладку.</summary>
/// <param name="Tracks">Дорожки первого файла с такой раскладкой — их и показывать при выборе озвучек.</param>
public sealed record HlsLayoutInfo(AudioLayout Layout, IReadOnlyList<HlsAudioTrack> Tracks, IReadOnlyList<HlsSourceFile> Files);

/// <summary>Файлы одного тайтла.</summary>
public sealed record HlsGroup(string Title, IReadOnlyList<HlsSourceFile> Files)
{
    /// <summary>Раскладки по первому появлению; файлы без аудио и непрочитанные сюда не входят.</summary>
    public IReadOnlyList<HlsLayoutInfo> Layouts =>
        [.. Files.Where(f => f.Error is null && f.Tracks.Count > 0)
            .GroupBy(f => f.Layout)
            .Select(g => new HlsLayoutInfo(g.Key, g.First().Tracks, [.. g]))];
}

/// <summary>Что нашлось в папке: видео с дорожками и группы по тайтлам.</summary>
public sealed record HlsInspection(string Folder, IReadOnlyList<HlsSourceFile> Files, IReadOnlyList<HlsGroup> Groups);

/// <summary>Решения по группе: папка тайтла и озвучки на каждую раскладку.</summary>
public sealed record HlsGroupOptions
{
    public required HlsGroup Group { get; init; }

    /// <summary>Папка тайтла, обычно «{ID Shikimori} - {название}» (<see cref="HlsOperation.TitleFolder"/>).</summary>
    public required string TitleFolder { get; init; }

    /// <summary>Выбор по ключу раскладки (<see cref="AudioLayout.Key"/>). Раскладки нет или ничего не выбрано — её файлы пропускаются.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<VoiceChoice>> Voices { get; init; }
}

/// <summary>Серия в плане HLS.</summary>
public sealed record HlsEpisode
{
    public required HlsSourceFile Source { get; init; }

    public required string TitleFolder { get; init; }

    /// <summary>Имя папки серии: имя файла без расширения, без недопустимых символов.</summary>
    public required string EpisodeName { get; init; }

    /// <summary>Папка тайтла в выходе: hls_multi\&lt;тайтл&gt;.</summary>
    public required string TitleOut { get; init; }

    public required IReadOnlyList<HlsVoice> Voices { get; init; }

    public required PlanItemStatus Status { get; init; }

    public string? Reason { get; init; }

    /// <summary>hls_multi\&lt;тайтл&gt;\&lt;серия&gt; — рабочая папка, если временные файлы пишутся «рядом с выходом».</summary>
    public string EpisodeOut => Path.Combine(TitleOut, EpisodeName);

    public string ZipPath => Path.Combine(TitleOut, EpisodeName + ".zip");

    /// <summary>Готовые .mka: hls_multi\&lt;тайтл&gt;\audio\&lt;озвучка&gt;\&lt;серия&gt;.&lt;озвучка&gt;.mka.</summary>
    public IReadOnlyList<string> AudioPaths => [.. Voices.Select(v => Path.Combine(TitleOut, "audio", v.Folder, $"{EpisodeName}.{v.Folder}.mka"))];

    /// <summary>Серия готова (резюм оригинала): zip есть и не пустой, все .mka на месте и не пустые.</summary>
    public bool IsDone => MediaFiles.IsDone(ZipPath) && AudioPaths.All(MediaFiles.IsDone);
}

/// <summary>План п.7: серии по порядку групп, у каждой — статус и причина.</summary>
public sealed record HlsPlan(string Folder, string OutputRoot, IReadOnlyList<HlsEpisode> Episodes)
{
    public int RunCount => Episodes.Count(e => e.Status == PlanItemStatus.Run);

    public int SkipCount => Episodes.Count(e => e.Status == PlanItemStatus.Skip);

    public int ErrorCount => Episodes.Count(e => e.Status == PlanItemStatus.Error);
}

/// <summary>
/// П.7 «HLS мульти-разрешение» (convert_videos_multi_res, py:4892): видео → архив &lt;серия&gt;.zip с качествами
/// 360p…4K (сегменты по 6 с) + озвучки отдельными .mka. Здесь — разбор папки и план; выполнение — <see cref="HlsRunner"/>.
/// </summary>
public static class HlsOperation
{
    public const string OutputFolderName = "hls_multi";

    public const string UntitledGroup = "Без названия";

    /// <summary>Причина пропуска серии, которая уже сконвертирована (резюм).</summary>
    public const string AlreadyDone = "уже готово";

    public static IReadOnlyList<string> Extensions { get; } = [".mp4", ".mkv", ".avi", ".m2ts", ".mov"];

    /// <summary>
    /// Видео верхнего уровня по порядку sorted(Path) оригинала: на Windows это сравнение имён в нижнем регистре
    /// по кодам символов («[Group] …» раньше «Hellsing …», в отличие от порядка NTFS в п.1–4).
    /// </summary>
    public static IReadOnlyList<string> ListFiles(string folder) =>
        [.. MediaFiles.List(folder, Extensions).OrderBy(p => TextUtils.Lower(Path.GetFileName(p)), TextUtils.CodePointComparer)];

    /// <summary>Видео верхнего уровня, их дорожки (ffprobe) и группы по тайтлам.</summary>
    /// <exception cref="PlanException">В папке нет видео.</exception>
    public static async Task<HlsInspection> InspectAsync(string folder, IMediaProbe probe, CancellationToken cancellationToken = default)
    {
        var paths = ListFiles(folder);
        if (paths.Count == 0)
        {
            throw new PlanException("Нет видеофайлов в текущей папке.");
        }

        var files = new List<HlsSourceFile>();
        foreach (var path in paths)
        {
            try
            {
                var info = await probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                files.Add(new HlsSourceFile(path, HlsAudioTrack.FromStreams(info.AudioStreams)));
            }
            catch (MediaProbeException ex)
            {
                files.Add(new HlsSourceFile(path, [], ex.Message));
            }
        }

        return new HlsInspection(folder, files, GroupByTitle(files));
    }

    /// <summary>Группы по тайтлу из имени (_parse_anime_group: название + OVA/ONA/Special/Movie), по первому появлению.</summary>
    public static IReadOnlyList<HlsGroup> GroupByTitle(IReadOnlyList<HlsSourceFile> files) =>
        [.. files.GroupBy(f => TitleText.AnimeGroup(f.Name) is { Length: > 0 } title ? title : UntitledGroup)
            .Select(g => new HlsGroup(g.Key, [.. g]))];

    /// <summary>Папка тайтла (_pick_shikimori): «{ID} - {название группы}» или просто название, если Shikimori пропущен.</summary>
    public static string TitleFolder(long? shikimoriId, string groupTitle) =>
        TitleText.SanitizeFolder(shikimoriId is { } id ? $"{id} - {groupTitle}" : groupTitle);

    public static HlsPlan Plan(HlsInspection inspection, IReadOnlyList<HlsGroupOptions> groups, string? outputRoot = null)
    {
        var root = outputRoot ?? Path.Combine(inspection.Folder, OutputFolderName);
        var episodes = new List<HlsEpisode>();
        // Имена папок серий — уникальны в пределах папки тайтла (в оригинале — в пределах группы)
        var usedNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in groups)
        {
            var titleFolder = TitleText.SanitizeFolder(options.TitleFolder);
            if (titleFolder.Length == 0)
            {
                throw new PlanException($"Пустое имя папки тайтла у группы «{options.Group.Title}».");
            }

            var titleOut = Path.Combine(root, titleFolder);
            if (!usedNames.TryGetValue(titleFolder, out var used))
            {
                used = [];
                usedNames[titleFolder] = used;
            }

            var layoutVoices = options.Group.Layouts.ToDictionary(
                l => l.Layout.Key,
                l => options.Voices.TryGetValue(l.Layout.Key, out var choices) ? VoiceAssignment.Build(l.Tracks, choices) : []);
            foreach (var file in options.Group.Files)
            {
                var episode = new HlsEpisode
                {
                    Source = file,
                    TitleFolder = titleFolder,
                    EpisodeName = "",
                    TitleOut = titleOut,
                    Voices = [],
                    Status = PlanItemStatus.Error,
                };
                if (file.Error is not null)
                {
                    episodes.Add(episode with { Reason = $"не удалось прочитать файл: {file.Error}" });
                    continue;
                }

                if (file.Tracks.Count == 0)
                {
                    episodes.Add(episode with { Reason = "нет аудиодорожек — файл пропускается" });
                    continue;
                }

                var voices = layoutVoices[file.Layout.Key];
                if (voices.Count == 0)
                {
                    episodes.Add(episode with { Status = PlanItemStatus.Skip, Reason = "не выбрано ни одной озвучки — файл пропускается" });
                    continue;
                }

                var planned = episode with { EpisodeName = EpisodeName(file.Name, used), Voices = voices, Status = PlanItemStatus.Run };
                episodes.Add(planned.IsDone ? planned with { Status = PlanItemStatus.Skip, Reason = AlreadyDone } : planned);
            }
        }

        return new HlsPlan(inspection.Folder, root, episodes);
    }

    /// <summary>
    /// Имя папки серии: имя файла без расширения; занято (без учёта регистра) — с расширением
    /// («01.mkv» и «01.mp4» → «01» и «01.mp4»), занято и так — с «_2», «_3»…
    /// </summary>
    private static string EpisodeName(string fileName, HashSet<string> used)
    {
        var name = TitleText.SanitizeFolder(TextUtils.Stem(fileName));
        if (used.Contains(TextUtils.Lower(name)))
        {
            name = TitleText.SanitizeFolder($"{TextUtils.Stem(fileName)}.{MediaFiles.Suffix(fileName).TrimStart('.')}");
        }

        var unique = name;
        for (var n = 2; used.Contains(TextUtils.Lower(unique)); n++)
        {
            unique = $"{name}_{n}";
        }

        used.Add(TextUtils.Lower(unique));
        return unique;
    }
}
