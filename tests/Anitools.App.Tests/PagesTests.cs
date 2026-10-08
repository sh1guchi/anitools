using System.Net;
using System.Text;
using Anitools.App.ViewModels;
using Anitools.App.ViewModels.Pages;
using Anitools.Core.Jobs;
using Anitools.Core.Operations.Rename;
using Anitools.Core.Shikimori;
using Avalonia.Headless.XUnit;

namespace Anitools.App.Tests;

/// <summary>Страницы п.2–п.5 на фейковых ffprobe/mkvmerge: что показывают и какие команды строят.</summary>
public sealed class PagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Три дорожки, как у типичного релиза: две русские озвучки и оригинал.</summary>
    internal const string ThreeVoices = """
        {"streams":[
          {"index":0,"codec_type":"video","codec_name":"h264"},
          {"index":1,"codec_type":"audio","codec_name":"aac","profile":"LC","channels":2,"tags":{"title":"AniLibria.TV","language":"rus"}},
          {"index":2,"codec_type":"audio","codec_name":"flac","channels":2,"tags":{"title":"Оригинальная","language":"jpn"}},
          {"index":3,"codec_type":"audio","codec_name":"ac3","channels":6,"tags":{"title":"DEEP","language":"rus"}}
        ],"format":{"duration":"1420.0"}}
        """;

    [AvaloniaFact]
    public async Task Audio_extract_separate_and_single_mka()
    {
        using var app = new AppFixture().WithFiles("Frieren - 01.mkv", "Frieren - 02.mkv");
        app.Runner.FfprobeJson = _ => ThreeVoices;
        var vm = app.CreateViewModel();
        var page = vm.AudioExtractPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Tracks.Count == 3 && page.Preview.Rows.Count == 2, "дорожки и план");

        Assert.Equal(["AniLibria.TV", "Оригинальная", "DEEP"], page.Tracks.Select(t => t.Title));
        Assert.Equal(["2.0", "2.0", "5.1"], page.Tracks.Select(t => t.Channels));
        Assert.Equal("→ " + Path.Combine("Audio only", "Frieren - 01.mka"), page.Preview.Rows[0].Target);
        Assert.False(page.HasSeveralSelected);

        page.Tracks[2].IsSelected = true;
        Assert.True(page.ShowsNumbering);
        Assert.Equal(4, page.Preview.Rows.Count);
        Assert.Equal("→ " + Path.Combine("Audio only", "3. DEEP", "3. Frieren - 01.DEEP.mka"), page.Preview.Rows[1].Target);
        page.NumberTracks = false;
        Assert.Equal("→ " + Path.Combine("Audio only", "DEEP", "Frieren - 01.DEEP.mka"), page.Preview.Rows[1].Target);

        page.IsSingleMka = true;
        page.Tracks[1].IsSelected = true;
        Assert.True(page.ShowsOutputTracks);
        Assert.Equal(["AniLibria.TV", "DEEP", "Оригинальная"], page.OutputTracks.Select(r => r.Title));
        Assert.Equal(["rus", "rus", "jpn"], page.OutputTracks.Select(r => r.Language));
        page.MoveUpCommand.Execute(page.OutputTracks[2]);
        page.OutputTracks[0].Title = "AniLiberty (AniLibria)";
        page.OutputTracks[2].Language = "";

        Assert.Equal([1, 2, 3], page.OutputTracks.Select(r => r.Position));
        var args = page.Preview.Plan!.Items[0].Command!.Arguments;
        Assert.Equal(["0:a:0", "0:a:1", "0:a:2"], args.Where((_, i) => i > 0 && args[i - 1] == "-map"));
        Assert.Contains("title=AniLiberty (AniLibria)", args);
        Assert.Contains("title=Оригинальная", args);
        Assert.Equal(["language=rus", "language=jpn"], args.Where(a => a.StartsWith("language=", StringComparison.Ordinal)));
        Assert.Equal("→ " + Path.Combine("Audio only", "Frieren - 01.mka"), page.Preview.Rows[0].Target);
    }

    [AvaloniaFact]
    public async Task Audio_mux_sets_differ_by_external_files()
    {
        using var app = new AppFixture().WithFiles(
            "Frieren - 01.mkv",
            "Frieren - 02.mkv",
            "Audio only/2. DEEP/2. Frieren - 01.DEEP.mka");
        app.Runner.FfprobeJson = path => path.EndsWith(".mka", StringComparison.Ordinal)
            ? """{"streams":[{"index":0,"codec_type":"audio","codec_name":"ac3","channels":6,"tags":{"title":"DEEP"}}],"format":{}}"""
            : """{"streams":[{"index":0,"codec_type":"video"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"title":"Japanese","language":"jpn"}}],"format":{}}""";
        var vm = app.CreateViewModel();
        var page = vm.AudioMuxPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Sets.Count == 2 && page.Preview.Rows.Count == 2, "наборы");

        var (first, second) = (page.Sets[0], page.Sets[1]);
        Assert.Equal("Набор 1 — 1 серия (01)", first.Header);
        Assert.Equal("Набор 2 — 1 серия (02)", second.Header);
        Assert.StartsWith("нет: Внешний файл: ", second.Difference);
        Assert.Equal(["Japanese", "DEEP"], first.Slots.Select(s => s.Title));
        Assert.Equal(["jpn", "rus"], first.Slots.Select(s => s.Language));
        Assert.True(first.Slots[0].IsDefault);

        first.MoveDownCommand.Execute(first.Slots[0]);
        var args = page.Preview.Plan!.Items[0].Command!.Arguments;
        Assert.Equal(["0:v:0?", "1:a:0", "0:a:0"], args.Where((_, i) => i > 0 && args[i - 1] == "-map"));
        Assert.Contains(Path.Combine(app.Folder, "Audio only", "2. DEEP", "2. Frieren - 01.DEEP.mka"), args);
        Assert.Equal("default", args[args.ToList().IndexOf("-disposition:a:0") + 1]);

        // без внешних — один набор
        page.UseExternal = false;
        await AppFixture.WaitUntilAsync(() => page.Sets.Count == 1, "один набор");
        Assert.Equal("Набор 1 — 2 серии (01–02)", page.Sets[0].Header);
    }

    [AvaloniaFact]
    public async Task Subtitles_by_title_find_the_track_in_each_episode()
    {
        using var app = new AppFixture().WithFiles("Frieren - 01.mkv", "Frieren - 02.mkv");
        app.Runner.MkvmergeJson = path => path.EndsWith("01.mkv", StringComparison.Ordinal)
            ? Mkv(("Надписи", 2), ("Полные", 3))
            : Mkv(("Полные", 2), ("Надписи", 3));
        var vm = app.CreateViewModel();
        var page = vm.SubtitlesPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Tracks.Count == 2 && page.Preview.Rows.Count == 4, "дорожки");

        Assert.Equal("S_TEXT/ASS → .ass", page.Tracks[0].Codec);
        // по тайтлам: «Надписи» — в надписи, «Полные» — в сабы; шаги — по сериям, обе папки за один запуск
        Assert.Equal([SubtitleRole.Signs, SubtitleRole.Subs], page.Tracks.Select(t => t.Role));
        Assert.Equal(
            ["Frieren - 01.mkv · надписи", "Frieren - 01.mkv · сабы", "Frieren - 02.mkv · надписи", "Frieren - 02.mkv · сабы"],
            page.Preview.Rows.Select(r => r.Label));
        Assert.Equal("2:", TrackOf(page, 2)[..2]); // по ID: во второй серии ID 2 — уже «Полные»

        page.IsByTitle = true;
        await AppFixture.WaitUntilAsync(() => TrackOf(page, 2).StartsWith("3:", StringComparison.Ordinal), "по тайтлу");
        Assert.Equal("→ " + Path.Combine("надписи", "Frieren - 02.надписи.ass"), page.Preview.Rows[2].Target);
        Assert.Equal("2:", TrackOf(page, 3)[..2]);
        Assert.Contains(".сабы.ass", page.Preview.Rows[3].Target, StringComparison.Ordinal);

        // у папки одна дорожка: «Полные» в надписи — прежняя дорожка надписей освобождается, сабов нет
        page.Tracks[1].IsSigns = true;
        Assert.Equal([SubtitleRole.None, SubtitleRole.Signs], page.Tracks.Select(t => t.Role));
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 2, "только надписи");
        Assert.All(page.Preview.Rows, r => Assert.Contains(".надписи.ass", r.Target, StringComparison.Ordinal));
        page.Tracks[1].IsNone = true;
        Assert.Equal("Выберите дорожку для надписей или для сабов.", page.Message);
        // mkvmerge -J по каждой серии — один раз, дальше из кэша
        Assert.Equal(2, app.Runner.Calls.Count(c => c.Arguments.Contains("-J")));
    }

    [AvaloniaFact]
    public async Task Rename_with_manual_number_shikimori_and_undo()
    {
        using var app = new AppFixture().WithFiles(
            "[SubsPlease] Sousou no Frieren - 01 (1080p).mkv",
            "[SubsPlease] Sousou no Frieren - 02 (1080p).mkv",
            "weird.mkv");
        var vm = app.CreateViewModel();
        var page = vm.RenamePage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Rows.Count == 3 && page.RenameCount == 2, "строки");

        Assert.Equal("Sousou no Frieren", page.BaseName);
        Assert.Equal("Sousou no Frieren - 01.mkv", page.Rows[0].NewName);
        Assert.Equal(RenameRowStatus.NoNumber, page.Rows[2].Status);

        page.Rows[2].Episode = "3";
        Assert.True(page.Rows[2].IsManual);
        Assert.Equal("Sousou no Frieren - 03.mkv", page.Rows[2].NewName);
        page.NumberingStart = "2";
        Assert.Equal("Sousou no Frieren - 01.mkv", page.Rows[1].NewName);
        Assert.Equal(RenameRowStatus.NoNumber, page.Rows[0].Status);
        Assert.Equal("Sousou no Frieren - 03.mkv", page.Rows[2].NewName);
        page.NumberingStart = "1";

        app.Dialogs.NextShikimori = new ShikimoriChoice(new ShikimoriAnime(52991, "Sousou no Frieren: Season 2", "Провожающая в последний путь Фрирен 2", "2026", "tv", 12), null);
        await page.SearchShikimoriCommand.ExecuteAsync(null);
        Assert.Equal("Sousou no Frieren", app.Dialogs.LastShikimori?.Title);
        Assert.Equal("Sousou no Frieren - Season 2", page.BaseName);

        await page.RenameCommand.ExecuteAsync(null);
        await AppFixture.WaitUntilAsync(() => page.Rows.Count == 3 && page.Rows.All(r => r.Status == RenameRowStatus.Unchanged), "после переименования");
        Assert.True(File.Exists(Path.Combine(app.Folder, "Sousou no Frieren - Season 2 - 03.mkv")));
        Assert.StartsWith("Откатить последнее переименование: 3 файла в ", page.UndoText);
        Assert.Contains(vm.Toasts, t => t.Text == "Переименовано: 3." && t.Kind == ToastKind.Ok);

        await page.UndoCommand.ExecuteAsync(null);
        await AppFixture.WaitUntilAsync(() => File.Exists(Path.Combine(app.Folder, "weird.mkv")), "откат");
        Assert.Null(page.UndoText);
        Assert.Null(RenameJournal.Latest(app.Services.Logs.Directory));
    }

    [AvaloniaFact]
    public async Task Shikimori_picker_ranks_by_season_and_returns_choice()
    {
        const string results = """
            [{"id":52991,"name":"Sousou no Frieren","russian":"Провожающая в последний путь Фрирен","kind":"tv","episodes":28,"aired_on":"2023-09-29"},
             {"id":59978,"name":"Sousou no Frieren 2nd Season","russian":"Фрирен 2","kind":"tv","episodes":0,"aired_on":"2026-01-16"}]
            """;
        using var http = new HttpClient(new Answer(results));
        var client = new ShikimoriClient(http, null, (_, _) => Task.CompletedTask);
        var picker = new ShikimoriPickerViewModel(client, "Sousou no Frieren 2nd Season");
        ShikimoriChoice? chosen = null;
        picker.Chosen += c => chosen = c;

        await picker.SearchAsync();

        Assert.Equal("Sousou no Frieren", picker.Query);
        Assert.Equal([59978L, 52991L], picker.Results.Select(r => r.Id));
        Assert.Same(picker.Results[0], picker.Selected);
        picker.ChooseCommand.Execute(null);
        Assert.Equal(59978, chosen?.Id);

        picker.ManualId = "abc";
        Assert.False(picker.UseManualIdCommand.CanExecute(null));
        picker.ManualId = " 12345 ";
        picker.UseManualIdCommand.Execute(null);
        Assert.Equal(12345, chosen?.Id);
        picker.SkipCommand.Execute(null);
        Assert.True(chosen?.IsSkip);
        await Task.CompletedTask.WaitAsync(Ct);
    }

    private static string TrackOf(SubtitlesPageViewModel page, int row) =>
        page.Preview.Plan!.Items[row].Command!.Arguments[^1];

    private static string Mkv(params (string Name, int Id)[] subtitles) =>
        """{"container":{"type":"Matroska"},"tracks":[{"id":0,"type":"video","codec":"AVC","properties":{"codec_id":"V_MPEG4/ISO/AVC"}}"""
        + string.Concat(subtitles.Select(s =>
            ",{\"id\":" + s.Id + ",\"type\":\"subtitles\",\"codec\":\"SubStationAlpha\",\"properties\":{\"codec_id\":\"S_TEXT/ASS\",\"track_name\":\"" + s.Name + "\",\"language\":\"rus\"}}"))
        + """],"attachments":[]}""";

    private sealed class Answer(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
