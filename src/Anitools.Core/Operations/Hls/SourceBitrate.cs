using System.Globalization;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Hls;

/// <summary>
/// Битрейт видео исходника по окнам длиной в сегмент — по размерам пакетов,
/// без декодирования. Нужен для подбора CQ под серию.
/// </summary>
/// <param name="Rates">Бит/с по окнам от начала файла; последнее (неполное) окно не берётся.</param>
public sealed record SourceBitrate(IReadOnlyList<double> Rates, double Average, double P99)
{
    /// <summary>ffprobe: pts и размер каждого пакета первого видеопотока, «pts,size» по строке.</summary>
    public static ProcessSpec Command(string ffprobe, string file, Action<string> onLine) =>
        new(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,size", "-of", "csv=p=0", file])
        {
            OnStdoutLine = onLine,
        };

    /// <summary>По строкам вывода ffprobe; меньше трёх окон или все нулевые — null.</summary>
    public static SourceBitrate? Analyze(IEnumerable<string> lines, int segmentSeconds)
    {
        var accumulator = new Accumulator(segmentSeconds);
        foreach (var line in lines)
        {
            accumulator.Add(line);
        }

        return accumulator.Result();
    }

    /// <summary>Сбор по строкам, пока ffprobe ещё пишет: пакетов в серии — десятки тысяч.</summary>
    public sealed class Accumulator(int segmentSeconds)
    {
        private readonly List<(double Pts, long Size)> _packets = [];

        public void Add(string line)
        {
            var parts = line.Split(',');
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                || !TryParseInt(parts[1], out var size))
            {
                return;
            }

            _packets.Add((pts, size));
        }

        public SourceBitrate? Result()
        {
            if (_packets.Count == 0)
            {
                return null;
            }

            // Окна отсчитываются от начала файла — как и -ss при калибровке
            var start = _packets.Min(p => p.Pts);
            var sizes = new Dictionary<long, long>();
            foreach (var (pts, size) in _packets)
            {
                var window = (long)TextUtils.FloorDiv(pts - start, segmentSeconds);
                sizes[window] = sizes.GetValueOrDefault(window) + size;
            }

            var last = sizes.Keys.Max();
            var rates = new List<double>();
            for (var w = 0L; w < last; w++)
            {
                rates.Add((double)(sizes.GetValueOrDefault(w) * 8) / segmentSeconds);
            }

            if (rates.Count < 3 || rates.All(r => r == 0))
            {
                return null;
            }

            var sorted = rates.Order().ToList();
            return new SourceBitrate(rates, TextUtils.Sum(rates) / rates.Count, sorted[Math.Min(sorted.Count - 1, (int)(0.99 * sorted.Count))]);
        }

        private static bool TryParseInt(string text, out long value)
        {
            try
            {
                value = (long)TextUtils.ParseInt(text); // допускаются пробелы по краям и «_» между цифрами
                return true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                value = 0;
                return false;
            }
        }
    }
}
