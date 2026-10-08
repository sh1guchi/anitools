using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Rename;
using Anitools.Core.Templates;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Operations;

/// <summary>Переименование по своему шаблону имени.</summary>
public sealed class RenameTemplateTests
{
    [Fact]
    public void Default_template_has_no_issues_and_lists_its_variables()
    {
        Assert.Empty(RenameTemplate.Check(RenameTemplate.Default));
        Assert.All(RenameTemplate.Conditions, c => Assert.Empty(RenameTemplate.Check($"{{серия}}{c.Token}x{{/}}")));
        Assert.Equal(
            ["название", "серия", "сезон", "суффикс", "группа", "качество", "имя", "разрешение", "видео", "аудио", "каналы", "мульти"],
            RenameTemplate.Variables.Select(v => v.Name));
    }

    [Fact]
    public void Characters_that_file_names_cannot_have_are_errors()
    {
        Assert.Equal(
        [
            new TemplateIssue(10, 11, "Символ «:» в имени файла нельзя"),
            new TemplateIssue(20, 21, "Символ «?» в имени файла нельзя"),
        ], RenameTemplate.Check("{название}: {серия} ?"));

        // «?» внутри непонятного условия — одна ошибка, а не две
        Assert.Equal(["Непонятное условие «{?сезон=1}» — пиши {?переменная} или {?сезон≠1}", "Условие не закрыто — нужен {/}"],
            RenameTemplate.Check("{серия}{?сезон=1}x{?сезон}").Select(i => i.Message));
    }

    [Fact]
    public void Without_episode_all_names_would_be_the_same_so_it_warns()
    {
        var issue = Assert.Single(RenameTemplate.Check("{название}"));
        Assert.True(issue.IsWarning);
        Assert.Equal("Нет {серия} — у всех файлов получится одно имя", issue.Message);
        Assert.False(RenameTemplate.HasErrors([issue]));

        Assert.Empty(RenameTemplate.Check("{название} ({имя})")); // {имя} у всех разное
        Assert.Equal("Шаблон пустой — впишите хотя бы {название} - {серия}", Assert.Single(RenameTemplate.Check("  ")).Message);
    }

    [Fact]
    public void Group_quality_season_and_old_name_come_from_the_file()
    {
        using var dir = new TempDir();
        dir.File("[SubsPlease] Kusuriya no Hitorigoto - 05 (1080p) [A1B2C3D4].mkv");
        dir.File("Kusuriya no Hitorigoto - 06.mkv");

        var rows = Plan(dir, new RenameOptions
        {
            BaseName = "Kusuriya no Hitorigoto",
            Season = "2",
            Template = "{?группа}[{группа}] {/}{название}{?сезон≠1} S{сезон}{/} - {серия}{?качество} [{качество}]{/} ({имя})",
        });

        Assert.Equal(
        [
            "Kusuriya no Hitorigoto S2 - 06 (Kusuriya no Hitorigoto - 06).mkv",
            "[SubsPlease] Kusuriya no Hitorigoto S2 - 05 [1080p] ([SubsPlease] Kusuriya no Hitorigoto - 05 (1080p) [A1B2C3D4]).mkv",
        ], rows.Select(r => r.NewName));
        Assert.All(rows, r => Assert.Equal(RenameRowStatus.Rename, r.Status));

        // сезон «01» — тоже первый: сравнение по числу
        rows = Plan(dir, new RenameOptions { BaseName = "K", Season = "01", Template = "{название}{?сезон≠1} S{сезон}{/} - {серия}" });
        Assert.Equal(["K - 06.mkv", "K - 05.mkv"], rows.Select(r => r.NewName));
    }

