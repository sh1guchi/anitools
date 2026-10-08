using System.Globalization;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.AudioMux;

public enum AudioSlotKind
{
    /// <summary>Дорожка из самого видео (0:a:N).</summary>
    Internal,

    /// <summary>Дорожка внешнего аудиофайла серии.</summary>
    External,
}

/// <summary>
/// Слот — будущая аудиодорожка выходного файла. Ключ одинаков у одной и той же озвучки во всех сериях:
/// у внутренней — номер дорожки, у внешней — папка, префикс «N. », хвост имени после названия серии и номер
/// дорожки в файле. По ключам серии с разным набором файлов не путаются (docs/PLAN.md §2.8 #6).
/// </summary>
public sealed record AudioSlot
{
    public required string Key { get; init; }

    public required AudioSlotKind Kind { get; init; }

    /// <summary>Internal: N в «0:a:N».</summary>
    public int TrackId { get; init; }

    /// <summary>External: файл первой серии набора — путь относительно рабочей папки.</summary>
    public string ExternalFile { get; init; } = "";

    /// <summary>External: номер аудиодорожки внутри файла.</summary>
    public int Stream { get; init; }

    /// <summary>Подпись: «Внутр. дорожка 1: AniLibria.TV», «Внешний файл: …».</summary>
    public required string Label { get; init; }

    /// <summary>Настоящий тайтл, а если его нет — «Track N» или имя файла.</summary>
    public required string DefaultTitle { get; init; }

    /// <summary><see cref="DefaultTitle"/> — настоящий тайтл (его и пишем, если свой не задан).</summary>
    public required bool HasTitle { get; init; }
}

/// <summary>Слот в конкретной серии: какой вход ffmpeg (0 — видео, 1… — внешние файлы) и какая дорожка.</summary>
public sealed record EpisodeSlot(string Key, int Input, int Stream, string? StreamTitle);

/// <summary>Серия «Сборки аудио»: видео, все найденные внешние файлы (это входы ffmpeg 1…N) и слоты.</summary>
public sealed record AudioMuxEpisode
{
    public required string Video { get; init; }

    public required string Output { get; init; }

    public required IReadOnlyList<string> ExternalFiles { get; init; }

    /// <summary>Слоты серии в исходном порядке: внутренние (как выбраны), потом внешние (как найдены).</summary>
    public required IReadOnlyList<EpisodeSlot> Slots { get; init; }
}

/// <summary>Набор: серии с одинаковым набором слотов. Порядок, тайтлы и язык настраиваются на набор.</summary>
public sealed record AudioSlotSet
{
    public required IReadOnlyList<AudioSlot> Slots { get; init; }

    public required IReadOnlyList<AudioMuxEpisode> Episodes { get; init; }

    /// <summary>Основной набор — тот, что у первой серии.</summary>
    public bool IsMain { get; init; }
}

/// <summary>Папка для «Сборки аудио»: серии к обработке (первая — образец), её дорожки и уже готовые серии.</summary>
public sealed record AudioMuxSource(string Folder, IReadOnlyList<string> Videos, IReadOnlyList<string> Done, IReadOnlyList<AudioTrackInfo> Tracks);

public sealed record AudioMuxAnalysis(AudioMuxSource Source, IReadOnlyList<int> TrackIds, IReadOnlyList<AudioSlotSet> Sets);

/// <summary>Настройка набора: выходной порядок слотов, свои тайтлы и языки по ключам слотов.</summary>
public sealed record AudioSlotSetConfig
{
    /// <summary>Ключи всех слотов набора в выходном порядке; первый станет дорожкой по умолчанию.</summary>
    public required IReadOnlyList<string> Order { get; init; }

    /// <summary>Свой тайтл слота; нет ключа — пишется настоящий тайтл, если он есть.</summary>
    public IReadOnlyDictionary<string, string> Titles { get; init; } = new Dictionary<string, string>();

