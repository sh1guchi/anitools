using System.Security.Cryptography;

namespace Anitools.Core.Install;

/// <summary>Скачивание файла с ходом и проверкой SHA-256 (программы из настроек, обновления anitools).</summary>
public static class Downloader
{
    /// <summary>
    /// Скачать в файл (через .part), считая SHA-256 на лету; ход — каждые ~0,5 МБ. Нет данных дольше
    /// <paramref name="stallTimeout"/> — связь оборвалась.
    /// </summary>
    /// <param name="sha256">Ожидаемая сумма; null — не проверять.</param>
    /// <exception cref="InstallException">Не скачалось, оборвалось, не сошлась сумма.</exception>
    public static async Task DownloadAsync(
        HttpClient http, string url, string target, string? sha256, string what, IProgress<InstallProgress>? progress, TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        var part = target + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            progress?.Report(new InstallProgress($"скачиваю {what}"));
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InstallException($"Не удалось скачать {what}: сайт ответил {(int)response.StatusCode}. Попробуйте позже.");
            }

            var total = response.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var file = File.Create(part);
                await using (file.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long done = 0, reported = 0;
                    while (true)
                    {
                        stall.CancelAfter(stallTimeout);
                        int read;
                        try
                        {
                            read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new InstallException($"Скачивание {what} остановилось: нет данных {stallTimeout.TotalSeconds:0} с. Проверьте интернет и попробуйте ещё раз.");
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        done += read;
                        if (done - reported >= 512 * 1024)
                        {
                            reported = done;
                            progress?.Report(new InstallProgress($"скачиваю {what}", done, total));
                        }
                    }

                    progress?.Report(new InstallProgress($"скачиваю {what}", done, total));
                    if (total is { } expected && done != expected)
                    {
                        throw new InstallException($"Скачивание {what} оборвалось ({done} из {expected} байт). Попробуйте ещё раз.");
                    }
                }
            }

            if (sha256 is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallException($"Скачанный {what} не совпал с контрольной суммой с сайта — попробуйте ещё раз.");
            }

            File.Move(part, target, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException($"Не удалось скачать {what}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InstallException($"Не удалось скачать {what}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InstallException($"Нет доступа к {Path.GetDirectoryName(target)}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(part);
        }
    }
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
}
