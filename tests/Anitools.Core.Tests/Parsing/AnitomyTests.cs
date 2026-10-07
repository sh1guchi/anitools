using System.Text.Json;
using Anitools.Core.Parsing;

namespace Anitools.Core.Tests.Parsing;

public sealed class AnitomyTests
{
    [Fact]
    public void Parse_matches_original() =>
        GoldenAssert.All("anitomy_parse", input => Anitomy.Parse(input.GetString()).ToDictionary());

    [Fact]
    public void Keywords_match_original()
    {
        var expected = Constant("_AT_KEYWORDS");
        Assert.Equal(
            expected.EnumerateObject().Select(p => p.Name),
            Anitomy.Keywords.Keys);
        foreach (var category in expected.EnumerateObject())
        {
            Assert.Equal(category.Value.EnumerateArray().Select(v => v.GetString()), Anitomy.Keywords[category.Name]);
        }
    }

    [Fact]
    public void Typical_release_name_is_parsed()
    {
        var r = Anitomy.Parse("[SubsPlease] Sousou no Frieren - 01 (1080p) [F02B9CB4].mkv");
        Assert.Equal("Sousou no Frieren", r.AnimeTitle);
        Assert.Equal("1", r.EpisodeNumber);
        Assert.Equal("SubsPlease", r.ReleaseGroup);
        Assert.Equal("mkv", r.FileExtension);
    }

    [Fact]
    public void Empty_input_gives_empty_result()
    {
        Assert.Empty(Anitomy.Parse("").ToDictionary());
        Assert.Equal(new Dictionary<string, string> { ["file_name"] = "" }, Anitomy.Parse("   ").ToDictionary());
    }

    internal static JsonElement Constant(string name) =>
        GoldenFile.Load("constants").Cases.Single(c => c.Input.GetString() == name).Output!.Value;
}
