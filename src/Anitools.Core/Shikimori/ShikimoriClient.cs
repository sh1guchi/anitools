using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Anitools.Core.Shikimori;

/// <summary>
/// API Shikimori (py:443–655): поиск тайтла и оригинальное название по ID. На 429 (лимит 5 запросов/с) и сбои
/// сети — до 3 попыток с паузой (429 — 1.5 с × номер попытки, остальное — 1 с); 404 — «нет такого».
/// </summary>
public sealed partial class ShikimoriClient
{
    public static readonly Uri DefaultBaseUri = new("https://shikimori.io/api/");

    /// <summary>Подробный поиск (GraphQL): всё для выбора тайтла одним запросом, по популярности.</summary>
    private const string DetailedSearchQuery = """
        query($search: String, $limit: PositiveInt) {
          animes(search: $search, limit: $limit, order: popularity) {
            id name russian english kind status episodes episodesAired airedOn { year } score duration
            poster { mainUrl } genres { russian } studios { name } description
          }
        }
        """;

    /// <summary>Сколько тайтлов берёт подробный поиск (больше Shikimori не отдаёт).</summary>
    public const int MaxLimit = 50;

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Пауза между попытками (в тестах — без реального ожидания).</param>
    public ShikimoriClient(HttpClient http, Uri? baseUri = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
        _delay = delay ?? Task.Delay;
    }

    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(8);

    public const int Tries = 3;

    /// <summary>Сайт для ссылок «открыть на Shikimori».</summary>
    public Uri Site => new(_baseUri.GetLeftPart(UriPartial.Authority));

