using Anitools.Core.Processes;

namespace Anitools.Core.Tests.Fixtures;

/// <summary>Настоящие ffmpeg/mkvtoolnix для интеграционных тестов; нет программ — тест пропускается.</summary>
internal static class TestTools
{
    private static readonly Lazy<ToolPaths> Found = new(() => ToolPaths.Find(new ToolLocator()));

    public static ToolPaths Paths => Found.Value;

    public static ToolPaths RequireFfmpeg()
    {
        Assert.SkipUnless(Paths.Ffmpeg is not null && Paths.Ffprobe is not null, "Нет ffmpeg/ffprobe — интеграционный тест пропущен");
        return Paths;
    }

    public static ToolPaths RequireMkvToolNix()
    {
        RequireFfmpeg();
        Assert.SkipUnless(Paths.Mkvmerge is not null && Paths.Mkvextract is not null, "Нет MKVToolNix — интеграционный тест пропущен");
        return Paths;
    }
}
