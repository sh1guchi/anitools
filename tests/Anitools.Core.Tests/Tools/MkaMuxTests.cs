using System.Text.Json;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.MkaMux;
using Anitools.Core.Parsing;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Tools;

/// <summary>Сборка озвучек в .mka — по эталонам mka_muxer.py (с нашим Anitomy вместо пакета anitopy).</summary>
public sealed class MkaMuxTests
{
    [Fact]
    public void Names_match_original() =>
        GoldenAssert.All("mka_names", input =>
        {
            var name = input.GetString()!;
            return new Dictionary<string, object?>
            {
                ["strip_track_num"] = MkaNames.StripTrackNumber(name),
                ["track_num"] = MkaNames.TrackNumber(name),
                ["parse_episode"] = MkaNames.ParseEpisode(name),
                ["episode_base"] = MkaNames.EpisodeBase(name),
                ["natural_key"] = new object?[] { MkaNames.TrackNumber(name), PyText.Lower(name) },
            };
        });

    [Fact]
    public void Trailing_episode_in_label_matches_original() =>
        GoldenAssert.All("mka_strip_trailing_episode", input =>
            MkaNames.StripTrailingEpisode(input.GetProperty("label").GetString(), input.GetProperty("ep").GetString()));

    [Fact]
    public void Voice_folder_matches_original() =>
        GoldenAssert.All("mka_voice_folder", input =>
        {
            var (label, number) = MkaNames.VoiceFolder(input.GetString()!);
            return new object?[] { label, number };
        });

    [Fact]
    public void Language_guess_matches_original() =>
        GoldenAssert.All("mka_detect_lang", input => LanguageGuess.Detect(input.ValueKind == JsonValueKind.Null ? null : input.GetString()));

    [Fact]
    public void Natural_order_puts_numbered_first()
    {
        string[] names = ["b.mka", "10. x.mka", "2. y.mka", "A.mka", "1. z.mka"];
        Assert.Equal(["1. z.mka", "2. y.mka", "10. x.mka", "A.mka", "b.mka"], names.Order(MkaNames.NaturalComparer));
    }

    /// <summary>Сценарии оригинала (mka_scenarios): та же папка и те же решения — те же команды ffmpeg.</summary>
    [Fact]
    public void Scenarios_issue_the_same_commands_as_original() =>
        GoldenAssert.All("mka_scenarios", input => RunAsync(input).GetAwaiter().GetResult());

    private static async Task<object> RunAsync(JsonElement input)
    {
        using var dir = new TempDir();
        var probes = new Dictionary<string, MediaInfo>();
        foreach (var file in input.GetProperty("files").EnumerateObject())
        {
            dir.File(file.Name);
            if (file.Value.ValueKind == JsonValueKind.Object)
            {
                var title = file.Value.GetProperty("title").GetString();
                probes[Path.Combine(dir.Path, file.Name.Replace('/', Path.DirectorySeparatorChar))] = new MediaInfo(
                    [.. Enumerable.Range(0, file.Value.GetProperty("streams").GetInt32()).Select(i => new MediaStream { Index = i, CodecType = "audio", Title = i == 0 ? title : null })],
                    10,
                    "matroska");
            }
        }

        if (input.GetProperty("existing") is { ValueKind: JsonValueKind.Array } existing)
        {
            foreach (var file in existing.EnumerateArray())
            {
                dir.File(file.GetString()!, "done");
            }
        }

        var options = input.GetProperty("options");
        var sources = await MkaMuxOperation.InspectAsync(dir.Path, new DictionaryProbe(probes), TestContext.Current.CancellationToken);
        var single = options.GetProperty("mode").GetString() == "single";
        var groups = MkaMuxOperation.Groups(sources, single ? MkaMuxMode.SingleFile : MkaMuxMode.ByEpisode, single ? options.GetProperty("name").GetString() : null);
        var labels = MkaMuxOperation.Labels(groups).Select(l => l.Label).ToList();
        var plan = MkaMuxOperation.Plan(dir.Path, groups, new MkaLabelOptions
        {
            Order = options.GetProperty("order") is { ValueKind: JsonValueKind.Array } order ? [.. order.EnumerateArray().Select(i => labels[i.GetInt32()])] : null,
            Titles = options.GetProperty("titles") is { ValueKind: JsonValueKind.Object } titles ? titles.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : null,
            Language = options.GetProperty("language").GetString(),
        });

        // Оригинал запускает ffmpeg по порядку и на ошибке идёт дальше — команды те же, что в плане
        var commands = plan.Items.Where(i => i.Status == PlanItemStatus.Run)
            .Select(i => (IReadOnlyList<string>)["ffmpeg", .. i.Command!.Arguments.Select(a => a.Replace('\\', '/').Replace(dir.Path.Replace('\\', '/'), "{root}", StringComparison.Ordinal))])
            .ToList();
        return new Dictionary<string, object> { ["commands"] = commands };
    }

    private sealed class DictionaryProbe(IReadOnlyDictionary<string, MediaInfo> probes) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            probes.TryGetValue(path, out var info) ? Task.FromResult(info) : throw new MediaProbeException(path, "нет фикстуры");

        public Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
