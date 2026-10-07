using System.Buffers.Binary;
using System.Text;
using Anitools.Core.Parsing;

namespace Anitools.Core.Media;

/// <summary>
/// Имена аудиодорожек прямо из боксов QuickTime/MP4 (py:1583–1701). Экспорты DaVinci Resolve пишут их
/// в moov/trak/udta/name, а ffprobe такие имена не показывает как title. Свой разбор боксов, без библиотек.
/// </summary>
public static class MovAtomReader
{
    /// <summary>Стандартные имена обработчиков из hdlr — это не тайтлы (_MOV_GENERIC_HANDLERS).</summary>
    public static IReadOnlyList<string> GenericHandlers { get; } =
    [
        "soundhandler", "sound handler", "apple sound media handler", "core media audio",
        "core media sound", "mainconcept", "audio", "sound", "isomediahandler", "ffmpeg",
        "lavf", "lavc", "handler",
    ];

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>Имена аудиодорожек по порядку (a:0, a:1, …); null — у дорожки нет имени. Любая ошибка → пустой список.</summary>
    public static IReadOnlyList<string?> ReadAudioTitles(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return ReadAudioTitles(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string?> ReadAudioTitles(Stream f)
    {
        var names = new List<string?>();
        foreach (var (type, start, end) in Boxes(f, 0, f.Length))
        {
            if (type != "moov")
            {
                continue;
            }

            foreach (var (type2, start2, end2) in Boxes(f, start, end))
            {
                if (type2 == "trak")
                {
                    var (isAudio, name) = TrackName(f, start2, end2);
                    if (isAudio)
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return names;
    }

    /// <summary>Боксы [(тип, начало данных, конец)] в диапазоне [start, end) (_mov_boxes).</summary>
    private static List<(string Type, long Start, long End)> Boxes(Stream f, long start, long end)
    {
        var boxes = new List<(string, long, long)>();
        var pos = start;
        Span<byte> header = stackalloc byte[8];
        Span<byte> ext = stackalloc byte[8];
        while (pos < end)
        {
            f.Seek(pos, SeekOrigin.Begin);
            if (f.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header[4..8]);
            ulong headerSize = 8;
            if (size == 1)
            {
                // 64-битный размер
                if (f.ReadAtLeast(ext, 8, throwOnEndOfStream: false) < 8)
                {
                    break;
                }

                size = BinaryPrimitives.ReadUInt64BigEndian(ext);
                headerSize = 16;
            }
            else if (size == 0)
            {
                // до конца контейнера
                size = (ulong)(end - pos);
            }

            // усечённый или битый бокс
            if (size < headerSize || size > (ulong)(end - pos))
            {
                break;
            }

            boxes.Add((type, pos + (long)headerSize, pos + (long)size));
            pos += (long)size;
        }

        return boxes;
    }

    /// <summary>(это аудио, имя) для одного trak (_mov_track_name): udta/name|©nam|titl, иначе нестандартное имя из hdlr.</summary>
    private static (bool IsAudio, string? Name) TrackName(Stream f, long start, long end)
    {
        var isAudio = false;
        string? name = null;
        string? hdlrName = null;
        foreach (var (type, ds, de) in Boxes(f, start, end))
        {
            if (type == "mdia")
            {
                foreach (var (type2, ds2, de2) in Boxes(f, ds, de))
                {
                    if (type2 != "hdlr")
                    {
                        continue;
                    }

                    var payload = Read(f, ds2, de2);
                    // version/flags(4) + pre_defined(4) + handler_type(4) + reserved(12) + name
                    if (payload.Length >= 12 && payload.AsSpan(8, 4).SequenceEqual("soun"u8))
                    {
                        isAudio = true;
                    }

                    if (payload.Length > 24)
                    {
                        hdlrName = DecodeHandlerName(payload[24..]);
                    }
                }
            }
            else if (type == "udta")
            {
                foreach (var (type2, ds2, de2) in Boxes(f, ds, de))
                {
                    if (type2 is "name" or "©nam" or "titl" && DecodeName(Read(f, ds2, de2)) is { } decoded)
                    {
                        name = decoded;
                    }
                }
            }
        }

        if (name is null && !string.IsNullOrEmpty(hdlrName) && !GenericHandlers.Contains(PyText.Lower(hdlrName)))
        {
            name = hdlrName;
        }

        return (isAudio, name);
    }

    /// <summary>Текст name/©nam/titl: сначала формат [size:2][lang:2][text], иначе сырой текст (_mov_decode_name).</summary>
    private static string? DecodeName(byte[] data)
    {
        if (data.Length >= 4)
        {
            var size = BinaryPrimitives.ReadUInt16BigEndian(data);
            if (size > 0 && size <= data.Length - 4)
            {
                var txt = PyText.Strip(Utf8.GetString(data, 4, size).Trim('\0'));
                if (txt.Length > 0)
                {
                    return txt;
                }
            }
        }

        var raw = PyText.Strip(Utf8.GetString(data).Trim('\0'));
        return raw.Length > 0 ? raw : null;
    }

    /// <summary>Имя из hdlr: Pascal-строка (QuickTime) или C-строка (ISO BMFF) (_mov_decode_hdlr_name).</summary>
    private static string? DecodeHandlerName(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        if (data[0] == data.Length - 1 || (data[0] < data.Length && data[^1] != 0))
        {
            var length = Math.Min(data[0], data.Length - 1);
            var txt = PyText.Strip(Utf8.GetString(data, 1, length));
            if (txt.Length > 0)
            {
                return txt;
            }
        }

        var text = Utf8.GetString(data);
        var zero = text.IndexOf('\0', StringComparison.Ordinal);
        var head = PyText.Strip(zero >= 0 ? text[..zero] : text);
        return head.Length > 0 ? head : null;
    }

    private static byte[] Read(Stream f, long start, long end)
    {
        f.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[end - start];
        var read = f.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }
}
