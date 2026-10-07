using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Processes;

public sealed class CommandLineAndProgressTests
{
    [Fact]
    public void CommandLine_matches_python_list2cmdline() =>
        GoldenAssert.All("list2cmdline", input => CommandLine.Format(input.EnumerateArray().Select(a => a.GetString()!)));

    [Fact]
    public void Progress_parser_reads_real_ffmpeg_output()
    {
        var events = new List<FfmpegProgress>();
        var parser = new FfmpegProgressParser(events.Add);
        foreach (var line in MediaFixtures.Read("ffmpeg_progress.txt").Split('\n'))
        {
            parser.Feed(line);
        }

        Assert.True(events.Count >= 2);
        Assert.Null(events[0].Speed);
        Assert.False(events[0].IsEnd);
        var last = events[^1];
        Assert.True(last.IsEnd);
        Assert.Equal(TimeSpan.FromMicroseconds(2958333), last.OutTime);
        Assert.Equal("290.9kbits/s", last.Bitrate);
        Assert.Equal(107558, last.TotalSize);
        Assert.True(last.Speed > 1);
        Assert.Equal(0.5, last.Fraction(5.916666)!.Value, 3);
    }

    [Theory]
    [InlineData("speed= 208x", 208.0)]
    [InlineData("speed=1.5x", 1.5)]
    [InlineData("speed=N/A", null)]
    public void Progress_speed(string line, double? speed)
    {
        FfmpegProgress? last = null;
        var parser = new FfmpegProgressParser(p => last = p);
        parser.Feed(line);
        parser.Feed("progress=continue");
        Assert.Equal(speed, last!.Speed);
    }
}
