using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Anitools.Core.Processes;

namespace Anitools.Core.Install;

/// <summary>Что anitools умеет поставить сам (кнопка «Установить» в настройках).</summary>
public enum ToolPackage
{
    /// <summary>ffmpeg и ffprobe.</summary>
    Ffmpeg,

    /// <summary>mkvmerge, mkvextract, mkvinfo, mkvpropedit.</summary>
    MkvToolNix,

    /// <summary>Драйвер RAM-диска.</summary>
    ImDisk,
}

/// <summary>Ход установки: что делается и сколько скачано (байты; всего — null, если сайт не сказал).</summary>
public sealed record InstallProgress(string Stage, long Downloaded = 0, long? Total = null);

/// <summary>Что поставилось: версия (у ImDisk — null) и пути программ (ImDisk ищется сам в System32 — путей нет).</summary>
public sealed record InstallResult(ToolPackage Package, string? Version, IReadOnlyDictionary<Tool, string> Paths);

/// <summary>Установка не удалась; сообщение — для пользователя.</summary>
public sealed class InstallException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Установка последних версий программ без прав администратора — в %LOCALAPPDATA%\anitools\tools:
/// ffmpeg — сборка «essentials» с gyan.dev (ffmpeg.exe, ffprobe.exe), MKVToolNix — portable-архив с
/// mkvtoolnix.download (mkvmerge, mkvextract, mkvinfo, mkvpropedit). Архив сверяется с SHA-256 с того же сайта,
/// из него берутся только нужные exe — в папку с номером версии (прежнюю может держать идущая задача; она
/// удаляется, когда освободится). ImDisk — драйвер: его ставит собственный установщик автора (подписан) с правами
/// администратора, Windows спросит разрешение.
/// </summary>
public sealed partial class ToolInstaller
{
    public const string FfmpegVersionUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";
    public const string MkvToolNixReleaseUrl = "https://mkvtoolnix.download/latest-release.xml";
    public const string ImDiskInstallerUrl = "https://www.ltr-data.se/files/imdiskinst.exe";

    private const int ErrorCancelled = 1223;

    private static readonly string[] FfmpegFiles = ["ffmpeg.exe", "ffprobe.exe"];
    private static readonly string[] MkvToolNixFiles = ["mkvmerge.exe", "mkvextract.exe", "mkvinfo.exe", "mkvpropedit.exe"];

    private readonly HttpClient _http;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<int>> _runElevated;
    private readonly Func<bool> _imdiskInstalled;

