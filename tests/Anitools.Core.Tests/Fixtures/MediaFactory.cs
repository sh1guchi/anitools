using Anitools.Core.Processes;

namespace Anitools.Core.Tests.Fixtures;

/// <summary>Крошечные тестовые медиафайлы через ffmpeg (docs/PLAN.md §5.3): 160×90, 1–3 с.</summary>
internal static class MediaFactory
{
    public const string Ass = """
        [Script Info]
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,Надпись

        """;

    public const string Srt = "1\n00:00:00,500 --> 00:00:01,500\nHello\n";

    public static readonly string[] Video = ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=2"];

    public static string[] Sine(int freq) => ["-f", "lavfi", "-i", $"sine=f={freq}:d=2"];

    /// <summary>Запускает ffmpeg с аргументами + выходной файл; падает тест, если ffmpeg вернул ошибку.</summary>
    public static async Task<string> CreateAsync(string output, params string[] args)
    {
        var tools = TestTools.RequireFfmpeg();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec(tools.Ffmpeg!, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. args, output]),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, $"ffmpeg не создал {output}: {result.StandardErrorTail}");
        return output;
    }

    /// <summary>Как «Test Show - 01.mkv» из фикстур: 3 аудио (aac/flac/ac3 5.1) + надписи и полные субтитры.</summary>
    public static async Task<string> EpisodeAsync(string output)
    {
        var dir = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(dir);
        var ass = Path.Combine(dir, "_signs.ass");
        var srt = Path.Combine(dir, "_full.srt");
        await File.WriteAllTextAsync(ass, Ass, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(srt, Srt, TestContext.Current.CancellationToken);
        try
        {
            return await CreateAsync(output, [
                .. Video, .. Sine(440), .. Sine(660), "-f", "lavfi", "-t", "2", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000",
                "-i", ass, "-i", srt,
                "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-map", "4", "-map", "5",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a:0", "aac", "-c:a:1", "flac", "-c:a:2", "ac3", "-c:s:0", "ass", "-c:s:1", "srt",
                "-metadata:s:a:0", "title=AniLibria.TV", "-metadata:s:a:0", "language=rus",
                "-metadata:s:a:1", "title=Оригинальная", "-metadata:s:a:1", "language=jpn",
                "-metadata:s:a:2", "title=DEEP", "-metadata:s:a:2", "language=rus",
                "-metadata:s:s:0", "title=Надписи", "-metadata:s:s:0", "language=rus",
                "-metadata:s:s:1", "title=Full", "-metadata:s:s:1", "language=eng",
                "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0"]);
        }
        finally
        {
            File.Delete(ass);
            File.Delete(srt);
        }
    }
}
