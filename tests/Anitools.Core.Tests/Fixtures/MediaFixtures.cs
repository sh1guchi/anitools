using Anitools.Tests.Shared;

namespace Anitools.Core.Tests.Fixtures;

/// <summary>Реальный вывод ffprobe / ffmpeg / mkvmerge для тестовых файлов (Fixtures/media, версии — в VERSIONS.txt).</summary>
internal static class MediaFixtures
{
    public static string DirectoryPath => RepoRoot.Combine("tests", "Anitools.Core.Tests", "Fixtures", "media");

    /// <summary>Имена исходных файлов, для которых снят вывод.</summary>
    public static IReadOnlyList<string> MediaNames() =>
        Directory.EnumerateFiles(DirectoryPath, "*.ffprobe.json")
            .Select(p => Path.GetFileName(p)[..^".ffprobe.json".Length])
            .Order(StringComparer.Ordinal)
            .ToList();

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(DirectoryPath, fileName));

    public static string Ffprobe(string media) => Read(media + ".ffprobe.json");

    public static string FfmpegInfo(string media) => Read(media + ".ffmpeg_i.txt");

    public static string Mkvmerge(string media) => Read(media + ".mkvmerge.json");
}
