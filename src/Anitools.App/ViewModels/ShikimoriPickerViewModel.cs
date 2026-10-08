using System.Collections.ObjectModel;
using System.Globalization;
using Anitools.Core.Shikimori;
using Avalonia.Media.Imaging;
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

/// <summary>Тайтл в списке выбора: данные Shikimori для строки и карточки, постер грузится после поиска.</summary>
public sealed partial class ShikimoriResultViewModel(ShikimoriAnime anime) : ObservableObject
{
    public ShikimoriAnime Anime { get; } = anime;

    public long Id => Anime.Id;

    /// <summary>Главное название — русское, как на сайте (нет русского — ромадзи).</summary>
    public string Title => Anime.Russian;

    /// <summary>Ромадзи и английское — те, что отличаются от главного.</summary>
    public string OtherNames => string.Join(" · ", new[] { Anime.Name, Anime.English }
        .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, Anime.Russian, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>«TV · 28 эп. · 2023» — строка под названием в списке.</summary>
    public string Meta => string.Join(" · ", new[] { Anime.KindName, EpisodesText, Anime.Year == "????" ? "" : Anime.Year }.Where(p => p.Length > 0));

    /// <summary>«28 эп.»; у онгоинга — «5 из 12 эп.»; неизвестно — пусто.</summary>
    public string EpisodesText => Anime switch
    {
        { Status: "ongoing", EpisodesAired: > 0 } a => $"{a.EpisodesAired} из {(a.Episodes > 0 ? a.Episodes.ToString(CultureInfo.InvariantCulture) : "?")} эп.",
        { Episodes: > 0 } a => $"{a.Episodes} эп.",
        _ => "",
    };

    public string? Score => Anime.Score?.ToString("0.00", CultureInfo.InvariantCulture);

    public string? Duration => Anime.Duration is { } minutes ? $"{minutes} мин" : null;

    public string? Status => Anime.StatusName is { Length: > 0 } status ? status : null;

    public string? Genres => Anime.Genres.Count > 0 ? string.Join(", ", Anime.Genres) : null;

    public string? Studios => Anime.Studios.Count > 0 ? string.Join(", ", Anime.Studios) : null;

    public string Description => Anime.Description ?? "Описания на Shikimori нет.";

    public bool HasDescription => Anime.Description is not null;

    [ObservableProperty]
    public partial Bitmap? Poster { get; set; }
}

/// <summary>
/// Диалог Shikimori (§4.9, _choose_shikimori): поиск по очищенному названию с запасными запросами, ранжирование по
/// сезону и типу из названия файлов. Показывает все найденные тайтлы (до 50) с постером и карточкой: описание, жанры,
/// студия, оценка; выбор двойным щелчком, ID вручную или «Пропустить».
/// </summary>
public sealed partial class ShikimoriPickerViewModel : ObservableObject
{
    /// <summary>Сколько постеров качается одновременно.</summary>
    private const int PosterDownloads = 4;

    private readonly ShikimoriClient _client;
    private readonly ShikimoriQuery _query;
    private CancellationTokenSource? _posters;

    /// <param name="source">Откуда название — для подписи в окне: «Название из файлов: …».</param>
    public ShikimoriPickerViewModel(ShikimoriClient client, string title, string source = "Название из файлов")
    {
        _client = client;
        _query = ShikimoriQuery.FromTitle(title);
        Title = title;
        Caption = $"{source}: {title}";
        Query = _query.SearchText;
    }

    /// <summary>Закрыть диалог с выбором (страница подписывается).</summary>
    public event Action<ShikimoriChoice>? Chosen;

    /// <summary>Название, для которого ищем (из файлов или из поля страницы).</summary>
    public string Title { get; }

    /// <summary>Подпись под заголовком окна: откуда название.</summary>
    public string Caption { get; }

    [ObservableProperty]
    public partial string Query { get; set; }

    public ObservableCollection<ShikimoriResultViewModel> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelected))]
    public partial ShikimoriResultViewModel? Selected { get; set; }

    public bool HasSelected => Selected is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseManualIdCommand))]
    public partial string ManualId { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>«Найдено: 12» над списком.</summary>
    [ObservableProperty]
    public partial string? Found { get; set; }

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
            // Сезон и тип — из названия файлов, запрос — какой ввели
            var found = ShikimoriQuery.Rank(await _client.SmartSearchDetailedAsync(query), query, _query.Season, _query.Kinds);
            StopPosters();
            Selected = null;
            Results.Clear();
            foreach (var anime in found)
            {
                Results.Add(new ShikimoriResultViewModel(anime));
            }

            Selected = Results.FirstOrDefault();
            Found = Results.Count > 0 ? $"Найдено: {Results.Count}" : null;
            Message = Results.Count == 0 ? "Shikimori не ответил или ничего не нашёл — измените запрос или введите ID вручную." : null;
            _posters = new CancellationTokenSource();
            _ = LoadPostersAsync([.. Results], _posters.Token);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Остановить загрузку постеров (новый поиск, окно закрыли).</summary>
    public void StopPosters()
    {
        _posters?.Cancel();
        _posters?.Dispose();
        _posters = null;
    }

    /// <summary>Постеры по порядку списка, по <see cref="PosterDownloads"/> сразу; не скачался или не картинка — без постера.</summary>
    private async Task LoadPostersAsync(IReadOnlyList<ShikimoriResultViewModel> rows, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(PosterDownloads);
        try
        {
            await Task.WhenAll(rows.Where(r => r.Anime.PosterUrl is not null).Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (await _client.DownloadAsync(row.Anime.PosterUrl!, ct) is { } bytes && !ct.IsCancellationRequested)
                    {
                        row.Poster = Decode(bytes);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
            // новый поиск или окно закрыли
        }
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        if (Selected is { } row)
        {
            Chosen?.Invoke(new ShikimoriChoice(row.Anime, null));
        }
    }

    private bool CanChoose() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanUseManualId))]
    private void UseManualId() => Chosen?.Invoke(new ShikimoriChoice(null, ManualId.Trim()));

    private bool CanUseManualId() => ManualId.Trim() is { Length: > 0 } id && id.All(char.IsAsciiDigit);

    [RelayCommand]
    private void Skip() => Chosen?.Invoke(ShikimoriChoice.Skip);
}
