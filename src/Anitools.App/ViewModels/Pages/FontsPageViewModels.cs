using System.Collections.ObjectModel;
using Anitools.Core.Jobs;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Fonts;
using Anitools.Core.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Шрифт, которого нет нигде: где поискать вручную.</summary>
public sealed partial class MissingFontViewModel(string name, IShell shell)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> Links { get; } = FontsCollectResult.SearchLinks(name);

    [RelayCommand]
    private Task OpenFontSquirrelAsync() => shell.Dialogs.OpenUrlAsync(Links[0]);

    [RelayCommand]
    private Task OpenFontsGeekAsync() => shell.Dialogs.OpenUrlAsync(Links[1]);
}

/// <summary>
/// «Шрифты для .ass» (§4.11): шрифты из стилей и \fn всех .ass папки → fonts.zip; ищутся в своей папке,
/// в системе, потом скачиваются (Google Fonts, dafont, 1001fonts) и сохраняются в свою папку.
/// </summary>
public sealed partial class AssFontsPageViewModel(IShell shell) : PageViewModel(shell, "Шрифты для .ass", MaterialIconKind.FormatFont)
{
    private IReadOnlyList<string> _assFiles = [];

    public ObservableCollection<string> FontNames { get; } = [];

    public ObservableCollection<MissingFontViewModel> NotFound { get; } = [];

    [ObservableProperty]
    public partial string CustomDir { get; set; } = shell.Services.Settings.Fonts.CustomDir;

    [ObservableProperty]
    public partial bool Download { get; set; } = true;

    [ObservableProperty]
    public partial string FilesCaption { get; set; } = "";

    /// <summary>Итог последнего сбора: архив, что упаковано, что сохранено в свою папку.</summary>
    [ObservableProperty]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial string? ZipPath { get; set; }

    /// <summary>Системные папки шрифтов (в тестах — свои, чтобы не читать настоящие C:\Windows\Fonts).</summary>
    internal IReadOnlyList<string> SystemDirs { get; set; } = FontsCollectOptions.DefaultSystemDirs();

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var (files, names) = await Task.Run(
            () =>
            {
                var files = AssFontsCollector.ListAssFiles(folder);
                var unique = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    try
                    {
                        foreach (var name in FontText.ParseFontNames(FontText.ReadAss(File.ReadAllBytes(file))))
                        {
                            unique.TryAdd(FontText.FontKey(name), name);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                return (files, unique.Values.Order(StringComparer.OrdinalIgnoreCase).ToList());
            },
            cancellationToken);
        if (files.Count == 0)
        {
            throw new PlanException("Не найдено .ass файлов.");
        }

        _assFiles = files;
        FontNames.Clear();
        foreach (var name in names)
        {
            FontNames.Add(name);
        }

        FilesCaption = $"{RuText.Plural(files.Count, "файл", "файла", "файлов")} .ass · {RuText.Plural(names.Count, "шрифт", "шрифта", "шрифтов")}";
    }

    protected override void Clear()
    {
        _assFiles = [];
        FontNames.Clear();
        FilesCaption = "";
    }

    [RelayCommand]
    private async Task BrowseCustomDirAsync()
    {
        if (await Shell.Dialogs.PickFolderAsync("Своя папка со шрифтами", CustomDir) is { } path)
        {
            CustomDir = path;
        }
    }

    [RelayCommand]
    private Task OpenZipFolderAsync() => ZipPath is { } zip ? Shell.Dialogs.OpenPathAsync(Path.GetDirectoryName(zip)!) : Task.CompletedTask;

    [RelayCommand]
    private void Run()
    {
        if (_assFiles.Count == 0 || CustomDir.Trim().Length == 0)
        {
            Message = CustomDir.Trim().Length == 0 ? "Укажите свою папку шрифтов." : Message;
            return;
        }

        var files = _assFiles;
        var options = new FontsCollectOptions { CustomDir = CustomDir.Trim(), SystemDirs = SystemDirs, Download = Download };
        var collector = new AssFontsCollector(Download ? new FontDownloader(Shell.Services.Http).Sources : null);
        Enqueue("Шрифты для .ass", async context =>
        {
            context.Report(null, $"ищу {RuText.Plural(FontNames.Count, "шрифт", "шрифта", "шрифтов")}…");
            var result = await collector.CollectAsync(files, options, context.CancellationToken).ConfigureAwait(false);
            foreach (var line in result.Log)
            {
                context.Log(line);
            }

            foreach (var name in result.NotFound)
            {
                context.Log($"✗ не найден нигде: {name}");
            }

            Dispatcher.UIThread.Post(() => Show(result));
            var summary = $"в архиве {result.Packed.Count}"
                + (result.SavedToCustom.Count > 0 ? $" · в свою папку {result.SavedToCustom.Count}" : "")
                + (result.NotFound.Count > 0 ? $" · не найдено {result.NotFound.Count}" : "");
            context.Log(result.ZipPath is { } zip ? $"{summary} → {zip}" : summary);
            return new JobOutcome(result.NotFound.Count == 0, summary);
        });
    }

    private void Show(FontsCollectResult result)
    {
        ZipPath = result.ZipPath;
        Result = result.ZipPath is null
            ? "Архив не собран — нечего паковать."
            : $"{Path.GetFileName(result.ZipPath)}: {RuText.Plural(result.Packed.Count, "файл", "файла", "файлов")}"
              + (result.SavedToCustom.Count > 0 ? $" · скачано и сохранено в свою папку: {string.Join(", ", result.SavedToCustom)}" : "");
        NotFound.Clear();
        foreach (var name in result.NotFound)
        {
            NotFound.Add(new MissingFontViewModel(name, Shell));
        }
    }
}

/// <summary>«Шрифты из видео» (§4.11): вложенные шрифты всех MKV папки → fonts.zip.</summary>
public sealed partial class VideoFontsPageViewModel(IShell shell) : PageViewModel(shell, "Шрифты из видео", MaterialIconKind.ArchiveArrowDownOutline)
{
    public ObservableCollection<string> Videos { get; } = [];

