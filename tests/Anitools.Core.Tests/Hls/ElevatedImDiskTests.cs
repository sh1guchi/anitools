using System.ComponentModel;
using System.IO.Pipes;
using Anitools.Core.WorkDir;

namespace Anitools.Core.Tests.Hls;

/// <summary>
/// RAM-диск без прав администратора: приложение и помощник говорят по настоящему именованному каналу, только
/// помощник работает в этом же процессе, а вместо ImDisk — запись команд.
/// </summary>
public sealed class ElevatedImDiskTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Helper_creates_and_removes_disks_and_cleans_up_when_the_app_closes()
    {
        var admin = new WorkDirTests.FakeAdmin();
        Task<int>? helper = null;
        var elevated = new ElevatedImDisk((pipe, _) =>
        {
            helper = Task.Run(() => ImDiskHelper.RunAsync(pipe, Environment.ProcessId, admin, Ct));
            return Task.FromResult<int?>(null);
        });

        await elevated.EnsureStartedAsync(Ct); // «Начать»: разрешение спрашивается сразу
        await elevated.CreateAsync(14, 'R', Ct);
        await elevated.CreateAsync(10, 'Z', Ct);
        await elevated.RemoveAsync('R');
        admin.CreateError = "Не удалось создать RAM-диск 32 ГБ.\nImDisk: Not enough memory";
        var ex = await Assert.ThrowsAsync<WorkDirException>(() => elevated.CreateAsync(32, 'Y', Ct));
        Assert.Equal(admin.CreateError, ex.Message);
        Assert.Equal(1, elevated.Launches); // одно разрешение Windows на всё время работы

        // приложение закрылось — помощник снимает оставшийся диск и выходит
        elevated.Dispose();
        Assert.Equal(0, await helper!.WaitAsync(Wait, Ct));
        Assert.Equal(["create R 14", "create Z 10", "remove R", "create Y 32", "remove Z"], admin.Calls);
    }

    [Fact]
    public async Task A_lost_helper_is_started_again()
    {
        var admin = new WorkDirTests.FakeAdmin();
        var helpers = new List<(Task<int> Run, CancellationTokenSource Stop)>();
        using var elevated = new ElevatedImDisk((pipe, _) =>
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            helpers.Add((Task.Run(() => ImDiskHelper.RunAsync(pipe, Environment.ProcessId, admin, stop.Token)), stop));
            return Task.FromResult<int?>(null);
        });
        await elevated.CreateAsync(14, 'R', Ct);

        // помощник пропал (канал закрыт) — следующая команда запускает нового
        await helpers[0].Stop.CancelAsync();
        await helpers[0].Run.WaitAsync(Wait, Ct);
        await elevated.CreateAsync(14, 'Z', Ct);
        Assert.Equal(2, elevated.Launches);

        // проверка перед «Начать» замечает пропажу заранее
        await helpers[1].Stop.CancelAsync();
        await helpers[1].Run.WaitAsync(Wait, Ct);
        await elevated.EnsureStartedAsync(Ct);
        Assert.Equal(3, elevated.Launches);
        await elevated.EnsureStartedAsync(Ct);
        Assert.Equal(3, elevated.Launches);
        Assert.Equal(["create R 14", "remove R", "create Z 14", "remove Z"], admin.Calls);
        foreach (var (_, stop) in helpers)
        {
            stop.Dispose();
        }
    }

    [Fact]
    public async Task Declined_permission_and_silent_helper_explain_what_to_do()
    {
        using var declined = new ElevatedImDisk((_, _) => throw new Win32Exception(1223));
        var ex = await Assert.ThrowsAsync<WorkDirException>(() => declined.CreateAsync(14, 'R', Ct));
        Assert.Contains("запрос Windows отклонён", ex.Message);
        Assert.Contains("папку", ex.Message);
        await declined.RemoveAsync('R'); // снять без прав не вышло — молча, снимется в следующий раз

        using var silent = new ElevatedImDisk((_, _) => Task.FromResult<int?>(null)) { ConnectTimeout = TimeSpan.FromMilliseconds(200) };
        Assert.Contains("не запустился", (await Assert.ThrowsAsync<WorkDirException>(() => silent.CreateAsync(14, 'R', Ct))).Message);
    }

    [Fact]
    public async Task Helper_accepts_only_its_two_commands()
    {
        var admin = new WorkDirTests.FakeAdmin();
        var name = "anitools-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var helper = Task.Run(() => ImDiskHelper.RunAsync(name, Environment.ProcessId, admin, Ct));
        await server.WaitForConnectionAsync(Ct);
        using var reader = new StreamReader(server, leaveOpen: true);
        var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await using (writer)
        {
            async Task<string?> Ask(string line)
            {
                await writer.WriteLineAsync(line.AsMemory(), Ct);
                return await reader.ReadLineAsync(Ct);
            }

            Assert.Contains("\"Ok\":false", await Ask("format C:"));
            Assert.Contains("\"Ok\":false", await Ask("""{"Op":"create","Letter":"R","SizeGb":5000}""")); // слишком большой
            Assert.Contains("\"Ok\":false", await Ask("""{"Op":"create","Letter":"1","SizeGb":14}""")); // не буква диска
            Assert.Contains("\"Ok\":false", await Ask("""{"Op":"run","Letter":"R"}"""));
            Assert.Contains("\"Ok\":true", await Ask("""{"Op":"ping","Letter":"A"}"""));
            Assert.Contains("\"Ok\":true", await Ask("""{"Op":"create","Letter":"R","SizeGb":14}"""));
            Assert.Equal(["create R 14"], admin.Calls);
        }

        server.Disconnect();
        Assert.Equal(0, await helper.WaitAsync(Wait, Ct));
        Assert.Equal(["create R 14", "remove R"], admin.Calls);
    }
}
