using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Anitools.Core.Templates;

/// <summary>Кусок шаблона: обычный текст, {переменная}, {?условие} или {/} — конец условия.</summary>
public enum TemplateTokenKind
{
    Text,
    Variable,
    Condition,
    End,
}

/// <summary>Кусок шаблона и где он в строке.</summary>
/// <param name="Start">Начало в строке шаблона.</param>
/// <param name="End">Конец, не включая.</param>
/// <param name="Value">Текст (уже без удвоенных «{{») или имя переменной.</param>
/// <param name="NotEqual">У сравнения {?сезон≠1} — «1».</param>
/// <param name="Format">Преобразование у {сезон:00} — «00», у {название:точки} — «точки».</param>
public sealed record TemplateToken(TemplateTokenKind Kind, int Start, int End, string Value, string? NotEqual = null, string? Format = null)
{
    public int Length => End - Start;

    /// <summary>Неделимая плашка в редакторе: переменная, условие или {/}.</summary>
    public bool IsPill => Kind != TemplateTokenKind.Text;
}

/// <summary>Ошибка или предупреждение в шаблоне: кусок Start..End и что с ним не так.</summary>
public sealed record TemplateIssue(int Start, int End, string Message, bool IsWarning = false)
{
    public int Length => End - Start;
}

/// <summary>Переменная шаблона и подпись к ней для подсказок.</summary>
public sealed record TemplateVariable(string Name, string Label)
{
    public string Token => "{" + Name + "}";
}

/// <summary>Сохранённый шаблон под своим именем.</summary>
public sealed record TemplatePreset(string Name, string Template);

/// <summary>Готовое условие для меню: «сезон≠1» — «если сезон не 1».</summary>
public sealed record TemplateCondition(string Expression, string Label)
{
    public string Token => "{?" + Expression + "}";
}

/// <summary>
/// Шаблоны как в Anime Uploader (core.js: tplTokenize, validateTemplate, tplRender), без HTML — для имён файлов:
/// <list type="bullet">
/// <item>обычный текст;</item>
/// <item>{переменная} — значение;</item>
/// <item>{?переменная}…{/} — кусок, только если значение непустое;</item>
/// <item>{?сезон≠1}…{/} (или !=) — если значение непустое и не «1» (числа сравниваются как числа: «01» = «1»);</item>
/// <item>{{ — буквальная «{».</item>
/// </list>
/// Для имён файлов в стиле релизов (To.Be.Hero.X.S01E01…) — преобразования, которых в Anime Uploader не было:
/// {название:точки} — слова через точку, {сезон:00} — число нулями до двух цифр ({серия:000} — до трёх).
/// Разбор не падает: ошибки — с позициями, неверный шаблон всё равно подставляется (мягко).
/// </summary>
public static partial class TextTemplate
{
    /// <summary>Длиннее — ошибка: имя файла всё равно не длиннее 255 символов.</summary>
    public const int MaxLength = 500;

    /// <summary>Преобразование {название:точки}.</summary>
    public const string Dots = "точки";

