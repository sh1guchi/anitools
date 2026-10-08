using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anitools.Core.Install;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Install;

/// <summary>Кнопка «Установить»: сайты — фейковые (адреса настоящие), архивы собираются в тесте.</summary>
public sealed class ToolInstallerTests
{
    private const string FfmpegZip = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip";
    private const string MkvZip = "https://mkvtoolnix.download/windows/releases/102.0/mkvtoolnix-64-bit-102.0.zip";

    /// <summary>Как latest-release.xml на mkvtoolnix.download (сокращён).</summary>
    private const string MkvRelease = """
        <?xml version="1.0" encoding="utf-8"?>
        <mkvtoolnix-releases><latest-source><source-code-url>https://mkvtoolnix.download/sources/mkvtoolnix-102.0.tar.xz</source-code-url><url>https://mkvtoolnix.download/downloads.html</url><version>102.0</version></latest-source><latest-windows-binary><installer-url><amd64>https://mkvtoolnix.download/downloads.html#windows</amd64></installer-url></latest-windows-binary></mkvtoolnix-releases>
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ffmpeg_is_checked_by_sha256_and_only_the_two_programs_are_unpacked()
    {
        using var dir = new TempDir();
        var zip = Zip(
            ("ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", "ffmpeg"),
            ("ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe", "ffprobe"),
            ("ffmpeg-9.0.2-essentials_build/bin/ffplay.exe", "ffplay"),
            ("ffmpeg-9.0.2-essentials_build/doc/ffmpeg.html", "doc"));
        var site = new FakeSite
        {
            [ToolInstaller.FfmpegVersionUrl] = Text("9.0.2\n"),
            [FfmpegZip + ".sha256"] = Text(Sha256(zip)),
            [FfmpegZip] = zip,
        };
        Directory.CreateDirectory(dir.Combine("tools", "ffmpeg-8.0")); // прежняя версия — уберётся
        var progress = new SyncProgress();

        var result = await site.Installer(dir.Combine("tools")).InstallAsync(ToolPackage.Ffmpeg, progress, Ct);

        var target = dir.Combine("tools", "ffmpeg-9.0.2");
        Assert.Equal("9.0.2", result.Version);
        Assert.Equal(Path.Combine(target, "ffmpeg.exe"), result.Paths[Tool.Ffmpeg]);
        Assert.Equal(Path.Combine(target, "ffprobe.exe"), result.Paths[Tool.Ffprobe]);
        Assert.Equal("ffprobe", File.ReadAllText(result.Paths[Tool.Ffprobe]));
        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], Directory.GetFiles(target).Select(f => Path.GetFileName(f)).Order());
        Assert.Equal(["ffmpeg-9.0.2"], Directory.GetDirectories(dir.Combine("tools"), "ffmpeg-*").Select(f => Path.GetFileName(f)));
        Assert.Empty(Directory.GetFiles(dir.Combine("tools", "downloads"))); // архив после распаковки не нужен
        Assert.Contains(progress.Reports, p => p.Downloaded == zip.Length && p.Total == zip.Length);
        Assert.Contains(progress.Reports, p => p.Stage == "распаковка ffmpeg 9.0.2");
        Assert.Equal("9.0.2", await site.Installer(dir.Path).LatestVersionAsync(ToolPackage.Ffmpeg, Ct));
    }

    [Fact]
    public async Task Archive_that_does_not_match_the_checksum_is_thrown_away()
    {
        using var dir = new TempDir();
        var zip = Zip(("x/bin/ffmpeg.exe", "ffmpeg"), ("x/bin/ffprobe.exe", "ffprobe"));
        var site = new FakeSite
        {
            [ToolInstaller.FfmpegVersionUrl] = Text("9.0.2"),
            [FfmpegZip + ".sha256"] = Text(new string('0', 64)),
            [FfmpegZip] = zip,
        };

        var ex = await Assert.ThrowsAsync<InstallException>(() => site.Installer(dir.Path).InstallAsync(ToolPackage.Ffmpeg, null, Ct));

        Assert.Contains("контрольной суммой", ex.Message);
        Assert.False(Directory.Exists(dir.Combine("ffmpeg-9.0.2")));
        Assert.Empty(Directory.GetFiles(dir.Combine("downloads")));
    }

    [Fact]
    public async Task MkvToolNix_version_and_checksum_come_from_its_site()
    {
        using var dir = new TempDir();
        var zip = Zip(
            ("mkvtoolnix/mkvmerge.exe", "merge"),
            ("mkvtoolnix/mkvextract.exe", "extract"),
            ("mkvtoolnix/mkvinfo.exe", "info"),
            ("mkvtoolnix/mkvpropedit.exe", "propedit"),
            ("mkvtoolnix/mkvtoolnix-gui.exe", "gui"),
            ("mkvtoolnix/tools/bluray_dump.exe", "tool"),
            ("mkvtoolnix/locale/ru/LC_MESSAGES/mkvtoolnix.mo", "ru"));
        var sums = $"""
            b8ae68d36b9d500759537d5ccdfddf635f187786ff7cd1f42d01f8869e7d1ab9  mkvtoolnix-32-bit-102.0-setup.exe
            {Sha256(zip)}  mkvtoolnix-64-bit-102.0.zip
            cd63c42caee3d9e5b631e029657b3bd322beba77e150cecbfc9ae4923638b049  mkvtoolnix-64-bit-102.0.7z
            """;
        var site = new FakeSite
        {
            [ToolInstaller.MkvToolNixReleaseUrl] = Text(MkvRelease),
            ["https://mkvtoolnix.download/windows/releases/102.0/sha256sums.txt"] = Text(sums),
            [MkvZip] = zip,
        };

        var result = await site.Installer(dir.Path).InstallAsync(ToolPackage.MkvToolNix, null, Ct);

        Assert.Equal("102.0", result.Version);
        Assert.Equal("merge", File.ReadAllText(result.Paths[Tool.Mkvmerge]));
        Assert.Equal("extract", File.ReadAllText(result.Paths[Tool.Mkvextract]));
        Assert.Equal(
            ["mkvextract.exe", "mkvinfo.exe", "mkvmerge.exe", "mkvpropedit.exe"],
            Directory.GetFiles(dir.Combine("mkvtoolnix-102.0")).Select(f => Path.GetFileName(f)).Order());
        Assert.Equal("cd63c42caee3d9e5b631e029657b3bd322beba77e150cecbfc9ae4923638b049", ToolInstaller.FindSha256(sums, "mkvtoolnix-64-bit-102.0.7z"));
        Assert.Null(ToolInstaller.FindSha256(sums, "mkvtoolnix-64-bit-102.0"));
    }

    [Fact]
    public async Task ImDisk_installer_runs_with_admin_rights_and_the_result_is_checked()
    {
        using var dir = new TempDir();
        var site = new FakeSite { [ToolInstaller.ImDiskInstallerUrl] = [0x4D, 0x5A] };
        var runs = new List<string>();
        var installed = false;
        var installer = site.Installer(dir.Path, (program, args, _) =>
        {
            runs.Add($"{Path.GetFileName(program)} {string.Join(' ', args)} ({File.ReadAllBytes(program).Length} B)");
            installed = true;
            return Task.FromResult(0);
        }, () => installed);

        var result = await installer.InstallAsync(ToolPackage.ImDisk, null, Ct);

        Assert.Equal(["imdiskinst.exe -y (2 B)"], runs);
        Assert.Empty(result.Paths); // imdisk.exe ищется сам в System32
        Assert.False(File.Exists(dir.Combine("downloads", "imdiskinst.exe")));
        Assert.Null(await installer.LatestVersionAsync(ToolPackage.ImDisk, Ct));

        var declined = site.Installer(dir.Path, (_, _, _) => throw new Win32Exception(1223), () => false);
        Assert.Contains("отклонён", (await Assert.ThrowsAsync<InstallException>(() => declined.InstallAsync(ToolPackage.ImDisk, null, Ct))).Message);

        var failed = site.Installer(dir.Path, (_, _, _) => Task.FromResult(1), () => false);
        Assert.Contains("imdisk.exe не появился", (await Assert.ThrowsAsync<InstallException>(() => failed.InstallAsync(ToolPackage.ImDisk, null, Ct))).Message);
    }

    [Fact]
    public async Task Site_problems_are_explained()
    {
        using var dir = new TempDir();
        var down = new FakeSite();
        Assert.Contains("ответил 404", (await Assert.ThrowsAsync<InstallException>(() => down.Installer(dir.Path).LatestVersionAsync(ToolPackage.Ffmpeg, Ct))).Message);

        // вместо номера — страница: в адрес и имя папки такое не пойдёт
        var odd = new FakeSite { [ToolInstaller.FfmpegVersionUrl] = Text("<html>../../x</html>") };
        Assert.Contains("ответил не то", (await Assert.ThrowsAsync<InstallException>(() => odd.Installer(dir.Path).InstallAsync(ToolPackage.Ffmpeg, null, Ct))).Message);
        Assert.Contains("ответил не то", Assert.Throws<InstallException>(() => ToolInstaller.ParseMkvToolNixVersion("<oops")).Message);

        var offline = new FakeSite { Offline = true };
        Assert.Contains("Нет связи", (await Assert.ThrowsAsync<InstallException>(() => offline.Installer(dir.Path).InstallAsync(ToolPackage.MkvToolNix, null, Ct))).Message);
    }

    [Fact]
    public async Task Cancelled_download_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var big = new byte[3 * 1024 * 1024];
        new Random(1).NextBytes(big);
        var zip = Zip(("x/bin/ffmpeg.exe", Convert.ToBase64String(big)), ("x/bin/ffprobe.exe", "p"));
        var site = new FakeSite
        {
            [ToolInstaller.FfmpegVersionUrl] = Text("9.0.2"),
            [FfmpegZip + ".sha256"] = Text(Sha256(zip)),
            [FfmpegZip] = zip,
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress { OnReport = p => { if (p.Downloaded > 0) cancel.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => site.Installer(dir.Path).InstallAsync(ToolPackage.Ffmpeg, progress, cancel.Token));

        Assert.Empty(Directory.GetFiles(dir.Combine("downloads")));
        Assert.False(Directory.Exists(dir.Combine("ffmpeg-9.0.2")));
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Ход без контекста синхронизации: отчёты сразу, по порядку.</summary>
    private sealed class SyncProgress : IProgress<InstallProgress>
    {
        public List<InstallProgress> Reports { get; } = [];

        public Action<InstallProgress>? OnReport { get; init; }

        public void Report(InstallProgress value)
        {
            Reports.Add(value);
            OnReport?.Invoke(value);
        }
    }

    /// <summary>Сайты: адрес → содержимое; чего нет — 404; Offline — сети нет вовсе.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public bool Offline { get; init; }

        public byte[] this[string url]
        {
            get => _files[url];
            set => _files[url] = value;
        }

        public ToolInstaller Installer(string root, Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? runElevated = null, Func<bool>? installed = null) =>
            new(new HttpClient(this, disposeHandler: false), root, runElevated ?? ((_, _, _) => Task.FromResult(0)), installed ?? (() => true));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("Нет сети");
            }

            return Task.FromResult(_files.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
