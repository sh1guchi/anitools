using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Anitools.Core.Processes;
using Microsoft.Win32.SafeHandles;

namespace Anitools.Core.WorkDir;

/// <summary>Команды ImDisk, которым нужны права администратора: создать диск (с форматированием) и снять его.</summary>
public interface IImDiskAdmin
{
    /// <exception cref="WorkDirException">Диск не создан: ошибка ImDisk или Windows не дала прав администратора.</exception>
    Task CreateAsync(int sizeGb, char letter, CancellationToken cancellationToken = default);

    /// <summary>Снять диск; не вышло — молча (снимается и при следующем запуске).</summary>
    Task RemoveAsync(char letter);
}

/// <summary>Команда помощнику: create (с размером), remove или ping (жив ли). Одна строка JSON.</summary>
public sealed record ImDiskHelperRequest(string Op, char Letter, int SizeGb = 0);

/// <summary>Ответ помощника. Одна строка JSON.</summary>
public sealed record ImDiskHelperReply(bool Ok, string? Error = null);

/// <summary>
/// Помощник с правами администратора: <c>Anitools.exe --imdisk-helper &lt;канал&gt; &lt;pid приложения&gt;</c>.
/// Подключается к именованному каналу приложения и создаёт/снимает RAM-диски по его командам. Канал закрылся
/// (приложение закрыли или оно упало) — снимает все созданные им диски и выходит. Принимает только «создать» и
/// «снять» с проверенными буквой и размером (и «ping»), а imdisk.exe берёт только из System32: работая от администратора, он не
/// запускает программ по путям, которые можно подменить без этих прав.
/// </summary>
public static partial class ImDiskHelper
{
    public const string Argument = "--imdisk-helper";

    /// <summary>Самый большой RAM-диск, который помощник согласится создать, ГБ.</summary>
    public const int MaxSizeGb = 1024;

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Точка входа процесса-помощника (из Main). 0 — канал закрыт штатно, 2 — не подключился, 3 — чужой канал.</summary>
    public static Task<int> RunAsync(string pipeName, int appProcessId) =>
        RunAsync(pipeName, appProcessId, new ImDisk(new ProcessRunner(), Path.Combine(Environment.SystemDirectory, "imdisk.exe")));

    public static async Task<int> RunAsync(string pipeName, int appProcessId, IImDiskAdmin imdisk, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return 2;
        }

