using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Anitools.Core.Operations.Common;

namespace Anitools.Core.Operations.Fonts;

/// <summary>Сервер ответил ошибкой или не ответил вовсе (тогда Status — null).</summary>
public sealed class FontDownloadException(string url, HttpStatusCode? status, string message)
    : Exception(message)
{
    public string Url { get; } = url;

    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Источник шрифтов: имя для журнала и загрузка «имя шрифта → файлы (имя → байты)».</summary>
public sealed record FontSource(string Name, Func<string, CancellationToken, Task<IReadOnlyDictionary<string, byte[]>>> DownloadAsync);

/// <summary>
/// Скачивание шрифтов: Google Fonts (все начертания семейства, .ttf/.otf), dafont (угаданный слаг и
/// слаги из поиска, до 4 архивов), 1001fonts. Из архивов берутся только файлы, чьё внутреннее имя совпало.
/// </summary>
public sealed partial class FontDownloader(HttpClient http)
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";

    /// <summary>Сколько архивов из выдачи dafont пробовать.</summary>
    public const int DafontMaxTries = 4;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Все веса, прямые и курсив: «100,…,900,100i,…,900i».</summary>
    public static string GoogleStyles { get; } =
        string.Join(',', new[] { "", "i" }.SelectMany(i => Enumerable.Range(1, 9).Select(w => $"{w * 100}{i}")));

    public static IReadOnlyList<string> FontExtensions { get; } = [".ttf", ".otf", ".ttc", ".otc"];

    /// <summary>Источники по порядку.</summary>
    public IReadOnlyList<FontSource> Sources => [new("Google Fonts", GoogleAsync), new("dafont", DafontAsync), new("1001fonts", Fonts1001Async)];

    /// <summary>Шрифты из архива, чьё внутреннее имя совпадает с искомым. Не архив — пусто.</summary>
    public static IReadOnlyDictionary<string, byte[]> FontsFromZip(byte[] data, string fontName)
    {
        var key = FontText.FontKey(fontName);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return new Dictionary<string, byte[]>();
        }

        var result = new Dictionary<string, byte[]>();
        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName[(Math.Max(entry.FullName.LastIndexOf('/'), entry.FullName.LastIndexOf('\\')) + 1)..];
                if (entry.FullName.EndsWith('/') || entry.FullName.Contains("__MACOSX", StringComparison.Ordinal)
                    || !FontExtensions.Contains(MediaFiles.Suffix(name).ToLowerInvariant()))
                {
                    continue;
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var font = buffer.ToArray();
                var names = SfntReader.ReadFontNames(font);
                if (names.Primary.Contains(key) || names.Fallback.Contains(key))
                {
                    result[name] = font;
                }
            }
        }

        return result;
    }

    /// <summary>Все начертания семейства с Google Fonts; семейства нет (400) — пусто.</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> GoogleAsync(string fontName, CancellationToken cancellationToken)
    {
        // Без браузерного User-Agent Google отдаёт .ttf (с браузерным — woff2)
        var url = "https://fonts.googleapis.com/css?family=" + QuotePlus(fontName) + ":" + GoogleStyles;
        string css;
        try
        {
            css = DecodeUtf8(await FetchAsync(url, null, cancellationToken).ConfigureAwait(false));
        }
        catch (FontDownloadException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            return new Dictionary<string, byte[]>();
        }

        var result = new Dictionary<string, byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in FontFaceRegex().Matches(css))
        {
            var body = block.Groups[1].Value;
            var m = UrlRegex().Match(body);
            if (!m.Success)
            {
                continue;
            }

            var fontUrl = m.Groups[1].Value;
            var ext = MediaFiles.Suffix(LastSegment(UrlPath(fontUrl))).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf") || !seen.Add(fontUrl))
            {
                continue;
            }

            var weight = WeightRegex().Match(body);
            var italic = ItalicRegex().IsMatch(body);
            var fileName = $"{FontText.SafeFileName(fontName)}-{(weight.Success ? weight.Groups[1].Value : "400")}{(italic ? "Italic" : "")}{ext}";
            result[fileName] = await FetchAsync(fontUrl, null, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>dafont: сначала угаданный слаг («Komika Axis» → komika_axis), потом слаги из поиска; до 4 архивов.</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> DafontAsync(string fontName, CancellationToken cancellationToken)
    {
        var html = DecodeUtf8(await FetchAsync("https://www.dafont.com/search.php?q=" + QuotePlus(fontName), BrowserUserAgent, cancellationToken).ConfigureAwait(false));
        var slugs = new List<string> { FontText.Slugify(fontName, '_') };
        slugs.AddRange(DafontSlugRegex().Matches(html).Select(m => m.Groups[1].Value));
        foreach (var slug in slugs.Distinct().Take(DafontMaxTries))
        {
            // Несуществующий слаг — пустой ответ, в нём шрифтов нет
            var found = FontsFromZip(await FetchAsync($"https://dl.dafont.com/dl/?f={slug}", BrowserUserAgent, cancellationToken).ConfigureAwait(false), fontName);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return new Dictionary<string, byte[]>();
    }

    /// <summary>1001fonts: архив по слагу через дефис; нет (404) — пусто.</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> Fonts1001Async(string fontName, CancellationToken cancellationToken)
    {
        byte[] data;
        try
        {
            data = await FetchAsync($"https://www.1001fonts.com/download/{FontText.Slugify(fontName, '-')}.zip", BrowserUserAgent, cancellationToken).ConfigureAwait(false);
        }
        catch (FontDownloadException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return new Dictionary<string, byte[]>();
        }

        return FontsFromZip(data, fontName);
    }

    /// <summary>Кодирование для строки запроса: пробел → «+», всё, кроме латиницы, цифр и «_.-~», — %XX в UTF-8.</summary>
    public static string QuotePlus(string text)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b == ' ')
            {
                result.Append('+');
            }
            else if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
            {
                result.Append((char)b);
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return result.ToString();
    }

    private async Task<byte[]> FetchAsync(string url, string? userAgent, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new FontDownloadException(url, response.StatusCode, $"HTTP {(int)response.StatusCode} {url}");
            }

            return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FontDownloadException(url, null, $"Сервер не ответил за {Timeout.TotalSeconds:0} с: {url}");
        }
        catch (HttpRequestException ex)
        {
            throw new FontDownloadException(url, null, ex.Message);
        }
    }

    private static string DecodeUtf8(byte[] data) => new UTF8Encoding(false).GetString(data);

    /// <summary>Путь из адреса: без схемы, хоста, запроса и якоря.</summary>
    private static string UrlPath(string url)
    {
        var rest = url;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            rest = rest[(scheme + 3)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            rest = slash >= 0 ? rest[slash..] : "";
        }

        var cut = rest.IndexOfAny(['?', '#']);
        return cut >= 0 ? rest[..cut] : rest;
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    [GeneratedRegex(@"@font-face\s*\{([^}]*)\}")]
    private static partial Regex FontFaceRegex();

    [GeneratedRegex(@"url\(\s*['""]?([^)'""]+)")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"font-weight:\s*(\d+)")]
    private static partial Regex WeightRegex();

    [GeneratedRegex(@"font-style:\s*italic")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"dl\.dafont\.com/dl/\?f=([a-z0-9_]+)")]
    private static partial Regex DafontSlugRegex();
}
