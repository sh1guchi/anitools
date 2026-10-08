using System.IO.Compression;
using System.Text.Json;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Hls;

/// <summary>
/// Сценарии п.7 из оригинала (эталон hls_scenarios): для той же папки и тех же решений C# запускает те же команды
/// ffmpeg, кладёт в архивы те же записи и оставляет те же файлы. ffmpeg фейковый: вместо кодирования создаёт
/// сегменты и .mka — так же, как фейк в tools/gen_golden.py.
/// </summary>
public sealed class HlsScenarioTests
{
    private static readonly ToolPaths Tools = new("ffmpeg", "ffprobe", null, null);

    [Fact]
    public void Same_commands_archives_and_files_as_original() =>
        GoldenAssert.All("hls_scenarios", input => RunAsync(input).GetAwaiter().GetResult());

    private static async Task<object> RunAsync(JsonElement input)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        using var logs = new TempDir();
        foreach (var file in input.GetProperty("files").EnumerateArray())
        {
            dir.File(file.GetString()!);
        }

        if (input.GetProperty("existing") is { ValueKind: JsonValueKind.Array } existing)
        {
            foreach (var file in existing.EnumerateArray())
            {
                dir.File(file.GetString()!, "done");
            }
        }

        var media = input.GetProperty("media").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var custom = new Dictionary<string, MediaInfo>();
        if (input.GetProperty("video") is { ValueKind: JsonValueKind.Object } video)
        {
            foreach (var file in video.EnumerateObject())
            {
                var info = MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe(media[file.Name]));
                custom[file.Name] = info with
                {
                    Streams = [.. info.Streams.Select(s => s.CodecType == "video"
                        ? s with { Width = file.Value.GetProperty("width").GetInt32(), Height = file.Value.GetProperty("height").GetInt32() }
                        : s)],
                };
            }
        }

        var probe = new NameFixtureProbe(media, custom);
        var inspection = await HlsOperation.InspectAsync(dir.Path, probe, ct);
        var options = input.GetProperty("options");
        var groupOptions = options.GetProperty("groups").EnumerateArray().Select(g => GroupOptions(inspection, g)).ToList();
        if (groupOptions.All(g => inspection.Groups.Contains(g.Group)))
        {
            // Без перегруппировки C# находит те же группы в том же порядке, что и оригинал
            Assert.Equal(groupOptions.Select(g => g.Group.Title), inspection.Groups.Select(g => g.Title).Where(t => groupOptions.Any(o => o.Group.Title == t)));
        }

        var plan = HlsOperation.Plan(inspection, groupOptions);
        // У фейка верхнее качество — 2 сегмента по 100 байт и плейлист в 8: чуть больше порога — отдельный архив
        var settings = options.TryGetProperty("separate_top_zip", out var top) && top.GetBoolean()
            ? HlsSettings.Default with { SeparateTopZipBytes = 207 }
            : HlsSettings.Default;
        var ffmpeg = new FakeFfmpeg();
        if (input.GetProperty("ffmpeg_fail") is { ValueKind: JsonValueKind.Array } fails)
        {
            foreach (var n in fails.EnumerateArray())
            {
                ffmpeg.ExitCodes[n.GetInt32()] = 1;
            }
        }

        var workRoot = options.GetProperty("work").GetString() == "folder" ? dir.Combine("work") : null;
        var result = await new HlsRunner(ffmpeg, Tools, probe, new ErrorLogWriter(logs.Path), settings).ExecuteAsync(plan, workRoot, cancellationToken: ct);

        var commands = ffmpeg.FfmpegArguments.Select(args =>
        {
            Assert.Equal(["-progress", "pipe:1", "-nostats"], args.Take(3)); // к команде оригинала добавлен только прогресс
            return (IReadOnlyList<string>)["ffmpeg", .. args.Skip(3).Select(a => Normalize(a, dir.Path))];
        }).ToList();
        var statuses = result.Episodes
            .Where(e => e.Episode.Status == PlanItemStatus.Run || e.Episode.Reason == HlsOperation.AlreadyDone)
            .Select(e => e.Outcome switch
            {
                HlsEpisodeOutcome.Done => "ok",
                HlsEpisodeOutcome.Skipped => "skip",
                HlsEpisodeOutcome.Failed => "fail",
                var other => other.ToString(),
            })
            .ToList();
        var zips = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var zipPath in Directory.EnumerateFiles(dir.Path, "*.zip", SearchOption.AllDirectories).Where(p => new FileInfo(p).Length > 4))
        {
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
            zips[Relative(zipPath, dir.Path)] = [.. zip.Entries.Select(e => e.FullName).Order(TextUtils.CodePointComparer)];
        }

        var files = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Select(p => Relative(p, dir.Path)).Order(TextUtils.CodePointComparer).ToList();
        return new Dictionary<string, object> { ["commands"] = commands, ["statuses"] = statuses, ["zips"] = zips, ["files"] = files };
    }

    private static HlsGroupOptions GroupOptions(HlsInspection inspection, JsonElement g)
    {
        var title = g.GetProperty("title").GetString()!;
        var group = g.TryGetProperty("files", out var files)
            ? new HlsGroup(title, [.. files.EnumerateArray().Select(f => inspection.Files.Single(x => x.Name == f.GetString()))])
            : inspection.Groups.Single(x => x.Title == title);
        var layouts = group.Layouts;
        var choices = g.GetProperty("layouts").EnumerateArray().ToList();
        Assert.Equal(choices.Count, layouts.Count);
        return new HlsGroupOptions
        {
            Group = group,
            TitleFolder = HlsOperation.TitleFolder(g.GetProperty("shikimori_id") is { ValueKind: JsonValueKind.Number } id ? id.GetInt64() : null, title),
            Voices = layouts.Select((l, i) => (l.Layout.Key, (IReadOnlyList<VoiceChoice>)[.. choices[i].EnumerateArray().Select(c => new VoiceChoice(c[0].GetInt32(), c[1].GetString()))]))
                .ToDictionary(),
        };
    }

    /// <summary>Слеши — к прямым до замены корня: путь сегментов и на Windows уже с прямыми (as_posix, как в оригинале).</summary>
    private static string Normalize(string arg, string root) => arg.Replace('\\', '/').Replace(root.Replace('\\', '/'), "{root}", StringComparison.Ordinal);

    private static string Relative(string path, string root) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
