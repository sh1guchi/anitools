using System.Globalization;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.AudioTools;

/// <summary>
/// Сдвиг аудио: секунды (плюс — звук позже, минус — обрезать начало), перекодировать ли в AAC (как в оригинале;
/// по умолчанию нет — без потерь), битрейт AAC, сколько файлов сразу.
/// </summary>
public sealed record AudioShiftOptions
{
    public double Seconds { get; init; } = 1.0;

    /// <summary>false — без перекодирования (mkvmerge --sync), true — в AAC, как delay+1s.py / delay-1s.py.</summary>
    public bool Reencode { get; init; }

    public string Bitrate { get; init; } = "256k";

    public int Workers { get; init; } = 6;
}

/// <summary>
/// Сдвиг аудио (delay+1s.py / delay-1s.py): файлы папки → audio_fixed\&lt;имя&gt;.mka.
/// По умолчанию без перекодирования: mkvmerge --sync сдвигает метки времени всех дорожек (плюс — звук начинается
/// позже, минус — начало отбрасывается с точностью до аудиокадра), кодек и качество — как в исходнике.
/// С перекодированием в AAC — как в оригинале: «+N» — оригинал склеивал тишину стерео 48 кГц фильтром concat
/// (5.1 и 44,1 кГц при этом приводились к ней), здесь — adelay по всем каналам; «−N» — -ss.
/// </summary>
public static class AudioShiftOperation
{
    public const string OutputFolderName = "audio_fixed";

    public static IReadOnlyList<string> Extensions { get; } = [".mka", ".m4a", ".aac", ".mp3", ".ac3", ".dts", ".flac", ".wav", ".ogg", ".opus"];

    public static OperationPlan Plan(string folder, AudioShiftOptions options)
    {
        if (options.Seconds == 0)
        {
            throw new PlanException("Сдвиг не может быть нулевым.");
        }

        var files = MediaFiles.List(folder, Extensions);
        if (files.Count == 0)
        {
            throw new PlanException("Аудиофайлы не найдены.");
        }

        var outputFolder = Path.Combine(folder, OutputFolderName);
        var seconds = TextUtils.FloatStr(Math.Abs(options.Seconds)); // как str() в оригинале: «1.0»
        var items = files.Select(file =>
        {
            var name = Path.GetFileName(file);
            var output = Path.Combine(outputFolder, TextUtils.Stem(name) + ".mka");
            if (MediaFiles.IsDone(output))
            {
                return new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже готово", Outputs = [output] };
            }

            var milliseconds = (long)Math.Round(options.Seconds * 1000);
            if (!options.Reencode)
            {
                // -1 — все дорожки файла; обложку (видео) не берём, как «-vn» в варианте с AAC
                return new PlanItem
                {
                    Source = file,
                    Label = name,
                    Status = PlanItemStatus.Run,
                    Outputs = [output],
                    Command = new PlannedCommand(Tool.Mkvmerge, ["-o", output, "--no-video", "--sync", FormattableString.Invariant($"-1:{milliseconds}"), file])
                    {
                        WarningExitCodes = [1],
                    },
                };
            }

            string[] shift = options.Seconds > 0
                ? ["-i", file, "-af", FormattableString.Invariant($"adelay=delays={milliseconds}:all=1")]
                : ["-i", file, "-ss", seconds];
            return new PlanItem
            {
                Source = file,
                Label = name,
                Status = PlanItemStatus.Run,
                Outputs = [output],
                Command = new PlannedCommand(Tool.Ffmpeg, ["-hide_banner", "-nostdin", .. shift, "-c:a", "aac", "-b:a", options.Bitrate, "-vn", "-y", output]),
            };
        }).ToList();
        var title = (options.Seconds > 0 ? $"Сдвиг аудио +{seconds} с" : $"Сдвиг аудио −{seconds} с") + (options.Reencode ? " (AAC)" : "");
        return new OperationPlan(title, folder, items);
    }
}

/// <summary>Во что перекодировать.</summary>
public enum AudioFormat
{
    /// <summary>AAC LC в Matroska (по умолчанию).</summary>
    Mka,

