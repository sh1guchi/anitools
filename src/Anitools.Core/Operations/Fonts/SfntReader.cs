using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Fonts;

/// <summary>Имена шрифта, под которыми его можно указать в .ass: основные (ID 1, 4, 6) и запасные (ID 16).</summary>
public sealed record FontNameSet(IReadOnlySet<string> Primary, IReadOnlySet<string> Fallback);

/// <summary>
/// Чтение шрифтов без внешних библиотек (ass_fonts.py): таблица name (sfnt и коллекции .ttc) и наличие кириллицы
/// в cmap (оригинал проверял через fontTools — здесь те же правила: лучшая таблица символов, форматы 0, 4, 6, 12).
/// Обрыв данных обрабатывается как у оригинала: всё, что прочитано до обрыва, остаётся.
/// </summary>
public static partial class SfntReader
{
    public static IReadOnlyList<int> PrimaryNameIds { get; } = [1, 4, 6];

    public static IReadOnlyList<int> FallbackNameIds { get; } = [16];

    /// <summary>Предпочтения таблиц символов (getBestCmap в fontTools).</summary>
    private static readonly (int Platform, int Encoding)[] CmapPreferences = [(3, 10), (0, 6), (0, 4), (3, 1), (0, 3), (0, 2), (0, 1), (0, 0)];

    /// <summary>Записи name с заданными ID по всем шрифтам файла (_read_names); значения — ключи <see cref="FontText.FontKey"/>.</summary>
    public static IReadOnlyDictionary<int, HashSet<string>> ReadNames(byte[] data, IEnumerable<int> ids)
    {
        var names = ids.Distinct().ToDictionary(i => i, _ => new HashSet<string>(StringComparer.Ordinal));
        try
        {
            foreach (var offset in FontOffsets(data))
            {
                ReadSfntNames(data, offset, names);
            }
        }
        catch (TruncatedFontException)
        {
            // как struct.error в оригинале: прочитанное до обрыва остаётся
        }

        return names;
    }

