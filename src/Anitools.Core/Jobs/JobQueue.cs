namespace Anitools.Core.Jobs;

/// <summary>
/// Очередь задач (docs/PLAN.md §3.4): выполняются строго по одной — видеокарта и диск заняты одной командой,
/// остальные ждут. Ошибка или исключение задачи не останавливает очередь.
/// </summary>
public sealed class JobQueue(Func<DateTime>? clock = null)
{
    private readonly object _lock = new();
    private readonly List<Job> _jobs = [];
    private bool _running;
    private Job? _current;

    public event Action<Job>? JobAdded;

    public event Action<Job>? JobRemoved;

    public event Action<Job>? JobStarted;

    /// <summary>Задача закончилась (из потока очереди): например, страницам её папки пора обновить план.</summary>
    public event Action<Job>? JobFinished;

    /// <summary>Все задачи по порядку постановки.</summary>
    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs];
            }
        }
    }

    public Job? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Есть ждущие или идущая задача.</summary>
    public bool IsBusy => Jobs.Any(j => !j.IsFinished);

    public Job Enqueue(string title, string folder, Func<JobContext, Task<JobOutcome>> work)
    {
        var job = new Job(title, folder, work, clock);
        bool start;
        lock (_lock)
        {
            _jobs.Add(job);
            start = !_running;
            _running = true;
        }

        JobAdded?.Invoke(job);
        if (start)
        {
            _ = Task.Run(RunLoopAsync);
        }

        return job;
    }

    /// <summary>Отменить всё: ждущие не запустятся, идущая получит отмену.</summary>
    public void CancelAll()
    {
        foreach (var job in Jobs)
        {
            job.Cancel();
        }
    }

    /// <summary>Дождаться, пока закончатся все задачи, поставленные к этому моменту.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(Jobs.Where(j => !j.IsFinished).Select(j => j.Completion));

    /// <summary>Убрать из списка закончившиеся задачи.</summary>
    public void ClearFinished()
    {
        List<Job> removed;
        lock (_lock)
        {
            removed = [.. _jobs.Where(j => j.IsFinished)];
            _jobs.RemoveAll(removed.Contains);
        }

        foreach (var job in removed)
        {
            JobRemoved?.Invoke(job);
        }
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            Job? next;
            lock (_lock)
            {
                next = _jobs.FirstOrDefault(j => j.State == JobState.Queued);
                _current = next;
                if (next is null)
                {
                    _running = false;
                    return;
                }
            }

            await RunAsync(next).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Job job)
    {
        if (!job.TryStart())
        {
            return; // отменили, пока ждала
        }

        JobStarted?.Invoke(job);
        try
        {
            var outcome = await job.Work(new JobContext(job)).ConfigureAwait(false);
            var state = job.Token.IsCancellationRequested ? JobState.Cancelled : outcome.Success ? JobState.Done : JobState.Failed;
            job.Finish(state, outcome.Summary);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.Finish(JobState.Cancelled, "отменена");
        }
#pragma warning disable CA1031 // задача не должна ронять очередь: любая ошибка — итог этой задачи
        catch (Exception ex)
#pragma warning restore CA1031
        {
            job.AddLog("Ошибка: " + ex);
            job.Finish(JobState.Failed, ex.Message);
        }

        // К событию «закончилась» текущей задачи уже нет — строка состояния не должна её показывать
        lock (_lock)
        {
            if (_current == job)
            {
                _current = null;
            }
        }

        JobFinished?.Invoke(job);
    }
}
