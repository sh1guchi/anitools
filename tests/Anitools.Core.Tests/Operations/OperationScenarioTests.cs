using System.Text.Json;
using Anitools.Core.Media;
using Anitools.Core.Operations.AudioExtract;
using Anitools.Core.Operations.AudioMux;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Remux;
using Anitools.Core.Operations.Subtitles;
using Anitools.Core.Operations.VideoOnly;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Operations;

/// <summary>
/// Сценарии операций (эталон operation_scenarios): для папки и настроек из эталона план должен выдать
/// те же команды ffmpeg / mkvextract.
/// </summary>
public sealed class OperationScenarioTests
{
    [Fact]
    public void Plans_issue_the_same_commands_as_golden() =>
        GoldenAssert.All("operation_scenarios", input => Run(input).GetAwaiter().GetResult());

    private static async Task<object> Run(JsonElement input)
    {
        using var dir = new TempDir();
        foreach (var file in input.GetProperty("files").EnumerateArray())
        {
            dir.File(file.GetString()!);
        }

        foreach (var file in input.GetProperty("existing").EnumerateArray())
        {
            dir.File(file.GetString()!, "done");
        }

        var media = input.GetProperty("media").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var probe = new FixtureProbe(dir.Path, media);
        var options = input.GetProperty("options");
        var ct = TestContext.Current.CancellationToken;

        var plan = input.GetProperty("operation").GetString() switch
        {
            "video_only" => VideoOnlyOperation.Plan(dir.Path),
            "remux" => RemuxOperation.Plan(dir.Path, new RemuxOptions(
                options.GetProperty("format").GetString() == "mkv" ? RemuxFormat.Mkv : RemuxFormat.Mp4,
                !options.TryGetProperty("subtitles", out var subs) || subs.GetBoolean())),
            "audio_extract" => AudioExtractOperation.Plan(await AudioExtractOperation.InspectAsync(dir.Path, probe, ct), AudioExtractOptions(options)),
            "audio_mux" => await PlanAudioMux(dir.Path, options, probe, ct),
            "subtitles" => await SubtitleExtractOperation.PlanAsync(
                await SubtitleExtractOperation.InspectAsync(dir.Path, probe, ct),
                new SubtitleExtractOptions(
                    options.GetProperty("ref").GetInt32(),
                    options.GetProperty("mode").GetString() switch { "title" => SubtitleMatchMode.ByTitle, "lang" => SubtitleMatchMode.ByLanguage, _ => SubtitleMatchMode.ById },
                    options.GetProperty("kind").GetString() == "subs" ? SubtitleKind.Subs : SubtitleKind.Signs),
                probe,
                ct),
            var op => throw new InvalidOperationException(op),
        };

        var commands = plan.Items.Where(i => i.Status == PlanItemStatus.Run).Select(i =>
        {
            var args = i.Command!.Arguments.Select(a => a.Replace(dir.Path, "{root}", StringComparison.Ordinal).Replace('\\', '/')).ToList();
            if (input.GetProperty("operation").GetString() == "remux")
            {
                // Намеренное отличие (docs/PLAN.md §2.8 #7): «-map 0:a?» вместо «0:a», чтобы не падать на файлах без звука.
                // Остальную команду сверяем с эталоном.
                Assert.Contains("0:a?", args);
                args = args.Select(a => a == "0:a?" ? "0:a" : a).ToList();
            }

            return (IReadOnlyList<string>)[i.Command.Tool.ToString().ToLowerInvariant(), .. args];
        }).ToList();
        return new Dictionary<string, object> { ["commands"] = commands };
    }

    private static AudioExtractOptions AudioExtractOptions(JsonElement o) => new()
    {
        TrackIds = [.. o.GetProperty("tracks").EnumerateArray().Select(t => t.GetInt32())],
        Mode = o.TryGetProperty("mode", out var mode) && mode.GetString() == "single" ? AudioExtractMode.SingleMka : AudioExtractMode.Separate,
        NumberTracks = !o.TryGetProperty("number", out var number) || number.GetBoolean(),
        Titles = o.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Object
            ? titles.EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetString()!)
            : null,
        Language = o.TryGetProperty("language", out var lang) ? lang.GetString() : "rus",
    };

    private static async Task<OperationPlan> PlanAudioMux(string folder, JsonElement o, IMediaProbe probe, CancellationToken ct)
    {
        var source = await AudioMuxOperation.InspectAsync(folder, probe, ct);
        var analysis = await AudioMuxOperation.AnalyzeAsync(
            source,
            [.. o.GetProperty("tracks").EnumerateArray().Select(t => t.GetInt32())],
            o.GetProperty("external").GetBoolean(),
            probe,
            ct);
        var main = analysis.Sets[0];
        var order = o.GetProperty("order").ValueKind == JsonValueKind.Array
            ? o.GetProperty("order").EnumerateArray().Select(i => main.Slots[i.GetInt32()].Key).ToList()
            : main.Slots.Select(s => s.Key).ToList();
        var titles = o.GetProperty("titles").ValueKind == JsonValueKind.Array
            ? o.GetProperty("titles").EnumerateArray().Select((t, i) => (order[i], t.GetString()!)).ToDictionary()
            : new Dictionary<string, string>();
        var language = o.GetProperty("language").GetString();
        var mainConfig = AudioSlotSetConfig.Default(main, language, titles, order);
        return AudioMuxOperation.Plan(analysis, [mainConfig, .. analysis.Sets.Skip(1).Select(s => AudioSlotSetConfig.FromMain(mainConfig, s, language))]);
    }

    /// <summary>ffprobe / mkvmerge по фикстурам: файл (относительно папки) → какой реальный вывод вернуть.</summary>
    private sealed class FixtureProbe(string root, IReadOnlyDictionary<string, string> media) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe(Fixture(path))));

        public Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaProbe.ParseMkvmerge(MediaFixtures.Mkvmerge(Fixture(path))));

        private string Fixture(string path)
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            return media.TryGetValue(rel, out var name) ? name : throw new MediaProbeException(path, $"в сценарии нет фикстуры для {rel}");
        }
    }
}
