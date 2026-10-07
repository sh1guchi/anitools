using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Processes;

public sealed class ToolLocatorTests
{
    private static ToolLocator Locator(bool windows, Dictionary<string, string> vars) =>
        new(new ToolEnvironment { IsWindows = windows, GetVariable = name => vars.GetValueOrDefault(name) });

    [Fact]
    public void Finds_in_path_with_exe_on_windows()
    {
        using var dir = new TempDir();
        var exe = dir.File("bin/ffmpeg.exe");
        var found = Locator(true, new() { ["PATH"] = dir.Combine("nothing") + Path.PathSeparator + dir.Combine("bin") }).Find(Tool.Ffmpeg);
        Assert.Equal(exe, found);
    }

    [Fact]
    public void Configured_path_wins_then_environment_variable()
    {
        using var dir = new TempDir();
        var inPath = dir.File("path/ffprobe");
        var fromEnv = dir.File("env/ffprobe");
        var configured = dir.File("cfg/ffprobe");
        var vars = new Dictionary<string, string> { ["PATH"] = dir.Combine("path"), ["FFPROBE_PATH"] = fromEnv };

        Assert.Equal(configured, Locator(false, vars).Find(Tool.Ffprobe, configured));
        Assert.Equal(configured, Locator(false, vars).Find(Tool.Ffprobe, dir.Combine("cfg")));
        Assert.Equal(fromEnv, Locator(false, vars).Find(Tool.Ffprobe));
        Assert.Equal(inPath, Locator(false, new() { ["PATH"] = dir.Combine("path") }).Find(Tool.Ffprobe));
        Assert.Null(Locator(false, new() { ["PATH"] = dir.Combine("nothing") }).Find(Tool.Ffprobe));
    }

    [Fact]
    public void Chocolatey_shim_is_replaced_by_real_exe()
    {
        using var dir = new TempDir();
        dir.File("chocolatey/bin/ffmpeg.exe");
        dir.File("chocolatey/bin/ffprobe.exe");
        dir.File("chocolatey/lib/7zip/tools/ffmpeg.exe"); // чужой пакет — не он
        var real = dir.File("chocolatey/lib/ffmpeg/tools/ffmpeg/bin/ffmpeg.exe");
        var realProbe = dir.File("chocolatey/lib/ffmpeg/tools/ffmpeg/bin/ffprobe.exe");
        var locator = Locator(true, new() { ["PATH"] = dir.Combine("chocolatey", "bin") });

        Assert.Equal(real, locator.Find(Tool.Ffmpeg));
        Assert.Equal(realProbe, locator.Find(Tool.Ffprobe));
    }

    [Fact]
    public void Shim_stays_when_no_real_exe()
    {
        using var dir = new TempDir();
        var shim = dir.File("chocolatey/bin/ffmpeg.exe");
        Assert.Equal(shim, Locator(true, new() { ["PATH"] = dir.Combine("chocolatey", "bin") }).Find(Tool.Ffmpeg));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Real_ffmpeg_behind_installed_chocolatey_shim()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Chocolatey — только на Windows");
        var shim = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
        Assert.SkipUnless(
            shim is not null && shim.Contains(@"\chocolatey\bin\", StringComparison.OrdinalIgnoreCase),
            "ffmpeg стоит не через Chocolatey");

        var found = new ToolLocator().Find(Tool.Ffmpeg);
        Assert.NotNull(found);
        Assert.Contains(@"\chocolatey\lib\", found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MkvToolNix_in_program_files_and_ffmpeg_name_from_env()
    {
        using var dir = new TempDir();
        var mkvmerge = dir.File("Program Files/MKVToolNix/mkvmerge.exe");
        var ffmpeg = dir.File("tools/ffmpeg.exe");
        var vars = new Dictionary<string, string>
        {
            ["PATH"] = dir.Combine("tools"),
            ["ProgramFiles"] = dir.Combine("Program Files"),
            ["FFMPEG_PATH"] = "ffmpeg",
        };
        Assert.Equal(mkvmerge, Locator(true, vars).Find(Tool.Mkvmerge));
        Assert.Equal(ffmpeg, Locator(true, vars).Find(Tool.Ffmpeg));
    }
}
