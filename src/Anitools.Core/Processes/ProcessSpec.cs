namespace Anitools.Core.Processes;

/// <summary>Что запустить: программа и аргументы по одному (без ручного экранирования).</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Строки stdout отдаются сюда (например, -progress pipe:1) и тогда не копятся в результате.</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>Каждая строка stderr — для живого лога.</summary>
    public Action<string>? OnStderrLine { get; init; }

    /// <summary>Сколько последних строк stderr хранить для лога ошибки (полный вывод ffmpeg бывает в десятки МБ).</summary>
    public int StderrTailLines { get; init; } = 400;

    /// <summary>Командная строка для лога и кнопки «скопировать команду».</summary>
    public override string ToString() => CommandLine.Format([FileName, .. Arguments]);
}

/// <summary>Итог процесса. stdout — целиком (если не отдавался в <see cref="ProcessSpec.OnStdoutLine"/>), stderr — хвост.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardErrorTail, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Запуск внешних программ. В тестах подменяется фейком.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Запускает процесс и ждёт завершения. При отмене убивает процесс со всеми дочерними
    /// (шим Chocolatey → настоящий ffmpeg) и бросает <see cref="OperationCanceledException"/>.
    /// Программа не найдена — <see cref="System.ComponentModel.Win32Exception"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}
