using System.ComponentModel;
using System.Text.Json;
using Anitools.Core.Processes;

namespace Anitools.Core.WorkDir;

/// <summary>Диски компьютера: занятые буквы, появился ли диск, создание папки (в тестах — фейк).</summary>
public interface IDriveSystem
{
    IReadOnlySet<char> UsedLetters();

    bool DriveExists(char letter);

    bool TryCreateDirectory(string path);
}

/// <summary>Настоящие диски Windows.</summary>
public sealed class WindowsDriveSystem : IDriveSystem
{
    public IReadOnlySet<char> UsedLetters() =>
        DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();

    public bool DriveExists(char letter) => Directory.Exists($@"{letter}:\");

    public bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Буква для RAM-диска (_find_free_drive_letter, py:4578): первая свободная из R, затем Z → D.</summary>
public static class DriveLetters
{
    public const string Preference = "RZYXWVUTSQPONMLKJIHGFED";

    public static char? FindFree(IReadOnlySet<char> used)
    {
        foreach (var letter in Preference)
        {
            if (!used.Contains(letter) && !used.Contains(char.ToLowerInvariant(letter)))
            {
                return letter;
            }
        }

        return null;
    }
}

/// <summary>
/// Буквы созданных RAM-дисков (%TEMP%\anitools_ramdisk.json, JSON-массив — тот же файл, что у оригинала):
/// если приложение убили и диск не сняли, он снимается при следующем запуске HLS.
/// </summary>
public sealed class RamDiskStateFile(string path)
{
    public static string DefaultPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anitools_ramdisk.json");

    public string Path { get; } = path;

    /// <summary>Буквы из файла; файла нет или он испорчен — пусто.</summary>
    public IReadOnlyList<string> Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Add(char letter)
    {
        var letters = Read().ToList();
        if (!letters.Contains(letter.ToString()))
        {
            letters.Add(letter.ToString());
        }

        Write(letters);
    }

    /// <summary>Убирает букву; больше букв нет — файл удаляется.</summary>
    public void Remove(char letter) => Write([.. Read().Where(l => l != letter.ToString())]);

    public void Delete() => Write([]);

    private void Write(IReadOnlyList<string> letters)
    {
        try
        {
            if (letters.Count == 0)
            {
                File.Delete(Path);
            }
            else
            {
                File.WriteAllText(Path, JsonSerializer.Serialize(letters));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // файл состояния — страховка, его сбой работу не останавливает
        }
    }
}

/// <summary>
/// Команды ImDisk (py:4563–4639). Создавать и снимать диски напрямую можно, только если приложение запущено от
/// администратора; иначе это делает <see cref="ElevatedImDisk"/>.
/// </summary>
public sealed class ImDisk(IProcessRunner runner, string? imdiskPath) : IImDiskAdmin
{
    /// <summary>Установлен ли ImDisk: «imdisk -l» отвечает 0 или 1 (1 — дисков нет, но программа есть).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["-l"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 or 1 };
    }

    /// <summary>RAM-диск в памяти (-t vm), сразу в NTFS. Нужны права администратора.</summary>
    /// <exception cref="WorkDirException">ImDisk вернул ошибку.</exception>
    public async Task CreateAsync(int sizeGb, char letter, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["-a", "-t", "vm", "-s", $"{sizeGb}G", "-m", $"{letter}:", "-p", "/fs:ntfs /q /y"], TimeSpan.FromSeconds(120), cancellationToken)
            .ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            var output = result is null ? "ImDisk не ответил" : string.Join('\n', new[] { result.StandardOutput, result.StandardErrorTail }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            throw new WorkDirException(
                $"Не удалось создать RAM-диск {sizeGb} ГБ. Частые причины — не хватает свободной памяти или ImDisk установлен не полностью."
                + (output.Length > 0 ? $"\nImDisk: {(output.Length > 300 ? output[..300] : output)}" : ""));
        }
    }

    /// <summary>Снять диск (принудительно). imdisk -D трогает только свои виртуальные диски — вызывать безопасно.</summary>
    public async Task RemoveAsync(char letter) =>
        await RunAsync(["-D", "-m", $"{letter}:"], TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);

    /// <returns>null — программы нет, не запустилась или не уложилась во время.</returns>
    private async Task<ProcessResult?> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (imdiskPath is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await runner.RunAsync(new ProcessSpec(imdiskPath, args), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // не уложился во время
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// RAM-диск ImDisk под временные файлы HLS (_setup_work_dir, вариант 1): свободная буква, диск нужного размера,
/// рабочая папка &lt;буква&gt;:\anitools_tmp. Буква записывается в файл состояния, чтобы снять диск, даже если
/// приложение убьют. Снимается при освобождении аренды.
/// </summary>
/// <param name="admin">Кто создаёт и снимает диск: сам ImDisk (приложение от администратора) или помощник с правами.</param>
public sealed class ImDiskRamDisk(ImDisk imdisk, int sizeGb, RamDiskStateFile state, IDriveSystem? drives = null, bool requireWindows = true, IImDiskAdmin? admin = null)
    : IWorkDirProvider
{
    private readonly IImDiskAdmin _admin = admin ?? imdisk;

    public const int DefaultSizeGb = 14;

    public const int MinSizeGb = 2;

    /// <summary>Готовые размеры: обычные серии ~24 мин, с запасом (по умолчанию), длинные серии, фильмы.</summary>
    public static IReadOnlyList<int> PresetSizesGb { get; } = [10, 14, 20, 32];

    private readonly IDriveSystem _drives = drives ?? new WindowsDriveSystem();

    public int SizeGb { get; } = Math.Max(MinSizeGb, sizeGb);

    /// <summary>Пауза между проверками, появился ли диск (в тестах — без ожидания).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<WorkDirLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (requireWindows && !OperatingSystem.IsWindows())
        {
            throw new WorkDirException("RAM-диск через ImDisk есть только на Windows. Выберите папку или «рядом с выходом».");
        }

        if (!await imdisk.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new WorkDirException(
                "ImDisk не найден. Установите бесплатный ImDisk Toolkit (https://sourceforge.net/projects/imdisk-toolkit/) "
                + "или выберите обычную папку для временных файлов.");
        }

        var letter = DriveLetters.FindFree(_drives.UsedLetters())
            ?? throw new WorkDirException("Нет свободной буквы диска. Выберите папку для временных файлов.");
        await _admin.CreateAsync(SizeGb, letter, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < 20 && !_drives.DriveExists(letter); i++)
        {
            await Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        if (!_drives.DriveExists(letter))
        {
            await _admin.RemoveAsync(letter).ConfigureAwait(false);
            throw new WorkDirException($"RAM-диск {letter}: создан, но не открывается (не отформатировался). Выберите папку для временных файлов.");
        }

        state.Add(letter);
        var work = $@"{letter}:\anitools_tmp";
        if (!_drives.TryCreateDirectory(work))
        {
            work = $@"{letter}:\";
        }

        return new WorkDirLease(work, $"RAM-диск {letter}: ({SizeGb} ГБ)", async () =>
        {
            await _admin.RemoveAsync(letter).ConfigureAwait(false);
            state.Remove(letter);
        });
    }

    /// <summary>
    /// Снимает RAM-диски, оставшиеся от аварийно завершённого запуска (_cleanup_orphan_ramdisks), и очищает
    /// файл состояния. Буквы, которых в системе уже нет (перезагрузка, диск снял помощник), не трогаются — чтобы
    /// не спрашивать права администратора зря. Возвращает снятые буквы.
    /// </summary>
    public static async Task<IReadOnlyList<char>> CleanupOrphansAsync(
        ImDisk imdisk, RamDiskStateFile state, IImDiskAdmin? admin = null, IDriveSystem? drives = null, CancellationToken cancellationToken = default)
    {
        var present = (drives ?? new WindowsDriveSystem()).UsedLetters();
        var letters = state.Read().Where(l => l.Length == 1 && char.IsAsciiLetter(l[0])).Select(l => char.ToUpperInvariant(l[0]))
            .Where(present.Contains).ToList();
        if (letters.Count > 0 && await imdisk.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var letter in letters)
            {
                await (admin ?? imdisk).RemoveAsync(letter).ConfigureAwait(false);
            }
        }
        else
        {
            letters = [];
        }

        state.Delete();
        return letters;
    }
}

/// <summary>Поставщик временной папки по настройкам.</summary>
public static class WorkDirProviders
{
    /// <param name="admin">Кто создаёт RAM-диск; null — сам ImDisk (нужны права администратора).</param>
    public static IWorkDirProvider Create(WorkDirSettings settings, IProcessRunner runner, string? imdiskPath, IImDiskAdmin? admin = null) => settings.Mode switch
    {
        WorkDirMode.RamDisk => new ImDiskRamDisk(new ImDisk(runner, imdiskPath), settings.RamDiskGb, new RamDiskStateFile(RamDiskStateFile.DefaultPath), admin: admin),
        WorkDirMode.Folder => new FolderWorkDir(settings.Folder),
        _ => new NearOutputWorkDir(),
    };
}