    /// <summary>Основные и запасные имена (read_font_names).</summary>
    public static FontNameSet ReadFontNames(byte[] data)
    {
        var names = ReadNames(data, [.. PrimaryNameIds, .. FallbackNameIds]);
        return new FontNameSet(
            PrimaryNameIds.SelectMany(i => names[i]).ToHashSet(StringComparer.Ordinal),
            FallbackNameIds.SelectMany(i => names[i]).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Начертание и версия (face_info): начертание — PostScript-имена (ID 6), иначе полные (ID 4), через «|»;
    /// версия — наибольшее число из ID 5. Файлы с одним начертанием — копии одного шрифта.
    /// </summary>
    public static (string Face, double Version) FaceInfo(byte[] data)
    {
        var names = ReadNames(data, [4, 5, 6]);
        var face = string.Join('|', (names[6].Count > 0 ? names[6] : names[4]).Order(PyText.CodePointComparer));
        var versions = names[5].Select(v => VersionRegex().Match(v)).Where(m => m.Success)
            .Select(m => double.Parse(AsciiDigits(m.Groups[1].Value), NumberStyles.Float, CultureInfo.InvariantCulture));
        return (face, versions.DefaultIfEmpty(0.0).Max());
    }

    /// <summary>
    /// Есть ли «Ж» (U+0416) хотя бы в одном шрифте файла. Коллекция — по расширению .ttc/.otc, как у оригинала.
    /// Не разобрать файл (нет cmap/maxp, обрыв, неизвестный формат) — считаем, что кириллица есть.
    /// </summary>
    public static bool HasCyrillic(byte[] data, string extension)
    {
        try
        {
            var collection = extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".otc", StringComparison.OrdinalIgnoreCase);
            if (collection != data.AsSpan().StartsWith("ttcf"u8))
            {
                return true; // TTFont не открывает коллекцию без номера шрифта, TTCollection — не коллекцию
            }

            foreach (var offset in FontOffsets(data))
            {
                if (FontHasCodePoint(data, offset, 0x0416))
                {
                    return true;
                }
            }

            return false;
        }
        catch (TruncatedFontException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private static IEnumerable<long> FontOffsets(byte[] data)
    {
        if (!Slice(data, 0, 4).SequenceEqual("ttcf"u8))
        {
            return [0];
        }

        var count = U32(Slice(data, 8, 4), 0);
        if (count >= 1000)
        {
            return [];
        }

        var table = Slice(data, 12, 4 * count);
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = U32(table, 4 * i);
        }

        return offsets;
    }

    private static void ReadSfntNames(byte[] data, long offset, Dictionary<int, HashSet<string>> names)
    {
        if (FindTable(data, offset, "name") is not { } name)
        {
            return;
        }

        var table = Slice(data, name.Offset, name.Length);
        var count = U16(table, 2);
        var strings = U16(table, 4);
        for (var i = 0; i < count; i++)
        {
            var platform = U16(table, 6 + (12 * i));
            var encoding = U16(table, 8 + (12 * i));
            var nameId = U16(table, 12 + (12 * i));
            var length = U16(table, 14 + (12 * i));
            var stringOffset = U16(table, 16 + (12 * i));
            if (!names.TryGetValue(nameId, out var set))
            {
                continue;
            }

            var raw = Clamp(table, strings + stringOffset, length);
            string text;
            if (platform is 0 or 3)
            {
                text = FontText.DecodeUtf16BeIgnore(raw);
            }
            else if (platform == 1 && encoding == 0)
            {
                text = FontText.DecodeMacRoman(raw);
            }
            else
            {
                continue;
            }

            if (PyText.Strip(text).Length > 0)
            {
                set.Add(FontText.FontKey(text));
            }
        }
    }

    /// <summary>Таблица шрифта по тегу или null; шрифт не sfnt — null; обрыв каталога — исключение.</summary>
    private static (long Offset, long Length)? FindTable(byte[] data, long offset, string tag)
    {
        var header = Slice(data, offset, 12);
        if (header.Length < 4 || !IsSfntMagic(header[..4]))
        {
            return null;
        }

        var tables = U16(header, 4);
        var directory = Slice(data, offset + 12, 16 * tables);
        for (var i = 0; i < tables; i++)
        {
            var entry = directory.Length >= (16 * i) + 16 ? directory.Slice(16 * i, 16) : throw new TruncatedFontException();
            if (entry[..4].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(tag)))
            {
                return (U32(entry, 8), U32(entry, 12));
            }
        }

        return null;
    }

    private static bool FontHasCodePoint(byte[] data, long offset, int codePoint)
    {
        if (Slice(data, offset, 4) is var magic && (magic.Length < 4 || !IsSfntMagic(magic)))
        {
            throw new NotSupportedException("не sfnt");
        }

        // fontTools берёт число глифов из maxp, прежде чем строить таблицу символов
        _ = FindTable(data, offset, "maxp") ?? throw new NotSupportedException("нет maxp");
        var cmap = FindTable(data, offset, "cmap") ?? throw new NotSupportedException("нет cmap");
        var table = Slice(data, cmap.Offset, cmap.Length);
        var subtables = U16(table, 2);
        var records = new Dictionary<(int, int), long>();
        for (var i = 0; i < subtables; i++)
        {
            records.TryAdd((U16(table, 4 + (8 * i)), U16(table, 6 + (8 * i))), U32(table, 8 + (8 * i)));
        }

        foreach (var preference in CmapPreferences)
        {
            if (records.TryGetValue(preference, out var subtable))
            {
                return SubtableHas(table, subtable, codePoint);
            }
        }

        return false; // подходящей таблицы нет — у этого шрифта символов нет
    }

    private static bool SubtableHas(ReadOnlySpan<byte> cmap, long offset, int codePoint)
    {
        var format = U16(cmap, offset);
        switch (format)
        {
            case 0:
                return codePoint < 256 && Clamp(cmap, offset + 6, 256) is var glyphs && glyphs.Length == 256 && glyphs[codePoint] != 0;
            case 4:
            {
                var segments = U16(cmap, offset + 6) / 2;
                var ends = offset + 14;
                var starts = ends + (2 * segments) + 2;
                var deltas = starts + (2 * segments);
                var ranges = deltas + (2 * segments);
                for (var i = 0; i < segments; i++)
                {
                    var end = U16(cmap, ends + (2 * i));
                    var start = U16(cmap, starts + (2 * i));
                    if (codePoint < start || codePoint > end)
                    {
                        continue;
                    }

                    var delta = U16(cmap, deltas + (2 * i));
                    var rangeOffset = U16(cmap, ranges + (2 * i));
                    var glyph = rangeOffset == 0
                        ? (codePoint + delta) & 0xFFFF
                        : U16(cmap, ranges + (2 * i) + rangeOffset + (2 * (codePoint - start))) is var g && g != 0 ? (g + delta) & 0xFFFF : 0;
                    return glyph != 0;
                }

                return false;
            }

            case 6:
            {
                var first = U16(cmap, offset + 6);
                var count = U16(cmap, offset + 8);
                return codePoint >= first && codePoint < first + count && U16(cmap, offset + 10 + (2 * (codePoint - first))) != 0;
            }

            case 12:
            {
                var groups = U32(cmap, offset + 12);
                for (var i = 0L; i < groups; i++)
                {
                    var start = U32(cmap, offset + 16 + (12 * i));
                    var end = U32(cmap, offset + 20 + (12 * i));
                    if (codePoint >= start && codePoint <= end)
                    {
                        return U32(cmap, offset + 24 + (12 * i)) + (codePoint - start) != 0;
                    }
                }

                return false;
            }

            default:
                throw new NotSupportedException($"формат cmap {format}");
        }
    }

    /// <summary>float() в Python понимает цифры любого письма — приводим к ASCII.</summary>
    private static string AsciiDigits(string s) =>
        new([.. s.Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c)]);

    private static bool IsSfntMagic(ReadOnlySpan<byte> magic) =>
        magic.SequenceEqual((byte[])[0, 1, 0, 0]) || magic.SequenceEqual("OTTO"u8) || magic.SequenceEqual("true"u8);

    /// <summary>fh.seek + fh.read: за концом файла — меньше байт (или ни одного), без ошибки.</summary>
    private static ReadOnlySpan<byte> Slice(byte[] data, long offset, long length) => Clamp(data, offset, length);

    private static ReadOnlySpan<byte> Clamp(ReadOnlySpan<byte> data, long offset, long length)
    {
        if (offset >= data.Length || length <= 0 || offset < 0)
        {
            return [];
        }

        return data.Slice((int)offset, (int)Math.Min(length, data.Length - offset));
    }

    private static int U16(ReadOnlySpan<byte> data, long offset) =>
        offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data[(int)offset..]) : throw new TruncatedFontException();

    private static long U32(ReadOnlySpan<byte> data, long offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data[(int)offset..]) : throw new TruncatedFontException();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();

    /// <summary>Данные оборвались (struct.error в оригинале).</summary>
    private sealed class TruncatedFontException : Exception;
}
