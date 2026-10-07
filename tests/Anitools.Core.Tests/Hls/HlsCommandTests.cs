using System.Text.Json;
using System.Text.Json.Nodes;
using Anitools.Core.Media;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Platform;
using Anitools.Core.Tests.Parsing;

namespace Anitools.Core.Tests.Hls;

/// <summary>Команды ffmpeg для HLS, лимиты NVDEC, дорожки и константы — по эталонам оригинала.</summary>
public sealed class HlsCommandTests
{
    [Fact]
    public void Video_input_args_match_original() =>
        GoldenAssert.All("hls_video_input_args", input =>
        {
            var seek = input.GetProperty("seek") is { ValueKind: JsonValueKind.Array } s ? (s[0].GetDouble(), s[1].GetDouble()) : ((double, double)?)null;
            var args = Builder(HlsSettings.Default, input).VideoInputArgs(
                Text(input, "input_path"), Ints(input.GetProperty("widths")), input.GetProperty("cpu_decode").GetBoolean(), seek);
            return WithProgram(Text(input, "ffmpeg_path"), args);
        });

    [Fact]
    public void Video_encode_args_match_original() =>
        GoldenAssert.All("hls_video_encode_args", input =>
            new HlsCommandBuilder(HlsSettings.Default).VideoEncodeArgs(input.GetProperty("vbr").GetInt64(), RateControlOf(input.GetProperty("rate_control"))));

    [Fact]
    public void Video_command_matches_original() =>
        GoldenAssert.All("hls_video_cmd", input =>
        {
            var settings = HlsSettings.Default with { Ladder = Ladder(input.GetProperty("resolutions")) };
            var rc = input.GetProperty("rate_control") is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(e => RateControlOf(e)!).ToList()
                : null;
            var args = Builder(settings, input).Video(Text(input, "input_path"), Text(input, "episode_out"), input.GetProperty("cpu_decode").GetBoolean(), rc);
            return WithProgram(Text(input, "ffmpeg_path"), args);
        });

    [Fact]
    public void Audio_command_matches_original() =>
        GoldenAssert.All("hls_audio_cmd", input =>
        {
            var voices = input.GetProperty("voices").EnumerateArray()
                .Select(v => new HlsVoice(v.GetProperty("track_index").GetInt32(), Text(v, "folder"), "und"))
                .ToList();
            var args = Builder(HlsSettings.Default, input).Audio(
                Text(input, "input_path"), Text(input, "episode_out"), voices, Ints(input.GetProperty("channels")), Text(input, "ep_name"));
            return WithProgram("ffmpeg", args);
        });

    [Fact]
    public void Software_profile_scales_and_encodes_on_cpu()
    {
        var builder = new HlsCommandBuilder(HlsSettings.Default with { Encoder = EncoderProfile.Software, Ladder = [new("360p", 640, 360, 800_000)] }, PathStyle.Posix);

        var args = builder.Video("/in.mkv", "/work/ep", cpuDecode: true, [new RateControl(21, 3_200_000)]);

        Assert.Equal(
            [
                "-nostdin", "-y", "-i", "/in.mkv", "-filter_complex", "[0:v]split=1[v0];[v0]scale=640:-2,format=yuv420p[v0out]",
                "-map", "[v0out]", "-c:v", "libx264", "-preset", "veryfast", "-force_key_frames", "expr:gte(t,n_forced*6)",
                "-crf", "21.00", "-maxrate", "3200000", "-bufsize", "6400000",
                "-f", "hls", "-hls_time", "6", "-hls_playlist_type", "vod", "-hls_flags", "independent_segments",
                "-hls_segment_filename", "/work/ep/360p/seg%03d.ts", "/work/ep/360p/master.m3u8",
            ],
            args);
        Assert.Equal(["-b:v", "800000", "-maxrate", "1600000", "-bufsize", "3200000"], builder.VideoEncodeArgs(800_000, null).TakeLast(6));
    }