    /// <summary>Куски шаблона и ошибки разбора (одиночная «{», непонятное условие).</summary>
    public static (IReadOnlyList<TemplateToken> Tokens, IReadOnlyList<TemplateIssue> Errors) Tokenize(string? source)
    {
        var src = source ?? "";
        var tokens = new List<TemplateToken>();
        var errors = new List<TemplateIssue>();
        var buffer = new StringBuilder();
        var bufferStart = 0;
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (c != '{')
            {
                Literal(c.ToString(), i);
                i++;
                continue;
            }

            if (i + 1 < src.Length && src[i + 1] == '{')
            {
                Literal("{", i);
                i += 2;
                continue;
            }

            var close = src.IndexOf('}', i + 1);
            var newline = src.IndexOf('\n', i + 1);
            var inner = close == -1 || (newline != -1 && newline < close) ? null : src[(i + 1)..close];
            if (inner == "/")
            {
                Flush(i);
                tokens.Add(new TemplateToken(TemplateTokenKind.End, i, close + 1, "/"));
                i = close + 1;
                continue;
            }

            if (inner is not null && inner.StartsWith('?'))
            {
                var m = ConditionRegex().Match(inner[1..]);
                if (m.Success)
                {
                    Flush(i);
                    tokens.Add(new TemplateToken(TemplateTokenKind.Condition, i, close + 1, m.Groups[1].Value,
                        m.Groups[2].Success ? m.Groups[3].Value : null));
                }
                else
                {
                    errors.Add(new TemplateIssue(i, close + 1, $"Непонятное условие «{{{inner}}}» — пиши {{?переменная}} или {{?сезон≠1}}"));
                    Literal(src[i..(close + 1)], i);
                }

                i = close + 1;
                continue;
            }

            if (inner is not null && VariableRegex().Match(inner) is { Success: true } v)
            {
                Flush(i);
                tokens.Add(new TemplateToken(TemplateTokenKind.Variable, i, close + 1, v.Groups[1].Value,
                    Format: v.Groups[2].Success ? v.Groups[2].Value : null));
                i = close + 1;
                continue;
            }

            errors.Add(new TemplateIssue(i, i + 1, "Одиночная «{» — для обычной скобки пиши {{"));
            Literal("{", i);
            i++;
        }

        Flush(src.Length);
        return (tokens, errors);

        void Literal(string s, int at)
        {
            if (buffer.Length == 0)
            {
                bufferStart = at;
            }

            buffer.Append(s);
        }

