using System.Globalization;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.AudioExtract;

public enum AudioExtractMode
{
    /// <summary>Отдельный файл на каждую дорожку.</summary>
    Separate,

    /// <summary>Все выбранные дорожки в один .mka на серию.</summary>
    SingleMka,
}

/// <summary>Настройки «Только аудио».</summary>
public sealed record AudioExtractOptions
{
    /// <summary>Дорожки 0-based в порядке ввода.</summary>
    public required IReadOnlyList<int> TrackIds { get; init; }

    /// <summary>Важно, только если дорожек больше одной.</summary>
    public AudioExtractMode Mode { get; init; } = AudioExtractMode.Separate;

    /// <summary>«N. » перед папкой и файлом (Separate, больше одной дорожки).</summary>
    public bool NumberTracks { get; init; } = true;

    /// <summary>SingleMka: свои тайтлы (номер дорожки → тайтл); нет ключа — настоящий тайтл дорожки.</summary>
    public IReadOnlyDictionary<int, string>? Titles { get; init; }

    /// <summary>SingleMka: язык по умолчанию (jpn/eng угадываются по тайтлу); null — язык не проставлять.</summary>
    public string? Language { get; init; } = "rus";

    /// <summary>
    /// SingleMka: язык дорожки, выбранный вручную (номер дорожки → код; пусто — не ставить), важнее
    /// <see cref="Language"/> и угадывания. Нет ключа — как задаёт <see cref="Language"/>.
    /// </summary>
    public IReadOnlyDictionary<int, string>? Languages { get; init; }
}

/// <summary>Что есть в папке для «Только аудио»: файлы и дорожки первого из них.</summary>
public sealed record AudioExtractSource(string Folder, IReadOnlyList<string> Files, IReadOnlyList<AudioTrackInfo> Tracks);

/// <summary>«Только аудио»: выбранные дорожки — по файлу на дорожку или все в один .mka на серию.</summary>
public static class AudioExtractOperation
{
    public const string OutputFolderName = "Audio only";

    /// <summary>.mka тоже: из уже собранного многодорожечного .mka можно достать нужные дорожки.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [.. MediaFiles.VideoExtensions, ".mka"];

    public static async Task<AudioExtractSource> InspectAsync(string folder, IMediaProbe probe, CancellationToken ct = default)
    {
        var files = MediaFiles.List(folder, Extensions);
        if (files.Count == 0)
        {
            throw new PlanException("Нет подходящих файлов в папке.");
        }

        return new AudioExtractSource(folder, files, await AudioTracks.ReadAsync(probe, files[0], ct).ConfigureAwait(false));
    }

    public static OperationPlan Plan(AudioExtractSource source, AudioExtractOptions options)
    {
        var ids = options.TrackIds;
        if (ids.Count == 0)
        {
            throw new PlanException("Нужно выбрать хотя бы одну аудиодорожку.");
        }

        if (ids.Any(t => t < 0 || t >= source.Tracks.Count))
        {
            throw new PlanException("Выбран ID, отсутствующий в файле.");
        }

        var outputFolder = Path.Combine(source.Folder, OutputFolderName);
        return ids.Count > 1 && options.Mode == AudioExtractMode.SingleMka
            ? PlanSingleMka(source, options, outputFolder)
            : PlanSeparate(source, options, outputFolder);
    }

    /// <summary>
    /// Имя папки дорожки: тайтл → кодек из «Audio: …» → номер; санитизация — <see cref="TitleText.SanitizeTrackFolder"/>;
    /// повтор имени → «имя_N» (N — номер дорожки в исходнике).
    /// </summary>
    public static IReadOnlyDictionary<int, string> TrackFolderNames(IReadOnlyList<AudioTrackInfo> tracks, IReadOnlyList<int> ids)
    {
        var names = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var track = tracks[id];
            var title = !string.IsNullOrEmpty(track.Title) ? track.Title
                : !string.IsNullOrEmpty(track.Description) ? track.Description
                : (id + 1).ToString(CultureInfo.InvariantCulture);
            var name = TitleText.SanitizeTrackFolder(title);
            if (used.Contains(name))
            {
                name = $"{name}_{id + 1}";
            }

            used.Add(name);
            names[id] = name;
        }

