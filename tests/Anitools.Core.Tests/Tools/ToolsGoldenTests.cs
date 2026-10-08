using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anitools.Core.Media;
using Anitools.Core.Operations.AssEdit;
using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Fonts;
using Anitools.Core.Operations.Hardsub;
using Anitools.Core.Operations.SubShift;
using Anitools.Core.Operations.TrackList;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Tools;

/// <summary>Инструменты из reference/python/extra — по эталонам соседних скриптов.</summary>
public sealed class ToolsGoldenTests
{
    [Fact]
    public void Subtitle_time_shift_matches_original() =>
        GoldenAssert.All("sub_shift_times", input =>
        {
            var time = input.GetProperty("time").GetString()!;
            var shift = input.GetProperty("shift").GetDouble();
            return input.GetProperty("kind").GetString() == "srt" ? SubtitleShift.ShiftSrtTime(time, shift) : SubtitleShift.ShiftAssTime(time, shift);
        });

    [Fact]
    public void Subtitle_file_shift_matches_original() =>
        GoldenAssert.All("sub_shift_text", input =>
        {
            var text = input.GetProperty("text").GetString()!.TrimStart('﻿'); // utf-8-sig снимает BOM при чтении
            var shift = input.GetProperty("shift").GetDouble();
            return input.GetProperty("kind").GetString() == "srt" ? SubtitleShift.ShiftSrt(text, shift) : SubtitleShift.ShiftAss(text, shift);
        });

    [Fact]
    public void Ass_cleanup_matches_original() =>
        GoldenAssert.All("ass_style_values", input =>
        {
            using var dir = new TempDir();
            foreach (var file in input.GetProperty("files").EnumerateObject())
            {
                File.WriteAllText(dir.Combine(file.Name), file.Value.GetString(), new UTF8Encoding(false));
            }

            var field = (AssField)input.GetProperty("field").GetInt32();
            var inspection = AssEditOperation.Inspect(dir.Path, field);
            var remove = input.GetProperty("remove").EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
            AssEditOperation.Execute(inspection, remove);
            return new Dictionary<string, object>
            {
                ["values"] = inspection.Values.Select(v => v.Value).ToList(),
                ["examples"] = inspection.Values.ToDictionary(v => v.Value, v => v.ExampleFile),
                ["files"] = inspection.Files.ToDictionary(f => Path.GetFileName(f), f => Encoding.UTF8.GetString(File.ReadAllBytes(f))),
            };
        });

    /// <summary>Совпадает с оригиналом, кроме имён с «'» и «:» — там оригинал ошибался (экранировал один уровень).</summary>
    [Fact]
    public void Subtitles_filter_escaping_matches_original() =>
        GoldenAssert.All(
            "escape_filter",
            input => HardsubOperation.EscapeFilter(input.GetString()!),
            new Dictionary<string, string>
            {
                ["\"C:\\\\anime\\\\[Group] Show - 01.ass\""] = "«:» экранируется и для разбора параметров фильтра",
                ["\"a'b,c;d[e]f:g.ass\""] = "«'» и «:» экранируются в два уровня — иначе ffmpeg не находит файл",
            });

    [Fact]
    public void Track_rows_and_copy_lists_match_original() =>
        GoldenAssert.All("track_rows", input =>
        {
            var json = JsonNode.Parse(input.GetProperty("ffprobe").GetRawText())!;
            var rows = TrackListOperation.Rows(MediaProbe.ParseFfprobe(json.ToJsonString()));
            return new Dictionary<string, object>
            {
                ["tracks"] = rows.Select(r => new Dictionary<string, object?>
                {
                    ["i"] = r.Number - 1,
                    ["title"] = r.Title,
                    ["lang"] = r.Language,
                    ["codec"] = r.Codec,
                    ["ch"] = r.Channels,
                    ["default"] = r.IsDefault,
                }).ToList(),
                ["copy"] = new Dictionary<string, string>
                {
                    // оригинал печатает список между двумя чертами; копируются только строки
                    ["1"] = Block(TrackListOperation.CopyList(rows, CopyListStyle.Bullets)),
                    ["2"] = Block(TrackListOperation.CopyList(rows, CopyListStyle.Numbers)),
                    ["3"] = Block(TrackListOperation.CopyList(rows, CopyListStyle.Plain)),
                },
            };

            static string Block(string list) => list.Length == 0 ? "" : $"\n─── для копирования ───\n{list}───────────────────────\n";
        });

    [Fact]
    public void Font_attachment_detection_matches_original() =>
        GoldenAssert.All("font_attachments", input => VideoFontsOperation.IsFont(new MkvAttachment(
            0,
            input.TryGetProperty("file_name", out var name) ? name.GetString()! : "",
            input.TryGetProperty("content_type", out var type) ? type.GetString()! : "",
            0)));

    /// <summary>Те же команды, что у audio_decod.py и delay-1s.py; добавлен только «-y» перед выходом (пустой остаток не мешает).</summary>
    [Fact]
    public void Audio_tool_commands_match_original() =>
        GoldenAssert.All("audio_tool_commands", input =>
        {
            using var dir = new TempDir();
            var source = input.GetProperty("source").GetString()!;
            dir.File(source);
            var plan = input.GetProperty("tool").GetString() == "delay-1s"
                ? AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = -input.GetProperty("seconds").GetDouble(), Reencode = true })
                : AudioConvertOperation.Plan(dir.Path, new AudioConvertOptions { Format = Format(input.GetProperty("format").GetString()!) });
            var args = Assert.Single(plan.Items).Command!.Arguments.Select(a => a.Replace(dir.Path + Path.DirectorySeparatorChar, "", StringComparison.Ordinal).Replace('\\', '/')).ToList();
            Assert.Equal("-y", args[^2]);
            args.RemoveAt(args.Count - 2);
            return new[] { (string[])["ffmpeg", .. args] };
        });

    private static AudioFormat Format(string name) => name switch
    {
        "aac" or "m4a" => AudioFormat.M4a,
        _ => Enum.Parse<AudioFormat>(name, ignoreCase: true),
    };
}
