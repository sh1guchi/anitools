using System.Globalization;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Platform;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Hls;

public enum HlsStage
{
    Preparing,

    /// <summary>Битрейт исходника по пакетам (только при подборе CQ).</summary>
    Analysis,

    /// <summary>Пробное кодирование окон (только при подборе CQ).</summary>
    Calibration,
    Video,
    Audio,
    Packing,
    Moving,
}

/// <summary>Ход HLS: серия i из n, стадия, доля готовности стадии и скорость ffmpeg.</summary>
public sealed record HlsProgress(
    int EpisodeIndex, int EpisodeCount, HlsEpisode Episode, HlsStage Stage, double? Fraction = null, double? Speed = null, string? Detail = null);

public enum HlsEpisodeOutcome
{
    Done,

    /// <summary>Уже готово или исключено планом (причина в сообщении).</summary>
    Skipped,
    Failed,
    Cancelled,

    /// <summary>Не дошли: отменили раньше.</summary>
    NotRun,
}

/// <summary>Итог серии; <see cref="LogPath"/> — лог ошибки ffmpeg, если записан.</summary>
public sealed record HlsEpisodeResult(HlsEpisode Episode, HlsEpisodeOutcome Outcome, string? Message = null, string? LogPath = null)
{
    /// <summary>Пояснения по ходу: даунскейл, декодирование на CPU, повтор, отдельный архив верхнего качества.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>С каким качеством кодировалось видео; null — средний битрейт из лестницы.</summary>
    public IReadOnlyList<RateControl>? RateControl { get; init; }

    /// <summary>Фактический битрейт видео по качествам, Мбит/с (если известна длительность).</summary>
    public IReadOnlyList<(string Rung, double Mbps)> Bitrates { get; init; } = [];
}

/// <summary>Итог HLS: серии по порядку плана, начало и конец.</summary>
public sealed record HlsResult(IReadOnlyList<HlsEpisodeResult> Episodes, DateTime Started, DateTime Finished)
{
    public int Count(HlsEpisodeOutcome outcome) => Episodes.Count(e => e.Outcome == outcome);

    public bool WasCancelled => Episodes.Any(e => e.Outcome == HlsEpisodeOutcome.Cancelled);

    public TimeSpan Elapsed => Finished - Started;
}

/// <summary>
/// Выполняет план HLS: серии строго по одной (видеокарта загружена одной командой). Отмена — текущая серия
/// убирается целиком, остальные не начинаются. Ошибка серии — дальше следующая; при повторном запуске
/// готовые серии пропускаются.
/// </summary>
public sealed class HlsRunner(IProcessRunner runner, ToolPaths tools, IMediaProbe probe, ErrorLogWriter logs, HlsSettings settings, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    /// <param name="workRoot">Папка для временных файлов (RAM-диск, HDD); null — рядом с выходом.</param>
    /// <param name="episodeDone">Итог каждой серии сразу, как она закончилась (для журнала и строки хода).</param>
    public async Task<HlsResult> ExecuteAsync(
        HlsPlan plan,
        string? workRoot,
        IProgress<HlsProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<HlsEpisodeResult>? episodeDone = null)
    {
        var started = _clock();
        var processor = new HlsEpisodeProcessor(runner, tools, probe, logs, settings);
        var count = plan.Episodes.Count(e => e.Status == PlanItemStatus.Run);
        var index = 0;
        var results = new List<HlsEpisodeResult>();
        foreach (var episode in plan.Episodes)
        {
            if (episode.Status != PlanItemStatus.Run)
            {
                results.Add(new HlsEpisodeResult(episode, HlsEpisodeOutcome.Skipped, episode.Reason));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new HlsEpisodeResult(episode, HlsEpisodeOutcome.NotRun));
                continue;
            }

            index++;
            try
            {
                results.Add(await processor.ProcessAsync(episode, workRoot, index, count, progress, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new HlsEpisodeResult(episode, HlsEpisodeOutcome.Failed, ex.Message));
            }

            episodeDone?.Invoke(results[^1]);
        }

        return new HlsResult(results, started, _clock());
    }
}

