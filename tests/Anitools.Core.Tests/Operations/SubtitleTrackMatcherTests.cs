using System.Text.Json;
using Anitools.Core.Media;
using Anitools.Core.Operations.Subtitles;
using Anitools.Core.Parsing;

namespace Anitools.Core.Tests.Operations;

public sealed class SubtitleTrackMatcherTests
{
    [Fact]
    public void CodecIdToExtension_matches_original() =>
        GoldenAssert.All("codec_id_to_ext", input => SubtitleTrackMatcher.CodecIdToExtension(input.GetString()!));

    [Fact]
    public void Roles_are_guessed_from_track_titles()
    {
        Assert.Equal(SubtitleKind.Signs, SubtitleTrackMatcher.GuessKind("Надписи [Anilibria]"));
        Assert.Equal(SubtitleKind.Signs, SubtitleTrackMatcher.GuessKind("Signs & Songs"));
        Assert.Equal(SubtitleKind.Subs, SubtitleTrackMatcher.GuessKind("Полные"));
        Assert.Equal(SubtitleKind.Subs, SubtitleTrackMatcher.GuessKind("Full Subs"));
        Assert.Null(SubtitleTrackMatcher.GuessKind("English"));

        SubtitleTrack[] tracks = [new(2, "English"), new(3, "Полные"), new(4, "Надписи")];
        Assert.Equal((tracks[2], tracks[1]), SubtitleTrackMatcher.GuessRoles(tracks));
        // ничего не похоже — первая дорожка в надписи, как раньше
        Assert.Equal((tracks[0], (SubtitleTrack?)null), SubtitleTrackMatcher.GuessRoles([tracks[0]]));
    }

    [Fact]
    public void FindByTitle_matches_original()
    {
        var fixtures = Fixtures("subtitle_track_by_title");
        GoldenAssert.All("subtitle_track_by_title", input => SubtitleTrackMatcher.FindByTitle(
            fixtures[input.GetProperty("tracks").GetString()!],
            input.GetProperty("ref_title").GetString()!));
    }

    [Fact]
    public void FindByLanguage_matches_original()
    {
        var fixtures = Fixtures("subtitle_track_by_lang");
        GoldenAssert.All("subtitle_track_by_lang", input => SubtitleTrackMatcher.FindByLanguage(
            fixtures[input.GetProperty("tracks").GetString()!],
            Track(0, input.GetProperty("ref_info")),
            input.GetProperty("ref_pos").GetInt32()));
    }

    [Fact]
    public void Languages_match_original() =>
        GoldenAssert.All("sub_langs", input =>
            SubtitleTrackMatcher.Languages(Track(0, input)).Order(PyText.CodePointComparer).ToList());

    private static Dictionary<string, List<SubtitleTrack>> Fixtures(string golden) =>
        GoldenFile.Load(golden).Root.GetProperty("fixtures").EnumerateObject().ToDictionary(
            f => f.Name,
            f => f.Value.EnumerateArray().Select(t => Track(t[0].GetInt32(), t[1])).ToList());

    private static SubtitleTrack Track(int id, JsonElement info) =>
        new(id, Text(info, "name"), Text(info, "codec_id"), Text(info, "language"), Text(info, "language_ietf"));

    private static string Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
