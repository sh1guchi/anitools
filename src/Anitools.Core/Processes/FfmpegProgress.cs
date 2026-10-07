using System.Globalization;

namespace Anitools.Core.Processes;

/// <summary>Один снимок прогресса ffmpeg (-progress pipe:1 -nostats).</summary>
/// <param name="OutTime">Сколько обработано от начала файла.</param>
/// <param name="Speed">Скорость относительно реального времени (2.3 = x2.3), null — неизвестна.</param>
/// <param name="Bitrate">Как пишет ffmpeg: «5123.4kbits/s», null — неизвестен.</param>
/// <param name="TotalSize">Байт записано, null — неизвестно.</param>
/// <param name="IsEnd">Последний снимок (progress=end).</param>
public sealed record FfmpegProgress(TimeSpan OutTime, double? Speed, string? Bitrate, long? TotalSize, bool IsEnd)
{
    /// <summary>Доля готовности 0..1 при известной длительности.</summary>
    public double? Fraction(double durationSeconds) =>
        durationSeconds > 0 ? Math.Clamp(OutTime.TotalSeconds / durationSeconds, 0, 1) : null;
}

/// <summary>
/// Разбор вывода «-progress pipe:1»: блоки строк key=value, каждый заканчивается progress=continue|end.
/// Подаётся в <see cref="ProcessSpec.OnStdoutLine"/>.
/// </summary>
public sealed class FfmpegProgressParser(Action<FfmpegProgress> onProgress)
{
    private TimeSpan _outTime;
    private double? _speed;
    private string? _bitrate;
    private long? _totalSize;

    public void Feed(string line)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return;
        }

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "out_time_us":
            case "out_time_ms": // у ffmpeg это тоже микросекунды (историческая ошибка в имени)
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    _outTime = TimeSpan.FromMicroseconds(us);
                }

                break;
            case "speed":
                _speed = double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                    ? speed
                    : null;
                break;
            case "bitrate":
                _bitrate = value is "N/A" or "" ? null : value;
                break;
            case "total_size":
                _totalSize = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null;
                break;
            case "progress":
                onProgress(new FfmpegProgress(_outTime, _speed, _bitrate, _totalSize, value == "end"));
                break;
        }
    }
}