/// <summary>
/// Одна серия: качество (постоянный CQ или подбор) → видео на все качества
/// одной командой → все озвучки одной командой → архивы → перенос .mka в папку тайтла.
/// Временные файлы — в workRoot/&lt;тайтл&gt;/&lt;серия&gt; (или рядом с выходом); на SSD пишутся только zip и .mka.
/// </summary>
public sealed class HlsEpisodeProcessor(IProcessRunner runner, ToolPaths tools, IMediaProbe probe, ErrorLogWriter logs, HlsSettings settings)
{
    private const int TopWidth = 3840;

    private readonly IProcessRunner _runner = runner;
    private readonly ToolPaths _tools = tools;
    private readonly IMediaProbe _probe = probe;
    private readonly ErrorLogWriter _logs = logs;
    private readonly HlsCommandBuilder _builder = new(settings);

    public async Task<HlsEpisodeResult> ProcessAsync(
        HlsEpisode episode, string? workRoot, int index, int count, IProgress<HlsProgress>? progress, CancellationToken cancellationToken)
    {
        // Резюм: zip пишется до переноса аудио, значит zip + все .mka на месте — серия готова целиком
        if (episode.IsDone)
        {
            return new HlsEpisodeResult(episode, HlsEpisodeOutcome.Skipped, HlsOperation.AlreadyDone);
        }

        var ffmpeg = _tools.Ffmpeg ?? throw new ToolNotFoundException(Tool.Ffmpeg);
        var run = new EpisodeRun(this, episode, ffmpeg, index, count, progress, cancellationToken)
        {
            WorkOut = workRoot is null ? episode.EpisodeOut : Path.Combine(workRoot, TitleText.SanitizeFolder(episode.TitleFolder), episode.EpisodeName),
            WorkRoot = workRoot,
        };
        Directory.CreateDirectory(episode.TitleOut);
        try
        {
            return await run.ExecuteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.CleanUp();
            return new HlsEpisodeResult(episode, HlsEpisodeOutcome.Cancelled, "Отменено") { Notes = run.Notes };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Диск переполнен, нет доступа, ffmpeg не запустился — серия не готова, временные файлы прочь
            run.CleanUp();
            return new HlsEpisodeResult(episode, HlsEpisodeOutcome.Failed, ex.Message) { Notes = run.Notes };
        }
    }

