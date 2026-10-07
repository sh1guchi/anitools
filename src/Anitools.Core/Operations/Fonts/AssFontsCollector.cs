using System.IO.Compression;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Fonts;

/// <summary>Где искать шрифты: своя папка (приоритет, туда же сохраняется скачанное) и системные папки.</summary>
public sealed record FontsCollectOptions
{
    /// <summary>Своя папка со шрифтами (CUSTOM_FONTS_DIR); создаётся, если её нет.</summary>
    public required string CustomDir { get; init; }

    /// <summary>%WINDIR%\Fonts и %LOCALAPPDATA%\Microsoft\Windows\Fonts.</summary>
    public IReadOnlyList<string> SystemDirs { get; init; } = DefaultSystemDirs();

    /// <summary>Пробовать скачать то, чего нет на диске.</summary>
    public bool Download { get; init; } = true;

    public static string DefaultCustomDir =>
        Environment.GetEnvironmentVariable("CUSTOM_FONTS_DIR") is { Length: > 0 } dir ? dir : @"C:\personal\Apps\MPV by shiguchi\fonts";

    public static IReadOnlyList<string> DefaultSystemDirs()
    {
        var windir = Environment.GetEnvironmentVariable("WINDIR") is { Length: > 0 } w ? w : @"C:\Windows";
        var dirs = new List<string> { Path.Combine(windir, "Fonts") };
        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
        {
            dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        }

        return dirs;
    }
}

/// <summary>Итог: архив (null — паковать нечего), имена шрифтов из .ass, что упаковано, что сохранено в свою папку, чего нет нигде.</summary>
public sealed record FontsCollectResult(
    string? ZipPath,
    IReadOnlyList<string> FontNames,
    IReadOnlyList<string> Packed,
    IReadOnlyList<string> SavedToCustom,
    IReadOnlyList<string> NotFound,
    IReadOnlyList<string> Log)
{
    /// <summary>Где поискать вручную то, что не нашлось.</summary>
    public static IReadOnlyList<string> SearchLinks(string fontName) =>
    [
        $"https://www.fontsquirrel.com/search?q={FontDownloader.QuotePlus(fontName)}",
        $"https://fontsgeek.com/search/?q={FontDownloader.QuotePlus(fontName)}",
    ];
}

/// <summary>
/// Шрифты для .ass → fonts.zip (ass_fonts.py): имена из стилей и тегов \fn, поиск по внутренним именам шрифтов —
/// своя папка → системные → скачивание. Одноимённые без кириллицы пропускаются, если есть кириллические;
/// из копий одного начертания берётся одна (кириллица, новее, полнее). Архив — рядом с первым .ass.
/// </summary>
/// <param name="sources">Где скачивать по порядку (обычно <see cref="FontDownloader.Sources"/>); null — не скачивать.</param>
public sealed class AssFontsCollector(IReadOnlyList<FontSource>? sources = null)
{
    public const string ZipName = "fonts.zip";

    /// <summary>Все .ass папки (без учёта регистра расширения), по порядку sorted(Path) на Windows.</summary>
    public static IReadOnlyList<string> ListAssFiles(string folder) =>
        [.. MediaFiles.List(folder, [".ass"]).OrderBy(p => PyText.Lower(Path.GetFileName(p)), PyText.CodePointComparer)];