    [ObservableProperty]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial string? ZipPath { get; set; }

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var videos = await Task.Run(() => VideoFontsOperation.ListVideos(folder), cancellationToken);
        if (videos.Count == 0)
        {
            throw new PlanException("Нет видео в папке.");
        }

        Videos.Clear();
        foreach (var video in videos)
        {
            Videos.Add(Path.GetFileName(video));
        }
    }

    protected override void Clear() => Videos.Clear();

    [RelayCommand]
    private Task OpenZipFolderAsync() => ZipPath is { } zip ? Shell.Dialogs.OpenPathAsync(Path.GetDirectoryName(zip)!) : Task.CompletedTask;

    [RelayCommand]
    private void Run()
    {
        if (Videos.Count == 0)
        {
            return;
        }

        var services = Shell.Services;
        var folder = Folder;
        var operation = new VideoFontsOperation(services.Runner, services.Tools, services.Probe, services.Logs);
        Enqueue("Шрифты из видео", async context =>
        {
            var progress = new SyncProgress<(int Index, int Count, string Video)>(p =>
                context.Report((p.Index - 1) / (double)Math.Max(1, p.Count), $"видео {p.Index}/{p.Count} · {Path.GetFileName(p.Video)}"));
            var result = await operation.ExecuteAsync(folder, progress, context.CancellationToken).ConfigureAwait(false);
            foreach (var (video, font) in result.Duplicates)
            {
                context.Log($"· {font} из {Path.GetFileName(video)} — уже есть");
            }

            foreach (var (video, error) in result.Errors)
            {
                context.Log($"✗ {Path.GetFileName(video)} — {error}");
            }

            var summary = result.ZipPath is null ? "шрифтов нет" : $"шрифтов в архиве {result.Packed.Count}";
            context.Log(result.ZipPath is { } zip ? $"{summary} → {zip}" : summary);
            Dispatcher.UIThread.Post(() =>
            {
                ZipPath = result.ZipPath;
                Result = result.ZipPath is null
                    ? "Шрифтов во вложениях нет."
                    : $"{Path.GetFileName(result.ZipPath)}: {RuText.Plural(result.Packed.Count, "шрифт", "шрифта", "шрифтов")}"
                      + (result.Errors.Count > 0 ? $" · не прочитались: {result.Errors.Count}" : "");
            });
            return new JobOutcome(result.Errors.Count == 0, summary);
        });
    }

    /// <summary>IProgress без SynchronizationContext — события сразу, в потоке задачи.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
