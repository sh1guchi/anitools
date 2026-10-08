using Anitools.Core.Operations.AudioTools;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Anitools.App.Views;

/// <summary>Мелкие преобразования для разметки.</summary>
public static class Converters
{
    /// <summary>Число больше нуля → true (кнопка «Запустить» доступна, если есть что запускать).</summary>
    public static FuncValueConverter<int, bool> IsPositive { get; } = new(n => n > 0);

    /// <summary>«Запустить (11)».</summary>
    public static FuncValueConverter<int, string> RunText { get; } = new(n => n > 0 ? $"Запустить ({n})" : "Запустить");

    public static FuncValueConverter<string?, bool> IsNotEmpty { get; } = new(s => !string.IsNullOrEmpty(s));

    /// <summary>0 серий у Shikimori — «ещё неизвестно»: пусто, как в оригинале.</summary>
    public static FuncValueConverter<int, string> EmptyIfZero { get; } = new(n => n == 0 ? "" : n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Формат «Перекодировать» в списке: «MKA · AAC», «MOV · AAC, только звук».</summary>
    public static FuncValueConverter<object?, string> AudioFormatLabel { get; } = new(value =>
        (value is AudioFormat format || Enum.TryParse(value as string, out format)) ? format switch
        {
            AudioFormat.Mka => "MKA · AAC",
            AudioFormat.M4a => "M4A · AAC",
            AudioFormat.Mp3 => "MP3",
            AudioFormat.Opus => "Opus",
            AudioFormat.Ogg => "OGG · Vorbis",
            AudioFormat.Flac => "FLAC",
            AudioFormat.Wav => "WAV",
            AudioFormat.Mov => "MOV · AAC, только звук",
            _ => format.ToString(),
        } : value?.ToString() ?? "");

    /// <summary>Номер, поправленный руками, — жирным.</summary>
    public static FuncValueConverter<bool, FontWeight> BoldIf { get; } = new(b => b ? FontWeight.Bold : FontWeight.Normal);
}
