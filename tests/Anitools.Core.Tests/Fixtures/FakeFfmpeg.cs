using Anitools.Core.Media;
using Anitools.Core.Processes;

namespace Anitools.Core.Tests.Fixtures;

/// <summary>
/// ffmpeg/ffprobe для тестов HLS без видеокарты: видео-команда создаёт сегменты и master.m3u8 в папках качеств,
/// аудио — .mka, калибровочное окно — .ts заданного размера. Код возврата — по номеру вызова ffmpeg.
/// </summary>
internal sealed class FakeFfmpeg : IProcessRunner
{
    /// <summary>Все вызовы по порядку.</summary>
    public List<ProcessSpec> Calls { get; } = [];

    /// <summary>Код возврата по номеру вызова ffmpeg (с нуля); нет в словаре — 0.</summary>
    public Dictionary<int, int> ExitCodes { get; } = [];

    /// <summary>Сколько байт в каждом сегменте качества (по имени папки); по умолчанию 100.</summary>
    public Dictionary<string, long> SegmentBytes { get; } = [];

    /// <summary>Размер калибровочного окна по имени качества и CQ (по умолчанию — 1000).</summary>
    public Func<string, double, long> WindowBytes { get; set; } = (_, _) => 1000;

    /// <summary>Строки ffprobe для анализа битрейта (packet=pts_time,size).</summary>
    public IReadOnlyList<string> PacketLines { get; set; } = [];

    /// <summary>Перед выполнением вызова ffmpeg (номер, аргументы) — например, отменить токен.</summary>
    public Action<int, IReadOnlyList<string>>? BeforeFfmpeg { get; set; }

    public int FfmpegCalls { get; private set; }

    public IEnumerable<IReadOnlyList<string>> FfmpegArguments =>
        Calls.Where(c => Path.GetFileNameWithoutExtension(c.FileName) == "ffmpeg").Select(c => c.Arguments);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        Calls.Add(spec);
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetFileNameWithoutExtension(spec.FileName) == "ffprobe")
        {
            foreach (var line in PacketLines)
            {
                spec.OnStdoutLine?.Invoke(line);
            }

            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }

        var number = FfmpegCalls++;
        BeforeFfmpeg?.Invoke(number, spec.Arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var code = ExitCodes.GetValueOrDefault(number);
        var args = spec.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-hls_segment_filename" && i + 2 < args.Count)
            {
                var dir = Path.GetDirectoryName(args[i + 1])!;
                var bytes = SegmentBytes.GetValueOrDefault(Path.GetFileName(dir), 100);
                // Недописанный выход при ошибке — как настоящий ffmpeg, упавший на середине
                File.WriteAllBytes(Path.Combine(dir, "seg000.ts"), new byte[bytes]);
                if (code == 0)
                {
                    File.WriteAllBytes(Path.Combine(dir, "seg001.ts"), new byte[bytes]);
                    File.WriteAllText(args[i + 2], "#EXTM3U\n");
                }
            }
            else if (args[i] == "-f" && i + 2 < args.Count && args[i + 1] == "mpegts" && code == 0)
            {
                var cq = CqBefore(args, i);
                using var file = File.Create(args[i + 2]);
                file.SetLength(WindowBytes(Path.GetFileNameWithoutExtension(args[i + 2]), cq));
            }
            else if (args[i].EndsWith(".mka", StringComparison.Ordinal) && code == 0)
            {
                File.WriteAllText(args[i], "mka");
            }
        }

        if (code == 0)
        {
            spec.OnStdoutLine?.Invoke("out_time_us=1000000");
            spec.OnStdoutLine?.Invoke("speed=2.5x");
            spec.OnStdoutLine?.Invoke("progress=end");
        }

        return Task.FromResult(new ProcessResult(code, "", code == 0 ? "" : "Error while decoding stream #0:0", TimeSpan.Zero));
    }

    private static double CqBefore(IReadOnlyList<string> args, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (args[i] is "-cq" or "-crf")
            {
                return double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }
}

/// <summary>ffprobe из фикстур: файл → имя фикстуры (Fixtures/media) или готовый MediaInfo.</summary>
internal sealed class NameFixtureProbe(IReadOnlyDictionary<string, string> media, IReadOnlyDictionary<string, MediaInfo>? custom = null) : IMediaProbe
{
    public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(path);
        if (custom is not null && custom.TryGetValue(name, out var info))
        {
            return Task.FromResult(info);
        }

        return media.TryGetValue(name, out var fixture)
            ? Task.FromResult(MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe(fixture)))
            : throw new MediaProbeException(path, "Invalid data found when processing input");
    }

    public Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
