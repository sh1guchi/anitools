using System.Net;
using System.Text;
using System.Text.Json;
using Anitools.Core.Shikimori;

namespace Anitools.Core.Tests.Shikimori;

public sealed class ShikimoriTests
{
    [Fact]
    public void Search_parses_like_original_and_requests_same_path() =>
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
    public void Ranking_matches_original() =>
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
    public void Kind_names_match_original()
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
