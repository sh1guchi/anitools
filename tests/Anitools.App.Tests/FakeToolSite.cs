using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anitools.Core.Install;

namespace Anitools.App.Tests;

/// <summary>
/// Сайты программ без сети: gyan.dev с архивом ffmpeg нужной версии (собран в тесте), остальное — 404.
/// MKVToolNix может «зависнуть» — чтобы проверить отмену и показать ход.
/// </summary>
internal sealed class FakeToolSite : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = [];

    /// <summary>Запросы к mkvtoolnix.download не отвечают, пока их не отменят.</summary>
    public bool MkvToolNixHangs { get; init; }

    public static FakeToolSite WithFfmpeg(string version, bool mkvToolNixHangs = false)
    {
        var site = new FakeToolSite { MkvToolNixHangs = mkvToolNixHangs };
        byte[] zip;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe", "ffplay.exe" })
                {
                    using var writer = new StreamWriter(archive.CreateEntry($"ffmpeg-{version}-essentials_build/bin/{name}").Open());
                    writer.Write(name);
                }
            }

            zip = stream.ToArray();
        }

        var url = ToolInstaller.FfmpegZipUrl(version);
        site._files[ToolInstaller.FfmpegVersionUrl] = Encoding.UTF8.GetBytes(version + "\n");
        site._files[url + ".sha256"] = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
        site._files[url] = zip;
        return site;
    }

    /// <summary>Установщик на этом сайте: в папку tools внутри root, ImDisk «ставится» сразу.</summary>
    public ToolInstaller Installer(string root) =>
        new(new HttpClient(this, disposeHandler: false), Path.Combine(root, "tools"), (_, _, _) => Task.FromResult(0), () => true);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (MkvToolNixHangs && url.StartsWith("https://mkvtoolnix.download/", StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return _files.TryGetValue(url, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
