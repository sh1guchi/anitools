using Anitools.Core.Media;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Integration;

/// <summary>Настоящие ffmpeg / ffprobe / mkvmerge на сгенерированных файлах. Без программ — пропуск.</summary>
[Trait("Category", "Integration")]
public sealed class MediaToolsIntegrationTests
{
    [Fact]
    public async Task Ffmpeg_with_cyrillic_paths_reports_progress()
    {
        var tools = TestTools.RequireFfmpeg();
        using var dir = new TempDir();
        var input = await MediaFactory.CreateAsync(dir.Combine("Провожающая — 01 [тест].mkv"),
            [.. MediaFactory.Video, "-c:v", "libx264", "-preset", "ultrafast"]);
        var output = dir.Combine("Выход", "Серия 01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var events = new List<FfmpegProgress>();
        var parser = new FfmpegProgressParser(events.Add);
        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec(tools.Ffmpeg!, ["-nostdin", "-y", "-i", input, "-c", "copy", "-progress", "pipe:1", "-nostats", output])
            {
                OnStdoutLine = parser.Feed,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.StandardErrorTail);
        Assert.True(File.Exists(output));
        Assert.True(events[^1].IsEnd);
        Assert.InRange(events[^1].OutTime.TotalSeconds, 1.5, 2.5);
    }

    [Fact]
    public async Task Probe_and_identify_an_episode()
    {
        var tools = TestTools.RequireMkvToolNix();
        using var dir = new TempDir();
        var episode = await MediaFactory.EpisodeAsync(dir.Combine("Test Show - 01.mkv"));
        var probe = new MediaProbe(new ProcessRunner(), tools);

        var info = await probe.ProbeAsync(episode, TestContext.Current.CancellationToken);
        Assert.Equal(["AniLibria.TV", "Оригинальная", "DEEP"], info.AudioStreams.Select(a => a.Title));
        Assert.Equal([1, 1, 6], info.AudioStreams.Select(a => a.Channels ?? 0)); // синусы — моно, третья — 5.1
        Assert.InRange(info.Duration ?? 0, 1.5, 2.5);

        var mkv = await probe.IdentifyAsync(episode, TestContext.Current.CancellationToken);
        Assert.True(mkv.IsMatroska);
        var subs = mkv.SubtitleTracks();
        Assert.Equal(["Надписи", "Full"], subs.Select(s => s.Name));
        Assert.Equal(["S_TEXT/ASS", "S_TEXT/UTF8"], subs.Select(s => s.CodecId));
        Assert.Equal([4, 5], subs.Select(s => s.Id));
    }

    [Fact]
    public async Task Probe_of_missing_file_is_a_clear_error()
    {
        var tools = TestTools.RequireFfmpeg();
        using var dir = new TempDir();
        var probe = new MediaProbe(new ProcessRunner(), tools);
        var ex = await Assert.ThrowsAsync<MediaProbeException>(() => probe.ProbeAsync(dir.Combine("нет такого.mkv"), TestContext.Current.CancellationToken));
        Assert.Contains("нет такого.mkv", ex.Message);
    }

    [Fact]
    public void Missing_tool_is_a_clear_error() =>
        Assert.Equal(
            "Не найдена программа ffprobe — поставьте её в «Настройках» («Установить») или укажите путь",
            Assert.Throws<ToolNotFoundException>(() =>
                new MediaProbe(new ProcessRunner(), new ToolPaths(null, null, null, null))
                    .ProbeAsync("x", TestContext.Current.CancellationToken).GetAwaiter().GetResult()).Message);
}
