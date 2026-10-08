using Anitools.App.ViewModels;
using Anitools.App.ViewModels.Pages;
using Anitools.App.Views;
using Anitools.App.Views.Controls;
using Anitools.Core.Operations.Rename;
using Anitools.Core.Settings;
using Anitools.Core.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Anitools.App.Tests;

/// <summary>Редактор шаблона имени на странице «Переименовать» (как в Anime Uploader), пресеты, экспорт настроек.</summary>
public sealed class TemplateEditorTests
{
    private const string Release = "{название:точки}.S{сезон:00}E{серия}.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar";

    [AvaloniaFact]
    public async Task Pills_are_inserted_deleted_whole_and_undone()
    {
        using var app = Frieren();
        var (vm, window) = await OpenAsync(app);
        var (page, editor, box) = await RenameEditorAsync(vm, window);
        Assert.IsType<TemplateTextPresenter>(box.Presenter); // стиль TextBox.tpl применился

        // поле ещё не трогали — чип вставляет в конец
        Chip(editor, "сезон");
        Assert.Equal(RenameTemplate.Default + "{сезон}", page.Template);

        // Backspace сразу за плашкой — плашка целиком; Ctrl+Z — обратно
        box.CaretIndex = box.Text!.Length;
        Press(window, Key.Back, PhysicalKey.Backspace);
        Assert.Equal(RenameTemplate.Default, page.Template);
        Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal(RenameTemplate.Default + "{сезон}", page.Template);

        // стрелка перескакивает плашку
        box.CaretIndex = box.Text!.Length;
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(box.Text!.Length - "{сезон}".Length, box.CaretIndex);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(box.Text!.Length - "{/}{сезон}".Length, box.CaretIndex);

        // Delete перед {?суффикс} снимает условие, текст внутри остаётся
        box.CaretIndex = box.Text!.IndexOf("{?суффикс}", StringComparison.Ordinal);
        Press(window, Key.Delete, PhysicalKey.Delete);
        Assert.Equal("{название} - {серия}.{суффикс}{сезон}", page.Template);
        Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal(RenameTemplate.Default + "{сезон}", page.Template);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Menus_wrap_in_condition_and_replace_a_clicked_pill()
    {
        using var app = Frieren();
        var (vm, window) = await OpenAsync(app);
        var (page, editor, box) = await RenameEditorAsync(vm, window);

        // «Условие ▾» вокруг выделенного {серия}
        box.Focus();
        box.Select(13, 20);
        Click(editor.FindControl<Button>("ConditionButton")!);
        // на экране — пункты, а не пустая полоска (заголовок + условия)
        Assert.Equal(1 + RenameTemplate.Conditions.Count, window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single().ItemCount);
        MenuClick(editor.OpenMenu!, "если сезон не 1");
        Assert.Equal("{название} - {?сезон≠1}{серия}{/}{?суффикс}.{суффикс}{/}", page.Template);
        Assert.Equal((23, 30), (box.SelectionStart, box.SelectionEnd)); // выделено то, что внутри условия

        // щелчок по плашке {название} — меню «удалить / заменить на»
        AppFixture.Flush();
        var presenter = box.Presenter!;
        var rect = presenter.TextLayout.HitTestTextRange(0, "{название}".Length).First();
        var point = presenter.TranslatePoint(rect.Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.NotNull(box.OpenMenu);
        MenuClick(box.OpenMenu!, "{имя}");
        Assert.Equal("{имя} - {?сезон≠1}{серия}{/}{?суффикс}.{суффикс}{/}", page.Template);
        page.Season = "2";
        Assert.Equal("[SubsPlease] Sousou no Frieren - 01 (1080p) - 01.mkv", page.Rows[0].NewName);

        // «+ Переменная ▾» — и с преобразованием для имён как у релизов
        box.CaretIndex = 0;
        Click(editor.FindControl<Button>("VariableButton")!);
        MenuClick(editor.OpenMenu!, "{название:точки}");
        Assert.StartsWith("{название:точки}{имя}", page.Template, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Errors_are_listed_with_a_piece_of_template_and_block_renaming()
    {
        using var app = Frieren();
        var (vm, window) = await OpenAsync(app);
        var (page, editor, box) = await RenameEditorAsync(vm, window);

        page.Template = "{название} - {сирия}";
        AppFixture.Flush();

        Assert.Equal(["Неизвестная переменная {сирия}", "Нет {серия} — у всех файлов получится одно имя"], editor.IssueRows.Select(r => r.Message));
        Assert.Equal(("{название} - ", "{сирия}", ""), (editor.IssueRows[0].Before, editor.IssueRows[0].Marked, editor.IssueRows[0].After));
        Assert.True(editor.IssueRows[1].IsWarning);
        Assert.Equal(0, page.RenameCount);
        Assert.All(page.Rows, r => Assert.Equal("⚠ исправьте шаблон имени", r.Note));
        Assert.True(editor.FindControl<Border>("CustomBadge")!.IsVisible);

        editor.JumpCommand.Execute(editor.IssueRows[0]);
        Assert.Equal((13, 20), (box.SelectionStart, box.SelectionEnd));

        // «Стандартный» — тоже правка: Ctrl+Z вернёт свой
        Click(editor.FindControl<Button>("ResetButton")!);
        Assert.Equal(RenameTemplate.Default, page.Template);
        Assert.Equal(2, page.RenameCount);
        Assert.False(editor.FindControl<Border>("CustomBadge")!.IsVisible);
        Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("{название} - {сирия}", page.Template);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Template_is_remembered_and_fields_follow_its_variables()
    {
        using var app = Frieren();
        var vm = app.CreateViewModel();
        var page = vm.RenamePage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.RenameCount == 2, "строки");
        Assert.True(page.UsesTitle && page.UsesEpisode && page.UsesSuffix);
        Assert.False(page.UsesSeason);
        Assert.Equal("1", page.Season);

        page.Template = "{название} S{сезон:00}E{серия}";
        Assert.True(page.UsesSeason);
        Assert.False(page.UsesSuffix);
        Assert.Equal("Sousou no Frieren S01E01.mkv", page.Rows[0].NewName);
        page.Season = "2";
        Assert.Equal("Sousou no Frieren S02E01.mkv", page.Rows[0].NewName);
        await AppFixture.WaitUntilAsync(() => app.Services.Settings.Rename.Template == "{название} S{сезон:00}E{серия}", "шаблон запомнился");

        // с ошибкой — не запоминается, в настройках остаётся прошлый
        page.Template = "{название} S{сезон:00}E{серия} {";
        page.SaveTemplateNow();
        Assert.Equal("{название} S{сезон:00}E{серия}", app.Services.Settings.Rename.Template);

        // стандартный — не хранится; новая страница берёт сохранённый
        page.Template = RenameTemplate.Default;
        page.SaveTemplateNow();
        Assert.Null(app.Services.Settings.Rename.Template);
        app.Services.UpdateSettingsQuietly(s => s with { Rename = s.Rename with { Template = Release } });
        Assert.Equal(Release, new RenamePageViewModel(vm).Template);
    }

    /// <summary>Имена для рутрекера: свой пресет из шаблона, переименование, удаление пресета.</summary>
    [AvaloniaFact]
    public async Task Own_presets_for_rutracker_names()
    {
        using var app = new AppFixture("To Be Hero X").WithFiles(
            "[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].mkv",
            "[SubsPlease] To Be Hero X - 02 (1080p) [6E7F8A9B].mkv");
        var (vm, window) = await OpenAsync(app);
        var (page, editor, _) = await RenameEditorAsync(vm, window);
        page.BaseName = "TO BE HERO X";

        app.Dialogs.Answers.Enqueue("Рутрекер");
        await page.SavePresetCommand.ExecuteAsync(Release);
        Assert.Equal([new TemplatePreset("Рутрекер", Release)], app.Services.Settings.Rename.Presets);
        Assert.Equal(app.Services.Settings.Rename.Presets, page.Presets);

        // имя готового пресета не занять; своё — заменить после вопроса
        app.Dialogs.Answers.Enqueue("Стандартный");
        await page.SavePresetCommand.ExecuteAsync("{название}");
        Assert.Contains(vm.Toasts, t => t.Text == "«Стандартный» — готовый пресет, выберите другое имя.");
        app.Dialogs.Answers.Enqueue("рутрекер");
        app.Dialogs.ConfirmResult = false;
        await page.SavePresetCommand.ExecuteAsync("{название}.{серия}");
        Assert.Equal(Release, Assert.Single(app.Services.Settings.Rename.Presets).Template);

        // «Пресеты ▾» → свой пресет: имена как у релизов
        Click(editor.FindControl<Button>("PresetsButton")!);
        MenuClick(editor.OpenMenu!, "Рутрекер");
        Assert.Equal(Release, page.Template);
        Assert.Equal(
            ["TO.BE.HERO.X.S01E01.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.mkv", "TO.BE.HERO.X.S01E02.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.mkv"],
            page.Rows.Select(r => r.NewName));
        await page.RenameCommand.ExecuteAsync(null);
        await AppFixture.WaitUntilAsync(() => File.Exists(Path.Combine(app.Folder, "TO.BE.HERO.X.S01E02.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.mkv")), "переименовано");
        Assert.Equal(Release, app.Services.Settings.Rename.Template);

        app.Dialogs.ConfirmResult = true;
        await page.DeletePresetCommand.ExecuteAsync(page.Presets[0]);
        Assert.Empty(app.Services.Settings.Rename.Presets);
        Assert.Empty(page.Presets);
        window.Close();
    }

    /// <summary>Один шаблон на любые релизы: параметры — из самого видео (ffprobe), субтитры — как их серия.</summary>
    [AvaloniaFact]
    public async Task Release_names_read_parameters_from_the_video()
    {
        const string auto = "{название:точки}.S{сезон:00}E{серия}.{разрешение}.BluRay.Remux.{видео}{?мульти}.{мульти}{/}.{аудио}.{каналы}-Sylvar";
        using var app = new AppFixture("To Be Hero X").WithFiles(
            "[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].mkv",
            "[SubsPlease] To Be Hero X - 01 (1080p) [5A1B2C3D].ass");
        app.Runner.FfprobeJson = _ => """
            {"streams": [
              {"index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080},
              {"index": 1, "codec_type": "audio", "codec_name": "flac", "channels": 2, "tags": {"language": "jpn"}, "disposition": {"default": 1}},
              {"index": 2, "codec_type": "audio", "codec_name": "flac", "channels": 2, "tags": {"language": "rus"}}],
             "format": {"duration": "1420.0"}}
            """;
        var vm = app.CreateViewModel();
        var page = vm.RenamePage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.RenameCount == 2, "строки");
        Assert.DoesNotContain(app.Runner.Calls, c => Path.GetFileName(c.FileName) == "ffprobe"); // стандартному шаблону видео не нужно

        page.BaseName = "TO BE HERO X";
        page.Template = auto;
        await AppFixture.WaitUntilAsync(() => !page.IsReadingVideo && page.RenameCount == 2, "видео прочитано");

        Assert.Equal(
            ["TO.BE.HERO.X.S01E01.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.ass", "TO.BE.HERO.X.S01E01.1080p.BluRay.Remux.AVC.MULTi.FLAC.2.0-Sylvar.mkv"],
            page.Rows.Select(r => r.NewName));
        Assert.Single(app.Runner.Calls, c => Path.GetFileName(c.FileName) == "ffprobe"); // только видео, один раз

        page.Template = RenameTemplate.Default;
        page.Template = auto;
        Assert.Equal(2, page.RenameCount);
        Assert.Single(app.Runner.Calls, c => Path.GetFileName(c.FileName) == "ffprobe");
    }

    [AvaloniaFact]
    public async Task Shikimori_searches_the_typed_title_and_folder_name_helps_with_episode_titled_files()
    {
        using var app = new AppFixture("[BD-Remux] Bleach Sennen Kessen Hen - Soukoku Tan").WithFiles(
            "01. A.mkv", "02. Kill the King.mkv", "03. The Dark Arm.mkv");
        var vm = app.CreateViewModel();
        var page = vm.RenamePage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Rows.Count == 3, "строки");
        Assert.Equal("Bleach Sennen Kessen Hen Soukoku Tan", page.BaseName); // из имени папки, а не «01. A»

        page.BaseName = "Bleach sennen kessen hen";
        await page.SearchShikimoriCommand.ExecuteAsync(null);

        Assert.Equal("Bleach sennen kessen hen", app.Dialogs.LastShikimori?.Title);
        Assert.Equal("Базовое название: Bleach sennen kessen hen", app.Dialogs.LastShikimori?.Caption);
    }

    [AvaloniaFact]
    public async Task Settings_export_and_import_bring_presets_to_the_rename_page()
    {
        using var app = Frieren();
        var vm = app.CreateViewModel();
        var settings = vm.SettingsPage;
        vm.SelectedNav = settings;
        app.Services.SaveSettings(app.Services.Settings with
        {
            SubShiftSeconds = 2.5,
            Rename = new RenameSettings { Template = Release, Presets = [new TemplatePreset("Рутрекер", Release)] },
        });
        settings.RevertCommand.Execute(null); // на экране — сохранённое: экспортируется то, что видно

        var file = Path.Combine(app.Root, "перенос", SettingsPageViewModel.ExportFileName);
        app.Dialogs.NextSaveFile = file;
        await settings.ExportSettingsCommand.ExecuteAsync(null);
        Assert.True(File.Exists(file));
        Assert.Contains(vm.Toasts, t => t.Text == $"Настройки выгружены: {file}");

        app.Services.SaveSettings(app.Services.Settings with { SubShiftSeconds = 9, Rename = new RenameSettings() });
        AppFixture.Flush();
        Assert.Empty(vm.RenamePage.Presets);
        Assert.Equal(RenameTemplate.Default, vm.RenamePage.Template);

        app.Dialogs.NextFile = file;
        await settings.ImportSettingsCommand.ExecuteAsync(null);
        AppFixture.Flush();

        Assert.Equal(2.5, app.Services.Settings.SubShiftSeconds);
        Assert.Equal(AudioShiftPageViewModel.Text(2.5), settings.SubShiftSeconds);
        Assert.Equal([new TemplatePreset("Рутрекер", Release)], vm.RenamePage.Presets);
        Assert.Equal(Release, vm.RenamePage.Template);
        Assert.Contains(vm.Toasts, t => t.Text == "Настройки импортированы.");

        // чужой файл — не настройки
        var other = Path.Combine(app.Root, "other.json");
        File.WriteAllText(other, """{ "name": "x" }""");
        app.Dialogs.NextFile = other;
        await settings.ImportSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Не получилось взять настройки из other.json: в файле нет настроек anitools.", app.Dialogs.Messages[^1]);
        Assert.Equal(2.5, app.Services.Settings.SubShiftSeconds);
    }

    private static AppFixture Frieren() => new AppFixture().WithFiles(
        "[SubsPlease] Sousou no Frieren - 01 (1080p).mkv",
        "[SubsPlease] Sousou no Frieren - 02 (1080p).mkv");

    private static async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenAsync(AppFixture app)
    {
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        return (vm, window);
    }

    private static async Task<(RenamePageViewModel Page, TemplateEditor Editor, TemplateBox Box)> RenameEditorAsync(MainWindowViewModel vm, MainWindow window)
    {
        var page = vm.RenamePage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.RenameCount > 0 && window.GetVisualDescendants().OfType<TemplateEditor>().Any(), "редактор шаблона");
        var editor = window.GetVisualDescendants().OfType<TemplateEditor>().Single();
        return (page, editor, editor.Editor);
    }

    private static void Chip(TemplateEditor editor, string name)
    {
        var chip = editor.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("chip") && Equals(b.Content, name));
        chip.Command!.Execute(chip.CommandParameter);
        AppFixture.Flush();
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AppFixture.Flush();
    }

    /// <summary>Пункт меню по первой строке подписи: сначала верхнего уровня, потом во вложенных.</summary>
    private static void MenuClick(MenuFlyout menu, string text)
    {
        static bool Is(MenuItem i, string text) => i.Header is StackPanel { Children: [TextBlock first, ..] } && first.Text == text;
        var item = menu.Items.OfType<MenuItem>().SingleOrDefault(i => Is(i, text)) ?? Items(menu.Items).Single(i => Is(i, text));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide();
        AppFixture.Flush();

        static IEnumerable<MenuItem> Items(IEnumerable<object?> items) =>
            items.OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(Items(i.Items)));
    }

    private static void Press(MainWindow window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        AppFixture.Flush();
    }
}
