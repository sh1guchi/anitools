using Anitools.Core.Media;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Media;

public sealed class MovAtomReaderTests
{
    [Fact]
    public void ReadAudioTitles_matches_original() =>
        GoldenAssert.All("mov_audio_titles", input =>
        {
            if (input.GetProperty("base64").GetString() is not { } base64)
            {
                return MovAtomReader.ReadAudioTitles(Path.Combine(Path.GetTempPath(), "anitools-missing", "missing.mov"));
            }

            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            return MovAtomReader.ReadAudioTitles(stream);
        });

    [Fact]
    public void Generic_handlers_match_original() =>
        Assert.Equal(
            Parsing.AnitomyTests.Constant("_MOV_GENERIC_HANDLERS").EnumerateArray().Select(v => v.GetString()),
            MovAtomReader.GenericHandlers);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Finds_track_names_that_ffprobe_does_not_show()
    {
        var tools = TestTools.RequireFfmpeg();
        using var dir = new TempDir();
        var mov = await MediaFactory.CreateAsync(dir.Combine("Resolve Export.mov"), [
            .. MediaFactory.Video, .. MediaFactory.Sine(440), .. MediaFactory.Sine(660),
            "-map", "0", "-map", "1", "-map", "2", "-c:v", "mpeg4", "-c:a", "pcm_s16le",
            "-metadata:s:a:0", "title=Dialogue RU", "-metadata:s:a:1", "title=Оригинал JP"]);

        var info = await new MediaProbe(new Core.Processes.ProcessRunner(), tools).ProbeAsync(mov, TestContext.Current.CancellationToken);
        Assert.All(info.AudioStreams, s => Assert.Null(s.Title));
        Assert.Equal(["Dialogue RU", "Оригинал JP"], MovAtomReader.ReadAudioTitles(mov));
    }
}
