using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.MkaMux;

/// <summary>Аудиофайл для сборки: путь относительно папки, тайтл первой дорожки и сколько в нём аудиодорожек.</summary>
public sealed record MkaSource(string RelativePath, string FullPath, string? Title, int AudioStreams)
{
    public string Name => Path.GetFileName(FullPath);

    /// <summary>Метка озвучки (voice_label): папка «N. Имя» → тайтл дорожки → последний [тег] → остаток имени.</summary>
    public string Label => MkaNames.VoiceFolder(RelativePath).Label ?? MkaNames.LabelFromFile(Name, Title, MkaNames.EpisodeBase(Name));

    /// <summary>Номер для порядка по умолчанию: «N. » у папки, иначе у файла.</summary>
    public int? Number => MkaNames.VoiceFolder(RelativePath).Number ?? MkaNames.TrackNumber(Name);
}

public enum MkaMuxMode
{
    /// <summary>По номеру серии: один .mka на серию (пакетно).</summary>
    ByEpisode,

    /// <summary>Все файлы в один .mka (фильм/OVA).</summary>
    SingleFile,
}

/// <summary>Выход и его файлы (в порядке папки).</summary>
public sealed record MkaGroup(string OutputName, IReadOnlyList<MkaSource> Files);

/// <summary>Озвучка: метка и пример файла (для подсказки).</summary>
public sealed record MkaLabel(string Label, string ExampleFile);

/// <summary>Порядок меток, тайтлы дорожек и язык — настраиваются один раз на все серии.</summary>
public sealed record MkaLabelOptions
{
    /// <summary>Метки по порядку дорожек; null — порядок по умолчанию.</summary>
    public IReadOnlyList<string>? Order { get; init; }

    /// <summary>Тайтл по метке; нет — сама метка.</summary>
    public IReadOnlyDictionary<string, string>? Titles { get; init; }

    /// <summary>Язык по умолчанию (ISO 639-2, «rus»); оригинальная/англ. озвучка определяются сами. null — язык не ставится.</summary>
    public string? Language { get; init; } = "rus";
}

/// <summary>
/// Сборка озвучек в .mka (mka_muxer.py, режим 1): аудиофайлы папки и подпапок (структура п.2 «N. Озвучка\…»)
/// группируются по номеру серии, на каждую серию — один MKA\&lt;имя&gt;.mka со всеми озвучками дорожками
/// (без перекодирования). Порядок, тайтлы и язык задаются один раз по меткам озвучек.
/// </summary>
public static class MkaMuxOperation
{
    public const string OutputFolderName = "MKA";

    public static IReadOnlyList<string> Extensions { get; } = [".mka", ".mp3", ".ac3", ".dts", ".flac", ".aac", ".m4a", ".opus", ".wav", ".eac3", ".thd"];

