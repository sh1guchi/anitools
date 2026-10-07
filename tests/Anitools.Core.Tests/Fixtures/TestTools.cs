using Anitools.Core.Processes;

namespace Anitools.Core.Tests.Fixtures;

/// <summary>Настоящие ffmpeg/mkvtoolnix для интеграционных тестов; нет программ — тест пропускается.</summary>
internal static class TestTools
{
    private static readonly Lazy<ToolPaths> Found = new(() => ToolPaths.Find(new ToolLocator()));

    public static ToolPaths Paths => Found.Value;

    /// <summary>В CI с установленными программами (ANITOOLS_REQUIRE_TOOLS=1) отсутствие программы — ошибка, а не пропуск.</summary>
    private static bool Required => Environment.GetEnvironmentVariable("ANITOOLS_REQUIRE_TOOLS") == "1";

    public static ToolPaths RequireFfmpeg()
    {
        Require(Paths.Ffmpeg is not null && Paths.Ffprobe is not null, "ffmpeg/ffprobe");
        return Paths;
    }

    public static ToolPaths RequireMkvToolNix()
    {
        RequireFfmpeg();
        Require(Paths.Mkvmerge is not null && Paths.Mkvextract is not null, "MKVToolNix");
        return Paths;
    }

    private static void Require(bool found, string what)
    {
        if (!found && Required)
        {
            Assert.Fail($"ANITOOLS_REQUIRE_TOOLS=1, а {what} не найден");
        }

        Assert.SkipUnless(found, $"Нет {what} — интеграционный тест пропущен");
    }
}
