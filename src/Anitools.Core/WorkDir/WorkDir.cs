namespace Anitools.Core.WorkDir;

/// <summary>Куда писать временные файлы HLS (выбор §2.5: RAM-диск / папка / «как раньше»).</summary>
public enum WorkDirMode
{
    /// <summary>RAM-диск ImDisk: SSD не изнашивается; создаётся перед работой и снимается после.</summary>
    RamDisk,

    /// <summary>Обычная папка (HDD/SSD).</summary>
    Folder,

    /// <summary>Рядом с выходом, в hls_multi\&lt;тайтл&gt;\&lt;серия&gt; — «как раньше».</summary>
    NearOutput,
}

public sealed record WorkDirSettings
{
    public WorkDirMode Mode { get; init; } = WorkDirMode.RamDisk;

    public string Folder { get; init; } = @"D:\anitools_tmp";

    public int RamDiskGb { get; init; } = ImDiskRamDisk.DefaultSizeGb;
}

/// <summary>Временную папку получить не удалось; сообщение — для пользователя (что делать дальше).</summary>
public sealed class WorkDirException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Временная папка на время работы. <see cref="Path"/> = null — писать рядом с выходом.
/// Dispose освобождает её (снимает RAM-диск); повторный вызов ничего не делает.
/// </summary>
public sealed class WorkDirLease : IAsyncDisposable
{
    private readonly Func<Task>? _release;
    private readonly EventHandler? _onProcessExit;
    private int _released;

    public WorkDirLease(string? path, string description, Func<Task>? release = null)
    {
        Path = path;
        Description = description;
        _release = release;
        if (release is not null)
        {
            // Страховка: приложение закрыли, а задача ещё шла — диск всё равно снимается
            _onProcessExit = (_, _) => ReleaseAsync().GetAwaiter().GetResult();
            AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
        }
    }

    public string? Path { get; }

    /// <summary>Для журнала и статус-строки: «RAM-диск R: (14 ГБ)», «папка D:\anitools_tmp», «рядом с выходом».</summary>
    public string Description { get; }

    public async ValueTask DisposeAsync()
    {
        if (_onProcessExit is not null)
        {
            AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    private Task ReleaseAsync() =>
        Interlocked.Exchange(ref _released, 1) == 0 && _release is not null ? _release() : Task.CompletedTask;
}

/// <summary>Выдаёт временную папку для HLS.</summary>
public interface IWorkDirProvider
{
    /// <exception cref="WorkDirException">Не получилось — с объяснением, что делать.</exception>
    Task<WorkDirLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>«Как раньше»: временные файлы рядом с выходом.</summary>
public sealed class NearOutputWorkDir : IWorkDirProvider
{
    public Task<WorkDirLease> AcquireAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkDirLease(null, "рядом с выходом"));
}

/// <summary>Обычная папка (по умолчанию D:\anitools_tmp); создаётся, если её нет.</summary>
public sealed class FolderWorkDir(string folder) : IWorkDirProvider
{
    public Task<WorkDirLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new WorkDirException($"Не удалось создать папку {folder}: {ex.Message}", ex);
        }

        return Task.FromResult(new WorkDirLease(folder, $"папка {folder}"));
    }
}
