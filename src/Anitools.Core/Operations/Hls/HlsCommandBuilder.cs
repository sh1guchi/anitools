using System.Globalization;
using Anitools.Core.Platform;

namespace Anitools.Core.Operations.Hls;

/// <summary>Озвучка серии: какая аудиодорожка (0:a:N) и в какую папку audio/&lt;папка&gt;/ она идёт.</summary>
public sealed record HlsVoice(int TrackIndex, string Folder, string Language);

/// <summary>
/// Команды ffmpeg для HLS — аргументы без самой программы. Профиль Nvenc кодирует на видеокарте;
/// Software — то же на процессоре: scale + libx264 вместо scale_cuda + h264_nvenc.
/// </summary>
public sealed class HlsCommandBuilder(HlsSettings settings, PathStyle? pathStyle = null)
{
    private readonly PathStyle _paths = pathStyle ?? PathStyle.Current;

    public HlsSettings Settings => settings;

    /// <summary>
    /// Начало команды видео: декодирование и split → масштаб на каждую ширину.
    /// Выходы фильтра — [v0out], [v1out], … по порядку ширин.
    /// </summary>
    /// <param name="cpuDecode">Декодировать на процессоре и загружать кадры на видеокарту (NVDEC не потянет исходник).</param>
    /// <param name="seek">Взять только отрезок (начало, длина) — для калибровки.</param>
    public IReadOnlyList<string> VideoInputArgs(string input, IReadOnlyList<int> widths, bool cpuDecode, (double Start, double Length)? seek = null)
    {
        var splitLabels = string.Concat(widths.Select((_, i) => Inv($"[v{i}]")));
        string[] seekArgs = seek is { } s ? ["-ss", s.Start.ToString("F3", CultureInfo.InvariantCulture), "-t", s.Length.ToString("F3", CultureInfo.InvariantCulture)] : [];
        if (settings.Encoder == EncoderProfile.Software)
        {
            var softwareScales = string.Join(';', widths.Select((w, i) => Inv($"[v{i}]scale={w}:-2,format=yuv420p[v{i}out]")));
            return ["-nostdin", "-y", .. seekArgs, "-i", input, "-filter_complex", Inv($"[0:v]split={widths.Count}{splitLabels};{softwareScales}")];
        }

        // Высота -2: по пропорциям, чётная. format=nv12 переводит 10 бит в 8 прямо на видеокарте (нужно h264_nvenc)
        var scales = string.Join(';', widths.Select((w, i) => Inv($"[v{i}]scale_cuda={w}:-2:format=nv12[v{i}out]")));
        if (cpuDecode)
        {
            return
            [
                "-nostdin", "-y", "-init_hw_device", "cuda=cu", "-filter_hw_device", "cu", .. seekArgs, "-i", input,
                "-filter_complex", Inv($"[0:v]format=nv12,hwupload_cuda,split={widths.Count}{splitLabels};{scales}"),
            ];
        }

        return
        [
            "-nostdin", "-y", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", .. seekArgs, "-i", input,
            "-filter_complex", Inv($"[0:v]split={widths.Count}{splitLabels};{scales}"),
        ];
    }

    /// <summary>
    /// Кодирование одного качества: с <paramref name="rateControl"/> — постоянное качество
    /// с потолком битрейта, без него — средний битрейт с потолком 2×.
    /// </summary>
    public IReadOnlyList<string> VideoEncodeArgs(long bitrate, RateControl? rateControl)
    {
        // Ключевой кадр ровно каждые SegmentSeconds при любом fps: иначе сегменты режутся по ~10 с,
        // а одно выражение на все качества выравнивает их границы (важно для переключения качеств)
        var keyFrames = Inv($"expr:gte(t,n_forced*{settings.SegmentSeconds})");
        List<string> args = settings.Encoder == EncoderProfile.Software
            ? ["-c:v", "libx264", "-preset", settings.SoftwarePreset, "-force_key_frames", keyFrames]
            :
            [
                "-c:v", "h264_nvenc", "-preset", settings.NvencPreset, "-tune", "hq", "-rc", "vbr",
                "-spatial-aq", "1", "-rc-lookahead", "32", "-forced-idr", "1", "-force_key_frames", keyFrames,
            ];
        if (rateControl is { } rc)
        {
            var cq = rc.Cq.ToString("F2", CultureInfo.InvariantCulture);
            args.AddRange(settings.Encoder == EncoderProfile.Software
                ? ["-crf", cq, "-maxrate", Inv(rc.MaxRate), "-bufsize", Inv(2 * rc.MaxRate)]
                : ["-cq", cq, "-b:v", "0", "-maxrate", Inv(rc.MaxRate), "-bufsize", Inv(2 * rc.MaxRate)]);
        }
        else
        {
            args.AddRange(["-b:v", Inv(bitrate), "-maxrate", Inv(2 * bitrate), "-bufsize", Inv(4 * bitrate)]);
        }

        return args;
    }