        // Команды принимаются только от приложения, которое запустило помощника
        if (OperatingSystem.IsWindows() && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != appProcessId))
        {
            return 3;
        }

        await ServeAsync(pipe, imdisk, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Выполнять команды, пока канал не закроется; затем снять оставшиеся созданные диски.</summary>
    public static async Task ServeAsync(Stream channel, IImDiskAdmin imdisk, CancellationToken cancellationToken = default)
    {
        var created = new HashSet<char>();
        using var reader = new StreamReader(channel, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(channel, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await using (writer.ConfigureAwait(false))
        {
            try
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    var reply = await ExecuteAsync(line, imdisk, created, cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // приложение пропало посреди ответа
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                foreach (var letter in created)
                {
                    await imdisk.RemoveAsync(letter).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<ImDiskHelperReply> ExecuteAsync(string line, IImDiskAdmin imdisk, HashSet<char> created, CancellationToken cancellationToken)
    {
        ImDiskHelperRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ImDiskHelperRequest>(line);
        }
        catch (JsonException)
        {
            request = null;
        }

        switch (request)
        {
            case { Op: "create", Letter: >= 'A' and <= 'Z', SizeGb: >= 1 and <= MaxSizeGb }:
                try
                {
                    await imdisk.CreateAsync(request.SizeGb, request.Letter, cancellationToken).ConfigureAwait(false);
                    created.Add(request.Letter);
                    return new ImDiskHelperReply(true);
                }
                catch (WorkDirException ex)
                {
                    return new ImDiskHelperReply(false, ex.Message);
                }

            case { Op: "ping" }:
                return new ImDiskHelperReply(true);

            case { Op: "remove", Letter: >= 'A' and <= 'Z' }:
                // imdisk -D снимает только виртуальные диски ImDisk — настоящий диск так не отключить
                await imdisk.RemoveAsync(request.Letter).ConfigureAwait(false);
                created.Remove(request.Letter);
                return new ImDiskHelperReply(true);

            default:
                return new ImDiskHelperReply(false, "Непонятная команда.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

/// <summary>
/// RAM-диск без запуска приложения от администратора. Создать и отформатировать диск ImDisk может только
/// администратор (без прав диск появляется без файловой системы), поэтому команды выполняет помощник
/// (<see cref="ImDiskHelper"/>), запущенный с правами администратора. Windows спрашивает разрешение (UAC) при
/// первом RAM-диске; помощник живёт до закрытия приложения, так что следующие задачи идут без вопросов.
/// Помощник пропал — при следующей команде запускается новый.
/// </summary>
public sealed class ElevatedImDisk : IImDiskAdmin, IDisposable
{
    private const int ErrorCancelled = 1223;

    private readonly Func<string, CancellationToken, Task<int?>> _launch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <param name="launch">
    /// Запустить помощника для канала с этим именем; результат — его PID (null — неизвестен). По умолчанию — этот же
    /// exe через «Запуск от имени администратора». Отказ в UAC — Win32Exception с кодом 1223.
    /// </param>
    public ElevatedImDisk(Func<string, CancellationToken, Task<int?>>? launch = null) => _launch = launch ?? LaunchElevatedAsync;

    /// <summary>Сколько ждать подключения помощника после разрешения.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько раз запускали помощника (для тестов).</summary>
    public int Launches { get; private set; }

    /// <summary>
    /// Запустить помощника заранее — когда пользователь нажал «Начать», а не когда задача дойдёт до очереди: запрос
    /// Windows появится сразу, а отказ виден до запуска.
    /// </summary>
    /// <exception cref="WorkDirException">Запрос отклонён или помощник не запустился.</exception>
    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        await SendAsync(new ImDiskHelperRequest("ping", 'A'), cancellationToken).ConfigureAwait(false); // жив ли прежний помощник

    public async Task CreateAsync(int sizeGb, char letter, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(new ImDiskHelperRequest("create", letter, sizeGb), cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            throw new WorkDirException(reply.Error ?? "Не удалось создать RAM-диск.");
        }
    }

    public async Task RemoveAsync(char letter)
    {
        try
        {
            await SendAsync(new ImDiskHelperRequest("remove", letter), CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkDirException)
        {
            // не дали прав или помощник не запустился — диск останется до следующего запуска (файл состояния)
        }
    }

    /// <summary>Закрыть канал: помощник снимет свои диски и выйдет.</summary>
    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }

    private async Task<ImDiskHelperReply> SendAsync(ImDiskHelperRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Вторая попытка — если прежний помощник успел пропасть (его убили): запускается новый
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_pipe is null)
                {
                    await ConnectAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line
                        && JsonSerializer.Deserialize<ImDiskHelperReply>(line) is { } reply)
                    {
                        return reply;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
                {
                }

                Disconnect();
            }

            throw new WorkDirException("Помощник с правами администратора не отвечает. Выберите папку для временных файлов.");
        }
        catch (OperationCanceledException)
        {
            // ответ на прерванную команду придёт позже и собьёт порядок — начинаем с новым помощником
            Disconnect();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var name = "anitools-imdisk-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        try
        {
            int? helper;
            try
            {
                Launches++;
                helper = await _launch(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new WorkDirException(
                    "Для RAM-диска нужны права администратора, а запрос Windows отклонён. Разрешите его при следующем запуске "
                    + "или выберите папку для временных файлов.");
            }
            catch (Win32Exception ex)
            {
                throw new WorkDirException($"Не удалось запустить помощника для RAM-диска: {ex.Message}. Выберите папку для временных файлов.");
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new WorkDirException("Помощник с правами администратора не запустился. Выберите папку для временных файлов.");
                }
            }

            if (OperatingSystem.IsWindows() && helper is { } pid
                && (!ImDiskHelper.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != pid))
            {
                throw new WorkDirException("К каналу RAM-диска подключилась чужая программа. Выберите папку для временных файлов.");
            }

            _pipe = pipe;
            _reader = new StreamReader(pipe, ImDiskHelper.Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(pipe, ImDiskHelper.Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            pipe = null;
        }
        finally
        {
            if (pipe is not null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Disconnect()
    {
        // читатель и писатель канал не закрывают (leaveOpen), а их Dispose сбрасывает буфер в оборванный канал и падает
        try
        {
            _pipe?.Dispose();
        }
        catch (IOException)
        {
        }

        (_reader, _writer, _pipe) = (null, null, null);
    }

    /// <summary>Этот же exe с ключом помощника, «от имени администратора» (Windows покажет запрос UAC).</summary>
    private static Task<int?> LaunchElevatedAsync(string pipeName, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var exe = Environment.ProcessPath ?? throw new Win32Exception("не найден путь к программе");
            var arguments = $"{ImDiskHelper.Argument} {pipeName} {Environment.ProcessId}";
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
            {
                arguments = $"\"{dll}\" {arguments}"; // запуск из исходников: dotnet Anitools.App.dll
            }

            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return process?.Id;
        },
        cancellationToken);
}
