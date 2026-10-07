using Anitools.App.Startup;

namespace Anitools.App.Tests;

/// <summary>Запуск командой ani (docs/PLAN.md §4.12): разбор папки и одна копия приложения.</summary>
public sealed class StartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anitools-app-tests", Guid.NewGuid().ToString("N"));

    public StartupTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Sousou no Frieren"));
        File.WriteAllText(Path.Combine(_root, "Sousou no Frieren", "Frieren - 01.mkv"), "");
    }

    [Fact]
    public void Folder_argument_variants()
    {
        var show = Path.Combine(_root, "Sousou no Frieren");

        Assert.Equal(StartupRequest.None, StartupArguments.Parse([], _root));
        Assert.Equal(show, StartupArguments.Parse([show], "/").Folder);
        // относительный путь — от текущей папки консоли; «.» — она сама (ani.bat передаёт "%CD%\.")
        Assert.Equal(show, StartupArguments.Parse(["Sousou no Frieren"], _root).Folder);
        Assert.Equal(show, StartupArguments.Parse([Path.Combine(show, ".")], "/").Folder);
        // без кавычек путь с пробелами приходит частями
        Assert.Equal(show, StartupArguments.Parse([Path.Combine(_root, "Sousou"), "no", "Frieren"], "/").Folder);
        // файл — его папка; хвостовая косая и кавычка срезаются
        Assert.Equal(show, StartupArguments.Parse([Path.Combine(show, "Frieren - 01.mkv")], "/").Folder);
        Assert.Equal(show, StartupArguments.Parse([show + Path.DirectorySeparatorChar + "\""], "/").Folder);

        var missing = StartupArguments.Parse([Path.Combine(_root, "нет такой")], "/");
        Assert.Null(missing.Folder);
        Assert.Equal($"Папка не найдена: {Path.Combine(_root, "нет такой")}", missing.Error);
    }

    [Fact]
    public void Drive_letter_cleaning()
    {
        // "D:\" в командной строке Windows приходит как D:" — это корень диска
        Assert.Equal("D:\\", StartupArguments.Clean("D:\""));
        Assert.Equal("D:\\", StartupArguments.Clean("D:"));
        Assert.Equal(@"D:\anime\X", StartupArguments.Clean(" \"D:\\anime\\X\" "));
    }

    [Fact]
    public async Task Second_instance_forwards_folder_to_first()
    {
        var name = "anitools-test-" + Guid.NewGuid().ToString("N")[..12];
        using var primary = SingleInstance.TryBecomePrimary(name);
        Assert.NotNull(primary);
        Assert.Null(SingleInstance.TryBecomePrimary(name));

        var received = new TaskCompletionSource<StartupRequest>();
        primary.StartListening(r => received.TrySetResult(r));
        var request = new StartupRequest(Path.Combine(_root, "Sousou no Frieren"), null);

        Assert.True(SingleInstance.TryForward(name, request, TimeSpan.FromSeconds(10)));
        Assert.Equal(request, await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(SingleInstance.TryForward("anitools-test-nobody-" + Guid.NewGuid().ToString("N")[..8], request, TimeSpan.FromMilliseconds(200)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
