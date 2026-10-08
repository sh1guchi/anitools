using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Settings;
using Anitools.Core.Templates;
using Anitools.Core.Tests.Fixtures;
using Anitools.Core.WorkDir;

namespace Anitools.Core.Tests.Settings;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_original_defaults()
    {
        using var dir = new TempDir();
        var (settings, error) = new SettingsStore(dir.Combine("anitools", "settings.json")).Load();

        Assert.Null(error);
        Assert.Equal(VoiceList.Default, settings.Voices);
        Assert.Equal(21.0, settings.Hls.FixedCq);
        Assert.Equal(6, settings.Hls.Ladder.Count);
        Assert.Equal(WorkDirMode.RamDisk, settings.WorkDir.Mode);
        Assert.Equal("https://shikimori.io/api/", settings.ShikimoriBaseUrl);
        Assert.Equal(6, settings.AudioShift.Workers);
        Assert.Equal(8, settings.AudioConvert.Workers);
        Assert.Empty(settings.RecentFolders);
        Assert.Null(settings.Rename.Template);
    }

    [Fact]
    public void Round_trip_keeps_everything()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("anitools", "settings.json"));
        var settings = new AppSettings
        {
            Tools = new ToolPathSettings { Ffmpeg = @"C:\ffmpeg\bin" },
            Voices = ["AniLibria.TV", "DEEP"],
            Hls = HlsSettings.Default with { FixedCq = null, Encoder = EncoderProfile.Software, Ladder = [new HlsRung("720p", 1280, 720, 3_000_000)] },
            WorkDir = new WorkDirSettings { Mode = WorkDirMode.Folder, Folder = @"E:\tmp", RamDiskGb = 20 },
            AudioConvert = new AudioConvertOptions { Format = AudioFormat.Flac, Channels = null },
            SubShiftSeconds = -0.5,
            RecentFolders = [@"D:\anime\Frieren"],
            Rename = new RenameSettings { Template = "{название} S{сезон}E{серия}" },
        };

        store.Save(settings);
        var (loaded, error) = store.Load();

        Assert.Null(error);
        Assert.Equal(@"C:\ffmpeg\bin", loaded.Tools.Ffmpeg);
        Assert.Equal(["AniLibria.TV", "DEEP"], loaded.Voices);
        Assert.Null(loaded.Hls.FixedCq);
        Assert.Equal(EncoderProfile.Software, loaded.Hls.Encoder);
        Assert.Equal([new HlsRung("720p", 1280, 720, 3_000_000)], loaded.Hls.Ladder);
        Assert.Equal(settings.WorkDir, loaded.WorkDir);
        Assert.Equal(AudioFormat.Flac, loaded.AudioConvert.Format);
        Assert.Null(loaded.AudioConvert.Channels);
        Assert.Equal(-0.5, loaded.SubShiftSeconds);
        Assert.Equal([@"D:\anime\Frieren"], loaded.RecentFolders);
        Assert.Equal("{название} S{сезон}E{серия}", loaded.Rename.Template);
        // Человекочитаемый файл: перечисления словами, кириллица как есть
        var json = File.ReadAllText(store.Path);
        Assert.Contains("\"mode\": \"folder\"", json);
        Assert.Contains("\"encoder\": \"software\"", json);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Partial_and_wrong_values_fall_back_to_defaults()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", """
            {
              // правили руками
              "voices": ["  DEEP ", "", "DEEP", "JAM"],
              "hls": { "segmentSeconds": 0, "ladder": [], "cqMin": 30, "cqMax": 20, "fixedCq": 18.5 },
              "workDir": { "ramDiskGb": 0 },
              "shikimoriBaseUrl": "не адрес",
              "hardsub": { "encodeArgs": [] },
              "audioShift": { "workers": 0 },
              "tools": null,
              "rename": { "template": "{название} - {серия}{?суффикс}.{суффикс}{/}" },
              "unknownField": 1,
            }
            """);

        var (settings, error) = new SettingsStore(path).Load();

        Assert.Null(error);
        Assert.Equal(["DEEP", "JAM"], settings.Voices);
        Assert.Equal(6, settings.Hls.SegmentSeconds);
        Assert.Equal(HlsSettings.DefaultLadder, settings.Hls.Ladder);
        Assert.Equal((14.0, 40.0), (settings.Hls.CqMin, settings.Hls.CqMax));
        Assert.Equal(18.5, settings.Hls.FixedCq);
        Assert.Equal(new WorkDirSettings().RamDiskGb, settings.WorkDir.RamDiskGb);
        Assert.Equal("https://shikimori.io/api/", settings.ShikimoriBaseUrl);
        Assert.NotEmpty(settings.Hardsub.EncodeArgs);
        Assert.Equal(6, settings.AudioShift.Workers);
        Assert.Equal("", settings.Tools.Ffmpeg);
        Assert.Null(settings.Rename.Template); // стандартный не хранится
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_defaults_are_used()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", "{ \"voices\": [ ");

        var (settings, error) = new SettingsStore(path).Load();

        Assert.NotNull(error);
        Assert.Contains("settings.json.bad", error);
        Assert.Equal(VoiceList.Default, settings.Voices);
        Assert.Equal("{ \"voices\": [ ", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void Recent_folders_are_unique_newest_first_and_limited()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 12; i++)
        {
            settings = settings.WithRecentFolder($"/anime/{i}");
        }

        settings = settings.WithRecentFolder("/anime/5");

        Assert.Equal(AppSettings.MaxRecentFolders, settings.RecentFolders.Count);
        Assert.Equal("/anime/5", settings.RecentFolders[0]);
        Assert.Equal("/anime/11", settings.RecentFolders[1]);
        Assert.Single(settings.RecentFolders, f => f == "/anime/5");
    }

    [Fact]
    public void Export_and_import_move_all_settings_but_not_this_computer_state()
    {
        using var dir = new TempDir();
        var mine = new AppSettings
        {
            Voices = ["DEEP", "JAM"],
            SubShiftSeconds = -2,
            Rename = new RenameSettings
            {
                Template = "{название:точки}.S{сезон:00}E{серия}",
                Presets = [new TemplatePreset("Рутрекер", "{название:точки}.S{сезон:00}E{серия}.1080p-Sylvar")],
            },
            RecentFolders = [@"D:\anime\X"],
            LastUpdateCheck = DateTimeOffset.UnixEpoch,
        };
        var file = dir.Combine("export", "anitools-settings.json");
        SettingsStore.Export(mine, file);

        var json = File.ReadAllText(file);
        Assert.DoesNotContain("recentFolders", json);
        Assert.DoesNotContain("lastUpdateCheck", json);
        Assert.Contains("\"name\": \"Рутрекер\"", json);

        var other = new AppSettings { RecentFolders = [@"E:\other"], Voices = ["AniLibria.TV"] };
        var (imported, error) = SettingsStore.Import(file, other);

        Assert.Null(error);
        Assert.Equal(["DEEP", "JAM"], imported!.Voices);
        Assert.Equal(-2, imported.SubShiftSeconds);
        Assert.Equal(mine.Rename.Template, imported.Rename.Template);
        Assert.Equal(mine.Rename.Presets, imported.Rename.Presets);
        Assert.Equal([@"E:\other"], imported.RecentFolders); // свои недавние папки остались
        Assert.Null(imported.LastUpdateCheck);
    }

    [Fact]
    public void Import_takes_only_what_is_in_the_file_and_refuses_strangers()
    {
        using var dir = new TempDir();
        var current = new AppSettings { Voices = ["DEEP"], SubShiftSeconds = 3 };

        var partial = dir.File("partial.json", """{ "subShiftSeconds": -1, "hls": { "segmentSeconds": 4 } }""");
        var (settings, error) = SettingsStore.Import(partial, current);
        Assert.Null(error);
        Assert.Equal((-1.0, 4), (settings!.SubShiftSeconds, settings.Hls.SegmentSeconds));
        Assert.Equal(["DEEP"], settings.Voices);
        Assert.Equal(HlsSettings.Default.Ladder, settings.Hls.Ladder); // вложенное — по полям

        Assert.Equal("в файле нет настроек anitools", SettingsStore.Import(dir.File("other.json", """{ "name": "x" }"""), current).Error);
        Assert.Equal("в файле нет настроек anitools", SettingsStore.Import(dir.File("list.json", "[1, 2]"), current).Error);
        Assert.StartsWith("файл не читается как JSON", SettingsStore.Import(dir.File("bad.json", "{ oops"), current).Error);
        Assert.StartsWith("в настройках из файла ошибка", SettingsStore.Import(dir.File("wrong.json", """{ "voices": 5 }"""), current).Error);
    }
}
