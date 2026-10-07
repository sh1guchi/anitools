namespace Anitools.Core.Jobs;

public enum JobState
{
    /// <summary>Ждёт своей очереди.</summary>
    Queued,
    Running,

    /// <summary>Выполнена без ошибок.</summary>
    Done,

    /// <summary>Выполнена с ошибками или упала.</summary>
    Failed,
    Cancelled,
}

/// <summary>Итог работы задачи: без ошибок ли и короткая сводка («11 готово · 1 пропуск»).</summary>
public sealed record JobOutcome(bool Success, string Summary);

/// <summary>Состояние задачи на момент чтения — для отображения.</summary>
/// <param name="Fraction">Общая готовность 0…1; null — неизвестна.</param>
/// <param name="Status">Ход работы одной строкой: «файл 3/12 · Frieren - 03.mkv · 41% · x2.3».</param>
/// <param name="Detail">Вторая строка, например итоги по сериям: «01 ✓ 23:41 · 02 ✓ 24:02».</param>
/// <param name="Eta">Сколько примерно осталось (по доле готовности и прошедшему времени).</param>
public sealed record JobSnapshot(
    JobState State,
    double? Fraction,
    string Status,
    string? Detail,
    string? Summary,
    DateTime? Started,
    DateTime? Finished,
    TimeSpan? Eta)
{
    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;
}

/// <summary>Что видит работа задачи: токен отмены, ход работы и журнал.</summary>
public sealed class JobContext
{
    private readonly Job _job;

    internal JobContext(Job job) => _job = job;

    public CancellationToken CancellationToken => _job.Token;

    /// <param name="fraction">Общая готовность 0…1; null — неизвестна.</param>
    public void Report(double? fraction, string status) => _job.Report(fraction, status);

    public void SetDetail(string? detail) => _job.SetDetail(detail);

    public void Log(string line) => _job.AddLog(line);
}

/// <summary>
/// Задача очереди: операция над папкой со своим ходом работы, журналом и отменой. Потокобезопасна: работа
/// сообщает о ходе из своих потоков, интерфейс читает <see cref="Snapshot"/> по событию <see cref="Changed"/>.
/// </summary>
public sealed class Job
{
    /// <summary>Сколько строк журнала хранить (старые отбрасываются).</summary>
    public const int MaxLogLines = 5000;

    private static int _lastId;

    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<DateTime> _clock;
    private JobSnapshot _state = new(JobState.Queued, null, "в очереди", null, null, null, null, null);

    public Job(string title, string folder, Func<JobContext, Task<JobOutcome>> work, Func<DateTime>? clock = null)
    {
        Id = Interlocked.Increment(ref _lastId);
        Title = title;
        Folder = folder;
        Work = work;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Ход работы, состояние или подробности изменились (вызывается из потока работы).</summary>
    public event Action<Job>? Changed;

    /// <summary>Новая строка журнала (вызывается из потока работы).</summary>
    public event Action<Job, string>? LogAdded;

    public int Id { get; }

    /// <summary>«HLS · Sousou no Frieren».</summary>
    public string Title { get; }

    /// <summary>Рабочая папка задачи (кнопка «Папка»).</summary>
    public string Folder { get; }

    public JobSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _state with { Eta = Eta() };
            }
        }
    }

    public JobState State => Snapshot.State;

    public bool IsFinished => Snapshot.IsFinished;

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>Завершается, когда задача закончилась (как угодно).</summary>
    public Task Completion => _completion.Task;

    internal Func<JobContext, Task<JobOutcome>> Work { get; }

    internal CancellationToken Token => _cts.Token;

    /// <summary>Отменить: ждущая — не запустится, идущая — получит отмену (процесс будет убит).</summary>
    public void Cancel()
    {
        bool cancelledWhileQueued;
        lock (_lock)
        {
            if (_state.IsFinished)
            {
                return;
            }

            cancelledWhileQueued = _state.State == JobState.Queued;
            if (cancelledWhileQueued)
            {
                _state = _state with { State = JobState.Cancelled, Status = "отменена", Summary = "отменена до запуска", Finished = _clock() };
            }
            else
            {
                _state = _state with { Status = "отмена…" };
            }
        }

        _cts.Cancel();
        if (cancelledWhileQueued)
        {
            _completion.TrySetResult();
        }

        Changed?.Invoke(this);
    }

    internal bool TryStart()
    {
        lock (_lock)
        {
            if (_state.State != JobState.Queued)
            {
                return false;
            }

            _state = _state with { State = JobState.Running, Status = "запуск…", Started = _clock() };
        }

        Changed?.Invoke(this);
        return true;
    }

    internal void Report(double? fraction, string status)
    {
        lock (_lock)
        {
            if (_state.State != JobState.Running)
            {
                return;
            }

            _state = _state with
            {
                Fraction = fraction is { } f ? Math.Clamp(f, 0, 1) : null,
                Status = _cts.IsCancellationRequested ? "отмена…" : status,
            };
        }

        Changed?.Invoke(this);
    }

    internal void SetDetail(string? detail)
    {
        lock (_lock)
        {
            _state = _state with { Detail = detail };
        }

        Changed?.Invoke(this);
    }

    internal void AddLog(string line)
    {
        var stamped = $"{_clock():HH:mm:ss}  {line}";
        lock (_lock)
        {
            _log.Add(stamped);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }

        LogAdded?.Invoke(this, stamped);
    }

    internal void Finish(JobState state, string summary)
    {
        lock (_lock)
        {
            var status = state switch
            {
                JobState.Done => "готово",
                JobState.Cancelled => "отменена",
                _ => "ошибка",
            };
            _state = _state with
            {
                State = state,
                Status = status,
                Summary = summary,
                Fraction = state == JobState.Done ? 1 : _state.Fraction,
                Finished = _clock(),
            };
        }

        _completion.TrySetResult();
        Changed?.Invoke(this);
    }

    /// <summary>Осталось ≈ прошло × (1 − доля) / доля; рано судить (меньше 1% или 5 секунд) — неизвестно.</summary>
    private TimeSpan? Eta()
    {
        if (_state is not { State: JobState.Running, Started: { } started, Fraction: { } fraction } || fraction < 0.01)
        {
            return null;
        }

        var elapsed = _clock() - started;
        return elapsed < TimeSpan.FromSeconds(5) ? null : elapsed * ((1 - fraction) / fraction);
    }
}
