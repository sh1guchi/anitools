using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Subtitles;

public enum SubtitleMatchMode
{
    /// <summary>Один и тот же ID дорожки во всех файлах.</summary>
    ById,

    /// <summary>В каждой серии дорожка с таким же тайтлом.</summary>
    ByTitle,

    /// <summary>В каждой серии дорожка на том же языке (если их несколько — с тем же тайтлом или на том же месте).</summary>
    ByLanguage,
}

public enum SubtitleKind
{
    /// <summary>Папка «надписи», приписка «.надписи».</summary>
    Signs,

    /// <summary>Папка «сабы», приписка «.сабы».</summary>
    Subs,
}

/// <param name="ReferenceTrackId">ID дорожки (mkvmerge) в первом файле.</param>
public sealed record SubtitleExtractOptions(int ReferenceTrackId, SubtitleMatchMode Mode = SubtitleMatchMode.ById, SubtitleKind Kind = SubtitleKind.Signs);

/// <summary>Что есть в папке для п.4: файлы и дорожки субтитров первого из них.</summary>
public sealed record SubtitleSource(string Folder, IReadOnlyList<string> Files, IReadOnlyList<SubtitleTrack> Tracks);

/// <summary>П.4 «Извлечь субтитры» (extract_subtitles, py:2841).</summary>
public static class SubtitleExtractOperation
{
    public static async Task<SubtitleSource> InspectAsync(string folder, IMediaProbe probe, CancellationToken ct = default)
    {
        var files = MediaFiles.List(folder, MediaFiles.VideoExtensions);
        if (files.Count == 0)
        {
            throw new PlanException("Нет подходящих файлов в папке.");
        }

        var tracks = (await probe.IdentifyAsync(files[0], ct).ConfigureAwait(false)).SubtitleTracks();
        if (tracks.Count == 0)
        {
            throw new PlanException($"Субтитры не найдены в файле {Path.GetFileName(files[0])}.");
        }

        return new SubtitleSource(folder, files, tracks);
    }

    public static (string Folder, string Suffix) Output(SubtitleKind kind) =>
        kind == SubtitleKind.Subs ? ("сабы", ".сабы") : ("надписи", ".надписи");

