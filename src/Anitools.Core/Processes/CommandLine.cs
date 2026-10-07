using System.Text;

namespace Anitools.Core.Processes;

/// <summary>Командная строка из аргументов — как subprocess.list2cmdline в оригинале (для логов ошибок).</summary>
public static class CommandLine
{
    public static string Format(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            var needQuote = arg.Length == 0 || arg.Contains(' ', StringComparison.Ordinal) || arg.Contains('\t', StringComparison.Ordinal);
            if (needQuote)
            {
                sb.Append('"');
            }

            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    // обратные косые перед кавычкой удваиваются, кавычка экранируется
                    sb.Append('\\', backslashes * 2).Append("\\\"");
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                }
            }

            sb.Append('\\', backslashes);
            if (needQuote)
            {
                // перед закрывающей кавычкой — ещё раз столько же
                sb.Append('\\', backslashes).Append('"');
            }
        }

        return sb.ToString();
    }
}
