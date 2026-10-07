using System.Text.RegularExpressions;

namespace Anitools.Core.Processes;

/// <summary>
/// Что нашлось для строки состояния (docs/PLAN.md §3.5): пути программ, версии ffmpeg и mkvmerge, ImDisk
/// и NVENC (в ffmpeg есть h264_nvenc и scale_cuda — без них HLS на видеокарте не запустится).
/// </summary>
/// <param name="Imdisk">Путь к imdisk (только Windows) или null.</param>
/// <param name="Nvenc">null — неизвестно (нет ffmpeg).</param>
public sealed record ToolsStatus(ToolPaths Paths, string? Imdisk, string? FfmpegVersion, string? MkvmergeVersion, bool? Nvenc)
{
    public static ToolsStatus Unknown { get; } = new(new ToolPaths(null, null, null, null), null, null, null, null);
}

public static partial class ToolStatusChecker
{
    public static async Task<ToolsStatus> CheckAsync(IProcessRunner runner, ToolPaths paths, string? imdisk, CancellationToken cancellationToken = default)
    {
        string? ffmpegVersion = null;
        bool? nvenc = null;
        if (paths.Ffmpeg is { } ffmpeg)
        {
            ffmpegVersion = ParseFfmpegVersion(await OutputAsync(runner, ffmpeg, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false));
            var encoders = await OutputAsync(runner, ffmpeg, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
            var filters = await OutputAsync(runner, ffmpeg, ["-hide_banner", "-filters"], cancellationToken).ConfigureAwait(false);
            nvenc = encoders is not null && filters is not null ? HasNvenc(encoders, filters) : null;
        }

        var mkvmergeVersion = paths.Mkvmerge is { } mkvmerge
            ? ParseMkvmergeVersion(await OutputAsync(runner, mkvmerge, ["--version"], cancellationToken).ConfigureAwait(false))
            : null;
        return new ToolsStatus(paths, imdisk, ffmpegVersion, mkvmergeVersion, nvenc);
    }

    /// <summary>«ffmpeg version 7.1.1-full_build-www.gyan.dev …» → «7.1.1»; сборка из git «N-118000-g…» → «N-118000».</summary>
    public static string? ParseFfmpegVersion(string? output)
    {
        var m = FfmpegVersionRegex().Match(output ?? "");
        if (!m.Success)
        {
            return null;
        }

        var token = m.Groups[1].Value;
        var numeric = NumericPrefixRegex().Match(token);
        return numeric.Success ? numeric.Value : GitBuildRegex().Match(token) is { Success: true } git ? git.Value : token[..Math.Min(token.Length, 24)];
    }

    /// <summary>«mkvmerge v82.0 ('I'm The President') 64-bit» → «82.0».</summary>
    public static string? ParseMkvmergeVersion(string? output) =>
        MkvmergeVersionRegex().Match(output ?? "") is { Success: true } m ? m.Groups[1].Value : null;

    public static bool HasNvenc(string encoders, string filters) =>
        NvencRegex().IsMatch(encoders) && ScaleCudaRegex().IsMatch(filters);

    private static async Task<string?> OutputAsync(IProcessRunner runner, string program, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessSpec(program, args), cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.StandardOutput : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex FfmpegVersionRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)*")]
    private static partial Regex NumericPrefixRegex();

    [GeneratedRegex(@"^N-\d+")]
    private static partial Regex GitBuildRegex();

    [GeneratedRegex(@"mkvmerge v(\d+(?:\.\d+)*)")]
    private static partial Regex MkvmergeVersionRegex();

    [GeneratedRegex(@"\bh264_nvenc\b")]
    private static partial Regex NvencRegex();

    [GeneratedRegex(@"\bscale_cuda\b")]
    private static partial Regex ScaleCudaRegex();
}