        return names;
    }

    /// <summary>Язык по умолчанию для «один .mka»: jpn/eng по своему и настоящему тайтлу, иначе <paramref name="fallback"/>.</summary>
    public static string DefaultLanguage(AudioTrackInfo track, string? title, string fallback) =>
        LanguageGuess.Detect(string.Join(" ", new[] { title, track.Title }.Where(t => !string.IsNullOrEmpty(t)))) ?? fallback;

    private static OperationPlan PlanSeparate(AudioExtractSource source, AudioExtractOptions options, string outputFolder)
    {
        var multi = options.TrackIds.Count > 1;
        var folders = TrackFolderNames(source.Tracks, options.TrackIds);
        var items = new List<PlanItem>();
        foreach (var file in source.Files)
        {
            var name = Path.GetFileName(file);
            var baseName = TitleText.OutputFileName(MediaFiles.WithoutLastExtension(name));
            foreach (var id in options.TrackIds)
            {
                string output;
                if (multi)
                {
                    var num = options.NumberTracks ? $"{id + 1}. " : "";
                    output = Path.Combine(outputFolder, num + folders[id], $"{num}{baseName}.{folders[id]}.mka");
                }
                else
                {
                    output = Path.Combine(outputFolder, baseName + ".mka");
                }

                var label = $"{name} → {folders[id]}";
                items.Add(MediaFiles.IsDone(output)
                    ? new PlanItem { Source = file, Label = label, Status = PlanItemStatus.Skip, Reason = "уже готово", Outputs = [output] }
                    : new PlanItem
                    {
                        Source = file,
                        Label = label,
                        Status = PlanItemStatus.Run,
                        Outputs = [output],
                        Command = new PlannedCommand(Tool.Ffmpeg, ["-nostdin", "-y", "-i", file, "-map", $"0:a:{id}", "-c:a", "copy", output]),
                    });
            }
        }

        return new OperationPlan("Только аудио", source.Folder, items);
    }

    private static OperationPlan PlanSingleMka(AudioExtractSource source, AudioExtractOptions options, string outputFolder)
    {
        var ids = options.TrackIds;
        var titles = ids.ToDictionary(
            id => id,
            id => options.Titles is not null && options.Titles.TryGetValue(id, out var own) ? own : source.Tracks[id].Title);
        var langs = new Dictionary<int, string>();
        foreach (var id in ids)
        {
            if (options.Languages is not null && options.Languages.TryGetValue(id, out var chosen))
            {
                langs[id] = chosen.Trim();
            }
            else if (options.Language is { } defaultLang)
            {
                langs[id] = DefaultLanguage(source.Tracks[id], titles[id], defaultLang);
            }
        }

        var items = new List<PlanItem>();
        foreach (var file in source.Files)
        {
            var name = Path.GetFileName(file);
            var output = Path.Combine(outputFolder, TitleText.OutputFileName(MediaFiles.WithoutLastExtension(name)) + ".mka");
            if (MediaFiles.IsDone(output))
            {
                items.Add(new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже готово", Outputs = [output] });
                continue;
            }

            List<string> args = ["-nostdin", "-y", "-i", file];
            foreach (var id in ids)
            {
                args.AddRange(["-map", $"0:a:{id}"]);
            }

            args.AddRange(["-c:a", "copy", "-vn", "-sn", "-dn", "-map_chapters", "-1"]);
            for (var i = 0; i < ids.Count; i++)
            {
                if (!string.IsNullOrEmpty(titles[ids[i]]))
                {
                    args.AddRange([$"-metadata:s:a:{i}", $"title={titles[ids[i]]}"]);
                }

                if (langs.TryGetValue(ids[i], out var lang) && lang.Length > 0)
                {
                    args.AddRange([$"-metadata:s:a:{i}", $"language={lang}"]);
                }

                args.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : "none"]);
            }

            args.Add(output);
            items.Add(new PlanItem
            {
                Source = file,
                Label = name,
                Status = PlanItemStatus.Run,
                Outputs = [output],
                Command = new PlannedCommand(Tool.Ffmpeg, args),
            });
        }

        return new OperationPlan("Только аудио → один .mka", source.Folder, items);
    }
}
