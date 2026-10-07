using Anitools.App.ViewModels;
using Anitools.App.ViewModels.Pages;
using Anitools.App.Views.Pages;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Anitools.App.Views;

/// <summary>Страница → её представление. Явный список вместо поиска по именам типов (без рефлексии).</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(VideoOnlyPageViewModel)] = () => new VideoOnlyPageView(),
        [typeof(RemuxPageViewModel)] = () => new RemuxPageView(),
        [typeof(AudioExtractPageViewModel)] = () => new AudioExtractPageView(),
        [typeof(AudioMuxPageViewModel)] = () => new AudioMuxPageView(),
        [typeof(SubtitlesPageViewModel)] = () => new SubtitlesPageView(),
        [typeof(RenamePageViewModel)] = () => new RenamePageView(),
        [typeof(PlaceholderPageViewModel)] = () => new PlaceholderPageView(),
        [typeof(JobsPageViewModel)] = () => new JobsPageView(),
        [typeof(SettingsPageViewModel)] = () => new SettingsPageView(),
    };

    public Control? Build(object? param) =>
        param is not null && Views.TryGetValue(param.GetType(), out var create)
            ? create()
            : new TextBlock { Text = $"Нет представления для {param?.GetType().Name}" };

    public bool Match(object? data) => data is PageViewModel;
}