    private sealed class EpisodeRun(
        HlsEpisodeProcessor owner, HlsEpisode episode, string ffmpeg, int index, int count, IProgress<HlsProgress>? progress, CancellationToken ct)
    {
        public required string WorkOut { get; init; }

        public required string? WorkRoot { get; init; }

        public List<string> Notes { get; } = [];

        private HlsSettings Settings => owner._builder.Settings;

        private string Source => episode.Source.Path;

        private string LogPrefix => TextUtils.Stem(episode.Source.Name);

        public async Task<HlsEpisodeResult> ExecuteAsync()
        {
            Directory.CreateDirectory(WorkOut);
            Report(HlsStage.Preparing);
            MediaInfo info;
            try
            {
                info = await owner._probe.ProbeAsync(Source, ct).ConfigureAwait(false);
            }
            catch (MediaProbeException ex)
            {
                CleanUp();
                return Result(HlsEpisodeOutcome.Failed, ex.Message);
            }

            var duration = info.Duration is > 0 ? info.Duration : null;
            var video = info.VideoStreams.FirstOrDefault();
            var nvenc = Settings.Encoder == EncoderProfile.Nvenc;
            if (video is { Width: > TopWidth } wide)
            {
                Notes.Add(Inv($"Источник {wide.Width}px → даунскейл до {TopWidth}px (4K)"));
            }

            var cpuDecode = false;
            if (nvenc && NvdecLimits.CpuDecodeReason(video?.CodecName, video?.Width, video?.Height) is { } reason)
            {
                Notes.Add($"{reason} → декодирую на CPU, масштаб и кодирование на GPU");
                cpuDecode = true;
            }

            IReadOnlyList<RateControl>? rateControl;
            if (Settings.FixedCq is { } fixedCq)
            {
                rateControl = Settings.FixedRateControl(fixedCq);
            }
            else
            {
                (rateControl, cpuDecode) = await CalibrateAsync(cpuDecode).ConfigureAwait(false);
            }

            // ── Видео: все качества одной командой; NVDEC не справился — ещё раз с декодированием на CPU ──
            var (ok, log) = await RunVideoAsync(cpuDecode, rateControl, duration).ConfigureAwait(false);
            if (!ok && !cpuDecode && nvenc)
            {
                Notes.Add("Видеокарта не смогла декодировать — повтор с декодированием на CPU");
                DeleteDirectory(WorkOut);
                Directory.CreateDirectory(WorkOut);
                (ok, log) = await RunVideoAsync(true, rateControl, duration).ConfigureAwait(false);
            }

            if (!ok)
            {
                CleanUp();
                return Result(HlsEpisodeOutcome.Failed, "Ошибка видео", log) with { RateControl = rateControl };
            }

            var bitrates = duration is { } seconds
                ? Settings.Ladder.Select(r => (Rung: r.Name, Mbps: TsBytes(owner._builder.RungDirectory(WorkOut, r)) * 8 / seconds / 1e6)).ToList()
                : [];

            // ── Аудио: все озвучки одной командой ──
            var audioArgs = owner._builder.Audio(
                Source, WorkOut, episode.Voices, [.. info.AudioStreams.Select(a => a.Channels ?? 0)], episode.EpisodeName);
            foreach (var voice in episode.Voices)
            {
                Directory.CreateDirectory(Path.Combine(WorkOut, "audio", voice.Folder));
            }

            (ok, log) = await RunFfmpegAsync(audioArgs, HlsStage.Audio, duration).ConfigureAwait(false);
            if (!ok)
            {
                CleanUp();
                return Result(HlsEpisodeOutcome.Failed, "Ошибка аудио", log) with { RateControl = rateControl, Bitrates = bitrates };
            }

            // ── Архивы прямо в папку тайтла, затем озвучки туда же ──
            Report(HlsStage.Packing);
            HlsPackResult pack;
            try
            {
                pack = HlsPackager.Pack(WorkOut, episode.TitleOut, episode.EpisodeName, Settings.Ladder, Settings.SeparateTopZipBytes, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                CleanUp(); // иначе сегменты (~6–7 ГБ) остаются на RAM-диске и следующие серии упираются в место
                return Result(HlsEpisodeOutcome.Failed, $"Ошибка архивирования: {ex.Message}") with { RateControl = rateControl, Bitrates = bitrates };
            }

            if (pack.TopZip is not null)
            {
                Notes.Add(Inv($"Папка {Settings.Ladder[^1].Name} весит {pack.TopBytes / (1024.0 * 1024 * 1024):F2} ГБ — упакована в отдельный архив"));
            }

            Report(HlsStage.Moving);
            try
            {
                HlsPackager.MoveAudio(WorkOut, episode.TitleOut);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CleanUp();
                return Result(HlsEpisodeOutcome.Failed, $"Ошибка переноса: {ex.Message}") with { RateControl = rateControl, Bitrates = bitrates };
            }

            CleanUp();
            if (WorkRoot is not null)
            {
                DeleteDirectory(episode.EpisodeOut); // остаток прошлого запуска «рядом с выходом»
            }

            return Result(HlsEpisodeOutcome.Done) with { RateControl = rateControl, Bitrates = bitrates };
        }

        /// <summary>Рабочая папка серии — прочь; на временном диске заодно пустая папка тайтла.</summary>
        public void CleanUp()
        {
            DeleteDirectory(WorkOut);
            if (WorkRoot is not null && Path.GetDirectoryName(WorkOut) is { } titleDir)
            {
                try
                {
                    if (Directory.Exists(titleDir) && !Directory.EnumerateFileSystemEntries(titleDir).Any())
                    {
                        Directory.Delete(titleDir);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Подбор CQ: анализ битрейта, окна; не вышло на видеокарте — ещё раз с CPU-декодированием.</summary>
        private async Task<(IReadOnlyList<RateControl>? RateControl, bool CpuDecode)> CalibrateAsync(bool cpuDecode)
        {
            Report(HlsStage.Analysis);
            var ffprobe = owner._tools.Ffprobe ?? throw new ToolNotFoundException(Tool.Ffprobe);
            var accumulator = new SourceBitrate.Accumulator(Settings.SegmentSeconds);
            await owner._runner.RunAsync(SourceBitrate.Command(ffprobe, Source, accumulator.Add), ct).ConfigureAwait(false);
            if (accumulator.Result() is not { } source)
            {
                Notes.Add("Не удалось подобрать качество — кодирую со средним битрейтом из таблицы");
                return (null, cpuDecode);
            }

            var started = DateTime.UtcNow;
            var calibrator = new CqCalibrator(owner._runner, ffmpeg, owner._builder, owner._logs);
            var status = new InlineProgress<CalibrationProgress>(p =>
                Report(HlsStage.Calibration, (double)p.Window / p.WindowCount, detail: Inv($"проход {p.Pass}, окно {p.Window}/{p.WindowCount}")));
            var rateControl = await calibrator.CalibrateAsync(Source, source, cpuDecode, WorkOut, status, ct).ConfigureAwait(false);
            if (rateControl is null && !cpuDecode && Settings.Encoder == EncoderProfile.Nvenc)
            {
                Notes.Add("Подбор качества не прошёл на видеокарте — повтор с декодированием на CPU");
                cpuDecode = true;
                rateControl = await calibrator.CalibrateAsync(Source, source, cpuDecode, WorkOut, status, ct).ConfigureAwait(false);
            }

            if (rateControl is null)
            {
                Notes.Add("Не удалось подобрать качество — кодирую со средним битрейтом из таблицы");
            }
            else
            {
                var elapsed = DateTime.UtcNow - started;
                Notes.Add(Inv($"CQ под серию ({(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}): ")
                    + string.Join(" · ", Settings.Ladder.Zip(rateControl, (r, rc) => Inv($"{r.Name} {rc.Cq:F1}"))));
            }

            return (rateControl, cpuDecode);
        }

        private Task<(bool Ok, string? Log)> RunVideoAsync(bool cpuDecode, IReadOnlyList<RateControl>? rateControl, double? duration)
        {
            foreach (var rung in Settings.Ladder)
            {
                Directory.CreateDirectory(owner._builder.RungDirectory(WorkOut, rung)); // hls-муксер папки не создаёт
            }

            return RunFfmpegAsync(owner._builder.Video(Source, WorkOut, cpuDecode, rateControl), HlsStage.Video, duration);
        }

        private async Task<(bool Ok, string? Log)> RunFfmpegAsync(IReadOnlyList<string> args, HlsStage stage, double? duration)
        {
            Report(stage, 0);
            var parser = new FfmpegProgressParser(p => Report(stage, duration is { } d ? p.Fraction(d) : null, p.Speed));
            var spec = new ProcessSpec(ffmpeg, ["-progress", "pipe:1", "-nostats", .. args]) { OnStdoutLine = parser.Feed };
            var result = await owner._runner.RunAsync(spec, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                Report(stage, 1);
                return (true, null);
            }

            return (false, owner._logs.WriteProcessError(LogPrefix, result.StandardErrorTail, [ffmpeg, .. spec.Arguments], result.ExitCode));
        }

        private HlsEpisodeResult Result(HlsEpisodeOutcome outcome, string? message = null, string? log = null) =>
            new(episode, outcome, message, log) { Notes = Notes };

        private void Report(HlsStage stage, double? fraction = null, double? speed = null, string? detail = null) =>
            progress?.Report(new HlsProgress(index, count, episode, stage, fraction, speed, detail));

        private static long TsBytes(string dir) =>
            Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.ts").Sum(f => f.Length) : 0;

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    ReadOnlyAttr.ClearTree(path);
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string Inv(FormattableString text) => FormattableString.Invariant(text);
    }

    /// <summary>IProgress без SynchronizationContext: событие сразу, в том же потоке и по порядку.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
