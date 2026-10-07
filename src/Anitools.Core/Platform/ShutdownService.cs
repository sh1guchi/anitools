using Anitools.Core.Processes;

namespace Anitools.Core.Platform;

/// <summary>
/// «Выключить компьютер по завершении» (п.7): shutdown /s /t 60 — минута на отмену (shutdown /a).
/// Есть только на Windows.
/// </summary>
public sealed class ShutdownService(IProcessRunner runner)
{
    public const int DelaySeconds = 60;

    public static bool IsSupported => OperatingSystem.IsWindows();

    private static string Program => Path.Combine(Environment.SystemDirectory, "shutdown.exe");

    public static ProcessSpec ScheduleSpec(string program) => new(program, ["/s", "/t", DelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    public static ProcessSpec AbortSpec(string program) => new(program, ["/a"]);

    /// <summary>Запланировать выключение; false — не поддерживается или Windows отказала.</summary>
    public async Task<bool> ScheduleAsync(CancellationToken cancellationToken = default) =>
        IsSupported && (await runner.RunAsync(ScheduleSpec(Program), cancellationToken).ConfigureAwait(false)).Succeeded;

    /// <summary>Отменить запланированное выключение.</summary>
    public async Task<bool> AbortAsync(CancellationToken cancellationToken = default) =>
        IsSupported && (await runner.RunAsync(AbortSpec(Program), cancellationToken).ConfigureAwait(false)).Succeeded;
}
