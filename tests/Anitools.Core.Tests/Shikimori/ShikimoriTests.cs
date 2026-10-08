using System.Net;
using System.Text;
using System.Text.Json;
using Anitools.Core.Shikimori;

namespace Anitools.Core.Tests.Shikimori;

public sealed class ShikimoriTests
{
    [Fact]
    public void Search_parses_like_golden_and_requests_same_path() =>
        GoldenAssert.All("shikimori_search", input =>
        {
            var api = FakeApi.Returning(Fixture("shikimori_search", input.GetProperty("response").GetString()!));
            var found = api.Client.SearchAsync(input.GetProperty("query").GetString()!, ct: TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.Equal(Expected(input, "shikimori_search"), api.Paths);
            return found.Select(a => new Dictionary<string, object>
            {
                ["id"] = a.Id, ["name"] = a.Name, ["russian"] = a.Russian, ["year"] = a.Year, ["kind"] = a.Kind, ["episodes"] = a.Episodes,
            }).ToList();
        });

    [Fact]
    public void Smart_search_tries_the_same_fallback_queries() =>
        GoldenAssert.All("shikimori_search_variants", input =>
        {
            var api = FakeApi.Returning("[]");
            var found = api.Client.SmartSearchAsync(input.GetString()!, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.Equal(Expected(input, "shikimori_search_variants"), api.Paths);
            return found;
        });

    [Fact]
    public void Ranking_matches_golden() =>
        GoldenAssert.All("shikimori_rank", input =>
        {
            var query = ShikimoriQuery.FromTitle(input.GetProperty("title").GetString()!);
            var api = FakeApi.Returning(Fixture("shikimori_rank", input.GetProperty("response").GetString()!));
            var results = api.Client.SearchAsync(query.SearchText, ct: TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return new Dictionary<string, object>
            {
                ["kinds"] = query.Kinds,
                ["base_title"] = query.BaseTitle,
                ["season"] = query.Season,
                ["query"] = query.SearchText,
                ["ranked_ids"] = query.Rank(results).Take(8).Select(a => a.Id).ToList(),
            };
        });

    [Fact]
    public void Original_name_by_id() =>
        GoldenAssert.All("shikimori_original_name", input =>
        {
            var response = input.GetProperty("response");
            var api = response.ValueKind == JsonValueKind.Null ? FakeApi.Status(HttpStatusCode.NotFound) : FakeApi.Returning(response.GetRawText());
            return api.Client.OriginalNameAsync(input.GetProperty("id").GetString()!, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        });

    [Fact]
    public void Kind_names_match_golden()
    {
        var expected = Parsing.AnitomyTests.Constant("_SHIKI_KIND_RU").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(expected, ShikimoriAnime.KindNames);
        var kinds = Parsing.AnitomyTests.Constant("_SHIKI_KINDS").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToList());
        Assert.Equal(kinds.Keys, ShikimoriQuery.KindsByLabel.Keys);
        Assert.All(kinds, k => Assert.Equal(k.Value, ShikimoriQuery.KindsByLabel[k.Key]));
    }

    [Fact]
    public async Task Too_many_requests_waits_longer_each_time_then_succeeds()
    {
        var api = new FakeApi((_, n) => n < 2 ? Response(HttpStatusCode.TooManyRequests, "") : Response(HttpStatusCode.OK, """[{"id": 1, "name": "X"}]"""));
        var found = await api.Client.SearchAsync("x", ct: TestContext.Current.CancellationToken);
        Assert.Single(found);
        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3)], api.Delays);
    }