    /// <summary>Поиск: animes?search=…&amp;limit=15&amp;order=popularity → список тайтлов (пусто — ничего или сбой).</summary>
    public async Task<IReadOnlyList<ShikimoriAnime>> SearchAsync(string query, int limit = 15, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"animes?search={Quote(query)}&limit={limit}&order=popularity", ct).ConfigureAwait(false);
        return doc is null ? [] : ParseSearch(doc.RootElement, Site);
    }

    /// <summary>
    /// Поиск с запасными запросами (_search_shikimori_smart): как есть → без знаков препинания → первые три слова;
    /// первый непустой результат.
    /// </summary>
    public async Task<IReadOnlyList<ShikimoriAnime>> SmartSearchAsync(string query, CancellationToken ct = default)
    {
        foreach (var q in ShikimoriQuery.SearchVariants(query))
        {
            var found = await SearchAsync(q, ct: ct).ConfigureAwait(false);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    /// <summary>
    /// Подробный поиск для выбора тайтла: до 50 тайтлов по популярности с постером, описанием, жанрами, студией,
    /// оценкой и статусом (GraphQL, один запрос). GraphQL не ответил — обычный поиск (REST) на те же 50.
    /// </summary>
    public async Task<IReadOnlyList<ShikimoriAnime>> SearchDetailedAsync(string query, int limit = MaxLimit, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["query"] = DetailedSearchQuery,
            ["variables"] = new JsonObject { ["search"] = query, ["limit"] = limit },
        }.ToJsonString();
        using var doc = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "graphql")) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            ct).ConfigureAwait(false);
        if (doc is not null && ParseDetailed(doc.RootElement) is { } found)
        {
            return found;
        }

        return await SearchAsync(query, limit, ct).ConfigureAwait(false);
    }

    /// <summary>Подробный поиск с теми же запасными запросами, что у <see cref="SmartSearchAsync"/>.</summary>
    public async Task<IReadOnlyList<ShikimoriAnime>> SmartSearchDetailedAsync(string query, CancellationToken ct = default)
    {
        foreach (var q in ShikimoriQuery.SearchVariants(query))
        {
            var found = await SearchDetailedAsync(q, ct: ct).ConfigureAwait(false);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    /// <summary>Картинка (постер) по адресу; null — не скачалась.</summary>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Site, url));
            request.Headers.TryAddWithoutValidation("User-Agent", "anitools/1.0");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException or UriFormatException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Ответ подробного поиска: {"data":{"animes":[…]}}; другое (ошибки GraphQL) — null.</summary>
    public static IReadOnlyList<ShikimoriAnime>? ParseDetailed(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("animes", out var animes) || animes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<ShikimoriAnime>();
        foreach (var r in animes.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || Id(r) is not { } id)
            {
                continue;
            }

            var name = Text(r, "name");
            // у анонсов airedOn есть, а year — null
            var year = r.TryGetProperty("airedOn", out var aired) && aired.ValueKind == JsonValueKind.Object && Int(aired, "year") is > 0 and var yearNumber
                ? yearNumber.ToString(CultureInfo.InvariantCulture)
                : "????";
            list.Add(new ShikimoriAnime(id, name, Text(r, "russian") is { Length: > 0 } ru ? ru : name, year, Text(r, "kind"), Int(r, "episodes"))
            {
                English = NullIfEmpty(Text(r, "english")),
                Status = NullIfEmpty(Text(r, "status")),
                Score = r.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.GetDouble() > 0 ? score.GetDouble() : null,
                EpisodesAired = Int(r, "episodesAired"),
                Duration = Int(r, "duration") is > 0 and var minutes ? minutes : null,
                PosterUrl = r.TryGetProperty("poster", out var poster) && poster.ValueKind == JsonValueKind.Object ? NullIfEmpty(Text(poster, "mainUrl")) : null,
                Genres = Names(r, "genres", "russian"),
                Studios = Names(r, "studios", "name"),
                Description = CleanDescription(Text(r, "description")),
            });
        }

        return list;
    }

    /// <summary>
    /// Описание без разметки: «[character=1]Фрирен[/character]» → «Фрирен», «[[Хокагэ]]» → «Хокагэ», [br] — перенос строки.
    /// Скобки с заглавной буквы («[Kushina Uzumaki]») — это текст, остаются.
    /// </summary>
    public static string? CleanDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plain = WikiLinkRegex().Replace(text.Replace("[br]", "\n", StringComparison.Ordinal), "$1");
        plain = BbCodeRegex().Replace(plain, "");
        plain = ManyNewlinesRegex().Replace(plain.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n");
        return plain.Trim() is { Length: > 0 } result ? result : null;
    }

    /// <summary>Оригинальное (ромадзи) название по ID; null — нет такого или сбой.</summary>
    public async Task<string?> OriginalNameAsync(string id, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"animes/{id}", ct).ConfigureAwait(false);
        return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString() is { Length: > 0 } text
            ? text
            : null;
    }

    /// <summary>Разбор ответа поиска: записи без id пропускаются; не список — пусто.</summary>
    /// <param name="site">Сайт для адреса постера (в ответе — путь); null — без постера.</param>
    public static IReadOnlyList<ShikimoriAnime> ParseSearch(JsonElement data, Uri? site = null)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ShikimoriAnime>();
        foreach (var r in data.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var idElement)
                || !idElement.TryGetInt64(out var id) || id == 0)
            {
                continue;
            }

            var name = Text(r, "name");
            var russian = Text(r, "russian") is { Length: > 0 } ru ? ru : name;
            var aired = Text(r, "aired_on") is { Length: > 0 } a ? a : "????";
            var episodes = r.TryGetProperty("episodes", out var ep) && ep.ValueKind == JsonValueKind.Number && ep.TryGetInt32(out var n) ? n : 0;
            var poster = r.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? Text(image, "original") : "";
            list.Add(new ShikimoriAnime(id, name, russian, aired.Length > 4 ? aired[..4] : aired, Text(r, "kind"), episodes)
            {
                Status = NullIfEmpty(Text(r, "status")),
                Score = double.TryParse(Text(r, "score"), NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && score > 0 ? score : null,
                PosterUrl = site is not null && poster.Length > 0 && !poster.Contains("/missing", StringComparison.Ordinal) ? new Uri(site, poster).ToString() : null,
            });
        }

        return list;
    }

    /// <summary>urllib.parse.quote: всё, кроме букв, цифр, «_.-~» и «/», — %XX в UTF-8.</summary>
    public static string Quote(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-' or '~' or '/')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private Task<JsonDocument?> GetAsync(string apiPath, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, apiPath)), ct);

    /// <summary>Запрос к API с повторами (429 и сбои сети); 404 — null сразу.</summary>
    private async Task<JsonDocument?> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            var status = 0;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                using var request = create();
                request.Headers.TryAddWithoutValidation("User-Agent", "anitools/1.0");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    return JsonDocument.Parse(body);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // таймаут запроса — пробуем ещё
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }

            if (attempt < Tries)
            {
                await _delay(status == 429 ? TimeSpan.FromSeconds(1.5 * attempt) : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static string Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>ID тайтла: в REST — число, в GraphQL — строка; 0 и мусор — null.</summary>
    private static long? Id(JsonElement obj)
    {
        long id = 0;
        var ok = obj.TryGetProperty("id", out var v) && v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out id),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id),
            _ => false,
        };
        return ok && id != 0 ? id : null;
    }

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static string? NullIfEmpty(string text) => text.Length > 0 ? text : null;

    /// <summary>Имена из списка объектов: genres[].russian, studios[].name.</summary>
    private static IReadOnlyList<string> Names(JsonElement obj, string list, string field) =>
        obj.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => Text(i, field)).Where(t => t.Length > 0)]
            : [];

    [GeneratedRegex(@"\[/?[a-z_]+(?:=[^\]]*)?\]", RegexOptions.CultureInvariant)]
    private static partial Regex BbCodeRegex();

    // [[Хокагэ]], [[Химмель|Химмеля]] → текст после «|»
    [GeneratedRegex(@"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ManyNewlinesRegex();
}
