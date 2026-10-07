using System.Collections.ObjectModel;
using System.Text;
using Anitools.Core.Jobs;
using Anitools.Core.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels;

/// <summary>Задача в списке «Задачи» (§4.10): значок состояния, ход работы, время, журнал, отмена.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly StringBuilder _log = new();
    private int _refreshPending;

    public JobViewModel(Job job, IShell shell)
    {
        Job = job;
        _shell = shell;
        foreach (var line in job.Log)
        {
            _log.AppendLine(line);
        }

        LogText = _log.ToString();
        job.Changed += _ => ScheduleRefresh();
        job.LogAdded += (_, line) => Dispatcher.UIThread.Post(() =>
        {
            _log.AppendLine(line);
            LogText = _log.ToString();
        });
        Refresh();
    }

    public Job Job { get; }

    public string Title => Job.Title;

    public string Folder => Job.Folder;

    [ObservableProperty]
    public partial JobState State { get; set; }

    /// <summary>0…100 для полосы.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; }

    /// <summary>«23:41» — сколько шла; у идущей — «идёт 12:03 · осталось ≈ 1:12:40».</summary>
    [ObservableProperty]
    public partial string Time { get; set; } = "";

    [ObservableProperty]
    public partial string LogText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLogOpen { get; set; }

    public bool IsRunning => State == JobState.Running;

    public bool CanCancel => State is JobState.Queued or JobState.Running;

    public MaterialIconKind Icon => State switch
    {
        JobState.Queued => MaterialIconKind.ClockOutline,
        JobState.Running => MaterialIconKind.ProgressClock,
        JobState.Done => MaterialIconKind.CheckCircle,
        JobState.Failed => MaterialIconKind.CloseCircle,
        _ => MaterialIconKind.Cancel,
    };

    /// <summary>Класс цвета значка: running, done, failed, muted.</summary>
    public bool IsDone => State == JobState.Done;

    public bool IsFailed => State == JobState.Failed;

    public bool IsMuted => State is JobState.Queued or JobState.Cancelled;

    /// <summary>Строка для статус-строки окна: «HLS · Frieren: серия 3/12 · видео 41% · x2.3».</summary>
    public string Short => $"{Title}: {Status}";

    public void Refresh()
    {
        var s = Job.Snapshot;
        State = s.State;
        Progress = (s.Fraction ?? 0) * 100;
        IsIndeterminate = s.State == JobState.Running && s.Fraction is null;
        Status = s.State == JobState.Running || s.Summary is null ? s.Status : s.Summary;
        Detail = s.Detail;
        Summary = s.Summary;
        Time = s switch
        {
            { State: JobState.Running, Started: { } started } => $"идёт {RuText.Duration(DateTime.Now - started)}"
                + (s.Eta is { } eta ? $" · осталось ≈ {RuText.Duration(eta)}" : ""),
            { Started: { } started, Finished: { } finished } => RuText.Duration(finished - started),
            _ => "",
        };
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(Short));
    }

    [RelayCommand]
    private void Cancel() => Job.Cancel();

    [RelayCommand]
    private Task OpenFolderAsync() => _shell.Dialogs.OpenPathAsync(Job.Folder);

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private Task CopyLogAsync() => _shell.Dialogs.CopyTextAsync(LogText);

    /// <summary>События хода работы приходят часто и из чужого потока — обновление одно на кадр.</summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _refreshPending, 0);
                Refresh();
            });
        }
    }
}

/// <summary>«Задачи» (§4.10): очередь по порядку постановки; идущая — с ходом работы, законченные — с итогом.</summary>
public sealed partial class JobsPageViewModel : PageViewModel
{
    public JobsPageViewModel(IShell shell)
        : base(shell, "Задачи", MaterialIconKind.FormatListChecks)
    {
        var queue = shell.Services.Jobs;
        foreach (var job in queue.Jobs)
        {
            Add(job);
        }

        queue.JobAdded += job => Dispatcher.UIThread.Post(() => Add(job));
        queue.JobRemoved += job => Dispatcher.UIThread.Post(() =>
        {
            if (Jobs.FirstOrDefault(j => j.Job == job) is { } vm)
            {
                Jobs.Remove(vm);
            }

            UpdateBadge();
        });
        queue.JobFinished += _ => Dispatcher.UIThread.Post(UpdateBadge);
        // время «идёт 12:03» и ETA тикают и без событий от задачи
        Timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var job in Jobs.Where(j => j.IsRunning))
            {
                job.Refresh();
            }
        });
        Timer.Start();
    }

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public override bool UsesFolder => false;

    public bool IsEmpty => Jobs.Count == 0;

    private DispatcherTimer Timer { get; }

    protected override Task LoadAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;

    private void Add(JobViewModel vm)
    {
        Jobs.Add(vm);
        UpdateBadge();
    }

    private void Add(Job job)
    {
        if (Jobs.All(j => j.Job != job))
        {
            Add(new JobViewModel(job, Shell));
        }
    }

    private void UpdateBadge()
    {
        var active = Jobs.Count(j => !j.Job.IsFinished);
        Badge = active > 0 ? active.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ClearFinished() => Shell.Services.Jobs.ClearFinished();
}