    [Fact]
    public async Task Server_errors_retry_three_times_then_give_up()
    {
        var api = FakeApi.Status(HttpStatusCode.InternalServerError);
        Assert.Empty(await api.Client.SearchAsync("x", ct: TestContext.Current.CancellationToken));
        Assert.Equal(3, api.Paths.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], api.Delays);
    }

    [Fact]
    public async Task Not_found_is_final_and_network_failure_is_retried()
    {
        var notFound = FakeApi.Status(HttpStatusCode.NotFound);
        Assert.Null(await notFound.Client.OriginalNameAsync("1", TestContext.Current.CancellationToken));
        Assert.Single(notFound.Paths);

        var flaky = new FakeApi((_, n) => n == 0 ? throw new HttpRequestException("сеть") : Response(HttpStatusCode.OK, """{"name": "Overlord"}"""));
        Assert.Equal("Overlord", await flaky.Client.OriginalNameAsync("29803", TestContext.Current.CancellationToken));
        Assert.Equal(["animes/29803", "animes/29803"], flaky.Paths);
    }

    [Fact]
    public async Task Request_timeout_is_retried()
    {
        // HttpClient при таймауте бросает TaskCanceledException — это не отмена пользователем, а повод повторить
        var api = new FakeApi((_, n) => n == 0 ? throw new TaskCanceledException("таймаут") : Response(HttpStatusCode.OK, """[{"id": 7, "name": "Y"}]"""));
        var found = await api.Client.SearchAsync("y", ct: TestContext.Current.CancellationToken);
        Assert.Equal(7, Assert.Single(found).Id);
        Assert.Equal([TimeSpan.FromSeconds(1)], api.Delays);
    }

    [Fact]
    public async Task Sends_user_agent_and_accept_headers()
    {
        HttpRequestMessage? seen = null;
        var api = new FakeApi((request, _) => { seen = request; return Response(HttpStatusCode.OK, "[]"); });
        await api.Client.SearchAsync("Re:Zero", ct: TestContext.Current.CancellationToken);
        Assert.Equal("anitools/1.0", seen!.Headers.UserAgent.ToString());
        Assert.Equal("application/json", seen.Headers.Accept.ToString());
        Assert.Equal("https://shikimori.io/api/animes?search=Re%3AZero&limit=15&order=popularity", seen.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Detailed_search_asks_graphql_for_everything_the_picker_shows()
    {
        const string answer = """
            {"data":{"animes":[
              {"id":"52991","name":"Sousou no Frieren","russian":"Провожающая в последний путь Фрирен","english":"Frieren: Beyond Journey's End",
               "kind":"tv","status":"released","episodes":28,"episodesAired":28,"airedOn":{"year":2023},"score":9.29,"duration":24,
               "poster":{"mainUrl":"https://shikimori.io/uploads/poster/animes/52991/main-1.webp"},
               "genres":[{"russian":"Приключения"},{"russian":"Драма"}],"studios":[{"name":"Madhouse"}],
               "description":"Путь [character=184947]Фрирен[/character] и [[Химмель|Химмеля]].[br][br][br]Смотри [anime=59978]второй сезон[/anime]."},
              {"id":"59978","name":"Sousou no Frieren 2nd Season","russian":"","english":null,"kind":"tv","status":"ongoing",
               "episodes":0,"episodesAired":5,"airedOn":null,"score":0,"duration":null,"poster":null,"genres":[],"studios":[],"description":null},
              {"id":"60000","name":"Anons","russian":"Анонс","kind":"tv","status":"anons","episodes":null,"episodesAired":null,
               "airedOn":{"year":null},"score":null,"duration":null,"poster":{"mainUrl":null},"genres":null,"studios":null,"description":""},
              {"id":"0","name":"мусор"}
            ]}}
            """;
        string? body = null;
        HttpMethod? method = null;
        var api = new FakeApi((request, _) =>
        {
            method = request.Method;
            body = request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return Response(HttpStatusCode.OK, answer);
        });

        var found = await api.Client.SearchDetailedAsync("Frieren", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["graphql"], api.Paths);
        Assert.Equal(HttpMethod.Post, method);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("Frieren", sent.RootElement.GetProperty("variables").GetProperty("search").GetString());
        Assert.Equal(50, sent.RootElement.GetProperty("variables").GetProperty("limit").GetInt32());
        Assert.Contains("order: popularity", sent.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);

        Assert.Equal([52991L, 59978L, 60000L], found.Select(a => a.Id));
        var frieren = found[0];
        Assert.Equal(("Sousou no Frieren", "Провожающая в последний путь Фрирен", "2023", "tv", 28), (frieren.Name, frieren.Russian, frieren.Year, frieren.Kind, frieren.Episodes));
        Assert.Equal("Frieren: Beyond Journey's End", frieren.English);
        Assert.Equal(("released", "вышло", 9.29, 24), (frieren.Status, frieren.StatusName, frieren.Score, frieren.Duration));
        Assert.Equal("https://shikimori.io/uploads/poster/animes/52991/main-1.webp", frieren.PosterUrl);
        Assert.Equal(["Приключения", "Драма"], frieren.Genres);
        Assert.Equal(["Madhouse"], frieren.Studios);
        Assert.Equal("Путь Фрирен и Химмеля.\n\nСмотри второй сезон.", frieren.Description);

        // пустое русское название — ромадзи, нет года — «????», оценка 0 — нет оценки
        var second = found[1];
        Assert.Equal(("Sousou no Frieren 2nd Season", "????", 5, "выходит"), (second.Russian, second.Year, second.EpisodesAired, second.StatusName));
        Assert.Null(second.Score);
        Assert.Null(second.Duration);
        Assert.Null(second.PosterUrl);
        Assert.Null(second.Description);
        Assert.Empty(second.Genres);

        // у анонса почти всё null
        var anons = found[2];
        Assert.Equal(("????", 0, "анонс"), (anons.Year, anons.Episodes, anons.StatusName));
        Assert.Null(anons.PosterUrl);
        Assert.Empty(anons.Studios);
    }

    [Fact]
    public async Task Detailed_search_falls_back_to_rest_when_graphql_fails()
    {
        const string rest = """
            [{"id":52991,"name":"Sousou no Frieren","russian":"Фрирен","kind":"tv","episodes":28,"aired_on":"2023-09-29","status":"released","score":"9.29",
              "image":{"original":"/system/animes/original/52991.jpg?1700000000","preview":"/system/animes/preview/52991.jpg"}},
             {"id":7,"name":"Без постера","kind":"tv","image":{"original":"/assets/globals/missing_original.jpg"}}]
            """;
        var api = new FakeApi((request, _) => request.Method == HttpMethod.Post
            ? Response(HttpStatusCode.OK, """{"errors":[{"message":"Field 'x' doesn't exist"}]}""")
            : Response(HttpStatusCode.OK, rest));

        var found = await api.Client.SearchDetailedAsync("Frieren", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["graphql", "animes?search=Frieren&limit=50&order=popularity"], api.Paths);
        Assert.Equal([52991L, 7L], found.Select(a => a.Id));
        Assert.Equal("https://shikimori.io/system/animes/original/52991.jpg?1700000000", found[0].PosterUrl);
        Assert.Equal((9.29, "вышло"), (found[0].Score, found[0].StatusName));
        Assert.Null(found[1].PosterUrl);
    }

    [Fact]
    public async Task Smart_detailed_search_tries_fallback_queries_until_something_is_found()
    {
        var api = new FakeApi((request, n) => Response(HttpStatusCode.OK, n < 2 ? """{"data":{"animes":[]}}""" : """{"data":{"animes":[{"id":"1","name":"X"}]}}"""));
        var found = await api.Client.SmartSearchDetailedAsync("Re:Zero kara Hajimeru Isekai Seikatsu", TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(found).Id);
        Assert.Equal(3, api.Paths.Count);
        Assert.All(api.Paths, p => Assert.Equal("graphql", p));
    }

    [Theory]
    [InlineData("[i]Курсив[/i] и [url=https://example.com]ссылка[/url]", "Курсив и ссылка")]
    [InlineData("Кусина Узумаки [Kushina Uzumaki] и [person=1904]автор[/person]", "Кусина Узумаки [Kushina Uzumaki] и автор")]
    [InlineData("Стал [[Хокагэ]]", "Стал Хокагэ")]
    [InlineData("  [br]  ", null)]
    [InlineData("", null)]
    public void Description_loses_shikimori_markup(string text, string? expected) =>
        Assert.Equal(expected, ShikimoriClient.CleanDescription(text));

    [Fact]
    public async Task Poster_download_failure_is_just_no_poster()
    {
        var api = FakeApi.Status(HttpStatusCode.NotFound);
        Assert.Null(await api.Client.DownloadAsync("https://shikimori.io/uploads/poster/animes/1/main.webp", TestContext.Current.CancellationToken));
        var broken = new FakeApi((_, _) => throw new HttpRequestException("сеть"));
        Assert.Null(await broken.Client.DownloadAsync("/system/animes/original/1.jpg", TestContext.Current.CancellationToken));
        var ok = new FakeApi((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        Assert.Equal([1, 2, 3], await ok.Client.DownloadAsync("/system/animes/original/1.jpg", TestContext.Current.CancellationToken));
    }

    private static string Fixture(string golden, string name) =>
        GoldenFile.Load(golden).Root.GetProperty("fixtures").GetProperty(name).GetRawText();

    /// <summary>api_paths из случая эталона с этим входом.</summary>
    private static List<string> Expected(JsonElement input, string golden) =>
        GoldenFile.Load(golden).Cases.Single(c => c.Input.GetRawText() == input.GetRawText())
            .Raw.GetProperty("api_paths").EnumerateArray().Select(p => p.GetString()!).ToList();

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>API на фейковом HttpMessageHandler: пути запросов (после /api/) и паузы между попытками.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;

        public FakeApi(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        {
            _respond = respond;
            Client = new ShikimoriClient(new HttpClient(this), delay: (d, _) => { Delays.Add(d); return Task.CompletedTask; });
        }

        public ShikimoriClient Client { get; }

        public List<string> Paths { get; } = [];

        public List<TimeSpan> Delays { get; } = [];

        public static FakeApi Returning(string json) => new((_, _) => Response(HttpStatusCode.OK, json));

        public static FakeApi Status(HttpStatusCode status) => new((_, _) => Response(status, ""));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery["/api/".Length..]);
            return Task.FromResult(_respond(request, Paths.Count - 1));
        }
    }
}
