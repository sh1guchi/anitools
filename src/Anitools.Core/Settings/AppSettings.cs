using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Fonts;
using Anitools.Core.Operations.Hardsub;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Processes;
using Anitools.Core.Shikimori;
using Anitools.Core.WorkDir;

namespace Anitools.Core.Settings;

/// <summary>Пути к программам из настроек: файл или папка с программой; пусто — искать самим (docs/PLAN.md §3.5).</summary>
public sealed record ToolPathSettings
{
    public string Ffmpeg { get; init; } = "";

    public string Ffprobe { get; init; } = "";

    public string Mkvmerge { get; init; } = "";

    public string Mkvextract { get; init; } = "";

    public string Imdisk { get; init; } = "";

    public string Get(Tool tool) => tool switch
    {
        Tool.Ffmpeg => Ffmpeg,
        Tool.Ffprobe => Ffprobe,
        Tool.Mkvmerge => Mkvmerge,
        Tool.Mkvextract => Mkvextract,
        Tool.Imdisk => Imdisk,
        _ => "",
    };

    public ToolPathSettings With(Tool tool, string path) => tool switch
    {
        Tool.Ffmpeg => this with { Ffmpeg = path },
        Tool.Ffprobe => this with { Ffprobe = path },
        Tool.Mkvmerge => this with { Mkvmerge = path },
        Tool.Mkvextract => this with { Mkvextract = path },
        Tool.Imdisk => this with { Imdisk = path },
        _ => this,
    };
}

/// <summary>Шрифты для .ass: своя папка (туда же сохраняется скачанное).</summary>
public sealed record FontSettings
{
    public string CustomDir { get; init; } = FontsCollectOptions.DefaultCustomDir;
}

/// <summary>
/// Настройки приложения (settings.json, docs/PLAN.md §3.6). Значения по умолчанию — константы оригинала;
/// чего нет в файле — берётся по умолчанию, неверное исправляет <see cref="Normalized"/>.
/// </summary>
public sealed record AppSettings
{
    public const int MaxRecentFolders = 10;

    public ToolPathSettings Tools { get; init; } = new();

    /// <summary>Войс-лист: названия озвучек для выбора в п.2, п.3, п.7 и сборке .mka.</summary>
    public IReadOnlyList<string> Voices { get; init; } = VoiceList.Default;

    public HlsSettings Hls { get; init; } = HlsSettings.Default;

    /// <summary>Куда HLS пишет временные файлы по умолчанию.</summary>
    public WorkDirSettings WorkDir { get; init; } = new();

    public string ShikimoriBaseUrl { get; init; } = ShikimoriClient.DefaultBaseUri.ToString();

    public FontSettings Fonts { get; init; } = new();

    public HardsubOptions Hardsub { get; init; } = new();

    public AudioShiftOptions AudioShift { get; init; } = new();

    public AudioConvertOptions AudioConvert { get; init; } = new();

    /// <summary>Сдвиг субтитров по умолчанию, с.</summary>
    public double SubShiftSeconds { get; init; } = 1.0;

    /// <summary>Недавние рабочие папки, последняя — первой.</summary>
    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    /// <summary>Команда ani закрывает консоль, из которой её набрали (окно anitools открывается само по себе).</summary>
    public bool CloseConsoleAfterAni { get; init; } = true;

    /// <summary>Раз в день проверять, не вышла ли новая версия (GitHub Releases).</summary>
    public bool CheckUpdates { get; init; } = true;

