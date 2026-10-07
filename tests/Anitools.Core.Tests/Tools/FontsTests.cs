using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anitools.Core.Operations.Fonts;
using Anitools.Core.Parsing;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Tools;

/// <summary>Шрифты для .ass — по эталонам ass_fonts.py (сняты с fontTools: кириллица в cmap — настоящая проверка).</summary>
public sealed class FontsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Font_names_face_and_cyrillic_match_original() =>
        GoldenAssert.All("sfnt_font_names", input =>
        {
            var data = Convert.FromBase64String(input.GetProperty("base64").GetString()!);
            var names = SfntReader.ReadFontNames(data);
            var (face, version) = SfntReader.FaceInfo(data);
            return new Dictionary<string, object>
            {
                ["primary"] = names.Primary.Order(PyText.CodePointComparer).ToList(),
                ["fallback"] = names.Fallback.Order(PyText.CodePointComparer).ToList(),
                ["face"] = face,
                ["version"] = version,
                ["cyrillic"] = SfntReader.HasCyrillic(data, input.GetProperty("ext").GetString()!),
            };
        });

    [Fact]
    public void Font_keys_match_original() =>
        GoldenAssert.All("font_keys", input =>
        {
            var s = input.GetString()!;
            return new Dictionary<string, string>
            {
                ["font_key"] = FontText.FontKey(s),
                ["normalize"] = FontText.Normalize(s),
                ["slug_"] = FontText.Slugify(s, '_'),
                ["slug-"] = FontText.Slugify(s, '-'),
                ["safe"] = FontText.SafeFileName(s),
                ["clean"] = FontText.CleanFontName(s),
            };
        });

    [Fact]
    public void Ass_font_names_match_original() =>
        GoldenAssert.All("ass_font_names", input =>
            FontText.ParseFontNames(FontText.ReadAss(Convert.FromBase64String(input.GetProperty("base64").GetString()!))).Order(PyText.CodePointComparer));

    [Fact]
    public void Fonts_from_zip_match_original() =>
        GoldenAssert.All("fonts_from_zip", input =>
            Digests(FontDownloader.FontsFromZip(Convert.FromBase64String(input.GetProperty("base64").GetString()!), input.GetProperty("font").GetString()!)));

    /// <summary>Ответы серверов проигрываются по URL; сверяются запросы (адрес и User-Agent) и итог.</summary>
    [Fact]
    public void Downloads_match_original() =>
        GoldenAssert.All("font_downloads", input =>
        {
            var handler = new ReplayHandler(input.GetProperty("responses"));
            var downloader = new FontDownloader(new HttpClient(handler));
            var font = input.GetProperty("font").GetString()!;
            var source = downloader.Sources.Single(s => s.Name.StartsWith(input.GetProperty("source").GetString() switch
            {
                "google" => "Google",
                "dafont" => "dafont",
                _ => "1001",
            }, StringComparison.Ordinal));
            var output = new Dictionary<string, object>();
            try
            {
                output["result"] = Digests(source.DownloadAsync(font, Ct).GetAwaiter().GetResult());
            }
            catch (FontDownloadException)
            {
                output["error"] = "HTTPError";
            }

            output["requests"] = handler.Requests;
            return output;
        });

    /// <summary>Сценарии оригинала целиком: свой и системные шрифты, скачивание, архив, сохранение в свою папку.</summary>
    [Fact]
    public void Scenarios_build_the_same_fonts_zip_as_original()
    {
        var samples = GoldenFile.Load("ass_fonts_scenarios").Root.GetProperty("fixtures");
        GoldenAssert.All("ass_fonts_scenarios", input => RunScenarioAsync(input, samples).GetAwaiter().GetResult());
    }

    [Fact]
    public void Ass_files_are_found_regardless_of_extension_case()
    {
        using var dir = new TempDir();
        dir.File("b.ASS");
        dir.File("A.ass");
        dir.File("c.ssa");
        Assert.Equal(["A.ass", "b.ASS"], AssFontsCollector.ListAssFiles(dir.Path).Select(Path.GetFileName));
    }

    private static async Task<object> RunScenarioAsync(JsonElement input, JsonElement samples)
    {
        using var root = new TempDir();
        foreach (var ass in input.GetProperty("ass").EnumerateObject())
        {
            root.File(ass.Name, ass.Value.GetString()!);
        }

        foreach (var font in input.GetProperty("fonts").EnumerateObject())
        {
            var path = root.Combine(font.Name.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Sample(samples, font.Value.GetString()!));
        }

        var downloads = input.GetProperty("downloads");
        FontSource Source(string key, string name) => new(name, (font, _) =>
        {
            if (!downloads.TryGetProperty(key, out var byFont) || !byFont.TryGetProperty(font, out var reply))
            {
                return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(new Dictionary<string, byte[]>());
            }

            if (reply.ValueKind == JsonValueKind.String)
            {
                throw new FontDownloadException("http://test", null, "сервер не ответил");
            }

            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(reply.EnumerateObject().ToDictionary(p => p.Name, p => Sample(samples, p.Value.GetString()!)));
        });

        var work = root.Combine("work");
        Directory.CreateDirectory(work);
        var collector = new AssFontsCollector([Source("google", "Google Fonts"), Source("dafont", "dafont"), Source("1001fonts", "1001fonts")]);
        var result = await collector.CollectAsync(
            AssFontsCollector.ListAssFiles(work),
            new FontsCollectOptions { CustomDir = root.Combine("custom"), SystemDirs = [root.Combine("sys1"), root.Combine("sys2")] },
            Ct);

        var zip = result.ZipPath is null ? [] : ZipEntries(result.ZipPath);
        var custom = Directory.EnumerateFiles(root.Combine("custom"), "*", SearchOption.AllDirectories)
            .Select(p => new object[] { Path.GetRelativePath(root.Combine("custom"), p).Replace('\\', '/'), Sha16(File.ReadAllBytes(p)) })
            .OrderBy(p => (string)p[0], PyText.CodePointComparer)
            .ToList();
        return new Dictionary<string, object>
        {
            ["zip"] = zip,
            ["custom"] = custom,
            ["not_found"] = result.NotFound,
            ["font_names"] = result.FontNames,
        };
    }

    /// <summary>Записи архива из центрального каталога: имя, хэш содержимого, внешние атрибуты, метод сжатия.</summary>
    private static List<object[]> ZipEntries(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entries = new List<object[]>();
        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x02014b50)
            {
                continue;
            }

            var method = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 10));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
            var external = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 38));
            var name = Encoding.UTF8.GetString(bytes, i + 46, nameLength);
            using var stream = archive.GetEntry(name)!.Open();
            using var content = new MemoryStream();
            stream.CopyTo(content);
            entries.Add([name, Sha16(content.ToArray()), external, method]);
            i += 45 + nameLength;
        }

        return entries;
    }

    private static byte[] Sample(JsonElement samples, string name) => Convert.FromBase64String(samples.GetProperty(name).GetString()!);

    private static Dictionary<string, string> Digests(IReadOnlyDictionary<string, byte[]> files) => files.ToDictionary(f => f.Key, f => Sha16(f.Value));

    private static string Sha16(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data))[..16];

    private sealed class ReplayHandler(JsonElement responses) : HttpMessageHandler
    {
        public List<object?[]> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            Requests.Add([url, request.Headers.UserAgent.Count > 0 ? request.Headers.UserAgent.ToString() : null]);
            if (!responses.TryGetProperty(url, out var reply) && !responses.TryGetProperty("*", out reply))
            {
                throw new HttpRequestException("нет ответа");
            }

            if (reply.TryGetProperty("status", out var status))
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status.GetInt32()));
            }

            var body = reply.TryGetProperty("base64", out var b64) ? Convert.FromBase64String(b64.GetString()!) : Encoding.UTF8.GetBytes(reply.GetProperty("text").GetString()!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