    /// <summary>Язык слота; нет ключа — язык не ставится. У внешних слотов язык ещё уточняется по тайтлу дорожки в каждой серии.</summary>
    public IReadOnlyDictionary<string, string> Languages { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// По умолчанию: порядок исходный (или <paramref name="order"/>), тайтлы — <paramref name="titles"/> или настоящие,
    /// язык — угадан по подписи и тайтлу, иначе <paramref name="defaultLanguage"/> (null — язык не ставить).
    /// </summary>
    public static AudioSlotSetConfig Default(
        AudioSlotSet set,
        string? defaultLanguage = "rus",
        IReadOnlyDictionary<string, string>? titles = null,
        IReadOnlyList<string>? order = null)
    {
        titles ??= new Dictionary<string, string>();
        var languages = new Dictionary<string, string>();
        if (defaultLanguage is not null)
        {
            foreach (var slot in set.Slots)
            {
                var text = string.Join(" ", new[] { slot.Label, titles.GetValueOrDefault(slot.Key), slot.HasTitle ? slot.DefaultTitle : null }
                    .Where(t => !string.IsNullOrEmpty(t)));
                languages[slot.Key] = LanguageGuess.Detect(text) ?? defaultLanguage;
            }
        }

        return new AudioSlotSetConfig { Order = order ?? [.. set.Slots.Select(s => s.Key)], Titles = titles, Languages = languages };
    }

    /// <summary>
    /// Настройка другого набора по основному: общие слоты — с теми же тайтлами и языком, порядок — как в основном
    /// (недостающих просто нет), слоты, которых в основном не было, — в конец, с настройками по умолчанию.
    /// </summary>
    public static AudioSlotSetConfig FromMain(AudioSlotSetConfig main, AudioSlotSet set, string? defaultLanguage = "rus")
    {
        var keys = set.Slots.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var defaults = Default(set, defaultLanguage);
        var order = main.Order.Where(keys.Contains).Concat(set.Slots.Select(s => s.Key).Where(k => !main.Order.Contains(k))).ToList();
        return new AudioSlotSetConfig
        {
            Order = order,
            Titles = main.Titles.Where(t => keys.Contains(t.Key)).ToDictionary(),
            Languages = set.Slots.ToDictionary(
                s => s.Key,
                s => main.Order.Contains(s.Key) ? main.Languages.GetValueOrDefault(s.Key) : defaults.Languages.GetValueOrDefault(s.Key))
                .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!),
        };
    }
}

/// <summary>
/// «Сборка аудио»: видео + выбранные дорожки исходника + внешние
/// аудиофайлы → новый файл без перекодирования, с заданным порядком, тайтлами, языком и дорожкой по умолчанию.
/// </summary>
public static class AudioMuxOperation
{
    public const string OutputFolderName = "Processed Audio";

    /// <summary>Расширения внешних аудиофайлов.</summary>
    public static IReadOnlyList<string> AudioExtensions { get; } = [".mka", ".wav", ".mp3", ".ac3", ".dts", ".flac", ".aac", ".m4a"];

    public static async Task<AudioMuxSource> InspectAsync(string folder, IMediaProbe probe, CancellationToken ct = default)
    {
        var files = MediaFiles.List(folder, MediaFiles.VideoExtensions);
        if (files.Count == 0)
        {
            throw new PlanException("Нет подходящих файлов в папке.");
        }

        var outputFolder = Path.Combine(folder, OutputFolderName);
        var done = files.Where(f => MediaFiles.IsDone(OutputPath(outputFolder, f))).ToList();
        var videos = files.Except(done).ToList();
        if (videos.Count == 0)
        {
            throw new PlanException("Все файлы уже обработаны.");
        }

        return new AudioMuxSource(folder, videos, done, await AudioTracks.ReadAsync(probe, videos[0], ct).ConfigureAwait(false));
    }

