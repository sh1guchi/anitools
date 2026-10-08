using System.Text.Json;
using System.Text.RegularExpressions;
using Anitools.Core.Media;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Media;

public sealed partial class MediaProbeParseTests
{
    [Fact]
    public void Subtitle_tracks_from_mkvmerge_match_golden() =>
        GoldenAssert.All("mkv_subtitle_tracks", input =>
        {
            var json = input.ValueKind == JsonValueKind.String
                ? MediaFixtures.Read(input.GetString()!)
                : input.GetProperty("inline").GetRawText();
            return MediaProbe.ParseMkvmerge(json).SubtitleTracks().Select(t => new object[]
            {
                t.Id,
                new Dictionary<string, string>
                {
                    ["name"] = t.Name, ["codec_id"] = t.CodecId, ["language"] = t.Language, ["language_ietf"] = t.LanguageIetf,
                },
            }).ToList();
        });

    [Fact]
    public void Ffprobe_json_of_an_episode()
    {
        var info = MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe("Test Show - 01.mkv"));

        Assert.Equal(3.023, info.Duration);
        Assert.Equal("matroska,webm", info.FormatName);
        Assert.Single(info.VideoStreams);
        Assert.Equal(["AniLibria.TV", "Оригинальная", "DEEP"], info.AudioStreams.Select(a => a.Title));
        Assert.Equal(["rus", "jpn", "rus"], info.AudioStreams.Select(a => a.Language));
        Assert.Equal([true, false, false], info.AudioStreams.Select(a => a.IsDefault));
        Assert.Equal(6, info.AudioStreams[2].Channels);
        Assert.Equal(2, info.SubtitleStreams.Count);
    }

    [Fact]
    public void Track_without_title_and_silent_file()
    {
        Assert.Null(MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe("Test Show - 02.mkv")).AudioStreams[1].Title);
        Assert.Empty(MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe("Silent Show - 01.mkv")).AudioStreams);
    }

    [Fact]
    public void CodecDescription_matches_ffmpeg_info_line_for_every_fixture()
    {
        // Имя дорожки без тайтла — как в строке «Audio: <кодек>,» вывода ffmpeg -i
        foreach (var media in MediaFixtures.MediaNames())
        {
            var fromFfmpeg = AudioLineRegex().Matches(MediaFixtures.FfmpegInfo(media)).Select(m => m.Groups[1].Value).ToList();
            var fromProbe = MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe(media)).AudioStreams.Select(a => a.CodecDescription).ToList();
            Assert.Equal(fromFfmpeg, fromProbe);
        }
    }

    [Fact]
    public void Mkvmerge_container_type_decides_extractor()
    {
        Assert.True(MediaProbe.ParseMkvmerge(MediaFixtures.Mkvmerge("Test Show - 01.mkv")).IsMatroska);
        Assert.False(MediaProbe.ParseMkvmerge(MediaFixtures.Mkvmerge("Test_Show_-_03.mp4")).IsMatroska);
    }

    [GeneratedRegex(@"Audio: ([^,]+)")]
    private static partial Regex AudioLineRegex();
}
