using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Common;

/// <summary>Внешняя команда плана: программа и аргументы — ровно как запускает их оригинал.</summary>
public sealed record PlannedCommand(Tool Tool, IReadOnlyList<string> Arguments)
{
    /// <summary>Коды выхода «с предупреждениями»: не ошибка, если все выходные файлы на месте и не пустые (mkvextract: 1).</summary>
    public IReadOnlyList<int> WarningExitCodes { get; init; } = [];

    /// <summary>Папка, из которой запускать (хардсаб: пути в фильтре subtitles — относительные); null — текущая.</summary>
    public string? WorkingDirectory { get; init; }

    public override string ToString() => CommandLine.Format([Tool.ToString().ToLowerInvariant(), .. Arguments]);
}

public enum PlanItemStatus
{
    /// <summary>Будет выполнено.</summary>
    Run,

    /// <summary>Уже готово или нечего делать (причина в <see cref="PlanItem.Reason"/>).</summary>
    Skip,

    /// <summary>Нельзя выполнить (причина в <see cref="PlanItem.Reason"/>).</summary>
    Error,
}

/// <summary>Один шаг плана: одна команда над одним входным файлом.</summary>
public sealed record PlanItem
{
    /// <summary>Входной файл (полный путь).</summary>
    public required string Source { get; init; }

    /// <summary>Что показать в списке: «Frieren - 01.mkv → AniLibria.TV».</summary>
    public required string Label { get; init; }

    public required PlanItemStatus Status { get; init; }

    public string? Reason { get; init; }

    /// <summary>Выходные файлы (полные пути): при ошибке и отмене недописанные удаляются.</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>
    /// После успеха переименовать (с заменой): так недописанный «x.part.mkv» никогда не спутать с готовым «x.mkv».
    /// </summary>
    public (string From, string To)? RenameOnSuccess { get; init; }

    public PlannedCommand? Command { get; init; }
}

/// <summary>План операции: что будет сделано с каждым файлом. Основа и для превью в GUI, и для тестов.</summary>
public sealed record OperationPlan(string Title, string Folder, IReadOnlyList<PlanItem> Items)
{
    public int RunCount => Items.Count(i => i.Status == PlanItemStatus.Run);

    public int SkipCount => Items.Count(i => i.Status == PlanItemStatus.Skip);

    public int ErrorCount => Items.Count(i => i.Status == PlanItemStatus.Error);
}

/// <summary>План нельзя построить: нет файлов, неверный выбор дорожек и т.п. Сообщение — для пользователя.</summary>
public sealed class PlanException(string message) : Exception(message);
