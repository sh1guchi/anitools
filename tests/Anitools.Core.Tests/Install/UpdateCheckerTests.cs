using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anitools.Core.Install;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Install;

/// <summary>Обновления: ответ GitHub releases/latest (как у настоящего API, сокращён) и скачивание установщика.</summary>
public sealed class UpdateCheckerTests
{
    private const string Latest = "https://api.github.com/repos/sh1guchi/anitools/releases/latest";
    private const string SetupUrl = "https://github.com/sh1guchi/anitools/releases/download/v1.2.0/anitools-setup.exe";
    private const string SumsUrl = "https://github.com/sh1guchi/anitools/releases/download/v1.2.0/SHA256SUMS.txt";

    private static readonly byte[] Setup = Encoding.UTF8.GetBytes("MZ установщик");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Latest_release_comes_with_the_setup_and_its_github_digest()
    {
        var site = new FakeGitHub { [Latest] = Release(digest: $"sha256:{Sha256(Setup).ToUpperInvariant()}") };

        var release = await new UpdateChecker(site.Client).LatestAsync(Ct);

        Assert.Equal(new ReleaseInfo("1.2.0", "https://github.com/sh1guchi/anitools/releases/tag/v1.2.0", SetupUrl, Sha256(Setup), Setup.Length), release);
        Assert.StartsWith("anitools/", site.UserAgents.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_digest_the_sum_comes_from_SHA256SUMS()
    {
        var site = new FakeGitHub
        {
            [Latest] = Release(digest: null),
            [SumsUrl] = $"{Sha256([1, 2])}  Anitools.exe\n{Sha256(Setup)}  anitools-setup.exe\n",
        };

        var release = await new UpdateChecker(site.Client).LatestAsync(Ct);

        Assert.Equal(Sha256(Setup), release?.SetupSha256);
    }

    [Fact]
    public async Task No_release_or_no_network_is_just_nothing_new()
    {
        Assert.Null(await new UpdateChecker(new FakeGitHub().Client).LatestAsync(Ct)); // 404 — выпусков ещё нет
        Assert.Null(await new UpdateChecker(new FakeGitHub { Offline = true }.Client).LatestAsync(Ct));
        Assert.Null(await new UpdateChecker(new FakeGitHub { [Latest] = "<html>" }.Client).LatestAsync(Ct));
        Assert.Null(await new UpdateChecker(new FakeGitHub { [Latest] = """{"tag_name":"nightly"}""" }.Client).LatestAsync(Ct));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("бред", "1.0.0", false)]
    public void Newer_version_is_compared_by_numbers(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(latest, current));

    [Fact]
    public async Task Setup_is_downloaded_only_with_a_matching_checksum()
    {
        using var dir = new TempDir();
        var site = new FakeGitHub { [SetupUrl] = Encoding.UTF8.GetString(Setup) };
        var checker = new UpdateChecker(site.Client);

        var path = await checker.DownloadSetupAsync(new ReleaseInfo("1.2.0", "", SetupUrl, Sha256(Setup), null), dir.Path, null, Ct);
        Assert.Equal(Path.Combine(dir.Path, "anitools-setup-1.2.0.exe"), path);
        Assert.Equal(Setup, await File.ReadAllBytesAsync(path, Ct));

        var wrong = new ReleaseInfo("1.2.1", "", SetupUrl, new string('0', 64), null);
        Assert.Contains("контрольной суммой", (await Assert.ThrowsAsync<InstallException>(() => checker.DownloadSetupAsync(wrong, dir.Path, null, Ct))).Message);
        var unsigned = new ReleaseInfo("1.2.2", "", SetupUrl, null, null);
        Assert.Contains("нет установщика с контрольной суммой", (await Assert.ThrowsAsync<InstallException>(() => checker.DownloadSetupAsync(unsigned, dir.Path, null, Ct))).Message);
        Assert.Equal(["anitools-setup-1.2.0.exe"], Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)));
    }

    private static string Release(string? digest) => $$"""
        {
          "html_url": "https://github.com/sh1guchi/anitools/releases/tag/v1.2.0",
          "tag_name": "v1.2.0",
          "name": "anitools 1.2.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            {"name": "Anitools.exe", "size": 157171289, "browser_download_url": "https://github.com/sh1guchi/anitools/releases/download/v1.2.0/Anitools.exe"},
            {"name": "anitools-setup.exe", "size": {{Setup.Length}}, {{(digest is null ? "" : $"\"digest\": \"{digest}\",")}}
             "browser_download_url": "{{SetupUrl}}"},
            {"name": "SHA256SUMS.txt", "size": 200, "browser_download_url": "{{SumsUrl}}"}
          ]
        }
        """;

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>GitHub без сети: адрес → ответ, чего нет — 404; запоминает User-Agent.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _answers = [];

        public bool Offline { get; init; }

        public List<string> UserAgents { get; } = [];

        public HttpClient Client => new(this, disposeHandler: false);

        public string this[string url]
        {
            get => _answers[url];
            set => _answers[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("нет сети");
            }

            if (request.RequestUri!.AbsoluteUri == Latest)
            {
                UserAgents.Add(request.Headers.UserAgent.ToString());
            }

            return Task.FromResult(_answers.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
