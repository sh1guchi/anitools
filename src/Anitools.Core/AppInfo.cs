using System.Reflection;

namespace Anitools.Core;

/// <summary>Общие сведения о приложении.</summary>
public static class AppInfo
{
    public const string Name = "anitools";

    /// <summary>Версия сборки без хвоста «+commit».</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return "0.0.0";
        }

        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }
}
