using System.Buffers;
using System.Text;

namespace Anitools.Core.Parsing;

/// <summary>Внешние аудиофайлы для «Сборки аудио».</summary>
public static class ExternalAudio
{
    /// <summary>
    /// Аудиофайл относится к серии <paramref name="baseName"/>: его имя
    /// начинается с base, и дальше идёт не буква и не цифра — иначе «Show - 01» цеплял бы «Show - 011».
    /// Принимается и префикс «N. » из «Только аудио»: «2. Show - 01.Title.mka» → «Show - 01».
    /// </summary>
    public static bool Matches(string fileName, string baseName)
    {
        foreach (var cand in new[] { fileName, NaturalSort.StripTrackNumber(fileName) })
        {
            if (cand.StartsWith(baseName, StringComparison.Ordinal) && cand.Length > baseName.Length
                && Rune.DecodeFromUtf16(cand.AsSpan(baseName.Length), out var next, out _) == OperationStatus.Done
                && !TextUtils.IsAlnum(next))
            {
                return true;
            }
        }

        return false;
    }
}
