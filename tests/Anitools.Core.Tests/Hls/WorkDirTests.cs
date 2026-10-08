using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;
using Anitools.Core.WorkDir;

namespace Anitools.Core.Tests.Hls;

/// <summary>Временная папка HLS: своя папка, «рядом с выходом», RAM-диск ImDisk (на фейках, без Windows).</summary>
public sealed class WorkDirTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Free_letter_goes_from_r_then_z_down_to_d()
    {
        Assert.Equal('R', DriveLetters.FindFree(new HashSet<char> { 'C', 'D' }));
        Assert.Equal('Z', DriveLetters.FindFree(new HashSet<char> { 'C', 'r' }));
        Assert.Equal('E', DriveLetters.FindFree(DriveLetters.Preference.TrimEnd('E', 'D').ToHashSet()));
        Assert.Null(DriveLetters.FindFree(DriveLetters.Preference.ToHashSet()));
    }

    [Fact]
    public void State_file_keeps_letters_and_disappears_when_empty()
    {
        using var dir = new TempDir();
        var state = new RamDiskStateFile(dir.Combine("anitools_ramdisk.json"));

        state.Add('R');
        state.Add('Z');
        state.Add('R');
        Assert.Equal(["R", "Z"], state.Read());

        state.Remove('R');
        Assert.Equal(["Z"], state.Read());
        state.Remove('Z');
        Assert.False(File.Exists(state.Path));

        // файл оригинала (json.dumps с пробелом) читается, испорченный — как пустой
        File.WriteAllText(state.Path, """["L", "M"]""");
        Assert.Equal(["L", "M"], state.Read());
        File.WriteAllText(state.Path, "{битый");
        Assert.Empty(state.Read());
        state.Add('Q');
        Assert.Equal(["Q"], state.Read());
    }

    [Fact]
    public async Task Ram_disk_is_created_used_and_removed()
    {
        using var dir = new TempDir();
        var state = new RamDiskStateFile(dir.Combine("state.json"));
        var runner = new ImDiskRunner();
        var drives = new FakeDrives { Used = ['C', 'D', 'R'], AppearAfter = 2 };
        var provider = new ImDiskRamDisk(new ImDisk(runner, "imdisk"), 14, state, drives, requireWindows: false) { Delay = (_, _) => Task.CompletedTask };

        await using (var lease = await provider.AcquireAsync(Ct))
        {
            Assert.Equal(@"Z:\anitools_tmp", lease.Path);
            Assert.Equal("RAM-диск Z: (14 ГБ)", lease.Description);
            Assert.Equal(["Z"], state.Read());
            Assert.Equal(
                [["-l"], ["-a", "-t", "vm", "-s", "14G", "-m", "Z:", "-p", "/fs:ntfs /q /y"]],
                runner.Calls);
        }

        Assert.Equal(["-D", "-m", "Z:"], runner.Calls[^1]);
        Assert.Empty(state.Read());
    }

    [Fact]
    public async Task Ram_disk_failures_explain_what_to_do()
    {
        using var dir = new TempDir();
        var state = new RamDiskStateFile(dir.Combine("state.json"));
        var drives = new FakeDrives();

        var noImDisk = new ImDiskRamDisk(new ImDisk(new ImDiskRunner(), null), 14, state, drives, requireWindows: false);
        Assert.Contains("ImDisk не найден", (await Assert.ThrowsAsync<WorkDirException>(() => noImDisk.AcquireAsync(Ct))).Message);

        var denied = new ImDiskRamDisk(new ImDisk(new ImDiskRunner { CreateExitCode = 1, CreateOutput = "Error creating virtual disk: Access is denied." }, "imdisk"), 14, state, drives, requireWindows: false);
        var ex = await Assert.ThrowsAsync<WorkDirException>(() => denied.AcquireAsync(Ct));
        Assert.Contains("не хватает свободной памяти", ex.Message);
        Assert.Contains("Access is denied", ex.Message);
        Assert.Empty(state.Read());

        var runner = new ImDiskRunner();
        var never = new ImDiskRamDisk(new ImDisk(runner, "imdisk"), 1, state, new FakeDrives { AppearAfter = int.MaxValue }, requireWindows: false)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        Assert.Equal(ImDiskRamDisk.MinSizeGb, never.SizeGb);
        await Assert.ThrowsAsync<WorkDirException>(() => never.AcquireAsync(Ct));
        Assert.Equal(["-D", "-m", "R:"], runner.Calls[^1]); // созданный, но не появившийся диск снимается

        var full = new ImDiskRamDisk(new ImDisk(new ImDiskRunner(), "imdisk"), 14, state, new FakeDrives { Used = [.. DriveLetters.Preference] }, requireWindows: false);
        Assert.Contains("Нет свободной буквы", (await Assert.ThrowsAsync<WorkDirException>(() => full.AcquireAsync(Ct))).Message);
    }

    [Fact]
    public async Task Disks_left_by_killed_run_are_removed()
    {
        using var dir = new TempDir();
        var state = new RamDiskStateFile(dir.Combine("state.json"));
        state.Add('R');
        state.Add('Y');
        state.Add('Q');
        var runner = new ImDiskRunner();
        var drives = new FakeDrives { Used = ['C', 'R', 'Y'] }; // Q уже нет (перезагрузка) — права администратора зря не просим

        var removed = await ImDiskRamDisk.CleanupOrphansAsync(new ImDisk(runner, "imdisk"), state, drives: drives, cancellationToken: Ct);

        Assert.Equal(['R', 'Y'], removed);
        Assert.Equal([["-l"], ["-D", "-m", "R:"], ["-D", "-m", "Y:"]], runner.Calls);
        Assert.False(File.Exists(state.Path));
        Assert.Empty(await ImDiskRamDisk.CleanupOrphansAsync(new ImDisk(runner, "imdisk"), state, drives: drives, cancellationToken: Ct));
    }

    [Fact]
    public async Task Ram_disk_without_admin_rights_goes_through_the_helper()
    {
        using var dir = new TempDir();
        var state = new RamDiskStateFile(dir.Combine("state.json"));
        var runner = new ImDiskRunner();
        var admin = new FakeAdmin();
        var provider = new ImDiskRamDisk(new ImDisk(runner, "imdisk"), 14, state, new FakeDrives(), requireWindows: false, admin: admin);

        await using (var lease = await provider.AcquireAsync(Ct))
        {
            Assert.Equal(@"R:\anitools_tmp", lease.Path);
        }

        // список дисков — сам ImDisk (прав не нужно), создать и снять — помощник
        Assert.Equal([["-l"]], runner.Calls);
        Assert.Equal(["create R 14", "remove R"], admin.Calls);

        state.Add('R');
        await ImDiskRamDisk.CleanupOrphansAsync(new ImDisk(runner, "imdisk"), state, admin, new FakeDrives { Used = ['R'] }, Ct);
        Assert.Equal("remove R", admin.Calls[^1]);
    }

    [Fact]
    public async Task Folder_and_near_output()
    {
        using var dir = new TempDir();
        await using (var lease = await new FolderWorkDir(dir.Combine("tmp", "hls")).AcquireAsync(Ct))
        {
            Assert.True(Directory.Exists(lease.Path));
        }

        var file = dir.File("busy");
        await Assert.ThrowsAsync<WorkDirException>(() => new FolderWorkDir(Path.Combine(file, "sub")).AcquireAsync(Ct));
        await using var near = await new NearOutputWorkDir().AcquireAsync(Ct);
        Assert.Null(near.Path);
    }

    [Fact]
    public async Task Lease_is_released_once()
    {
        var count = 0;
        var lease = new WorkDirLease("X:", "тест", () =>
        {
            count++;
            return Task.CompletedTask;
        });
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, count);
    }

    private sealed class ImDiskRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public int CreateExitCode { get; init; }

        public string CreateOutput { get; init; } = "";

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Calls.Add(spec.Arguments);
            var code = spec.Arguments[0] switch
            {
                "-l" => 1, // дисков нет, программа есть
                "-a" => CreateExitCode,
                _ => 0,
            };
            return Task.FromResult(new ProcessResult(code, spec.Arguments[0] == "-a" ? CreateOutput : "", "", TimeSpan.Zero));
        }
    }

    /// <summary>Помощник с правами администратора: только записывает команды.</summary>
    internal sealed class FakeAdmin : IImDiskAdmin
    {
        public List<string> Calls { get; } = [];

        /// <summary>Ошибка ImDisk на «создать»; null — создаётся.</summary>
        public string? CreateError { get; set; }

        public Task CreateAsync(int sizeGb, char letter, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add($"create {letter} {sizeGb}");
            }

            return CreateError is null ? Task.CompletedTask : throw new WorkDirException(CreateError);
        }

        public Task RemoveAsync(char letter)
        {
            lock (Calls)
            {
                Calls.Add($"remove {letter}");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDrives : IDriveSystem
    {
        private int _checks;

        public HashSet<char> Used { get; init; } = ['C'];

        /// <summary>Диск «появляется» после стольких проверок.</summary>
        public int AppearAfter { get; init; }

        public IReadOnlySet<char> UsedLetters() => Used;

        public bool DriveExists(char letter) => ++_checks > AppearAfter;

        public bool TryCreateDirectory(string path) => true;
    }
}