    /// <summary>Аудиофайлы рекурсивно (без папки MKA и папок на «.»), порядок — по номеру «N. », затем по имени.</summary>
    public static IReadOnlyList<string> Scan(string folder)
    {
        var files = new List<string>();
        Walk(folder);
        return files;

        void Walk(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => Extensions.Contains(MediaFiles.Suffix(Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(f => Path.GetFileName(f), MkaNames.NaturalComparer))
            {
                files.Add(Path.GetRelativePath(folder, file));
            }

            foreach (var sub in Directory.EnumerateDirectories(dir)
                         .Where(d => Path.GetFileName(d) is var name && name != OutputFolderName && !name.StartsWith('.'))
                         .OrderBy(f => Path.GetFileName(f), MkaNames.NaturalComparer))
            {
                Walk(sub);
            }
        }
    }

    /// <summary>Файлы и что о них знает ffprobe (тайтл первой дорожки, число дорожек; не прочитался — без тайтла, одна).</summary>
    public static async Task<IReadOnlyList<MkaSource>> InspectAsync(string folder, IMediaProbe probe, CancellationToken cancellationToken = default)
    {
        var files = Scan(folder);
        if (files.Count == 0)
        {
            throw new PlanException("Нет подходящих аудиофайлов в папке.");
        }

        var sources = new List<MkaSource>();
        foreach (var rel in files)
        {
            var full = Path.Combine(folder, rel);
            try
            {
                var audio = (await probe.ProbeAsync(full, cancellationToken).ConfigureAwait(false)).AudioStreams;
                var title = audio.Count > 0 && audio[0].Title is { } t && PyText.Strip(t).Length > 0 ? PyText.Strip(t) : null;
                sources.Add(new MkaSource(rel, full, title, Math.Max(1, audio.Count)));
            }
            catch (MediaProbeException)
            {
                sources.Add(new MkaSource(rel, full, null, 1));
            }
        }

        return sources;
    }

    /// <summary>
    /// Выходы: по серии (имя — «Тайтл - 01» первого файла; совпало у разных серий — «имя [ключ]», без номера — ключ no_ep)
    /// или один файл с заданным именем.
    /// </summary>
    public static IReadOnlyList<MkaGroup> Groups(IReadOnlyList<MkaSource> sources, MkaMuxMode mode, string? singleName = null)
    {
        if (mode == MkaMuxMode.SingleFile)
        {
            return [new MkaGroup(MkaNames.SanitizeName(singleName ?? DefaultSingleName(sources)), sources)];
        }

        var groups = new List<MkaGroup>();
        foreach (var episode in sources.GroupBy(s => MkaNames.ParseEpisode(s.Name) ?? "no_ep"))
        {
            var name = MkaNames.EpisodeBase(episode.First().Name);
            if (groups.Any(g => g.OutputName == name))
            {
                name = $"{name} [{episode.Key}]";
            }

            groups.Add(new MkaGroup(name, [.. episode]));
        }

        return groups;
    }

    /// <summary>Имя по умолчанию для «всё в один»: «Тайтл - 01» первого файла.</summary>
    public static string DefaultSingleName(IReadOnlyList<MkaSource> sources) => MkaNames.EpisodeBase(sources[0].Name);

    /// <summary>Озвучки по первому появлению; есть номера «N. » — по номеру (без номера — в конец).</summary>
    public static IReadOnlyList<MkaLabel> Labels(IReadOnlyList<MkaGroup> groups)
    {
        var first = new List<MkaSource>();
        foreach (var source in groups.SelectMany(g => g.Files))
        {
            if (first.All(f => f.Label != source.Label))
            {
                first.Add(source);
            }
        }

        if (first.Any(f => f.Number is not null))
        {
            first = [.. first.OrderBy(f => f.Number ?? long.MaxValue)];
        }

        return [.. first.Select(f => new MkaLabel(f.Label, f.Name))];
    }

    public static OperationPlan Plan(string folder, IReadOnlyList<MkaGroup> groups, MkaLabelOptions options)
    {
        var order = options.Order ?? [.. Labels(groups).Select(l => l.Label)];
        var rank = order.Select((label, i) => (label, i)).GroupBy(p => p.label).ToDictionary(g => g.Key, g => g.First().i);
        var outputFolder = Path.Combine(folder, OutputFolderName);
        var items = groups.Select(group =>
        {
            var output = Path.Combine(outputFolder, group.OutputName + ".mka");
            var ordered = group.Files
                .OrderBy(f => rank.GetValueOrDefault(f.Label, 10_000))
                .ThenBy(f => f.Name, MkaNames.NaturalComparer)
                .ToList();
            if (MediaFiles.IsDone(output))
            {
                return new PlanItem { Source = ordered[0].FullPath, Label = group.OutputName + ".mka", Status = PlanItemStatus.Skip, Reason = "уже собран", Outputs = [output] };
            }

            return new PlanItem
            {
                Source = ordered[0].FullPath,
                Label = group.OutputName + ".mka",
                Status = PlanItemStatus.Run,
                Outputs = [output],
                Command = new PlannedCommand(Tool.Ffmpeg, Command(ordered, options, output)),
            };
        }).ToList();
        return new OperationPlan("Сборка озвучек в .mka", folder, items);
    }

    /// <summary>
    /// ffmpeg (mux_group): все входы, каждая их аудиодорожка — отдельной дорожкой; первая — по умолчанию.
    /// Язык метки уточняется тайтлом самого файла (англ./ориг. дорожка важнее метки).
    /// </summary>
    private static IReadOnlyList<string> Command(IReadOnlyList<MkaSource> files, MkaLabelOptions options, string output)
    {
        var args = new List<string> { "-nostdin", "-y" };
        foreach (var f in files)
        {
            args.AddRange(["-i", f.FullPath]);
        }

        var meta = new List<string>();
        var dispositions = new List<string>();
        var outIndex = 0;
        for (var input = 0; input < files.Count; input++)
        {
            var file = files[input];
            var title = options.Titles is not null && options.Titles.TryGetValue(file.Label, out var t) && PyText.Strip(t).Length > 0 ? PyText.Strip(t) : file.Label;
            var language = options.Language is null
                ? null
                : LanguageGuess.Detect(file.Title ?? "") ?? LanguageGuess.Detect($"{file.Label} {title}") ?? DefaultLanguage(options.Language);
            for (var local = 0; local < file.AudioStreams; local++)
            {
                args.AddRange(["-map", $"{input}:a:{local}?"]);
                if (title.Length > 0)
                {
                    meta.AddRange([$"-metadata:s:a:{outIndex}", $"title={title}"]);
                }

                if (language is not null)
                {
                    meta.AddRange([$"-metadata:s:a:{outIndex}", $"language={language}"]);
                }

                dispositions.AddRange([$"-disposition:a:{outIndex}", outIndex == 0 ? "default" : "none"]);
                outIndex++;
            }
        }

        args.AddRange(["-c:a", "copy", "-vn", "-map_chapters", "-1", .. meta, .. dispositions, output]);
        return args;
    }

    private static string DefaultLanguage(string language) => PyText.Strip(language) is { Length: > 0 } l ? l : "rus";
}
