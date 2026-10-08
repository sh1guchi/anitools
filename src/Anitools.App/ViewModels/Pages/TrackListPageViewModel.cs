using System.Collections.ObjectModel;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.TrackList;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>«Дорожки файла» (§4.11, mka_muxer.py, режим 2): таблица аудиодорожек и список тайтлов для копирования.</summary>
public sealed partial class TrackListPageViewModel(IShell shell) : PageViewModel(shell, "Дорожки файла", MaterialIconKind.FileMusicOutline)
{
    private CancellationTokenSource? _reading;

    public ObservableCollection<string> Files { get; } = [];

    public ObservableCollection<TrackRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string? SelectedFile { get; set; }

    [ObservableProperty]
    public partial CopyListStyle Style { get; set; } = CopyListStyle.Bullets;

    /// <summary>Список для копирования — как он уйдёт в буфер.</summary>
    [ObservableProperty]
    public partial string CopyText { get; set; } = "";

    public bool IsBullets
    {
        get => Style == CopyListStyle.Bullets;
        set => SetStyle(value, CopyListStyle.Bullets);
    }

    public bool IsNumbers
    {
        get => Style == CopyListStyle.Numbers;
        set => SetStyle(value, CopyListStyle.Numbers);
    }

    public bool IsPlain
    {
        get => Style == CopyListStyle.Plain;
        set => SetStyle(value, CopyListStyle.Plain);
    }

    protected override Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var files = TrackListOperation.ListFiles(folder);
        if (files.Count == 0)
        {
            throw new PlanException("Нет файлов с дорожками (.mka, .mkv, .mp4, .mov, .m4a, .webm).");
        }

        Files.Clear();
        foreach (var file in files)
        {
            Files.Add(Path.GetFileName(file));
        }

        SelectedFile = Files.Contains(SelectedFile ?? "") ? SelectedFile : Files[0];
        return ReadAsync();
    }

    protected override void Clear()
    {
        Files.Clear();
        Rows.Clear();
        CopyText = "";
    }

    partial void OnSelectedFileChanged(string? value) => _ = ReadAsync();

    partial void OnStyleChanged(CopyListStyle value)
    {
        OnPropertyChanged(nameof(IsBullets));
        OnPropertyChanged(nameof(IsNumbers));
        OnPropertyChanged(nameof(IsPlain));
        CopyText = TrackListOperation.CopyList([.. Rows], Style);
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        await Shell.Dialogs.CopyTextAsync(CopyText);
        Shell.Toast("Скопировано в буфер обмена.", ToastKind.Ok);
    }

    private async Task ReadAsync()
    {
        if (SelectedFile is not { } name || Folder.Length == 0)
        {
            return;
        }

        _reading?.Cancel();
        var cts = new CancellationTokenSource();
        _reading = cts;
        try
        {
            var info = await Shell.Services.Probe.ProbeAsync(Path.Combine(Folder, name), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Rows.Clear();
            foreach (var row in TrackListOperation.Rows(info))
            {
                Rows.Add(row);
            }

            CopyText = TrackListOperation.CopyList([.. Rows], Style);
            Message = Rows.Count == 0 ? "В файле нет аудиодорожек." : null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is MediaProbeException or ToolNotFoundException)
        {
            Rows.Clear();
            CopyText = "";
            Message = ex.Message;
        }
    }

    private void SetStyle(bool selected, CopyListStyle style)
    {
        if (selected)
        {
            Style = style;
        }
    }
}
