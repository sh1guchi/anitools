using System.Globalization;
using System.Text.Json;
using Anitools.Core.Processes;

namespace Anitools.Core.Media;

/// <summary>Сведения о медиафайлах через ffprobe и mkvmerge.</summary>
public interface IMediaProbe
{
    /// <summary>ffprobe -show_streams -show_format: потоки, тайтлы, языки, длительность.</summary>
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>mkvmerge -J: дорожки с ID для mkvextract, вложения.</summary>
    Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Программа не найдена (нет в PATH и в настройках).</summary>
public sealed class ToolNotFoundException(Tool tool)
    : Exception($"Не найдена программа {tool.ToString().ToLowerInvariant()} — укажите путь в настройках")
{
    public Tool Tool { get; } = tool;
}

/// <summary>ffprobe/mkvmerge не смогли прочитать файл.</summary>
public sealed class MediaProbeException(string path, string details)
    : Exception($"Не удалось прочитать {Path.GetFileName(path)}: {details}")
{
    public string FilePath { get; } = path;
}

public sealed class MediaProbe(IProcessRunner runner, ToolPaths tools) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var ffprobe = tools.Ffprobe ?? throw new ToolNotFoundException(Tool.Ffprobe);
        var result = await runner.RunAsync(
            new ProcessSpec(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path]),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new MediaProbeException(path, LastLine(result.StandardErrorTail) ?? $"ffprobe вернул код {result.ExitCode}");
        }

        return ParseFfprobe(result.StandardOutput);
    }

    public async Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default)
    {
        var mkvmerge = tools.Mkvmerge ?? throw new ToolNotFoundException(Tool.Mkvmerge);
        // Явный UTF-8: иначе кодировка вывода зависит от локали/кодовой страницы и кириллица в JSON ломается
        var result = await runner.RunAsync(new ProcessSpec(mkvmerge, ["--output-charset", "UTF-8", "-J", path]), cancellationToken).ConfigureAwait(false);
        // mkvmerge -J: 0 — ок, 1 — с предупреждениями (JSON есть), 2 — не смог прочитать файл
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new MediaProbeException(path, LastLine(result.StandardOutput) ?? LastLine(result.StandardErrorTail) ?? $"mkvmerge вернул код {result.ExitCode}");
        }

        return ParseMkvmerge(result.StandardOutput);
    }

    /// <summary>Разбор JSON ffprobe -show_streams -show_format -of json.</summary>
    public static MediaInfo ParseFfprobe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                var tags = s.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
                var disposition = s.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                streams.Add(new MediaStream
                {
                    Index = Int(s, "index") ?? streams.Count,
                    CodecType = Text(s, "codec_type") ?? "",
                    CodecName = Text(s, "codec_name") ?? "",
                    Profile = Text(s, "profile"),
                    CodecTagString = Text(s, "codec_tag_string"),
                    CodecTag = Text(s, "codec_tag"),
                    Width = Int(s, "width"),
                    Height = Int(s, "height"),
                    Channels = Int(s, "channels"),
                    ChannelLayout = Text(s, "channel_layout"),
                    Title = tags.ValueKind == JsonValueKind.Object ? Text(tags, "title") : null,
                    Language = tags.ValueKind == JsonValueKind.Object ? Text(tags, "language") : null,
                    IsDefault = disposition.ValueKind == JsonValueKind.Object && Int(disposition, "default") == 1,
                    IsAttachedPicture = disposition.ValueKind == JsonValueKind.Object && Int(disposition, "attached_pic") == 1,
                });
            }
        }

        double? duration = null;
        string? formatName = null;
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
        {
            formatName = Text(format, "format_name");
            if (Text(format, "duration") is { } dur
                && double.TryParse(dur, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = seconds;
            }
        }

        return new MediaInfo(streams, duration, formatName);
    }

    /// <summary>Разбор JSON mkvmerge -J.</summary>
    public static MkvIdentification ParseMkvmerge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tracks = new List<MkvTrack>();
        if (root.TryGetProperty("tracks", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in list.EnumerateArray())
            {
                var props = t.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                var hasProps = props.ValueKind == JsonValueKind.Object;
                var tags = new List<(string, string)>();
                var hasTags = t.TryGetProperty("tags", out var tagsElement);
                if (hasTags && tagsElement.ValueKind == JsonValueKind.Object
                    && tagsElement.TryGetProperty("simple", out var simple) && simple.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in simple.EnumerateArray())
                    {
                        tags.Add((Text(tag, "name") ?? "", Text(tag, "value") ?? ""));
                    }
                }

                tracks.Add(new MkvTrack
                {
                    Id = Int(t, "id") ?? tracks.Count,
                    Type = Text(t, "type") ?? "",
                    Codec = Text(t, "codec") ?? "",
                    CodecId = Text(t, "codec_id") ?? "",
                    PropertiesCodecId = hasProps ? Text(props, "codec_id") ?? "" : "",
                    PropertiesCodec = hasProps ? Text(props, "codec") ?? "" : "",
                    TrackName = hasProps ? Text(props, "track_name") ?? "" : "",
                    Language = hasProps ? Text(props, "language") ?? "" : "",
                    LanguageIetf = hasProps ? Text(props, "language_ietf") ?? "" : "",
                    SimpleTags = tags,
                    HasTags = hasTags,
                });
            }
        }

        var attachments = new List<MkvAttachment>();
        if (root.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in att.EnumerateArray())
            {
                attachments.Add(new MkvAttachment(
                    Int(a, "id") ?? attachments.Count,
                    Text(a, "file_name") ?? "",
                    Text(a, "content_type") ?? "",
                    a.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            }
        }

        string? containerType = null;
        if (root.TryGetProperty("container", out var container) && container.ValueKind == JsonValueKind.Object)
        {
            containerType = Text(container, "type");
        }

        return new MkvIdentification(tracks, attachments, containerType);
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
