using Anitools.Core.Logging;
using Anitools.Core.Parsing;
using Anitools.Core.Platform;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Hls;

/// <summary>Ход подбора качества: проход и окно.</summary>
public sealed record CalibrationProgress(int Pass, int Window, int WindowCount);

/// <summary>
/// Подбор CQ под серию, когда <see cref="HlsSettings.FixedCq"/> = null:
/// окна разной сложности пробно кодируются, CQ каждого разрешения подгоняется, пока средний битрейт серии
/// не совпадёт с битрейтом из лестницы (± допуск). Повторные проходы — только по разрешениям мимо цели.
/// </summary>
public sealed class CqCalibrator
{
    private readonly IProcessRunner _runner;
    private readonly string _ffmpeg;
    private readonly HlsCommandBuilder _builder;
    private readonly ErrorLogWriter? _logs;
    private readonly PathStyle _paths;
    private readonly Func<string, long> _fileSize;
    private readonly Action<string> _createDirectory;
    private readonly Action<string> _deleteDirectory;

    /// <param name="fileSize">Размер выхода окна; в тестах — без диска, как и создание и удаление папки.</param>
    public CqCalibrator(
        IProcessRunner runner,
        string ffmpeg,
        HlsCommandBuilder builder,
        ErrorLogWriter? logs = null,
        PathStyle? pathStyle = null,
        Func<string, long>? fileSize = null,
        Action<string>? createDirectory = null,
        Action<string>? deleteDirectory = null)
    {
        _runner = runner;
        _ffmpeg = ffmpeg;
        _builder = builder;
        _logs = logs;
        _paths = pathStyle ?? PathStyle.Current;
        _fileSize = fileSize ?? (path => new FileInfo(path).Length);
        _createDirectory = createDirectory ?? (path => Directory.CreateDirectory(path));
        _deleteDirectory = deleteDirectory ?? DeleteQuietly;
    }

    private HlsSettings Settings => _builder.Settings;

    /// <summary>
    /// Начала калибровочных окон, с: окна сортируются по битрейту исходника
    /// и делятся на <paramref name="count"/> равных страт, из каждой берётся середина — выборка покрывает
    /// и тихие сцены, и экшен, а простое среднее по окнам оценивает среднее по всей серии.
    /// </summary>
    public static IReadOnlyList<double> PickWindows(IReadOnlyList<double> rates, int count, int segmentSeconds)
    {
        var order = Enumerable.Range(0, rates.Count).OrderBy(i => rates[i]).ToList();
        count = Math.Min(count, order.Count);
        var picks = new SortedSet<int>();
        for (var k = 0; k < count; k++)
        {
            picks.Add(order[(int)((k + 0.5) / count * order.Count)]);
        }

        return [.. picks.Select(w => (double)w * segmentSeconds)];
    }

    /// <summary>
    /// Следующий CQ по замерам (CQ, битрейт): линейно по логарифму битрейта. Наклон по двум
    /// последним замерам (в пределах −0,25…−0,04), иначе «+6 к CQ ≈ битрейт / 2».
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Битрейт или цель не больше нуля (логарифм не определён).</exception>
    public static double NextCq(IReadOnlyList<(double Cq, double Rate)> history, double target, double cqMin, double cqMax)
    {
        var (cq1, rate1) = history[^1];
        var slope = -Math.Log(2) / 6;
        if (history.Count > 1)
        {
            var (cq0, rate0) = history[^2];
            if (Math.Abs(cq1 - cq0) > 0.1 && rate0 > 0 && rate1 != rate0)
            {
                slope = Math.Min(-0.04, Math.Max(-0.25, (Log(rate1) - Log(rate0)) / (cq1 - cq0)));
            }
        }

        return Math.Min(cqMax, Math.Max(cqMin, cq1 + ((Log(target) - Log(rate1)) / slope)));
    }

    /// <summary>
    /// Подбирает CQ каждому разрешению; потолок битрейта — во сколько раз пики сложности исходника выше
    /// среднего (2–3×). Возвращает по элементу на разрешение или null, если ffmpeg упал.
    /// Окна пишутся в workOut/_calibration — папка удаляется в конце в любом случае.
    /// </summary>
    public async Task<IReadOnlyList<RateControl>?> CalibrateAsync(
        string input,
        SourceBitrate source,
        bool cpuDecode,
        string workOut,
        IProgress<CalibrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ladder = Settings.Ladder;
        var peak = Math.Min(3.0, Math.Max(2.0, source.P99 / source.Average));
        var rateControl = ladder.Select(r => new RateControl(Settings.CqStart, (long)(r.Bitrate * peak))).ToArray();
        var windows = PickWindows(source.Rates, Settings.CalibrationWindows, Settings.SegmentSeconds);
        var history = ladder.Select(_ => new List<(double Cq, double Rate)>()).ToArray();
        var active = Enumerable.Range(0, ladder.Count).ToList();

        var calibrationDir = _paths.Join(workOut, "_calibration");
        _createDirectory(calibrationDir);
        try
        {
            for (var pass = 1; pass <= Settings.CalibrationMaxPasses; pass++)
            {
                var rates = await PassAsync(input, active, rateControl, windows, cpuDecode, calibrationDir, pass, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (rates is null)
                {
                    return null;
                }

                var still = new List<int>();
                for (var k = 0; k < active.Count; k++)
                {
                    var i = active[k];
                    var rate = rates[k];
                    var target = (double)ladder[i].Bitrate;
                    history[i].Add((rateControl[i].Cq, rate));
                    if (rate <= 0 || Math.Abs((rate / target) - 1) <= Settings.CalibrationTolerance)
                    {
                        continue;
                    }

                    var cq = NextCq(history[i], target, Settings.CqMin, Settings.CqMax);
                    if (Math.Abs(cq - rateControl[i].Cq) < 0.05)
                    {
                        continue; // упёрлись в границу диапазона CQ
                    }

                    rateControl[i] = rateControl[i] with { Cq = cq };
                    still.Add(i);
                }

                active = still;
                if (active.Count == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            _deleteDirectory(calibrationDir);
        }

        return rateControl;
    }

    /// <summary>
    /// Кодирует окна для разрешений <paramref name="rungs"/> и возвращает оценку среднего битрейта серии
    /// по каждому или null, если ffmpeg упал.
    /// </summary>
    private async Task<double[]?> PassAsync(
        string input,
        IReadOnlyList<int> rungs,
        IReadOnlyList<RateControl> rateControl,
        IReadOnlyList<double> windows,
        bool cpuDecode,
        string calibrationDir,
        int pass,
        IProgress<CalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totals = new long[rungs.Count];
        for (var w = 0; w < windows.Count; w++)
        {
            progress?.Report(new CalibrationProgress(pass, w + 1, windows.Count));
            var args = _builder.CalibrationWindow(input, rungs, rateControl, windows[w], cpuDecode, calibrationDir);
            var result = await _runner.RunAsync(new ProcessSpec(_ffmpeg, args), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logs?.WriteProcessError(TextUtils.Stem(Path.GetFileName(input)), result.StandardErrorTail, [_ffmpeg, .. args], result.ExitCode);
                return null;
            }

            for (var k = 0; k < rungs.Count; k++)
            {
                totals[k] += _fileSize(_builder.CalibrationOutput(calibrationDir, Settings.Ladder[rungs[k]]));
            }
        }

        return [.. totals.Select(b => (double)(b * 8) / (Settings.SegmentSeconds * windows.Count))];
    }

    private static double Log(double x) =>
        x > 0 ? Math.Log(x) : throw new ArgumentOutOfRangeException(nameof(x), x, "math domain error");

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
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
}