    /// <param name="root">Куда ставить; по умолчанию %LOCALAPPDATA%\anitools\tools.</param>
    /// <param name="runElevated">Запустить программу с правами администратора и дождаться кода выхода (установщик ImDisk).</param>
    /// <param name="imdiskInstalled">Есть ли imdisk.exe после установки (по умолчанию — в System32).</param>
    public ToolInstaller(
        HttpClient http,
        string? root = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? runElevated = null,
        Func<bool>? imdiskInstalled = null)
    {
        _http = http;
        Root = root ?? DefaultRoot;
        _runElevated = runElevated ?? RunElevatedAsync;
        _imdiskInstalled = imdiskInstalled ?? (() => File.Exists(Path.Combine(Environment.SystemDirectory, "imdisk.exe")));
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anitools", "tools");

    public string Root { get; }

    /// <summary>Сколько ждать следующего куска при скачивании, прежде чем считать связь оборванной.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public static string FfmpegZipUrl(string version) => $"https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-{version}-essentials_build.zip";

    public static string MkvToolNixZipUrl(string version) =>
        $"https://mkvtoolnix.download/windows/releases/{version}/mkvtoolnix-64-bit-{version}.zip";

    public static string MkvToolNixSumsUrl(string version) => $"https://mkvtoolnix.download/windows/releases/{version}/sha256sums.txt";

    /// <summary>Последняя версия на сайте; у ImDisk номера нет — null.</summary>
    /// <exception cref="InstallException">Сайт не ответил или ответил не то.</exception>
    public async Task<string?> LatestVersionAsync(ToolPackage package, CancellationToken cancellationToken = default) => package switch
    {
        ToolPackage.Ffmpeg => Version((await GetTextAsync(FfmpegVersionUrl, cancellationToken).ConfigureAwait(false)).Trim(), "ffmpeg"),
        ToolPackage.MkvToolNix => ParseMkvToolNixVersion(await GetTextAsync(MkvToolNixReleaseUrl, cancellationToken).ConfigureAwait(false)),
        _ => null,
    };

    /// <summary>Скачать и поставить последнюю версию.</summary>
    /// <exception cref="InstallException">Не скачалось, не сошлась контрольная сумма, отказ в правах и т.п.</exception>
    public Task<InstallResult> InstallAsync(ToolPackage package, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        package switch
        {
            ToolPackage.Ffmpeg => InstallFfmpegAsync(progress, cancellationToken),
            ToolPackage.MkvToolNix => InstallMkvToolNixAsync(progress, cancellationToken),
            ToolPackage.ImDisk => InstallImDiskAsync(progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(package)),
        };

    /// <summary>«&lt;latest-source&gt;&lt;version&gt;102.0&lt;/version&gt;» из latest-release.xml.</summary>
    public static string ParseMkvToolNixVersion(string xml)
    {
        try
        {
            var version = XDocument.Parse(xml).Root?.Element("latest-source")?.Element("version")?.Value.Trim();
            return Version(version ?? "", "MKVToolNix");
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InstallException("Сайт MKVToolNix ответил не то — попробуйте позже.", ex);
        }
    }

    /// <summary>Строка «&lt;sha256&gt;  &lt;имя файла&gt;» из sha256sums.txt; нет — null.</summary>
    public static string? FindSha256(string sums, string fileName) =>
        sums.Split('\n').Select(line => line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == fileName && Sha256Regex().IsMatch(parts[0]))
            .Select(parts => parts[0].ToLowerInvariant())
            .FirstOrDefault();

    private async Task<InstallResult> InstallFfmpegAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("узнаю последнюю версию"));
        var version = (await LatestVersionAsync(ToolPackage.Ffmpeg, cancellationToken).ConfigureAwait(false))!;
        var url = FfmpegZipUrl(version);
        var sha256 = (await GetTextAsync(url + ".sha256", cancellationToken).ConfigureAwait(false)).Trim().Split(' ')[0];
        if (!Sha256Regex().IsMatch(sha256))
        {
            throw new InstallException("Сайт ffmpeg не дал контрольную сумму архива — попробуйте позже.");
        }

        var target = await DownloadAndExtractAsync($"ffmpeg {version}", url, sha256, $"ffmpeg-{version}", FfmpegFiles,
            entry => entry.Contains("/bin/", StringComparison.Ordinal), progress, cancellationToken).ConfigureAwait(false);
        return new InstallResult(ToolPackage.Ffmpeg, version, new Dictionary<Tool, string>
        {
            [Tool.Ffmpeg] = Path.Combine(target, "ffmpeg.exe"),
            [Tool.Ffprobe] = Path.Combine(target, "ffprobe.exe"),
        });
    }