    [Fact]
    public void Fixed_quality_is_cq_with_peak_times_ladder_bitrate()
    {
        var rc = HlsSettings.Default.FixedRateControl(21.0);
        Assert.Equal([3_200_000L, 6_000_000, 12_000_000, 20_000_000, 32_000_000, 64_000_000], rc.Select(r => r.MaxRate));
        Assert.All(rc, r => Assert.Equal(21.0, r.Cq));
    }

    [Fact]
    public void Cpu_decode_reason_matches_original() =>
        GoldenAssert.All("needs_cpu_decode", input =>
        {
            JsonElement stream;
            try
            {
                using var doc = JsonDocument.Parse(input.GetString()!);
                stream = doc.RootElement.TryGetProperty("streams", out var list) && list.GetArrayLength() > 0 ? list[0].Clone() : default;
            }
            catch (JsonException)
            {
                return null; // оригинал: ffprobe не дал JSON → видеокарта справится
            }

            return stream.ValueKind == JsonValueKind.Object
                ? NvdecLimits.CpuDecodeReason(
                    stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null,
                    stream.TryGetProperty("width", out var w) ? w.GetInt32() : null,
                    stream.TryGetProperty("height", out var h) ? h.GetInt32() : null)
                : null;
        });

    [Fact]
    public void Audio_tracks_and_layout_match_original()
    {
        var failures = new List<string>();
        foreach (var c in GoldenFile.Load("audio_tracks_for_episode").Cases)
        {
            var tracks = TracksFromProbeJson(c.Input.GetString()!);
            var actual = JsonSerializer.SerializeToElement(tracks.Select(t => new { index = t.Index, title = t.Title, lang = t.Language }));
            var layout = JsonSerializer.SerializeToElement(AudioLayout.Of(tracks).Tracks.Select(p => new[] { p.Title, p.Language }));
            if (!GoldenAssert.JsonEquals(c.Output!.Value, actual) || !GoldenAssert.JsonEquals(c.Raw.GetProperty("layout"), layout))
            {
                failures.Add($"{c.Input}\n  ждали: {c.Output} / {c.Raw.GetProperty("layout")}\n  вышло: {actual} / {layout}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// C# берёт каналы из JSON ffprobe (поле channels), а не из CSV, как оригинал; сверяется то же правило:
    /// неизвестное число каналов → 0, порядок — порядок аудиодорожек.
    /// </summary>
    [Fact]
    public void Unknown_channel_count_is_zero_like_original() =>
        GoldenAssert.All("audio_channels", input =>
        {
            var lines = input.ValueKind == JsonValueKind.String ? input.GetString()!.Split('\n').ToList() : [];
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1); // splitlines(): последний перевод строки не даёт пустой строки
            }

            var streams = lines.Select(l => l.Trim().Trim(','))
                .Select(l => new MediaStream { CodecType = "audio", Channels = l.Length > 0 && l.All(char.IsAsciiDigit) ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : null })
                .ToList();
            return HlsAudioTrack.FromStreams(streams).Select(t => t.Channels);
        });

    /// <summary>
    /// Длительность — из format.duration того же JSON ffprobe. Запасной «ffmpeg -i» оригинала не нужен:
    /// он читает ту же длительность контейнера и при «N/A» у ffprobe тоже пишет «Duration: N/A».
    /// </summary>
    [Fact]
    public void Duration_from_ffprobe_matches_original()
    {
        foreach (var c in GoldenFile.Load("video_duration").Cases)
        {
            var stdout = c.Input.GetProperty("stdout").GetString()!;
            var json = new JsonObject { ["format"] = new JsonObject { ["duration"] = stdout.Trim() } };
            var duration = MediaProbe.ParseFfprobe(json.ToJsonString()).Duration;
            if (c.Raw.GetProperty("commands").GetArrayLength() == 1)
            {
                Assert.Equal(c.Output!.Value.GetDouble(), duration);
            }
            else
            {
                Assert.Null(duration);
            }
        }
    }

    [Fact]
    public void Defaults_match_original_constants()
    {
        var d = HlsSettings.Default;
        Assert.Equal(
            AnitomyTests.Constant("_HLS_RESOLUTIONS").EnumerateArray().Select(r => (r[0].GetString(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt64())),
            d.Ladder.Select(r => ((string?)r.Name, r.Width, r.Height, r.Bitrate)));
        Assert.Equal(AnitomyTests.Constant("_HLS_SEGMENT_TIME").GetInt32(), d.SegmentSeconds);
        Assert.Equal(AnitomyTests.Constant("_HLS_FIXED_CQ").GetDouble(), d.FixedCq);
        Assert.Equal(AnitomyTests.Constant("_HLS_FIXED_CQ_PEAK").GetDouble(), d.FixedCqPeak);
        Assert.Equal(AnitomyTests.Constant("_HLS_NVENC_PRESET").GetString(), d.NvencPreset);
        Assert.Equal(AnitomyTests.Constant("_HLS_CAL_WINDOWS").GetInt32(), d.CalibrationWindows);
        Assert.Equal(AnitomyTests.Constant("_HLS_CAL_TOLERANCE").GetDouble(), d.CalibrationTolerance);
        Assert.Equal(AnitomyTests.Constant("_HLS_CAL_MAX_PASSES").GetInt32(), d.CalibrationMaxPasses);
        Assert.Equal(AnitomyTests.Constant("_HLS_CQ_START").GetDouble(), d.CqStart);
        Assert.Equal([d.CqMin, d.CqMax], AnitomyTests.Constant("_HLS_CQ_RANGE").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal(
            AnitomyTests.Constant("_NVDEC_MAX_SIZE").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())),
            NvdecLimits.MaxSize.Select(p => (p.Key, p.Value)));
        Assert.Equal(AnitomyTests.Constant("_VOICE_OPTIONS").EnumerateArray().Select(v => v.GetString()), VoiceList.Default);
    }

    [Fact]
    public void Voice_folders_are_sanitized_and_unique_by_track_order()
    {
        HlsAudioTrack[] tracks =
        [
            new(0, "AniLibria.TV", "rus", 2), new(1, "Комментарии", "rus", 2), new(2, " DEEP ", "rus", 6), new(3, "Track 4", "jpn", 2),
        ];

        var voices = VoiceAssignment.Build(tracks, [new(3, "deep"), new(2, "DEEP"), new(0, "Re:Zero / AniLibria"), new(1, null)]);

        Assert.Equal(["Re_Zero _ AniLibria", "DEEP", "deep_2"], voices.Select(v => v.Folder));
        Assert.Equal([0, 2, 3], voices.Select(v => v.TrackIndex));
        Assert.Equal(["rus", "rus", "jpn"], voices.Select(v => v.Language));
        Assert.Equal("DEEP", VoiceAssignment.SuggestedName(tracks[2]));
        Assert.Equal("Track3", VoiceAssignment.SuggestedName(tracks[2] with { Title = "  " }));
    }

    internal static IReadOnlyList<HlsAudioTrack> TracksFromProbeJson(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return []; // оригинал: ffprobe не дал JSON → дорожек нет
        }

        foreach (var stream in root?["streams"]?.AsArray() ?? new JsonArray())
        {
            stream!["codec_type"] = "audio";
        }

        return HlsAudioTrack.FromStreams(MediaProbe.ParseFfprobe(root!.ToJsonString()).AudioStreams);
    }

    internal static string[] WithProgram(string program, IReadOnlyList<string> args) => [program, .. args];

    internal static HlsCommandBuilder Builder(HlsSettings settings, JsonElement input) =>
        new(settings, Text(input, "platform") == "windows" ? PathStyle.Windows : PathStyle.Posix);

    internal static IReadOnlyList<HlsRung> Ladder(JsonElement list) =>
        [.. list.EnumerateArray().Select(r => new HlsRung(r[0].GetString()!, r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt64()))];

    internal static RateControl? RateControlOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? new RateControl(e.GetProperty("cq").GetDouble(), e.GetProperty("maxrate").GetInt64()) : null;

    internal static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    internal static int[] Ints(JsonElement list) => [.. list.EnumerateArray().Select(v => v.GetInt32())];
}