    /// <summary>AAC LC в MP4 (.m4a).</summary>
    M4a,
    Mp3,
    Opus,

    /// <summary>Vorbis в Ogg.</summary>
    Ogg,

    /// <summary>Без потерь.</summary>
    Flac,

    /// <summary>PCM 16 бит, без потерь.</summary>
    Wav,

    /// <summary>AAC LC в QuickTime (.mov) — только аудио, без обложки (для монтажных программ).</summary>
    Mov,
}

public sealed record AudioConvertOptions
{
    public AudioFormat Format { get; init; } = AudioFormat.Mka;

    public string Bitrate { get; init; } = "256k";

    /// <summary>Каналов на выходе (5.1/7.1 сводятся); null — как в исходнике.</summary>
    public int? Channels { get; init; } = 2;

    /// <summary>Чинить битые таймстампы (aresample=async=1): наложения обрезаются, дыры заполняются тишиной.</summary>
    public bool FixTimestamps { get; init; } = true;

    public int Workers { get; init; } = 8;
}

/// <summary>Перекодирование аудио (audio_decod.py): файлы папки → converted\&lt;имя&gt;&lt;расширение формата&gt;.</summary>
public static class AudioConvertOperation
{
    public const string OutputFolderName = "converted";

    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".ogg", ".oga", ".opus", ".ape", ".alac", ".aiff", ".aif", ".aifc",
        ".ac3", ".dts", ".amr", ".mka", ".wv", ".tta", ".mpc", ".spx", ".caf", ".dsf", ".dff",
    ];

    /// <summary>Расширение и параметры кодека (CODEC_MAP оригинала).</summary>
    public static (string Extension, IReadOnlyList<string> CodecArgs) Codec(AudioFormat format, string bitrate) => format switch
    {
        AudioFormat.Mka => (".mka", ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", bitrate]),
        AudioFormat.M4a => (".m4a", ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", bitrate]),
        AudioFormat.Mp3 => (".mp3", ["-c:a", "libmp3lame", "-b:a", bitrate]),
        AudioFormat.Opus => (".opus", ["-c:a", "libopus", "-b:a", bitrate]),
        AudioFormat.Ogg => (".ogg", ["-c:a", "libvorbis", "-b:a", bitrate]),
        AudioFormat.Flac => (".flac", ["-c:a", "flac"]),
        AudioFormat.Wav => (".wav", ["-c:a", "pcm_s16le"]),
        AudioFormat.Mov => (".mov", ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", bitrate]),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static OperationPlan Plan(string folder, AudioConvertOptions options)
    {
        var files = MediaFiles.List(folder, Extensions);
        if (files.Count == 0)
        {
            throw new PlanException("Аудиофайлы не найдены.");
        }

        var (ext, codec) = Codec(options.Format, options.Bitrate);
        var outputFolder = Path.Combine(folder, OutputFolderName);
        var items = files.Select(file =>
        {
            var name = Path.GetFileName(file);
            var output = Path.Combine(outputFolder, TextUtils.Stem(name) + ext);
            if (MediaFiles.IsDone(output))
            {
                return new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже готово", Outputs = [output] };
            }

            List<string> args = ["-hide_banner", "-nostdin", "-i", file, "-map", "0:a", .. codec];
            if (options.FixTimestamps)
            {
                args.AddRange(["-af", "aresample=async=1"]);
            }

            if (options.Channels is { } channels)
            {
                args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
            }

            args.AddRange(["-map_metadata", "0"]);
            if (ext is not (".mka" or ".mov"))
            {
                // Обложку как attached_pic понимают mp4/m4a/mp3 и т.п., но не Matroska; в .mov — только аудио
                args.AddRange(["-map", "0:v?", "-c:v", "copy", "-disposition:v", "attached_pic"]);
            }

            args.AddRange(["-y", output]);
            return new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Run, Outputs = [output], Command = new PlannedCommand(Tool.Ffmpeg, args) };
        }).ToList();
        return new OperationPlan($"Перекодирование аудио в {ext.TrimStart('.')}", folder, items);
    }
}
