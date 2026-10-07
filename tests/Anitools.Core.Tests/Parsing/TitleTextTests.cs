using Anitools.Core.Parsing;

namespace Anitools.Core.Tests.Parsing;

public sealed class TitleTextTests
{
    [Fact]
    public void AnimeTitle_matches_original() =>
        GoldenAssert.All("parse_anime_title", input => TitleText.AnimeTitle(input.GetString()!));

    [Fact]
    public void AnimeGroup_matches_original() =>
        GoldenAssert.All("parse_anime_group", input => TitleText.AnimeGroup(input.GetString()!));

    [Fact]
    public void Season_matches_original() =>
        GoldenAssert.All("title_season", input => TitleText.Season(input.GetString()));

    [Fact]
    public void CleanForSearch_matches_original() =>
        GoldenAssert.All("clean_title_for_search", input => TitleText.CleanForSearch(input.GetString()!));

    [Fact]
    public void Normalize_matches_original() =>
        GoldenAssert.All("norm_title", input => TitleText.Normalize(input.GetString()));

    [Fact]
    public void FileNameSafe_matches_original() =>
        GoldenAssert.All("filename_safe_title", input => TitleText.FileNameSafe(input.GetString()!));

    [Fact]
    public void SanitizeFolder_matches_original() =>
        GoldenAssert.All("sanitize_folder", input => TitleText.SanitizeFolder(input.GetString()!));

    [Fact]
    public void SanitizeTrackFolder_matches_original() =>
        GoldenAssert.All("sanitize_folder_name", input => TitleText.SanitizeTrackFolder(input.GetString()!));

    [Fact]
    public void OutputFileName_matches_original() =>
        GoldenAssert.All("process_output_filename", input => TitleText.OutputFileName(input.GetString()!));

    [Fact]
    public void Constants_match_original()
    {
        Assert.Equal(
            AnitomyTests.Constant("_SPECIAL_LABELS").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
            TitleText.SpecialLabels);
        Assert.Equal(
            AnitomyTests.Constant("_ROMAN_SEASON").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            TitleText.RomanSeason);
    }

    [Theory]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season", 2)]
    [InlineData("Overlord III", 3)]
    [InlineData("Enen no Shouboutai 3 pt 1", 3)]
    [InlineData("Sousou no Frieren", 1)]
    public void Season_examples(string title, int season) => Assert.Equal(season, TitleText.Season(title));

    [Fact]
    public void Three_sanitizers_differ_as_in_original()
    {
        Assert.Equal("52991 - Re_Zero", TitleText.SanitizeFolder("52991 - Re:Zero"));
        Assert.Equal("Re_Zero", TitleText.SanitizeTrackFolder("Re:Zero."));
        Assert.Equal("Re Zero - Starting Life", TitleText.FileNameSafe("Re:Zero: Starting Life"));
        Assert.Equal("audio_track", TitleText.SanitizeTrackFolder(" .. "));
    }
}