    public static async Task<OperationPlan> PlanAsync(SubtitleSource source, SubtitleExtractOptions options, IMediaProbe probe, CancellationToken ct = default)
    {
        var reference = source.Tracks.FirstOrDefault(t => t.Id == options.ReferenceTrackId)
            ?? throw new PlanException($"В первом файле нет дорожки субтитров с ID {options.ReferenceTrackId}.");
        var refLangs = SubtitleTrackMatcher.Languages(reference);
        if (options.Mode == SubtitleMatchMode.ByLanguage && refLangs.Count == 0)
        {
            throw new PlanException("У этой дорожки не указан язык — выбери другую или режим «По ID» / «По тайтлу».");
        }

        // Какая это по счёту дорожка своего языка — чтобы различать, например, русские надписи и полные
        var refPosition = options.Mode == SubtitleMatchMode.ByLanguage
            ? source.Tracks.Where(t => SubtitleTrackMatcher.Languages(t).Overlaps(refLangs)).ToList().FindIndex(t => t.Id == reference.Id)
            : 0;
        var (folderName, suffix) = Output(options.Kind);
        var outputFolder = Path.Combine(source.Folder, folderName);

        var items = new List<PlanItem>();
        foreach (var file in source.Files)
        {
            var name = Path.GetFileName(file);
            MkvIdentification info;
            try
            {
                info = await probe.IdentifyAsync(file, ct).ConfigureAwait(false);
            }
            catch (MediaProbeException ex)
            {
                items.Add(new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Error, Reason = ex.Message });
                continue;
            }

            var tracks = info.SubtitleTracks();
            (int Id, string Extension)? hit = options.Mode switch
            {
                SubtitleMatchMode.ByTitle => SubtitleTrackMatcher.FindByTitle(tracks, reference.Name),
                SubtitleMatchMode.ByLanguage => SubtitleTrackMatcher.FindByLanguage(tracks, reference, refPosition),
                _ => (reference.Id, SubtitleTrackMatcher.CodecIdToExtension(reference.CodecId)),
            };
            if (hit is null)
            {
                var what = options.Mode == SubtitleMatchMode.ByTitle
                    ? $"с тайтлом «{reference.Name}»"
                    : $"на языке {(reference.Language.Length > 0 ? reference.Language : reference.LanguageIetf)}";
                var have = string.Join(", ", tracks.Select(t => options.Mode == SubtitleMatchMode.ByTitle ? t.Name : (t.Language.Length > 0 ? t.Language : "?")));
                items.Add(new PlanItem
                {
                    Source = file,
                    Label = name,
                    Status = PlanItemStatus.Skip,
                    Reason = $"дорожка {what} не найдена ({(have.Length > 0 ? have : "нет субтитров")})",
                });
                continue;
            }

            var (id, extension) = hit.Value;
            PlannedCommand command;
            if (info.IsMatroska || info.ContainerType is null)
            {
                var output = Path.Combine(outputFolder, TitleText.OutputFileName(MediaFiles.WithoutLastExtension(name)) + suffix + extension);
                command = new PlannedCommand(Tool.Mkvextract, ["tracks", file, $"{id}:{output}"]) { WarningExitCodes = [1] };
                items.Add(Item(file, name, output, command));
            }
            else
            {
                // mkvextract работает только с Matroska — остальное (MP4 и т.п.) извлекает ffmpeg (docs/PLAN.md §2.8 #4).
                // Номер среди дорожек субтитров = «0:s:N»; mov_text (Timed Text) переводится в SRT.
                var track = tracks.First(t => t.Id == id);
                var codec = info.Tracks.First(t => t.Id == id).Codec;
                var timedText = codec.Contains("Timed Text", StringComparison.OrdinalIgnoreCase)
                    || codec.Contains("tx3g", StringComparison.OrdinalIgnoreCase)
                    || codec.Contains("mov_text", StringComparison.OrdinalIgnoreCase);
                var ext = timedText ? ".srt" : extension;
                var output = Path.Combine(outputFolder, TitleText.OutputFileName(MediaFiles.WithoutLastExtension(name)) + suffix + ext);
                var n = tracks.ToList().IndexOf(track);
                command = new PlannedCommand(Tool.Ffmpeg, ["-nostdin", "-y", "-i", file, "-map", $"0:s:{n}", "-c:s", timedText ? "srt" : "copy", output]);
                items.Add(Item(file, name, output, command));
            }
        }

        return new OperationPlan($"Субтитры → {folderName}", source.Folder, items);
    }

    /// <summary>
    /// Надписи и сабы за один запуск: у каждой папки — своя дорожка (режим поиска в сериях общий). Шаги идут по
    /// сериям: «01 · надписи», «01 · сабы», «02 · надписи»…
    /// </summary>
    public static async Task<OperationPlan> PlanAsync(
        SubtitleSource source, IReadOnlyList<SubtitleExtractOptions> selections, IMediaProbe probe, CancellationToken ct = default)
    {
        if (selections.Count == 0)
        {
            throw new PlanException("Выберите дорожку для надписей или для сабов.");
        }

        if (selections.Count == 1)
        {
            return await PlanAsync(source, selections[0], probe, ct).ConfigureAwait(false);
        }

        var plans = new List<OperationPlan>();
        foreach (var selection in selections)
        {
            plans.Add(await PlanAsync(source, selection, probe, ct).ConfigureAwait(false));
        }

        var items = plans
            .SelectMany((plan, kind) => plan.Items.Select((item, index) => (item, index, kind, folder: Output(selections[kind].Kind).Folder)))
            .OrderBy(x => x.index)
            .ThenBy(x => x.kind)
            .Select(x => x.item with { Label = $"{x.item.Label} · {x.folder}" })
            .ToList();
        var folders = string.Join(" и ", selections.Select(s => Output(s.Kind).Folder));
        return new OperationPlan($"Субтитры → {folders}", source.Folder, items);
    }

    private static PlanItem Item(string file, string name, string output, PlannedCommand command) =>
        MediaFiles.IsDone(output)
            ? new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже готово", Outputs = [output] }
            : new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Run, Outputs = [output], Command = command };
}
