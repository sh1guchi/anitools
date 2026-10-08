using Anitools.Core;
using Anitools.Core.Install;
using Anitools.Core.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anitools.App.ViewModels;

/// <summary>Обновления anitools (этап 9) и плашка «не хватает программ» с кнопкой «Установить».</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Как часто проверять обновления сами (при запуске).</summary>
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(20);

    private bool _missingToolsDismissed;

    /// <summary>Закрыть приложение (установщик обновления запущен); подписывается App.</summary>
    public event Action? ExitRequested;

    /// <summary>Вышедшая новая версия; null — новой нет или не проверяли.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial ReleaseInfo? Update { get; set; }

    /// <summary>Ход скачивания обновления: «скачиваю 34%»; null — не качается.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial string? UpdateProgress { get; set; }

    /// <summary>Кнопка в шапке: «Обновить до 1.1.0».</summary>
    public string UpdateText => UpdateProgress ?? (Update is { } release ? $"Обновить до {release.Version}" : "");

    /// <summary>Чего не хватает для работы: «ffmpeg и MKVToolNix»; null — всё есть или плашку скрыли.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingToolsText))]
    public partial string? MissingTools { get; set; }

    public string MissingToolsText => MissingTools is null ? "" : $"Не найдены {MissingTools} — без них большинство инструментов не работает.";

    /// <summary>Ставить программы умеем (Windows) — кнопка «Установить», иначе — «Настройки».</summary>
    public bool CanInstallTools => Services.Installer is not null;

    public string MissingToolsButtonText => CanInstallTools ? "Установить" : "Настройки";

    /// <summary>Проверка при запуске: через несколько секунд, чтобы не мешать открытию окна.</summary>
    public async Task CheckUpdatesLaterAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        await CheckUpdatesAsync(manual: false);
    }

    /// <summary>
    /// Есть ли новая версия. Сама (при запуске) — не чаще раза в <see cref="UpdateCheckInterval"/> и только если
    /// это не выключено в настройках, молча; по кнопке — всегда, с ответом уведомлением.
    /// </summary>
    public async Task CheckUpdatesAsync(bool manual)
    {
        var settings = Services.Settings;
        if (!manual && (!settings.CheckUpdates
            || settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < UpdateCheckInterval && DateTimeOffset.Now >= last))
        {
            return;
        }

        var release = await Services.Updates.LatestAsync();
        Services.UpdateSettingsQuietly(s => s with { LastUpdateCheck = DateTimeOffset.Now });
        if (release is not null && UpdateChecker.IsNewer(release.Version, AppInfo.Version))
        {
            Update = release;
            Toast($"Вышла версия {release.Version} — «Обновить до {release.Version}» сверху.", ToastKind.Ok);
        }
        else if (manual)
        {
            Toast(release is null ? "Не удалось узнать о новых версиях — нет связи с GitHub." : $"У вас последняя версия — {AppInfo.Version}.");
        }
    }

    /// <summary>
    /// Обновить: установленный anitools скачивает установщик (SHA-256 сверяется), запускает его тихо и закрывается —
    /// установщик заменит программу и откроет её снова. exe без установки — страница выпуска в браузере.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (Update is not { } release)
        {
            return;
        }

        if (!Services.IsInstalled)
        {
            await Dialogs.OpenUrlAsync(release.PageUrl);
            return;
        }

        if (Services.Jobs.IsBusy)
        {
            Toast("Дождитесь конца задач: при обновлении anitools закроется.", ToastKind.Warn);
            return;
        }

        var size = release.SetupSize is { } bytes ? $" (≈{bytes / (1024 * 1024)} МБ)" : "";
        if (!await Dialogs.ConfirmAsync("Обновление", $"Обновить anitools до {release.Version}? Установщик скачается{size}, anitools закроется и откроется снова.", "Обновить", "Отмена"))
        {
            return;
        }

        UpdateProgress = "скачиваю…";
        try
        {
            var setup = await Services.Updates.DownloadSetupAsync(release, Services.UpdatesFolder, new Progress<InstallProgress>(p =>
                UpdateProgress = p.Total is > 0 and var total ? $"скачиваю {p.Downloaded * 100 / total}%" : "скачиваю…"));
            if (Services.RunInstaller(setup))
            {
                ExitRequested?.Invoke();
                return;
            }

            Toast("Не удалось запустить установщик обновления.", ToastKind.Error);
        }
        catch (InstallException ex)
        {
            Toast(ex.Message, ToastKind.Error);
        }
        finally
        {
            UpdateProgress = null;
        }
    }

    private bool CanInstallUpdate() => Update is not null && UpdateProgress is null;

    /// <summary>«Установить» на плашке: перейти в настройки и поставить недостающее (ffmpeg, MKVToolNix) по очереди.</summary>
    [RelayCommand]
    private async Task InstallMissingToolsAsync()
    {
        SelectedNav = SettingsPage;
        if (CanInstallTools)
        {
            await SettingsPage.InstallMissingAsync();
        }
    }

    [RelayCommand]
    private void DismissMissingTools()
    {
        _missingToolsDismissed = true;
        MissingTools = null;
    }

    /// <summary>Плашка — только про то, без чего не работает почти ничего; ImDisk и NVENC — в плашках сверху.</summary>
    private void UpdateMissingTools(ToolPaths paths)
    {
        List<string> missing = [];
        if (paths.Ffmpeg is null || paths.Ffprobe is null)
        {
            missing.Add("ffmpeg");
        }

        if (paths.Mkvmerge is null || paths.Mkvextract is null)
        {
            missing.Add("MKVToolNix");
        }

        MissingTools = missing.Count > 0 && !_missingToolsDismissed ? string.Join(" и ", missing) : null;
    }
}
