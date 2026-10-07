using System.Collections.Concurrent;

namespace Anitools.Core.Media;

/// <summary>
/// Запоминает ответы ffprobe и mkvmerge по файлу (путь, размер, время изменения): экран пересчитывает план при каждой
/// смене настроек, а читать заново все серии незачем. Файл изменился — читается снова; ошибки не запоминаются.
/// </summary>
public sealed class CachedMediaProbe(IMediaProbe inner) : IMediaProbe
{
    private readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), Task<MediaInfo>> _probes = new();
    private readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), Task<MkvIdentification>> _identifications = new();

    public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
        GetAsync(_probes, path, () => inner.ProbeAsync(path, CancellationToken.None), cancellationToken);

    public Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
        GetAsync(_identifications, path, () => inner.IdentifyAsync(path, CancellationToken.None), cancellationToken);

    /// <summary>Забыть всё (кнопка «Перечитать папку»).</summary>
    public void Clear()
    {
        _probes.Clear();
        _identifications.Clear();
    }

    private static async Task<T> GetAsync<T>(
        ConcurrentDictionary<(string, long, DateTime), Task<T>> cache, string path, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var key = (path, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
        // Чтение не отменяется вместе с экраном: результат пригодится в следующий раз; ждём его с отменой
        var task = cache.GetOrAdd(key, _ => read());
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (task.IsFaulted || task.IsCanceled)
        {
            cache.TryRemove(new KeyValuePair<(string, long, DateTime), Task<T>>(key, task));
            throw;
        }
    }
}