    /// <summary>Когда последний раз проверяли обновления; null — ни разу.</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>Папка — первой в недавних (без повторов, не больше <see cref="MaxRecentFolders"/>).</summary>
    public AppSettings WithRecentFolder(string folder)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return this with
        {
            RecentFolders = [.. new[] { folder }.Concat(RecentFolders.Where(f => !comparer.Equals(f, folder))).Take(MaxRecentFolders)],
        };
    }

    /// <summary>
    /// Пустые и неверные значения (файл правили руками, старая версия) заменяются значениями по умолчанию,
    /// чтобы дальше по коду не было null и нулей там, где их быть не может.
    /// </summary>
    public AppSettings Normalized()
    {
        var defaults = new AppSettings();
        var tools = Tools ?? new ToolPathSettings();
        var hls = NormalizeHls(Hls);
        var workDir = WorkDir ?? new WorkDirSettings();
        var hardsub = Hardsub ?? new HardsubOptions();
        var shift = AudioShift ?? new AudioShiftOptions();
        var convert = AudioConvert ?? new AudioConvertOptions();
        var fonts = Fonts ?? new FontSettings();
        return this with
        {
            Tools = new ToolPathSettings
            {
                Ffmpeg = Trim(tools.Ffmpeg),
                Ffprobe = Trim(tools.Ffprobe),
                Mkvmerge = Trim(tools.Mkvmerge),
                Mkvextract = Trim(tools.Mkvextract),
                Imdisk = Trim(tools.Imdisk),
            },
            Voices = Voices is null
                ? defaults.Voices
                : [.. Voices.Select(Trim).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)],
            Hls = hls,
            WorkDir = workDir with
            {
                Folder = Trim(workDir.Folder) is { Length: > 0 } folder ? folder : defaults.WorkDir.Folder,
                RamDiskGb = workDir.RamDiskGb >= 1 ? workDir.RamDiskGb : defaults.WorkDir.RamDiskGb,
                Mode = Enum.IsDefined(workDir.Mode) ? workDir.Mode : defaults.WorkDir.Mode,
            },
            ShikimoriBaseUrl = Uri.TryCreate(Trim(ShikimoriBaseUrl), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                ? uri.ToString()
                : defaults.ShikimoriBaseUrl,
            Fonts = fonts with { CustomDir = Trim(fonts.CustomDir) is { Length: > 0 } dir ? dir : defaults.Fonts.CustomDir },
            Hardsub = hardsub with
            {
                EncodeArgs = hardsub.EncodeArgs is { Count: > 0 } args && args.All(a => !string.IsNullOrEmpty(a)) ? args : HardsubOptions.DefaultEncodeArgs,
                FontsDirName = Trim(hardsub.FontsDirName) is { Length: > 0 } fontsDir ? fontsDir : defaults.Hardsub.FontsDirName,
            },
            AudioShift = shift with
            {
                Seconds = double.IsFinite(shift.Seconds) ? shift.Seconds : defaults.AudioShift.Seconds,
                Bitrate = Trim(shift.Bitrate) is { Length: > 0 } shiftRate ? shiftRate : defaults.AudioShift.Bitrate,
                Workers = shift.Workers >= 1 ? shift.Workers : defaults.AudioShift.Workers,
            },
            AudioConvert = convert with
            {
                Format = Enum.IsDefined(convert.Format) ? convert.Format : defaults.AudioConvert.Format,
                Bitrate = Trim(convert.Bitrate) is { Length: > 0 } convertRate ? convertRate : defaults.AudioConvert.Bitrate,
                Channels = convert.Channels is null or >= 1 ? convert.Channels : defaults.AudioConvert.Channels,
                Workers = convert.Workers >= 1 ? convert.Workers : defaults.AudioConvert.Workers,
            },
            SubShiftSeconds = double.IsFinite(SubShiftSeconds) ? SubShiftSeconds : defaults.SubShiftSeconds,
            RecentFolders = RecentFolders is null
                ? []
                : [.. RecentFolders.Select(Trim).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxRecentFolders)],
        };
    }

    private static HlsSettings NormalizeHls(HlsSettings? hls)
    {
        var d = HlsSettings.Default;
        if (hls is null)
        {
            return d;
        }

        var ladder = hls.Ladder is { Count: > 0 } rungs
            && rungs.All(r => r is not null && !string.IsNullOrWhiteSpace(r.Name) && r.Width > 0 && r.Height > 0 && r.Bitrate > 0)
            ? rungs
            : d.Ladder;
        var cqMin = Finite(hls.CqMin, d.CqMin);
        var cqMax = Finite(hls.CqMax, d.CqMax);
        if (cqMin <= 0 || cqMin >= cqMax)
        {
            (cqMin, cqMax) = (d.CqMin, d.CqMax);
        }

        return hls with
        {
            Ladder = ladder,
            SegmentSeconds = hls.SegmentSeconds >= 1 ? hls.SegmentSeconds : d.SegmentSeconds,
            Encoder = Enum.IsDefined(hls.Encoder) ? hls.Encoder : d.Encoder,
            NvencPreset = Trim(hls.NvencPreset) is { Length: > 0 } nvenc ? nvenc : d.NvencPreset,
            SoftwarePreset = Trim(hls.SoftwarePreset) is { Length: > 0 } software ? software : d.SoftwarePreset,
            FixedCq = hls.FixedCq is { } cq && (!double.IsFinite(cq) || cq <= 0) ? d.FixedCq : hls.FixedCq,
            FixedCqPeak = Finite(hls.FixedCqPeak, d.FixedCqPeak) is > 0 and var peak ? peak : d.FixedCqPeak,
            CalibrationWindows = hls.CalibrationWindows >= 1 ? hls.CalibrationWindows : d.CalibrationWindows,
            CalibrationTolerance = Finite(hls.CalibrationTolerance, d.CalibrationTolerance) is > 0 and var tolerance ? tolerance : d.CalibrationTolerance,
            CalibrationMaxPasses = hls.CalibrationMaxPasses >= 1 ? hls.CalibrationMaxPasses : d.CalibrationMaxPasses,
            CqMin = cqMin,
            CqMax = cqMax,
            CqStart = Math.Clamp(Finite(hls.CqStart, d.CqStart), cqMin, cqMax),
            SeparateTopZipBytes = hls.SeparateTopZipBytes > 0 ? hls.SeparateTopZipBytes : d.SeparateTopZipBytes,
        };
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static string Trim(string? s) => (s ?? "").Trim();
}