    [Fact]
    public void Spaces_left_by_empty_conditions_are_trimmed()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");
        var row = Assert.Single(Plan(dir, new RenameOptions { BaseName = "Show", Template = " {название} - {серия} {?сезон}S{сезон}{/}" }));
        Assert.Equal("Show - 01.mkv", row.NewName);
        Assert.Equal(RenameRowStatus.Unchanged, row.Status);
    }

    [Fact]
    public void Template_without_episode_does_not_skip_files_without_number()
    {
        using var dir = new TempDir();
        dir.File("Movie.mkv");
        dir.File("Extra.mkv");

        var rows = Plan(dir, new RenameOptions { BaseName = "Film", Template = "{название} ({имя})" });
        Assert.Equal(["Film (Extra).mkv", "Film (Movie).mkv"], rows.Select(r => r.NewName));

        // а стёртый вручную номер — по-прежнему «файл не трогать»
        rows = Plan(dir, new RenameOptions
        {
            BaseName = "Film",
            Template = "{название} ({имя})",
            ManualNumbers = new Dictionary<string, string> { ["Extra.mkv"] = "" },
        });
        Assert.Equal([RenameRowStatus.NoNumber, RenameRowStatus.Rename], rows.Select(r => r.Status));
    }

    [Fact]
    public void Bad_names_from_field_values_are_invalid_rows()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");
        dir.File("Show - 02.mkv");

        var rows = Plan(dir, new RenameOptions { BaseName = "Show", Suffix = "rus/eng" });
        Assert.All(rows, r =>
        {
            Assert.Equal(RenameRowStatus.Invalid, r.Status);
            Assert.Equal("в имени запрещённый символ «/»", r.Reason);
        });

        var row = Plan(dir, new RenameOptions { BaseName = new string('x', 300) })[0];
        Assert.Equal((RenameRowStatus.Invalid, "имя длиннее 255 символов"), (row.Status, row.Reason));

        row = Plan(dir, new RenameOptions { BaseName = "Show", Template = "{?сезон}{сезон}{/}{серия}" })[0];
        Assert.Equal((RenameRowStatus.Rename, "01.mkv"), (row.Status, row.NewName));
        row = Plan(dir, new RenameOptions { BaseName = "Show", Template = "{?сезон}{сезон}{/}{?суффикс}{серия}{/}" })[0];
        Assert.Equal((RenameRowStatus.Invalid, "по шаблону вышло пустое имя"), (row.Status, row.Reason));
    }

    [Fact]
    public void Template_errors_stop_the_plan_and_title_is_needed_only_when_used()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");

        var ex = Assert.Throws<PlanException>(() => Plan(dir, new RenameOptions { BaseName = "Show", Template = "{название} - {сирия}" }));
        Assert.Equal("В шаблоне имени ошибка: Неизвестная переменная {сирия}", ex.Message);

        Assert.Equal("Серия 01.mkv", Plan(dir, new RenameOptions { BaseName = "", Template = "Серия {серия}" })[0].NewName);
    }

    [Fact]
    public void Season_hint_is_the_most_common_season_in_video_names()
    {
        Assert.Equal("2", RenameOperation.SeasonHint(["Show S2 - 01.mkv", "Show S2 - 02.mkv", "Show S2 - 01.ass", "NCOP.mkv"]));
        Assert.Equal("", RenameOperation.SeasonHint(["Show - 01.mkv", "Show S3 - 01.ass"]));
    }

    /// <summary>Имена для рутрекера в стиле релизов: и переименовать в такое, и разобрать такое имя.</summary>
    [Fact]
    public void Rutracker_release_style_names()
    {
        const string release = "TO.BE.HERO.X.S01E01.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar";
        using var dir = new TempDir();
        dir.File("[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].mkv");
        dir.File("[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].ass");
        dir.File("[SubsPlease] To Be Hero X - 02 (1080p) [6E7F8A9B].mkv");

        var rows = Plan(dir, new RenameOptions
        {
            BaseName = "TO BE HERO X",
            Season = "1",
            Template = "{название:точки}.S{сезон:00}E{серия}.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar",
        });
        Assert.Equal([release + ".ass", release + ".mkv", release.Replace("E01", "E02", StringComparison.Ordinal) + ".mkv"], rows.Select(r => r.NewName));
        Assert.All(rows, r => Assert.Equal(RenameRowStatus.Rename, r.Status));

        // качество и группа — из старого имени
        rows = Plan(dir, new RenameOptions
        {
            BaseName = "To Be Hero X",
            Season = "1",
            Template = "{название:точки}.S{сезон:00}E{серия}{?качество}.{качество}{/}{?группа}-{группа}{/}",
        });
        Assert.Equal("To.Be.Hero.X.S01E01.1080p-SubsPlease.mkv", rows[1].NewName);

        // обратно: из таких имён находятся номер, название и сезон
        using var releases = new TempDir();
        releases.File(release + ".mkv");
        releases.File(release.Replace("E01", "E02", StringComparison.Ordinal) + ".mkv");
        var files = RenameOperation.ListFiles(releases.Path);
        Assert.Equal(["01", "02"], files.Select(f => RenameOperation.AutoEpisode(f, 1)));
        Assert.Equal("TO BE HERO X", RenameOperation.TitleHint(files));
        Assert.Equal("1", RenameOperation.SeasonHint(files)); // anitomy: «S01» → 1
        rows = Plan(releases, new RenameOptions { BaseName = "TO BE HERO X", Season = "1", Template = "{название} {?сезон≠1}{сезон} {/}- {серия} [{качество}]" });
        Assert.Equal(["TO BE HERO X - 01 [1080p].mkv", "TO BE HERO X - 02 [1080p].mkv"], rows.Select(r => r.NewName));
    }

    /// <summary>Один шаблон на любые релизы: разрешение, кодеки, каналы и MULTi — из самого видео; субтитры — как их серия.</summary>
    [Fact]
    public void Release_names_take_parameters_from_the_video_itself()
    {
        using var dir = new TempDir();
        dir.File("[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].mkv");
        dir.File("[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].ass");
        dir.File("[Other] To Be Hero X - 02 [WEB 720p].mkv");
        var media = new Dictionary<string, MediaInfo>
        {
            ["[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].mkv"] = MediaProbe.ParseFfprobe("""
                {"streams": [
                  {"index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080},
                  {"index": 1, "codec_type": "audio", "codec_name": "flac", "channels": 2, "tags": {"language": "jpn"}, "disposition": {"default": 1}},
                  {"index": 2, "codec_type": "audio", "codec_name": "flac", "channels": 2, "tags": {"language": "rus"}}]}
                """),
            ["[Other] To Be Hero X - 02 [WEB 720p].mkv"] = MediaProbe.ParseFfprobe("""
                {"streams": [
                  {"index": 0, "codec_type": "video", "codec_name": "hevc", "width": 1280, "height": 720},
                  {"index": 1, "codec_type": "audio", "codec_name": "eac3", "channels": 6, "tags": {"language": "jpn"}}]}
                """),
        };

        var preset = RenameTemplate.BuiltInPresets.Single(p => p.Name == "Как у релизов").Template.Replace("-Group", "-Sylvar", StringComparison.Ordinal);
        var rows = Plan(dir, new RenameOptions { BaseName = "TO BE HERO X", Season = "1", Template = preset, Media = media });

        Assert.Equal(
        [
            "TO.BE.HERO.X.S01E02.720p.BluRay.Remux.HEVC.EAC3.5.1-Sylvar.mkv",
            "TO.BE.HERO.X.S01E01.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.ass",
            "TO.BE.HERO.X.S01E01.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.mkv",
        ], rows.Select(r => r.NewName));

        // видео не прочитано — пустые переменные не оставляют «..»
        rows = Plan(dir, new RenameOptions { BaseName = "TO BE HERO X", Season = "1", Template = preset });
        Assert.Equal("TO.BE.HERO.X.S01E02.BluRay.Remux.-Sylvar.mkv", rows[0].NewName);
    }

    [Theory]
    [InlineData(1920, 800, "1080p")]
    [InlineData(3840, 2160, "2160p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(720, 576, "576p")]
    [InlineData(640, 480, "480p")]
    public void Resolution_from_frame_size(int width, int height, string expected) =>
        Assert.Equal(expected, RenameMedia.Resolution(new MediaInfo([new MediaStream { CodecType = "video", Width = width, Height = height }], null, null)));

    [Fact]
    public void Audio_of_the_default_track_and_multi_by_languages()
    {
        static MediaStream Audio(string codec, int channels, string? lang = null, bool isDefault = false, string? profile = null) =>
            new() { CodecType = "audio", CodecName = codec, Channels = channels, Language = lang, IsDefault = isDefault, Profile = profile };

        var info = new MediaInfo([Audio("aac", 2, "rus"), Audio("dts", 8, "jpn", isDefault: true, profile: "DTS-HD MA")], null, null);
        Assert.Equal(("DTS-HD.MA", "7.1", "MULTi"), (RenameMedia.AudioCodec(info), RenameMedia.Channels(info), RenameMedia.Multi(info)));

        info = new MediaInfo([Audio("ac3", 6, "rus"), Audio("ac3", 2, "rus")], null, null);
        Assert.Equal(("AC3", "5.1", ""), (RenameMedia.AudioCodec(info), RenameMedia.Channels(info), RenameMedia.Multi(info)));

        info = new MediaInfo([Audio("pcm_s24le", 2), Audio("opus", 2, "und")], null, null);
        Assert.Equal(("LPCM", "MULTi"), (RenameMedia.AudioCodec(info), RenameMedia.Multi(info))); // без языка — свой язык
        Assert.Equal("", RenameMedia.AudioCodec(MediaInfo.Empty));
    }

    private static IReadOnlyList<RenameRow> Plan(TempDir dir, RenameOptions options) =>
        RenameOperation.Plan(dir.Path, RenameOperation.ListFiles(dir.Path), options);
}
