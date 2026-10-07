using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Anitools.App.Startup;

/// <summary>
/// Одна копия приложения (docs/PLAN.md §4.12). Первая копия держит именованный мьютекс и слушает именованный канал;
/// следующая (команда ani в другой папке) передаёт ей папку и сразу выходит. Канал и мьютекс — только для текущего
/// пользователя.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listening;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Имя на пользователя: у разных пользователей Windows — разные копии.</summary>
    public static string DefaultName => "anitools-" + new string([.. Environment.UserName.Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>Первая копия — объект, который надо держать до выхода; копия уже запущена — null.</summary>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, name);
    }

    /// <summary>Передать запрос первой копии; false — она не ответила (зависла или ещё запускается).</summary>
    public static bool TryForward(string name, StartupRequest request, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            if (OperatingSystem.IsWindows())
            {
                // Иначе Windows не даст первой копии выйти на передний план: сейчас фокус у консоли этой копии
                _ = AllowSetForegroundWindow(-1);
            }

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(request));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Слушать запросы следующих копий; <paramref name="onRequest"/> вызывается из фонового потока.</summary>
    public void StartListening(Action<StartupRequest> onRequest)
    {
        _listening ??= Task.Run(() => ListenAsync(onRequest, _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listening?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // мьютекс захвачен другим потоком — освободится при выходе процесса
        }

        _mutex.Dispose();
    }

    private async Task ListenAsync(Action<StartupRequest> onRequest, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is not null && Deserialize(line) is { } request)
                {
                    onRequest(request);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // клиент отвалился посреди передачи — ждём следующего
            }
        }
    }

    private static StartupRequest? Deserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<StartupRequest>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
