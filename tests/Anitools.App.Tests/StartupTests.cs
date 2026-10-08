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
    public void Ani_without_arguments_opens_the_console_folder()
    {
        Assert.True(AniCommand.IsAni(Path.Combine(_root, "Programs", "anitools", "ani.exe")));
        Assert.True(AniCommand.IsAni("/opt/anitools/ANI"));
        Assert.False(AniCommand.IsAni(Path.Combine(_root, "Programs", "anitools", "Anitools.exe")));
        Assert.False(AniCommand.IsAni(null));

        // ani — текущая папка консоли; ani с папкой и обычный запуск Anitools.exe — как есть
        Assert.Equal([_root], AniCommand.Arguments([], "ani.exe", _root));
        Assert.Equal(["D:\\anime"], AniCommand.Arguments(["D:\\anime"], "ani.exe", _root));
        Assert.Empty(AniCommand.Arguments([], "Anitools.exe", _root));
        Assert.Equal(Path.Combine(_root, "Sousou no Frieren"), StartupArguments.Parse(AniCommand.Arguments([], "ani", Path.Combine(_root, "Sousou no Frieren")), _root).Folder);
    }

    [Fact]
    public void Exit_is_typed_into_the_console_as_key_presses()
    {
        var keys = AniCommand.KeyPresses("exit\r");

        Assert.Equal(20, System.Runtime.InteropServices.Marshal.SizeOf<AniCommand.InputRecord>()); // INPUT_RECORD
        Assert.Equal(10, keys.Length); // на каждый символ — нажатие и отпускание
        Assert.All(keys, k => Assert.Equal(1, k.EventType)); // KEY_EVENT
        Assert.Equal([1, 0, 1, 0, 1, 0, 1, 0, 1, 0], keys.Select(k => k.KeyDown));
        Assert.Equal("eexxiitt\r\r", new string([.. keys.Select(k => (char)k.UnicodeChar)]));
        Assert.Equal(['E', 'X', 'I', 'T', '\r'], keys.Where(k => k.KeyDown == 1).Select(k => (char)k.VirtualKeyCode));
    }

    [Fact]
    public void Folder_argument_variants()
    {
        var show = Path.Combine(_root, "Sousou no Frieren");
        var elsewhere = Path.GetTempPath();

        Assert.Equal(StartupRequest.None, StartupArguments.Parse([], _root));
        Assert.Equal(show, StartupArguments.Parse([show], elsewhere).Folder);
        // относительный путь — от текущей папки консоли; «.» — она сама (ani.bat передаёт "%CD%\.")
        Assert.Equal(show, StartupArguments.Parse(["Sousou no Frieren"], _root).Folder);
        Assert.Equal(show, StartupArguments.Parse([Path.Combine(show, ".")], elsewhere).Folder);
        // без кавычек путь с пробелами приходит частями
        Assert.Equal(show, StartupArguments.Parse([Path.Combine(_root, "Sousou"), "no", "Frieren"], elsewhere).Folder);
        // файл — его папка; хвостовая косая и кавычка срезаются
        Assert.Equal(show, StartupArguments.Parse([Path.Combine(show, "Frieren - 01.mkv")], elsewhere).Folder);
        Assert.Equal(show, StartupArguments.Parse([show + Path.DirectorySeparatorChar + "\""], elsewhere).Folder);

        var missing = StartupArguments.Parse([Path.Combine(_root, "нет такой")], elsewhere);
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
