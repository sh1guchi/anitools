using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Anitools.App.Tests;

/// <summary>
/// Shikimori без сети: на GraphQL (POST) — заданный ответ, на постеры (GET) — картинки по адресу, прочее — 404.
/// Считает запросы постеров, чтобы дождаться их загрузки.
/// </summary>
internal sealed class FakeShikimori(string graphql) : HttpMessageHandler
{
    private int _posterRequests;

    public Dictionary<string, byte[]> Posters { get; } = [];

    public int PosterRequests => Volatile.Read(ref _posterRequests);

    /// <summary>Постер 225×318 (как main у Shikimori): вертикальный градиент, цвета — 0xAARRGGBB.</summary>
    public static byte[] Poster(uint top, uint bottom)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(225, 318), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            var row = new int[frame.Size.Width];
            for (var y = 0; y < frame.Size.Height; y++)
            {
                Array.Fill(row, unchecked((int)Mix(top, bottom, y / (double)(frame.Size.Height - 1))));
                Marshal.Copy(row, 0, frame.Address + (y * frame.RowBytes), row.Length);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(graphql, Encoding.UTF8, "application/json") });
        }

        Interlocked.Increment(ref _posterRequests);
        return Task.FromResult(Posters.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift) => (uint)Math.Round((((a >> shift) & 0xFF) * (1 - t)) + (((b >> shift) & 0xFF) * t)) << shift;
        return 0xFF000000 | Channel(16) | Channel(8) | Channel(0);
    }
}
