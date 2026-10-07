using Anitools.Core.Parsing;

namespace Anitools.Core.Tests.Parsing;

public sealed class EpisodeNumberTests
{
    [Fact]
    public void Extract_matches_original_smart() =>
        GoldenAssert.All("extract_episode_number_smart", input => EpisodeNumber.Extract(input.GetString()!));

    [Fact]
    public void ExtractAdvanced_matches_original() =>
        GoldenAssert.All("extract_episode_number_advanced", input => EpisodeNumber.ExtractAdvanced(input.GetString()!));

    [Fact]
    public void ExtractBasic_matches_original() =>
        GoldenAssert.All("extract_episode_number", input => EpisodeNumber.ExtractBasic(input.GetString()!));

    [Theory]
    [InlineData("[SubsPlease] Sousou no Frieren - 01 (1080p) [F02B9CB4].mkv", "01")]
    [InlineData("HunterHunter.11_062.mkv", "62")]
    [InlineData("[SubsPlease] One Piece - 1100 (1080p) [AB12CD34].mkv", "1100")]
    [InlineData("Sousou no Frieren - 01.надписи.ass", "01")]
    [InlineData("ダンジョン飯 第０３話.mkv", "03")]
    [InlineData("readme.txt", null)]
    public void Typical_names(string name, string? expected) => Assert.Equal(expected, EpisodeNumber.Extract(name));
}