    private async Task<InstallResult> InstallMkvToolNixAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("узнаю последнюю версию"));
        var version = (await LatestVersionAsync(ToolPackage.MkvToolNix, cancellationToken).ConfigureAwait(false))!;
        var url = MkvToolNixZipUrl(version);
        var sha256 = FindSha256(await GetTextAsync(MkvToolNixSumsUrl(version), cancellationToken).ConfigureAwait(false), Path.GetFileName(url))
            ?? throw new InstallException("Сайт MKVToolNix не дал контрольную сумму архива — попробуйте позже.");
        var target = await DownloadAndExtractAsync($"MKVToolNix {version}", url, sha256, $"mkvtoolnix-{version}", MkvToolNixFiles,
            entry => entry.Count(c => c == '/') == 1, progress, cancellationToken).ConfigureAwait(false);
        return new InstallResult(ToolPackage.MkvToolNix, version, new Dictionary<Tool, string>
        {
            [Tool.Mkvmerge] = Path.Combine(target, "mkvmerge.exe"),
            [Tool.Mkvextract] = Path.Combine(target, "mkvextract.exe"),
        });
    }

    private async Task<InstallResult> InstallImDiskAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var installer = Path.Combine(Root, "downloads", "imdiskinst.exe");
        await DownloadAsync(ImDiskInstallerUrl, installer, null, "ImDisk", progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new InstallProgress("установка ImDisk — подтвердите запрос Windows"));
        int code;
        try
        {
            // -y — без вопросов распаковщика; в конце установщик сам покажет, всё ли прошло
            code = await _runElevated(installer, ["-y"], cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new InstallException("Установка ImDisk отменена: запрос прав администратора отклонён.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new InstallException($"Не удалось запустить установщик ImDisk: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(installer);
        }

        if (!_imdiskInstalled())
        {
            throw new InstallException($"Установщик ImDisk завершился (код {code}), но imdisk.exe не появился. Перезагрузите компьютер и попробуйте ещё раз.");
        }

        return new InstallResult(ToolPackage.ImDisk, null, new Dictionary<Tool, string>());
    }

    /// <summary>Скачать архив, сверить SHA-256 и достать нужные файлы в Root\&lt;папка&gt;; прежние версии — убрать.</summary>
    private async Task<string> DownloadAndExtractAsync(
        string what, string url, string sha256, string folder, IReadOnlyList<string> files, Func<string, bool> entryFilter,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(Root, "downloads", Path.GetFileName(url));
        await DownloadAsync(url, archive, sha256, what, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new InstallProgress($"распаковка {what}"));
        var target = Path.Combine(Root, folder);
        try
        {
            await Task.Run(() => Extract(archive, target, files, entryFilter), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(archive);
        }

        // Прежние версии этой программы: занятую идущей задачей папку удалим в следующий раз
        var prefix = folder[..(folder.LastIndexOf('-') + 1)];
        foreach (var old in Directory.EnumerateDirectories(Root, prefix + "*").Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return target;
    }

    /// <summary>Только файлы с этими именами — имена берутся из списка, а не из архива (никаких «..\» из чужого zip).</summary>
    private static void Extract(string archive, string target, IReadOnlyList<string> files, Func<string, bool> entryFilter)
    {
        var temp = target + ".new";
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            Directory.CreateDirectory(temp);
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = files.FirstOrDefault(f => string.Equals(f, entry.Name, StringComparison.OrdinalIgnoreCase));
                    if (name is not null && entryFilter(entry.FullName.Replace('\\', '/')) && !File.Exists(Path.Combine(temp, name)))
                    {
                        entry.ExtractToFile(Path.Combine(temp, name));
                    }
                }
            }

            if (files.FirstOrDefault(f => !File.Exists(Path.Combine(temp, f))) is { } missing)
            {
                throw new InstallException($"В архиве нет {missing} — сайт мог поменять сборку. Сообщите об этом.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true); // та же версия ещё раз
            }

            Directory.Move(temp, target);
        }
        catch (InvalidDataException ex)
        {
            throw new InstallException("Скачанный архив повреждён — попробуйте ещё раз.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException($"Не удалось распаковать в {target}: {ex.Message}. Если эта версия сейчас работает в задаче — дождитесь её конца.", ex);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Скачать в файл с проверкой SHA-256 (общий помощник — им же качаются обновления anitools).</summary>
    private Task DownloadAsync(string url, string target, string? sha256, string what, IProgress<InstallProgress>? progress, CancellationToken cancellationToken) =>
        Downloader.DownloadAsync(_http, url, target, sha256, what, progress, StallTimeout, cancellationToken);

    private async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : throw new InstallException($"Сайт {new Uri(url).Host} ответил {(int)response.StatusCode} — попробуйте позже.");
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException($"Нет связи с {new Uri(url).Host}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallException($"{new Uri(url).Host} не отвечает — попробуйте позже.", ex);
        }
    }

    /// <summary>Номер версии идёт в адрес и имя папки — только цифры с точками.</summary>
    private static string Version(string text, string what) =>
        VersionRegex().IsMatch(text) ? text : throw new InstallException($"Сайт {what} ответил не то вместо номера версии — попробуйте позже.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>«Запуск от имени администратора» и ожидание конца; отмена — перестать ждать (процесс с правами не убить).</summary>
    private static async Task<int> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = await Task.Run(
            () => Process.Start(new ProcessStartInfo(program, string.Join(' ', arguments)) { UseShellExecute = true, Verb = "runas" }),
            cancellationToken).ConfigureAwait(false) ?? throw new Win32Exception("процесс не запустился");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    [GeneratedRegex(@"^\d{1,4}(?:\.\d{1,4}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
