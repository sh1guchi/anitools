using Anitools.Core.Processes;
using Anitools.Core.Text;

namespace Anitools.Core.Tests.Processes;

public sealed class ToolStatusTests
{
    [Theory]
    [InlineData("ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers", "7.1.1")]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers", "6.1.1")]
    [InlineData("ffmpeg version N-118000-gfa1d2a3b4c-20250101 Copyright", "N-118000")]
    [InlineData("что-то другое", null)]
    public void Ffmpeg_version_is_read_from_first_line(string output, string? expected) =>
        Assert.Equal(expected, ToolStatusChecker.ParseFfmpegVersion(output));

    [Fact]
    public void Mkvmerge_version_and_nvenc()
    {
        Assert.Equal("82.0", ToolStatusChecker.ParseMkvmergeVersion("mkvmerge v82.0 ('I'm The President') 64-bit"));
        const string encoders = " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\n";
        const string filters = " ... scale_cuda        V->V       GPU accelerated video resizer\n";
        Assert.True(ToolStatusChecker.HasNvenc(encoders, filters));
        Assert.False(ToolStatusChecker.HasNvenc(encoders, " ..C scale             V->V       Scale the input video size\n"));
        Assert.False(ToolStatusChecker.HasNvenc(" V....D av1_nvenc   NVIDIA NVENC av1 encoder\n", filters));
    }

    [Fact]
    public async Task Check_asks_programs_and_tolerates_missing_ones()
    {
        var runner = new AnswerRunner(spec => spec.Arguments switch
        {
            ["-hide_banner", "-version"] => "ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright",
            ["-hide_banner", "-encoders"] => " V....D h264_nvenc   NVIDIA NVENC H.264 encoder\n",
            ["-hide_banner", "-filters"] => " ... scale_cuda   V->V   GPU accelerated video resizer\n",
            ["--version"] => "mkvmerge v82.0 ('I'm The President') 64-bit",
            _ => null,
        });

        var status = await ToolStatusChecker.CheckAsync(runner, new ToolPaths("ffmpeg", "ffprobe", "mkvmerge", null), null, TestContext.Current.CancellationToken);
        var none = await ToolStatusChecker.CheckAsync(runner, new ToolPaths(null, null, null, null), null, TestContext.Current.CancellationToken);

        Assert.Equal(("7.1.1", "82.0", (bool?)true), (status.FfmpegVersion, status.MkvmergeVersion, status.Nvenc));
        Assert.Equal((null, null, (bool?)null), (none.FfmpegVersion, none.MkvmergeVersion, none.Nvenc));
    }

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(14, "14 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(22, "22 файла")]
    [InlineData(111, "111 файлов")]
    [InlineData(0, "0 файлов")]
    public void Russian_plural(int count, string expected) => Assert.Equal(expected, RuText.Plural(count, "файл", "файла", "файлов"));

    [Theory]
    [InlineData(512L, "512 Б")]
    [InlineData(12_345L, "12 КБ")]
    [InlineData(5_400_000L, "5,1 МБ")]
    [InlineData(1_503_238_553L, "1,4 ГБ")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 ГБ")]
    public void File_sizes(long bytes, string expected) => Assert.Equal(expected, RuText.FileSize(bytes));

    [Fact]
    public void Durations_percent_and_speed()
    {
        Assert.Equal("0:42", RuText.Duration(TimeSpan.FromSeconds(42)));
        Assert.Equal("23:41", RuText.Duration(new TimeSpan(0, 23, 41)));
        Assert.Equal("1:12:40", RuText.Duration(new TimeSpan(1, 12, 40)));
        Assert.Equal("41%", RuText.Percent(0.4199));
        Assert.Equal("x2.3", RuText.Speed(2.31));
        Assert.Equal("x12", RuText.Speed(12.04));
    }

    private sealed class AnswerRunner(Func<ProcessSpec, string?> answer) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
            Task.FromResult(answer(spec) is { } output
                ? new ProcessResult(0, output, "", TimeSpan.Zero)
                : new ProcessResult(1, "", "Unrecognized option", TimeSpan.Zero));
    }
}