    /// <summary>Находит внешние файлы и слоты каждой серии и раскладывает серии по наборам.</summary>
    /// <param name="trackIds">Дорожки исходника (0-based, по порядку ввода); пусто — не брать ничего из исходника.</param>
    public static async Task<AudioMuxAnalysis> AnalyzeAsync(
        AudioMuxSource source, IReadOnlyList<int> trackIds, bool useExternal, IMediaProbe probe, CancellationToken ct = default)
    {
        if (trackIds.Any(t => t < 0))
        {
            throw new PlanException("ID аудиодорожки должен быть положительным числом.");
        }

        if (source.Tracks.Count > 0 && trackIds.Any(t => t >= source.Tracks.Count))
        {
            throw new PlanException($"В файле только {source.Tracks.Count} аудиодорожек — выбран несуществующий ID.");
        }

        if (trackIds.Count == 0 && !useExternal)
        {
            throw new PlanException("Не выбрано ни одной аудиодорожки и отключено добавление внешних файлов.");
        }

        var outputFolder = Path.Combine(source.Folder, OutputFolderName);
        var episodes = new List<(AudioMuxEpisode Episode, IReadOnlyList<AudioSlot> Slots)>();
        foreach (var video in source.Videos)
        {
            var tracks = video == source.Videos[0] ? source.Tracks : await AudioTracks.ReadAsync(probe, video, ct).ConfigureAwait(false);
            var slots = new List<EpisodeSlot>();
            var described = new List<AudioSlot>();
            foreach (var tid in trackIds.Where(t => t < tracks.Count))
            {
                var title = tracks[tid].Title;
                var defaultTitle = !string.IsNullOrEmpty(title) ? title : $"Track {tid + 1}";
                slots.Add(new EpisodeSlot($"int:{tid}", 0, tid, title));
                described.Add(new AudioSlot
                {
                    Key = $"int:{tid}",
                    Kind = AudioSlotKind.Internal,
                    TrackId = tid,
                    Label = $"Внутр. дорожка {tid + 1}: {defaultTitle}",
                    DefaultTitle = defaultTitle,
                    HasTitle = !string.IsNullOrEmpty(title),
                });
            }

            var externals = new List<string>();
            if (useExternal)
            {
                var baseName = MediaFiles.WithoutLastExtension(Path.GetFileName(video));
                foreach (var file in FindExternal(source.Folder, baseName, outputFolder))
                {
                    externals.Add(file);
                    var input = externals.Count;
                    var rel = Path.GetRelativePath(source.Folder, file).Replace('\\', '/');
                    var name = Path.GetFileName(file);
                    var relDir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                    var (prefix, tail) = SplitName(name, baseName);
                    var titles = await StreamTitlesAsync(probe, file, ct).ConfigureAwait(false);
                    var multi = titles.Count > 1;
                    for (var k = 0; k < titles.Count; k++)
                    {
                        var key = $"ext:{relDir}|{prefix}|{tail}|{k}";
                        var hasTitle = titles[k].Length > 0;
                        var defaultTitle = hasTitle ? titles[k] : name + (multi ? $" #{k + 1}" : "");
                        slots.Add(new EpisodeSlot(key, input, k, titles[k]));
                        described.Add(new AudioSlot
                        {
                            Key = key,
                            Kind = AudioSlotKind.External,
                            ExternalFile = rel,
                            Stream = k,
                            Label = $"Внешний файл: {rel.Replace('/', Path.DirectorySeparatorChar)}" + (multi ? $" [дорожка {k + 1}: {defaultTitle}]" : ""),
                            DefaultTitle = defaultTitle,
                            HasTitle = hasTitle,
                        });
                    }
                }
            }

            episodes.Add((new AudioMuxEpisode
            {
                Video = video,
                Output = OutputPath(outputFolder, video),
                ExternalFiles = externals,
                Slots = slots,
            }, described));
        }

        // Наборы — по точному списку ключей; порядок наборов — по первой серии
        var sets = episodes
            .GroupBy(e => string.Join('\n', e.Episode.Slots.Select(s => s.Key)), StringComparer.Ordinal)
            .Select((g, i) => new AudioSlotSet { Slots = g.First().Slots, Episodes = [.. g.Select(e => e.Episode)], IsMain = i == 0 })
            .ToList();
        return new AudioMuxAnalysis(source, trackIds, sets);
    }

    /// <summary>План: по настройке на каждый набор (в порядке <see cref="AudioMuxAnalysis.Sets"/>).</summary>
    public static OperationPlan Plan(AudioMuxAnalysis analysis, IReadOnlyList<AudioSlotSetConfig> configs)
    {
        if (configs.Count != analysis.Sets.Count)
        {
            throw new ArgumentException("Нужна настройка на каждый набор дорожек", nameof(configs));
        }

        var items = new List<PlanItem>();
        var outputFolder = Path.Combine(analysis.Source.Folder, OutputFolderName);
        foreach (var done in analysis.Source.Done)
        {
            items.Add(new PlanItem
            {
                Source = done,
                Label = Path.GetFileName(done),
                Status = PlanItemStatus.Skip,
                Reason = "уже готово",
                Outputs = [OutputPath(outputFolder, done)],
            });
        }

        for (var s = 0; s < analysis.Sets.Count; s++)
        {
            var set = analysis.Sets[s];
            var config = configs[s];
            var keys = set.Slots.Select(x => x.Key).ToList();
            if (config.Order.Count != keys.Count || !config.Order.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
            {
                throw new PlanException("Порядок дорожек должен содержать все дорожки набора без повторов.");
            }

            var slotByKey = set.Slots.ToDictionary(x => x.Key);
            foreach (var episode in set.Episodes)
            {
                var label = Path.GetFileName(episode.Video);
                if (config.Order.Count == 0)
                {
                    items.Add(new PlanItem { Source = episode.Video, Label = label, Status = PlanItemStatus.Error, Reason = "у серии нет ни одной аудиодорожки для сборки" });
                    continue;
                }

                items.Add(new PlanItem
                {
                    Source = episode.Video,
                    Label = label,
                    Status = PlanItemStatus.Run,
                    Outputs = [episode.Output],
                    Command = new PlannedCommand(Tool.Ffmpeg, Command(episode, config, slotByKey)),
                });
            }
        }

        // по порядку файлов, а не по наборам
        var order = analysis.Source.Done.Concat(analysis.Source.Videos).ToList();
        items.Sort((a, b) => order.IndexOf(a.Source).CompareTo(order.IndexOf(b.Source)));
        return new OperationPlan("Обработка аудио", analysis.Source.Folder, items);
    }

    private static List<string> Command(AudioMuxEpisode episode, AudioSlotSetConfig config, Dictionary<string, AudioSlot> slotByKey)
    {
        List<string> args = ["-nostdin", "-y", "-i", episode.Video];
        foreach (var external in episode.ExternalFiles)
        {
            args.AddRange(["-i", external]);
        }

        // «?» — у аудио-only источника (экспорт без видео) видеодорожки нет
        args.AddRange(["-map", "0:v:0?"]);
        var slots = config.Order.Select(key => episode.Slots.First(x => x.Key == key)).ToList();
        foreach (var slot in slots)
        {
            args.AddRange(["-map", slot.Input == 0 ? $"0:a:{slot.Stream}" : $"{slot.Input}:a:{slot.Stream}"]);
        }

        for (var i = 0; i < slots.Count; i++)
        {
            args.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : "none"]);
        }

