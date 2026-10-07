using System.Collections.ObjectModel;
using Anitools.Core.Shikimori;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anitools.App.ViewModels;

/// <summary>Что выбрали в диалоге Shikimori: тайтл из поиска, ID вручную или ничего (пропустить).</summary>
public sealed record ShikimoriChoice(ShikimoriAnime? Anime, string? ManualId)
{
    public static ShikimoriChoice Skip { get; } = new(null, null);

    public bool IsSkip => Anime is null && ManualId is null;

    /// <summary>ID тайтла: из поиска или введённый вручную.</summary>
    public long? Id => Anime?.Id ?? (long.TryParse(ManualId, out var id) ? id : null);
}

/// <summary>
/// Диалог Shikimori (§4.9, _choose_shikimori): поиск по очищенному названию с запасными запросами, ранжирование по
/// сезону и типу из названия файлов, первые 8 результатов; выбор двойным щелчком, ID вручную или «Пропустить».
/// </summary>
public sealed partial class ShikimoriPickerViewModel : ObservableObject
{
    public const int MaxResults = 8;

    private readonly ShikimoriClient _client;
    private readonly ShikimoriQuery _query;

    public ShikimoriPickerViewModel(ShikimoriClient client, string title)
    {
        _client = client;
        _query = ShikimoriQuery.FromTitle(title);
        Title = title;
        Query = _query.SearchText;
    }

    /// <summary>Закрыть диалог с выбором (страница подписывается).</summary>
    public event Action<ShikimoriChoice>? Chosen;

    /// <summary>Название из файлов, для которого ищем.</summary>
    public string Title { get; }

    [ObservableProperty]
    public partial string Query { get; set; }

    public ObservableCollection<ShikimoriAnime> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial ShikimoriAnime? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseManualIdCommand))]
    public partial string ManualId { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public Uri Site => _client.Site;

    [RelayCommand]
    public async Task SearchAsync()
    {
        var query = Query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        IsSearching = true;
        Message = null;
        try
        {
            // Сезон и тип — из названия файлов, запрос — какой ввели (как при повторном поиске в оригинале)
            var found = ShikimoriQuery.Rank(await _client.SmartSearchAsync(query), query, _query.Season, _query.Kinds);
            Results.Clear();
            foreach (var anime in found.Take(MaxResults))
            {
                Results.Add(anime);
            }

            Selected = Results.FirstOrDefault();
            Message = Results.Count == 0 ? "Shikimori не ответил или ничего не нашёл — измените запрос или введите ID вручную." : null;
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        if (Selected is { } anime)
        {
            Chosen?.Invoke(new ShikimoriChoice(anime, null));
        }
    }

    private bool CanChoose() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanUseManualId))]
    private void UseManualId() => Chosen?.Invoke(new ShikimoriChoice(null, ManualId.Trim()));

    private bool CanUseManualId() => ManualId.Trim() is { Length: > 0 } id && id.All(char.IsAsciiDigit);

    [RelayCommand]
    private void Skip() => Chosen?.Invoke(ShikimoriChoice.Skip);
}
