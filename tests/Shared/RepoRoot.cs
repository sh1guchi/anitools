namespace Anitools.Tests.Shared;

/// <summary>Корень репозитория (папка с Anitools.slnx) — для эталонов, оригинала и скриншотов.</summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> Root = new(Find);

    public static string FullPath => Root.Value;

    public static string Combine(params string[] parts) => Path.Combine([FullPath, .. parts]);

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Anitools.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Не найден корень репозитория (Anitools.slnx) выше {AppContext.BaseDirectory}");
    }
}