    /// <summary>Папка качества внутри рабочей папки серии.</summary>
    public string RungDirectory(string workOut, HlsRung rung) => _paths.Join(workOut, rung.Name);

    /// <summary>
    /// Видео на все качества лестницы одной командой:
    /// workOut/&lt;качество&gt;/seg%03d.ts + master.m3u8. Папки качеств создаёт тот, кто запускает.
    /// </summary>
    /// <param name="rateControl">По элементу на качество; null — средний битрейт из лестницы.</param>
    public IReadOnlyList<string> Video(string input, string workOut, bool cpuDecode, IReadOnlyList<RateControl>? rateControl)
    {
        var ladder = settings.Ladder;
        var args = new List<string>(VideoInputArgs(input, [.. ladder.Select(r => r.Width)], cpuDecode));
        for (var i = 0; i < ladder.Count; i++)
        {
            var outDir = RungDirectory(workOut, ladder[i]);
            args.AddRange(["-map", Inv($"[v{i}out]")]);
            args.AddRange(VideoEncodeArgs(ladder[i].Bitrate, rateControl?[i]));
            args.AddRange(
            [
                "-f", "hls",
                "-hls_time", Inv(settings.SegmentSeconds),
                "-hls_playlist_type", "vod",
                "-hls_flags", "independent_segments",
                "-hls_segment_filename", _paths.AsPosix(outDir) + "/seg%03d.ts",
                _paths.Join(outDir, "master.m3u8"),
            ]);
        }

        return args;
    }

    /// <summary>
    /// Калибровочное окно: только качества <paramref name="rungs"/> (индексы в лестнице),
    /// отрезок одного сегмента, выход — calDir/&lt;качество&gt;.ts, как настоящие сегменты.
    /// </summary>
    public IReadOnlyList<string> CalibrationWindow(
        string input, IReadOnlyList<int> rungs, IReadOnlyList<RateControl> rateControl, double start, bool cpuDecode, string calibrationDir)
    {
        var ladder = settings.Ladder;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        args.AddRange(VideoInputArgs(input, [.. rungs.Select(i => ladder[i].Width)], cpuDecode, (start, settings.SegmentSeconds)));
        for (var k = 0; k < rungs.Count; k++)
        {
            var rung = ladder[rungs[k]];
            args.AddRange(["-map", Inv($"[v{k}out]")]);
            args.AddRange(VideoEncodeArgs(rung.Bitrate, rateControl[rungs[k]]));
            args.AddRange(["-f", "mpegts", CalibrationOutput(calibrationDir, rung)]);
        }

        return args;
    }

    public string CalibrationOutput(string calibrationDir, HlsRung rung) => _paths.Join(calibrationDir, rung.Name + ".ts");

    /// <summary>
    /// Все озвучки одной командой: workOut/audio/&lt;папка&gt;/&lt;серия&gt;.&lt;папка&gt;.mka.
    /// Дорожки копируются как есть, только 5.1 и 7.1 (6 и 8 каналов) сводятся в стерео AAC 192k.
    /// </summary>
    /// <param name="channels">Каналы аудиодорожек по порядку; неизвестно — дорожка копируется.</param>
    public IReadOnlyList<string> Audio(string input, string workOut, IReadOnlyList<HlsVoice> voices, IReadOnlyList<int> channels, string episodeName)
    {
        var args = new List<string> { "-nostdin", "-y", "-i", input, "-vn" };
        foreach (var voice in voices)
        {
            var output = _paths.AsPosix(AudioOutput(workOut, voice, episodeName));
            var downmix = voice.TrackIndex < channels.Count && channels[voice.TrackIndex] is 6 or 8;
            args.AddRange(["-map", Inv($"0:a:{voice.TrackIndex}")]);
            args.AddRange(downmix ? ["-c:a", "aac", "-b:a", "192k", "-ac", "2"] : ["-c:a", "copy"]);
            args.Add(output);
        }

        return args;
    }

    public string AudioOutput(string workOut, HlsVoice voice, string episodeName) =>
        _paths.Join(workOut, "audio", voice.Folder, $"{episodeName}.{voice.Folder}.mka");

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
