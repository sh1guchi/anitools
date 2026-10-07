using System.Net;
using System.Text;
using System.Text.Json;

namespace Anitools.Core.Shikimori;

/// <summary>
/// API Shikimori (py:443–655): поиск тайтла и оригинальное название по ID. На 429 (лимит 5 запросов/с) и сбои
/// сети — до 3 попыток с паузой (429 — 1.5 с × номер попытки, остальное — 1 с); 404 — «нет такого».
/// </summary>
public sealed class ShikimoriClient
{
    public static readonly Uri DefaultBaseUri = new("https://shikimori.io/api/");

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
        return doc is null ? [] : ParseSearch(doc.RootElement);
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
    public static IReadOnlyList<ShikimoriAnime> ParseSearch(JsonElement data)
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
            list.Add(new ShikimoriAnime(id, name, russian, aired.Length > 4 ? aired[..4] : aired, Text(r, "kind"), episodes));
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

    private async Task<JsonDocument?> GetAsync(string apiPath, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            var status = 0;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, apiPath));
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
}
