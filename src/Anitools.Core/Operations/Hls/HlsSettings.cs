namespace Anitools.Core.Operations.Hls;

/// <summary>Качество лестницы HLS: папка в архиве, ширина, высота (для справки) и опорный битрейт, бит/с.</summary>
public sealed record HlsRung(string Name, int Width, int Height, long Bitrate);

/// <summary>Чем кодировать видео.</summary>
public enum EncoderProfile
{
    /// <summary>Видеокарта NVIDIA: NVDEC → scale_cuda → h264_nvenc, как в оригинале.</summary>
    Nvenc,

    /// <summary>Процессор: scale → libx264. Для тестов и компьютеров без NVIDIA.</summary>
    Software,
}

/// <summary>Постоянное качество (CQ) с потолком битрейта для одного разрешения.</summary>
public sealed record RateControl(double Cq, long MaxRate);

/// <summary>Параметры п.7; значения по умолчанию — константы оригинала (py:3697–3751).</summary>
public sealed record HlsSettings
{
    /// <summary>Лестница качеств (_HLS_RESOLUTIONS): всегда все шесть, верхнее — 4K.</summary>
    public static IReadOnlyList<HlsRung> DefaultLadder { get; } =
    [
        new("360p", 640, 360, 800_000),
        new("480p", 854, 480, 1_500_000),
        new("720p", 1280, 720, 3_000_000),
        new("1080p", 1920, 1080, 5_000_000),
        new("2K", 2560, 1440, 8_000_000),
        new("4K", 3840, 2160, 16_000_000),
    ];

    /// <summary>Объявлено после <see cref="DefaultLadder"/>: статические поля инициализируются по порядку.</summary>
    public static HlsSettings Default { get; } = new();

    public IReadOnlyList<HlsRung> Ladder { get; init; } = DefaultLadder;

    /// <summary>Длина сегмента, с; ключевой кадр ставится ровно на каждой границе.</summary>
    public int SegmentSeconds { get; init; } = 6;

    public EncoderProfile Encoder { get; init; } = EncoderProfile.Nvenc;

    /// <summary>p5–p7 по VMAF в пределах погрешности, но в 1,7 раза медленнее.</summary>
    public string NvencPreset { get; init; } = "p4";

    public string SoftwarePreset { get; init; } = "veryfast";

    /// <summary>Постоянный CQ на все разрешения; null — CQ подбирается под каждую серию (<see cref="CqCalibrator"/>).</summary>
    public double? FixedCq { get; init; } = 21.0;

    /// <summary>Потолок битрейта при постоянном CQ: во столько раз выше битрейта из лестницы.</summary>
    public double FixedCqPeak { get; init; } = 4;

    public int CalibrationWindows { get; init; } = 10;

    public double CalibrationTolerance { get; init; } = 0.03;

    public int CalibrationMaxPasses { get; init; } = 4;

    public double CqStart { get; init; } = 26.0;

    public double CqMin { get; init; } = 14.0;

    public double CqMax { get; init; } = 40.0;

    /// <summary>Верхнее качество тяжелее этого — отдельным архивом «серия.4K.zip» (7 ГиБ).</summary>
    public long SeparateTopZipBytes { get; init; } = 7L * 1024 * 1024 * 1024;

    /// <summary>Постоянное качество на каждое разрешение: CQ и потолок «пик × битрейт».</summary>
    public IReadOnlyList<RateControl> FixedRateControl(double cq) =>
        [.. Ladder.Select(r => new RateControl(cq, (long)(FixedCqPeak * r.Bitrate)))];
}