        void Flush(int end)
        {
            if (buffer.Length > 0)
            {
                tokens.Add(new TemplateToken(TemplateTokenKind.Text, bufferStart, end, buffer.ToString()));
                buffer.Clear();
            }
        }
    }

    /// <summary>Ошибки шаблона по порядку: разбор, неизвестные переменные, непарные условия, длина.</summary>
    public static IReadOnlyList<TemplateIssue> Validate(string? source, IReadOnlyCollection<string> variables)
    {
        var src = source ?? "";
        var (tokens, parseErrors) = Tokenize(src);
        var errors = parseErrors.ToList();
        if (src.Length > MaxLength)
        {
            errors.Add(new TemplateIssue(MaxLength, src.Length, $"Шаблон длиннее {MaxLength} символов"));
        }

        var open = new Stack<TemplateToken>();
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TemplateTokenKind.Variable:
                    CheckVariable(token);
                    break;
                case TemplateTokenKind.Condition:
                    CheckVariable(token);
                    open.Push(token);
                    break;
                case TemplateTokenKind.End when open.Count == 0:
                    errors.Add(new TemplateIssue(token.Start, token.End, "Лишний {/} — условие не открыто"));
                    break;
                case TemplateTokenKind.End:
                    open.Pop();
                    break;
            }
        }

        errors.AddRange(open.Select(c => new TemplateIssue(c.Start, c.End, "Условие не закрыто — нужен {/}")));
        return [.. errors.OrderBy(e => e.Start).ThenBy(e => e.End)];

        void CheckVariable(TemplateToken token)
        {
            if (!variables.Contains(token.Value))
            {
                errors.Add(new TemplateIssue(token.Start, token.End, $"Неизвестная переменная {{{token.Value}}}"));
            }
            else if (token.Format is { } format && FormatLabel(format) is null)
            {
                errors.Add(new TemplateIssue(token.Start, token.End,
                    $"Непонятное «:{format}» — можно {{{token.Value}:точки}} (слова через точку) или {{{token.Value}:00}} (нули до двух цифр)"));
            }
        }
    }

    /// <summary>
    /// Подстановка: неизвестная переменная — пусто. Неверный шаблон не падает: лишний {/} пропускается,
    /// незакрытое условие тянется до конца.
    /// </summary>
    public static string Render(string? source, Func<string, string?> values)
    {
        var output = new StringBuilder();
        var conditions = new List<bool>();
        var skipping = false;
        foreach (var token in Tokenize(source).Tokens)
        {
            switch (token.Kind)
            {
                case TemplateTokenKind.Condition:
                    conditions.Add(skipping || Holds(values(token.Value), token.NotEqual));
                    skipping = conditions.Contains(false);
                    break;
                case TemplateTokenKind.End:
                    if (conditions.Count > 0)
                    {
                        conditions.RemoveAt(conditions.Count - 1);
                    }

                    skipping = conditions.Contains(false);
                    break;
                case TemplateTokenKind.Text when !skipping:
                    output.Append(token.Value);
                    break;
                case TemplateTokenKind.Variable when !skipping:
                    output.Append(Apply(values(token.Value), token.Format));
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>Условие {?x} — значение непустое; {?x≠y} — ещё и не y (два целых числа — по величине: «01» = «1»).</summary>
    public static bool Holds(string? value, string? notEqual)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (notEqual is null)
        {
            return true;
        }

        if (long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            && long.TryParse(notEqual.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b))
        {
            return a != b;
        }

        return !string.Equals(value, notEqual, StringComparison.Ordinal);
    }

    /// <summary>Что делает преобразование: «точки» и «00», «000»… (до 9 нулей); непонятное — null.</summary>
    public static string? FormatLabel(string format) =>
        format == Dots ? "слова через точку"
        : format.Length is >= 1 and <= 9 && format.All(c => c == '0') ? $"число нулями до {format.Length} цифр"
        : null;

    /// <summary>
    /// Значение с преобразованием: «точки» — слова через точку (одиночные тире и повторные точки выпадают:
    /// «Re Zero - Season 2» → «Re.Zero.Season.2»); нули — целое число дополняется слева, нечисло остаётся как есть.
    /// </summary>
    public static string Apply(string? value, string? format)
    {
        var v = value ?? "";
        if (format == Dots)
        {
            var words = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Trim('-', '–', '—').Length > 0);
            return MultipleDotsRegex().Replace(string.Join('.', words), ".").Trim('.');
        }

        return format is { Length: > 0 } && format.All(c => c == '0') && v.Length > 0 && v.All(char.IsAsciiDigit)
            ? v.PadLeft(format.Length, '0')
            : v;
    }

    /// <summary>Обычный текст → кусок шаблона: «{» удваивается.</summary>
    public static string Escape(string? text) => (text ?? "").Replace("{", "{{", StringComparison.Ordinal);

    /// <summary>Есть ли в шаблоне эта переменная — значением или в условии.</summary>
    public static bool Uses(string? source, string variable) =>
        Tokenize(source).Tokens.Any(t => t.Kind is TemplateTokenKind.Variable or TemplateTokenKind.Condition && t.Value == variable);

    /// <summary>
    /// Пары условие → его {/} (по индексам в списке кусков); у непарных — нет пары.
    /// Нужны редактору: рамка вокруг условного куска, «убрать условие».
    /// </summary>
    public static IReadOnlyDictionary<int, int> Pairs(IReadOnlyList<TemplateToken> tokens)
    {
        var pairs = new Dictionary<int, int>();
        var open = new Stack<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TemplateTokenKind.Condition)
            {
                open.Push(i);
            }
            else if (tokens[i].Kind == TemplateTokenKind.End && open.Count > 0)
            {
                var start = open.Pop();
                pairs[start] = i;
                pairs[i] = start;
            }
        }

        return pairs;
    }

    // core.js: VAR_NAME_RE (+ «:преобразование») и разбор {?…}
    [GeneratedRegex(@"^([\p{L}_][\p{L}\p{N}_]*)(?::([^{}:\s]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleDotsRegex();

    [GeneratedRegex(@"^\s*([\p{L}_][\p{L}\p{N}_]*)\s*(?:(≠|!=)\s*(.*?)\s*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionRegex();
}
