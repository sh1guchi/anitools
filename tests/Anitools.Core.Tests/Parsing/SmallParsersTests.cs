using Anitools.Core.Parsing;

namespace Anitools.Core.Tests.Parsing;

/// <summary>Ввод дорожек, язык, внешнее аудио, натуральная сортировка.</summary>
public sealed class SmallParsersTests
{
    [Fact]
    public void TrackIdList_matches_golden() =>
        GoldenAssert.All("parse_track_ids", input => TrackIdList.Parse(input.GetString()!));

    [Fact]
    public void TrackIdList_refuses_huge_range() =>
        Assert.Throws<FormatException>(() => TrackIdList.Parse("1-99999999"));

    [Fact]
    public void LanguageGuess_matches_golden() =>
        GoldenAssert.All("detect_lang", input => LanguageGuess.Detect(input.GetString()));

    [Fact]
    public void ExternalAudio_matches_golden() =>
        GoldenAssert.All("external_audio_matches", input =>
            ExternalAudio.Matches(input.GetProperty("filename").GetString()!, input.GetProperty("base_name").GetString()!));

    [Fact]
    public void NaturalSort_matches_golden() =>
        GoldenAssert.All("natural_sort", input =>
        {
            var names = input.EnumerateArray().Select(n => n.GetString()!).ToList();
            return new Dictionary<string, object>
            {
                ["sorted"] = NaturalSort.Order(names).ToList(),
                ["keys"] = names.Select(NaturalSort.Key).Select(k => new object?[] { (long?)k.Number, k.Lower }).ToList(),
            };
        });

    [Fact]
    public void NaturalSort_puts_ten_after_nine_and_unnumbered_last()
    {
        string[] names = ["Audio", "10. DEEP", "9. AniLibria", "1. Оригинальная"];
        Assert.Equal(["1. Оригинальная", "9. AniLibria", "10. DEEP", "Audio"], NaturalSort.Order(names));
    }
}
