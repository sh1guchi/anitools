using Anitools.Tests.Shared;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Anitools.App.Tests;

/// <summary>
/// Скриншоты экранов. По умолчанию пишутся в artifacts/screenshots (не в git).
/// С ANITOOLS_UPDATE_SCREENSHOTS=1 — в docs/screenshots, оттуда их смотрят в PR.
/// </summary>
internal static class Screenshots
{
    public const string UpdateVariable = "ANITOOLS_UPDATE_SCREENSHOTS";

    public static string Directory =>
        Environment.GetEnvironmentVariable(UpdateVariable) == "1"
            ? RepoRoot.Combine("docs", "screenshots")
            : RepoRoot.Combine("artifacts", "screenshots");

    /// <summary>Рендерит кадр окна, сохраняет PNG и возвращает кадр для проверок.</summary>
    public static WriteableBitmap Capture(TopLevel topLevel, string name)
    {
        // Первый кадр может нести старое положение элементов, у которых только что сменился размер (кнопка
        // «Запустить (12)» после «Запустить»): он отбрасывается, снимается следующий
        topLevel.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = topLevel.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Headless-платформа не отрисовала кадр");
        System.IO.Directory.CreateDirectory(Directory);
        frame.Save(Path.Combine(Directory, name + ".png"), PngBitmapEncoderOptions.Default);
        return frame;
    }

    /// <summary>Число разных цветов в кадре: пустое или залитое одним цветом окно даёт 1–2.</summary>
    public static int CountColors(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var colors = new HashSet<int>();
        var rowBytes = buffer.RowBytes;
        var row = new int[buffer.Size.Width];
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * rowBytes), row, 0, row.Length);
            colors.UnionWith(row);
        }

        return colors.Count;
    }
}
