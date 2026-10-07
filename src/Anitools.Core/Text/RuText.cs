using System.Globalization;

namespace Anitools.Core.Text;

/// <summary>Числа по-русски для интерфейса и журналов: склонение, размеры файлов, длительность.</summary>
public static class RuText
{
    /// <summary>«1 файл», «2 файла», «5 файлов», «11 файлов», «21 файл».</summary>
    public static string Plural(long count, string one, string few, string many)
    {
        var n = Math.Abs(count) % 100;
        var word = n is >= 11 and <= 14 ? many : (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
        return count.ToString(CultureInfo.InvariantCulture) + " " + word;
    }

    /// <summary>«512 Б», «12 КБ», «1,4 ГБ» — дробная часть только у МБ и больше.</summary>
    public static string FileSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = unit >= 2 && value < 100
            ? value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',')
            : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }

    /// <summary>«0:42», «23:41», «1:12:40» — как на таймере.</summary>
    public static string Duration(TimeSpan time)
    {
        var seconds = (long)Math.Max(0, Math.Round(time.TotalSeconds));
        var (h, m, s) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>Доля как проценты без дробной части: 0.414 → «41%».</summary>
    public static string Percent(double fraction) =>
        Math.Floor(Math.Clamp(fraction, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Скорость ffmpeg: 2.3 → «x2.3», 12.04 → «x12».</summary>
    public static string Speed(double speed) =>
        "x" + (speed >= 10 ? Math.Round(speed).ToString("0", CultureInfo.InvariantCulture) : speed.ToString("0.0", CultureInfo.InvariantCulture));
}