    public async Task<FontsCollectResult> CollectAsync(IReadOnlyList<string> assFiles, FontsCollectOptions options, CancellationToken cancellationToken = default)
    {
        if (assFiles.Count == 0)
        {
            throw new PlanException("Не найдено .ass файлов.");
        }

        var run = new Run(options, sources);
        Directory.CreateDirectory(options.CustomDir);

        // Уникальные имена по всем файлам: первое написание, порядок — без учёта регистра
        var unique = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ass in assFiles)
        {
            try
            {
                foreach (var name in FontText.ParseFontNames(FontText.ReadAss(await File.ReadAllBytesAsync(ass, cancellationToken).ConfigureAwait(false))))
                {
                    unique.TryAdd(FontText.FontKey(name), name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.Log.Add($"Не читается {Path.GetFileName(ass)}: {ex.Message}");
            }
        }

        var fontNames = unique.Values.OrderBy(PyText.CaseFold, PyText.CodePointComparer).ToList();
        if (fontNames.Count == 0)
        {
            return new FontsCollectResult(null, [], [], [], [], run.Log);
        }

        var missing = run.TakeLocal(new FontIndex([options.CustomDir]), fontNames);
        if (missing.Count > 0)
        {
            missing = run.TakeLocal(new FontIndex(options.SystemDirs), missing);
        }

        var notFound = new List<string>();
        foreach (var name in missing)
        {
            if (!options.Download || sources is null || !await run.DownloadAsync(name, cancellationToken).ConfigureAwait(false))
            {
                notFound.Add(name);
            }
        }

        string? zipPath = null;
        if (run.Fonts.Count > 0)
        {
            zipPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assFiles[0]))!, ZipName);
            WriteZip(zipPath, run.Fonts);
        }

        return new FontsCollectResult(zipPath, fontNames, [.. run.Fonts.Select(f => f.Name)], run.Saved, notFound, run.Log);
    }

    /// <summary>Deflate, у записей атрибут «архивный» (0x20) и дата 1980-01-01 — как у оригинала.</summary>
    private static void WriteZip(string path, IReadOnlyList<(string Name, byte[] Data)> fonts)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in fonts)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.ExternalAttributes = 0x20;
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var entryStream = entry.Open();
            entryStream.Write(data);
        }
    }

    private sealed class Run(FontsCollectOptions options, IReadOnlyList<FontSource>? sources)
    {
        private readonly Dictionary<string, string> _packed = new(StringComparer.Ordinal);

        public List<(string Name, byte[] Data)> Fonts { get; } = [];

        public List<string> Saved { get; } = [];

        public List<string> Log { get; } = [];

        /// <summary>take_local: найденные — в архив, ненайденные — в ответ.</summary>
        public List<string> TakeLocal(FontIndex index, IReadOnlyList<string> names)
        {
            var missing = new List<string>();
            foreach (var name in names)
            {
                var files = index.Find(name);
                if (files.Count == 0)
                {
                    missing.Add(name);
                    continue;
                }

                // Одноимённый шрифт без кириллицы (системный calligr0.ttf — тоже «Calligrapher»): только кириллические
                var cyrillic = files.Where(f => SfntReader.HasCyrillic(ReadOrEmpty(f), Path.GetExtension(f))).ToList();
                if (cyrillic.Count > 0 && cyrillic.Count < files.Count)
                {
                    foreach (var f in files.Except(cyrillic))
                    {
                        Log.Add($"{Path.GetFileName(f)} — то же имя, но без кириллицы");
                    }

                    files = cyrillic;
                }

                // Копии и версии одного начертания — одна: с кириллицей, новее по версии, при равенстве — больше файл
                var best = new List<(string Face, (bool Cyrillic, double Version, long Size) Rank, string File, byte[] Data)>();
                foreach (var f in files)
                {
                    byte[] data;
                    try
                    {
                        data = File.ReadAllBytes(f);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Add($"{Path.GetFileName(f)}: ошибка чтения: {ex.Message}");
                        continue;
                    }

                    var (face, version) = SfntReader.FaceInfo(data);
                    var rank = (SfntReader.HasCyrillic(data, Path.GetExtension(f)), version, (long)data.Length);
                    if (face.Length == 0)
                    {
                        face = "file:" + Path.GetFileName(f);
                    }

                    var at = best.FindIndex(b => b.Face == face);
                    if (at < 0)
                    {
                        best.Add((face, rank, f, data));
                    }
                    else if (Compare(rank, best[at].Rank) > 0)
                    {
                        Log.Add($"{Path.GetFileName(best[at].File)} — дубль, берётся {Path.GetFileName(f)}");
                        best[at] = (face, rank, f, data);
                    }
                    else
                    {
                        Log.Add($"{Path.GetFileName(f)} — дубль, берётся {Path.GetFileName(best[at].File)}");
                    }
                }

                foreach (var b in best)
                {
                    Add(Path.GetFileName(b.File), b.Data);
                }
            }

            return missing;
        }

        /// <summary>Источники по порядку до первого, где нашлось; ошибка источника — следующий.</summary>
        public async Task<bool> DownloadAsync(string name, CancellationToken cancellationToken)
        {
            foreach (var source in sources!)
            {
                IReadOnlyDictionary<string, byte[]> found;
                try
                {
                    found = await source.DownloadAsync(name, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FontDownloadException or InvalidDataException or IOException)
                {
                    Log.Add($"{name}: {source.Name} — ошибка: {ex.Message}");
                    continue;
                }

                if (found.Count == 0)
                {
                    continue;
                }

                foreach (var (fileName, data) in found)
                {
                    Add(fileName, data);
                    SaveToCustom(fileName, data);
                }

                return true;
            }

            return false;
        }

        /// <summary>В архив: то же начертание — не второй раз; то же имя с другим содержимым — «_2», «_3».</summary>
        private void Add(string fileName, byte[] data)
        {
            var face = SfntReader.FaceInfo(data).Face;
            if (face.Length == 0)
            {
                face = "file:" + fileName;
            }

            if (_packed.TryGetValue(face, out var packedAs))
            {
                Log.Add($"{fileName} — дубль {packedAs}, уже в архиве");
                return;
            }

            if (Fonts.FindIndex(f => f.Name == fileName) is >= 0 and var existing)
            {
                if (Fonts[existing].Data.AsSpan().SequenceEqual(data))
                {
                    return;
                }

                var (stem, ext) = PyText.SplitExt(fileName);
                var n = 2;
                while (Fonts.Any(f => f.Name == $"{stem}_{n}{ext}"))
                {
                    n++;
                }

                fileName = $"{stem}_{n}{ext}";
            }

            Fonts.Add((fileName, data));
            _packed[face] = fileName;
        }

        /// <summary>Скачанное — в свою папку, чтобы в следующий раз нашлось сразу; существующее не затирается.</summary>
        private void SaveToCustom(string fileName, byte[] data)
        {
            var output = Path.Combine(options.CustomDir, fileName);
            for (var n = 2; File.Exists(output); n++)
            {
                try
                {
                    if (File.ReadAllBytes(output).AsSpan().SequenceEqual(data))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                output = Path.Combine(options.CustomDir, $"{PyText.Stem(fileName)}_{n}{MediaFiles.Suffix(fileName)}");
            }

            try
            {
                File.WriteAllBytes(output, data);
                Saved.Add(Path.GetFileName(output));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Add($"Не удалось сохранить {Path.GetFileName(output)} в свою папку: {ex.Message}");
            }
        }

        private static int Compare((bool Cyrillic, double Version, long Size) a, (bool Cyrillic, double Version, long Size) b) =>
            a.Cyrillic != b.Cyrillic ? a.Cyrillic.CompareTo(b.Cyrillic)
            : a.Version != b.Version ? a.Version.CompareTo(b.Version)
            : a.Size.CompareTo(b.Size);

        private static byte[] ReadOrEmpty(string path)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}

/// <summary>Индекс «имя шрифта → файлы» по папкам (рекурсивно): основные имена, запасные, имя файла.</summary>
public sealed class FontIndex
{
    private readonly Dictionary<string, HashSet<string>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byFamily = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byStem = new(StringComparer.Ordinal);

    public FontIndex(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                         .Where(f => FontDownloader.FontExtensions.Contains(MediaFiles.Suffix(Path.GetFileName(f)).ToLowerInvariant())))
            {
                FontNameSet names;
                try
                {
                    names = SfntReader.ReadFontNames(File.ReadAllBytes(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    names = new FontNameSet(new HashSet<string>(), new HashSet<string>());
                }

                foreach (var n in names.Primary)
                {
                    Add(_byName, n, file);
                }

                foreach (var n in names.Fallback)
                {
                    Add(_byFamily, n, file);
                }

                Add(_byStem, FontText.Normalize(PyText.Stem(Path.GetFileName(file))), file);
            }
        }
    }

    /// <summary>
    /// Файлы шрифта: по основным именам, иначе по запасному (ID 16 — «Arial» у Arial Narrow), иначе по имени файла.
    /// Порядок — как sorted(Path) на Windows.
    /// </summary>
    public IReadOnlyList<string> Find(string fontName)
    {
        var key = FontText.FontKey(fontName);
        var files = _byName.GetValueOrDefault(key) ?? _byFamily.GetValueOrDefault(key) ?? _byStem.GetValueOrDefault(FontText.Normalize(fontName)) ?? [];
        return [.. files.Order(Comparer<string>.Create(ComparePaths))];
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string file)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }

        set.Add(file);
    }

    /// <summary>Пути по частям, в нижнем регистре, по кодам символов (PureWindowsPath).</summary>
    private static int ComparePaths(string a, string b)
    {
        var pa = a.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var pb = b.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var c = PyText.CompareCodePoints(PyText.Lower(pa[i]), PyText.Lower(pb[i]));
            if (c != 0)
            {
                return c;
            }
        }

        return pa.Length.CompareTo(pb.Length);
    }
}
