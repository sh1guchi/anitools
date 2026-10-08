using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anitools.Core.Install;

/// <summary>Вышедшая версия на GitHub: номер, страница выпуска, установщик и его SHA-256 (нет — null).</summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string? SetupUrl, string? SetupSha256, long? SetupSize);

/// <summary>
/// Обновления anitools (этап 9): последняя версия — <c>releases/latest</c> на GitHub (раз в день при запуске или
/// кнопкой в настройках). Установщик скачивается только с контрольной суммой: digest файла от GitHub или строка из
/// SHA256SUMS.txt того же выпуска.
/// </summary>
public sealed partial class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository)
{
    public const string DefaultRepository = "sh1guchi/anitools";
    public const string SetupAssetName = "anitools-setup.exe";
    public const string SumsAssetName = "SHA256SUMS.txt";

    /// <summary>Сколько ждать следующего куска при скачивании установщика.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string LatestUrl => $"https://api.github.com/repos/{repository}/releases/latest";

    /// <summary>Последний выпуск; null — выпусков нет или GitHub не ответил (проверка — не повод для ошибок).</summary>
    public async Task<ReleaseInfo?> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await GetJsonAsync(LatestUrl, cancellationToken).ConfigureAwait(false);
            if (doc is null || Parse(doc.RootElement) is not { } release)
            {
                return null;
            }

            // Нет digest у файла (старые выпуски) — сумма из SHA256SUMS.txt
            if (release is { SetupUrl: not null, SetupSha256: null } && SumsUrl(doc.RootElement) is { } sumsUrl
                && await GetTextAsync(sumsUrl, cancellationToken).ConfigureAwait(false) is { } sums)
            {
                release = release with { SetupSha256 = ToolInstaller.FindSha256(sums, SetupAssetName) };
            }

            return release;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Ответ releases/latest: tag_name «v1.2.0» → «1.2.0», установщик — файл anitools-setup.exe.</summary>
    public static ReleaseInfo? Parse(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object || Text(release, "tag_name") is not { } tag || TagRegex().Match(tag) is not { Success: true } m)
        {
            return null;
        }

        string? setupUrl = null, sha256 = null;
        long? size = null;
        foreach (var asset in Assets(release))
        {
            if (Text(asset, "name") == SetupAssetName)
            {
                setupUrl = Text(asset, "browser_download_url");
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : null;
                sha256 = Text(asset, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    && Sha256Regex().IsMatch(digest[7..]) ? digest[7..].ToLowerInvariant() : null;
            }
        }

        return new ReleaseInfo(m.Groups[1].Value, Text(release, "html_url") ?? $"https://github.com/{DefaultRepository}/releases", setupUrl, sha256, size);
    }

    /// <summary>«1.2.0» новее «1.1.9»? Непонятные номера — нет.</summary>
    public static bool IsNewer(string latest, string current) =>
        System.Version.TryParse(Normalize(latest), out var a) && System.Version.TryParse(Normalize(current), out var b) && a > b;

    /// <summary>Скачать установщик выпуска в папку (сверка SHA-256 обязательна).</summary>
    /// <exception cref="InstallException">Нет установщика или суммы, не скачалось, не сошлась сумма.</exception>
    public async Task<string> DownloadSetupAsync(ReleaseInfo release, string folder, IProgress<InstallProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (release.SetupUrl is not { } url || release.SetupSha256 is not { } sha256)
        {
            throw new InstallException($"В выпуске {release.Version} нет установщика с контрольной суммой — скачайте его со страницы выпуска.");
        }

        var target = Path.Combine(folder, $"anitools-setup-{release.Version}.exe");
        await Downloader.DownloadAsync(http, url, target, sha256, $"anitools {release.Version}", progress, StallTimeout, cancellationToken).ConfigureAwait(false);
        return target;
    }

    private static string Normalize(string version)
    {
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => version + ".0",
            _ => version,
        };
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken) =>
        await GetTextAsync(url, cancellationToken).ConfigureAwait(false) is { } text ? JsonDocument.Parse(text) : null;

    private async Task<string?> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"anitools/{AppInfo.Version}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    private static string? SumsUrl(JsonElement release) =>
        Assets(release).Where(a => Text(a, "name") == SumsAssetName).Select(a => Text(a, "browser_download_url")).FirstOrDefault();

    private static IEnumerable<JsonElement> Assets(JsonElement release) =>
        release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    [GeneratedRegex(@"^v?(\d{1,4}(?:\.\d{1,4}){1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
