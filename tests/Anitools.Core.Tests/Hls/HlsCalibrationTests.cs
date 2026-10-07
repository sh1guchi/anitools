using System.Text.Json;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Tests.Hls;

/// <summary>Битрейт исходника и подбор CQ — по эталонам оригинала.</summary>
public sealed class HlsCalibrationTests
{
    [Fact]
    public void Source_bitrate_matches_original() =>
        GoldenAssert.All("source_bitrate", input =>
        {
            var lines = input.GetString()!.Split('\n');
            var result = SourceBitrate.Analyze(lines, 6);
            return result is null ? null : new Dictionary<string, object> { ["rates"] = result.Rates, ["avg"] = result.Average, ["p99"] = result.P99 };
        });

    [Fact]
    public void Calibration_windows_match_original() =>
        GoldenAssert.All("hls_pick_calibration_windows", input =>
            CqCalibrator.PickWindows(Doubles(input.GetProperty("rates")), input.GetProperty("count").GetInt32(), 6));

    [Fact]
    public void Next_cq_matches_original() =>
        GoldenAssert.All(
            "hls_next_cq",
            input => CqCalibrator.NextCq(
                [.. input.GetProperty("history").EnumerateArray().Select(h => (h[0].GetDouble(), h[1].GetDouble()))],
                input.GetProperty("target").GetDouble(),
                14,
                40),
            relativeTolerance: 1e-9);

    /// <summary>
    /// Подбор проигрывается по записанным вызовам оригинала: те же команды по порядку, код возврата и размеры
    /// выходных окон — из эталона, итоговые CQ совпадают.
    /// </summary>
    [Fact]
    public async Task Calibration_replays_original_calls()
    {
        var failures = new List<string>();
        foreach (var c in GoldenFile.Load("hls_calibrate_cq").Cases)
        {
            var input = c.Input;
            var calls = c.Raw.GetProperty("calls").EnumerateArray().ToList();
            var settings = HlsSettings.Default with { Ladder = HlsCommandTests.Ladder(input.GetProperty("resolutions")) };
            var builder = HlsCommandTests.Builder(settings, input);
            var runner = new ReplayRunner(calls);
            var created = new List<string>();
            var deleted = new List<string>();
            var calibrator = new CqCalibrator(
                runner,
                HlsCommandTests.Text(input, "ffmpeg_path"),
                builder,
                pathStyle: input.GetProperty("platform").GetString() == "windows" ? Anitools.Core.Platform.PathStyle.Windows : Anitools.Core.Platform.PathStyle.Posix,
                fileSize: runner.SizeOf,
                createDirectory: created.Add,
                deleteDirectory: deleted.Add);
            var source = input.GetProperty("source");
            var result = await calibrator.CalibrateAsync(
                HlsCommandTests.Text(input, "input_path"),
                new SourceBitrate(Doubles(source.GetProperty("rates")), source.GetProperty("avg").GetDouble(), source.GetProperty("p99").GetDouble()),
                input.GetProperty("cpu_decode").GetBoolean(),
                HlsCommandTests.Text(input, "work_out"),
                cancellationToken: TestContext.Current.CancellationToken);

            var actual = JsonSerializer.SerializeToElement(result?.Select(r => new { cq = r.Cq, maxrate = r.MaxRate }));
            if (runner.Mismatch is { } mismatch)
            {
                failures.Add($"{c.Input.GetProperty("platform")} {calls.Count} вызовов: {mismatch}");
            }
            else if (runner.Count != calls.Count)
            {
                failures.Add($"вызовов {runner.Count}, а в эталоне {calls.Count}");
            }
            else if (!GoldenAssert.JsonEquals(c.Output!.Value, actual, 1e-9))
            {
                failures.Add($"итог\n  ждали: {c.Output}\n  вышло: {actual}");
            }

            Assert.Single(created);
            Assert.Equal(created, deleted); // папка окон удаляется и при ошибке ffmpeg
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void Python_float_sum_is_compensated()
    {
        // Простое накопление дало бы 0: 1e100 «съедает» единицы
        Assert.Equal(2.0, PyText.Sum([1e100, 1.0, -1e100, 1.0]));
        Assert.Equal(0.30000000000000004, PyText.Sum([0.1, 0.2]));
        Assert.Equal(2.0, PyText.FloorDiv(17.999999999999996, 6));
        Assert.Equal(-1.0, PyText.FloorDiv(-0.5, 6));
    }

    private static double[] Doubles(JsonElement list) => [.. list.EnumerateArray().Select(v => v.GetDouble())];

    /// <summary>ffmpeg из эталона: сверяет команду, возвращает записанный код, размеры окон — из записи.</summary>
    private sealed class ReplayRunner(IReadOnlyList<JsonElement> calls) : IProcessRunner
    {
        private Dictionary<string, long> _sizes = [];

        public int Count { get; private set; }

        public string? Mismatch { get; private set; }

        public long SizeOf(string path) => _sizes.TryGetValue(path, out var size) ? size : throw new FileNotFoundException(path);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            if (Count >= calls.Count)
            {
                Mismatch ??= $"лишний вызов {spec}";
                return Task.FromResult(new ProcessResult(1, "", "", TimeSpan.Zero));
            }

            var call = calls[Count++];
            var expected = call.GetProperty("cmd").EnumerateArray().Select(a => a.GetString()!).ToList();
            List<string> actual = [spec.FileName, .. spec.Arguments];
            if (!expected.SequenceEqual(actual))
            {
                Mismatch ??= $"вызов {Count}:\n  ждали: {string.Join(' ', expected)}\n  вышло: {string.Join(' ', actual)}";
            }

            _sizes = call.GetProperty("sizes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
            return Task.FromResult(new ProcessResult(call.GetProperty("returncode").GetInt32(), "", "", TimeSpan.Zero));
        }
    }
}
