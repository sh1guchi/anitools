using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels;

/// <summary>
/// Страница инструмента: файлы → настройки → превью → [Запустить] (docs/PLAN.md §4.1). Содержимое строится
/// по рабочей папке, когда страница открыта; папка сменилась или задача в ней закончилась — перестраивается.
/// </summary>
public abstract partial class PageViewModel(IShell shell, string title, MaterialIconKind icon) : ObservableObject, INavItem
{
    public const string NoFolderMessage = "Выберите рабочую папку: «Обзор», перетащите папку на окно или наберите ani в консоли в папке с сериями.";

    private CancellationTokenSource? _loading;
    private bool _stale = true;

    public bool IsPage => true;

    public IShell Shell { get; } = shell;

    public string Title { get; } = title;

    public MaterialIconKind Icon { get; } = icon;

    /// <summary>Подпись справа в навигации: число задач и т.п.</summary>
    [ObservableProperty]
    public partial string? Badge { get; set; }

    /// <summary>Пульсирующая точка в навигации: идёт задача.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial string Folder { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Почему нечего показать или запустить: нет файлов, нет программы, ошибка чтения.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>Страница работает с рабочей папкой («Задачи» и «Настройки» — нет).</summary>
    public virtual bool UsesFolder => true;

    /// <summary>Пояснение рядом с названием у страниц без рабочей папки (у остальных там папка).</summary>
    public virtual string? Subtitle => null;

    public bool IsActive { get; private set; }

    public void SetFolder(string folder)
    {
        if (folder == Folder)
        {
            return;
        }

        Folder = folder;
        Invalidate();
    }

    /// <summary>Содержимое устарело: открыта — перестроить сейчас, нет — при открытии.</summary>
    public void Invalidate()
    {
        _stale = true;
        if (IsActive)
        {
            _ = RefreshAsync();
        }
    }

    public void Activate()
    {
        IsActive = true;
        if (_stale)
        {
            _ = RefreshAsync();
        }
    }

    public void Deactivate() => IsActive = false;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;
        _stale = false;
        Message = null;
        if (UsesFolder && Folder.Length == 0)
        {
            Clear();
            Message = NoFolderMessage;
            return;
        }

        IsLoading = true;
        try
        {
            await LoadAsync(Folder, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PlanException or ToolNotFoundException or MediaProbeException)
        {
            Clear();
            Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Clear();
            Message = $"Не удалось прочитать папку: {ex.Message}";
        }
        finally
        {
            if (_loading == cts)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Построить содержимое по папке; <see cref="PlanException"/> и подобное — в <see cref="Message"/>.</summary>
    protected abstract Task LoadAsync(string folder, CancellationToken cancellationToken);

    /// <summary>Убрать содержимое прошлой папки.</summary>
    protected virtual void Clear()
    {
    }

    /// <summary>Поставить задачу на рабочую папку страницы.</summary>
    protected Job Enqueue(string title, Func<JobContext, Task<JobOutcome>> work)
    {
        var jobs = Shell.Services.Jobs;
        var waiting = jobs.IsBusy;
        var job = jobs.Enqueue($"{title} · {Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}", Folder, work);
        Shell.Toast(waiting ? "Поставлено в очередь — ход работы в «Задачах»." : "Запущено — ход работы в «Задачах» и в полосе сверху.");
        return job;
    }

    [RelayCommand]
    private void ShowJobs() => Shell.ShowJobs();
}