        // Тайтлы: свой или настоящий (у MOV ffmpeg при ремуксе свои тайтлы не переносит); заглушки «Track N» не пишутся
        for (var i = 0; i < slots.Count; i++)
        {
            var described = slotByKey[slots[i].Key];
            if (config.Titles.TryGetValue(slots[i].Key, out var title))
            {
                args.AddRange([$"-metadata:s:a:{i}", $"title={title}"]);
            }
            else if (described.HasTitle)
            {
                args.AddRange([$"-metadata:s:a:{i}", $"title={described.DefaultTitle}"]);
            }
        }

        // Язык: у внешних дорожек тайтл этой серии (eng / jp) важнее общей настройки слота
        for (var i = 0; i < slots.Count; i++)
        {
            if (!config.Languages.TryGetValue(slots[i].Key, out var lang) || lang.Length == 0)
            {
                continue;
            }

            if (slots[i].Input > 0)
            {
                lang = LanguageGuess.Detect(slots[i].StreamTitle ?? "") ?? lang;
            }

            args.AddRange([$"-metadata:s:a:{i}", $"language={lang}"]);
        }

        args.AddRange(["-c:v", "copy", "-c:a", "copy", episode.Output]);
        return args;
    }

    /// <summary>Выход — «Processed Audio\имя видео с _ → пробел».</summary>
    public static string OutputPath(string outputFolder, string video) =>
        Path.Combine(outputFolder, TitleText.OutputFileName(Path.GetFileName(video)));

    /// <summary>
    /// Внешние аудио серии: рекурсивно по рабочей папке, кроме выходной и папок
    /// на «.»; сначала файлы папки, потом подпапки; всё — натуральной сортировкой.
    /// </summary>
    public static IReadOnlyList<string> FindExternal(string folder, string baseName, string excludeFolder)
    {
        var found = new List<string>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var excluded = Path.GetFullPath(excludeFolder).TrimEnd(Path.DirectorySeparatorChar);
        Walk(folder);
        return found;

        void Walk(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, NaturalSort.Comparer))
            {
                var name = Path.GetFileName(file);
                if (AudioExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && ExternalAudio.Matches(name, baseName))
                {
                    found.Add(file);
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, NaturalSort.Comparer))
            {
                if (!Path.GetFileName(sub).StartsWith('.') && !string.Equals(Path.GetFullPath(sub).TrimEnd(Path.DirectorySeparatorChar), excluded, comparison))
                {
                    Walk(sub);
                }
            }
        }
    }

    /// <summary>Префикс «N. » и хвост имени после названия серии: «2. Show - 01.DEEP.mka» → («2. », «.DEEP.mka»).</summary>
    private static (string Prefix, string Tail) SplitName(string name, string baseName)
    {
        foreach (var candidate in new[] { name, NaturalSort.StripTrackNumber(name) })
        {
            if (candidate.StartsWith(baseName, StringComparison.Ordinal))
            {
                return (name[..(name.Length - candidate.Length)], candidate[baseName.Length..]);
            }
        }

        return ("", name);
    }

    /// <summary>Тайтлы аудиодорожек внешнего файла ('' — нет тайтла); ffprobe не смог — одна дорожка без тайтла.</summary>
    private static async Task<IReadOnlyList<string>> StreamTitlesAsync(IMediaProbe probe, string path, CancellationToken ct)
    {
        try
        {
            var titles = (await probe.ProbeAsync(path, ct).ConfigureAwait(false)).AudioStreams
                .Select(s => TextUtils.Strip(s.Title ?? "")).ToList();
            return titles.Count > 0 ? titles : [""];
        }
        catch (MediaProbeException)
        {
            return [""];
        }
    }
}
