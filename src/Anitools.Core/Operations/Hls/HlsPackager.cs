using System.IO.Compression;
using Anitools.Core.Platform;

namespace Anitools.Core.Operations.Hls;

/// <summary>Что получилось при упаковке: основной архив, отдельный архив верхнего качества (если был нужен) и его вес.</summary>
public sealed record HlsPackResult(string MainZip, string? TopZip, long TopBytes);

/// <summary>Архивы серии и перенос озвучек (py:4436–4556).</summary>
public static class HlsPackager
{
    /// <summary>
    /// Папки качеств из <paramref name="workOut"/> → titleOut/&lt;серия&gt;.zip без сжатия (Stored), пути вида
    /// «360p/seg000.ts»; каждая папка удаляется сразу после упаковки — экономия места на RAM-диске. Верхнее качество
    /// тяжелее порога — в отдельный «&lt;серия&gt;.&lt;качество&gt;.zip». Ошибка или отмена → недописанные архивы удаляются,
    /// исключение летит дальше (рабочую папку чистит вызывающий).
    /// </summary>
    public static HlsPackResult Pack(
        string workOut, string titleOut, string episodeName, IReadOnlyList<HlsRung> ladder, long separateTopBytes, CancellationToken cancellationToken = default)
    {
        var top = ladder[^1].Name;
        var topDir = Path.Combine(workOut, top);
        var topBytes = Directory.Exists(topDir)
            ? new DirectoryInfo(topDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
        var separate = Directory.Exists(topDir) && topBytes > separateTopBytes;
        var mainZip = Path.Combine(titleOut, episodeName + ".zip");
        var topZip = Path.Combine(titleOut, $"{episodeName}.{top}.zip");
        try
        {
            WriteZip(mainZip, workOut, ladder.Select(r => r.Name).Where(name => !(separate && name == top)), cancellationToken);
            if (separate)
            {
                WriteZip(topZip, workOut, [top], cancellationToken);
            }
            else
            {
                DeleteQuietly(topZip); // от прошлого запуска, когда верхнее качество шло отдельно: теперь оно в основном архиве
            }
        }
        catch
        {
            DeleteQuietly(mainZip);
            DeleteQuietly(topZip);
            throw;
        }

        return new HlsPackResult(mainZip, separate ? topZip : null, topBytes);
    }

    /// <summary>
    /// Озвучки из workOut/audio/&lt;озвучка&gt;/ → titleOut/audio/&lt;озвучка&gt;/ с заменой старых файлов.
    /// С RAM-диска это копирование — единственная запись .mka на SSD.
    /// </summary>
    public static void MoveAudio(string workOut, string titleOut)
    {
        var audioDir = Path.Combine(workOut, "audio");
        if (!Directory.Exists(audioDir))
        {
            return;
        }

        foreach (var voiceDir in Directory.EnumerateDirectories(audioDir))
        {
            var destination = Path.Combine(titleOut, "audio", Path.GetFileName(voiceDir));
            Directory.CreateDirectory(destination);
            ReadOnlyAttr.Clear(destination);
            foreach (var file in Directory.EnumerateFiles(voiceDir))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                DeleteFile(target);
                File.Move(file, target);
                ReadOnlyAttr.Clear(target);
            }
        }
    }

    private static void WriteZip(string zipPath, string workOut, IEnumerable<string> qualities, CancellationToken cancellationToken)
    {
        DeleteFile(zipPath);
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var quality in qualities)
            {
                var dir = Path.Combine(workOut, quality);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => (Path: f, Entry: Path.GetRelativePath(workOut, f).Replace('\\', '/')))
                    .OrderBy(f => f.Entry, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file.Path, file.Entry, CompressionLevel.NoCompression);
                }

                Directory.Delete(dir, recursive: true);
            }
        }

        ReadOnlyAttr.Clear(zipPath);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            ReadOnlyAttr.Clear(path);
            File.Delete(path);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            DeleteFile(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
