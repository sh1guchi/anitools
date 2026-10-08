using Anitools.Core.Templates;
using Avalonia.Controls;
using Avalonia.Media;

namespace Anitools.App.Views.Controls;

/// <summary>Разметка строки шаблона для отрисовки: куски, пары условие ↔ {/}, вложенность и что подсветить ошибкой.</summary>
internal sealed class TemplateMarkup
{
    private TemplateMarkup(string text, IReadOnlyList<TemplateIssue>? issues)
    {
        Text = text;
        Issues = issues;
        Tokens = TextTemplate.Tokenize(text).Tokens;
        Pairs = TextTemplate.Pairs(Tokens);
        Depth = new int[Tokens.Count];
        Bad = new bool[Tokens.Count];

        var open = 0;
        for (var i = 0; i < Tokens.Count; i++)
        {
            if (Tokens[i].Kind == TemplateTokenKind.Condition)
            {
                Depth[i] = open;
                open += Pairs.ContainsKey(i) ? 1 : 0;
            }
            else if (Tokens[i].Kind == TemplateTokenKind.End && Pairs.TryGetValue(i, out var start))
            {
                open--;
                Depth[i] = Depth[start];
            }
        }

        // Ошибки могут прийти от прошлой версии строки — обрезаем по длине
        var bad = new List<(int, int)>();
        foreach (var issue in issues ?? [])
        {
            var a = Math.Clamp(issue.Start, 0, text.Length);
            var b = Math.Clamp(issue.End, 0, text.Length);
            if (issue.IsWarning || b <= a)
            {
                continue;
            }

            for (var i = 0; i < Tokens.Count; i++)
            {
                var t = Tokens[i];
                if (a < t.End && t.Start < b)
                {
                    if (t.IsPill)
                    {
                        Bad[i] = true;
                    }
                    else
                    {
                        bad.Add((Math.Max(a, t.Start), Math.Min(b, t.End)));
                    }
                }
            }
        }

        BadText = bad;
    }

    public string Text { get; }

    public IReadOnlyList<TemplateIssue>? Issues { get; }

    public IReadOnlyList<TemplateToken> Tokens { get; }

    /// <summary>Индекс условия → индекс его {/} и обратно.</summary>
    public IReadOnlyDictionary<int, int> Pairs { get; }

    /// <summary>Вложенность условия и его {/}: 0 — снаружи (жёлтая рамка), 1 и глубже — бирюзовая.</summary>
    public int[] Depth { get; }

    /// <summary>Плашки, на которые попала ошибка.</summary>
    public bool[] Bad { get; }

    /// <summary>Куски обычного текста с ошибкой: одиночная «{», запрещённый символ…</summary>
    public IReadOnlyList<(int Start, int End)> BadText { get; }

    public static TemplateMarkup For(TemplateMarkup? cached, string text, IReadOnlyList<TemplateIssue>? issues) =>
        cached is not null && cached.Text == text && ReferenceEquals(cached.Issues, issues) ? cached : new TemplateMarkup(text, issues);

    /// <summary>Плашка, которая начинается в позиции (вперёд) или кончается в ней (назад).</summary>
    public int PillAt(int position, bool forward)
    {
        for (var i = 0; i < Tokens.Count; i++)
        {
            if (Tokens[i].IsPill && (forward ? Tokens[i].Start : Tokens[i].End) == position)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Плашка, внутри которой позиция (не на краю).</summary>
    public int PillAround(int position)
    {
        for (var i = 0; i < Tokens.Count; i++)
        {
            if (Tokens[i].IsPill && Tokens[i].Start < position && position < Tokens[i].End)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Цвета плашек — токены из App.axaml (как у Anime Uploader: .tx-var, .tx-cond, .tx-raw.is-bad).</summary>
internal sealed class TemplatePalette
{
    public TemplatePalette(Control owner)
    {
        Accent = Brush(owner, "Accent2Brush", Color.Parse("#B89BFF"));
        AccentBg = Brush(owner, "AccentBgBrush", Color.Parse("#249B73FF"));
        AccentLine = Brush(owner, "AccentLineBrush", Color.Parse("#739B73FF"));
        Yellow = Brush(owner, "YellowBrush", Color.Parse("#FFC34B"));
        YellowBg = Brush(owner, "YellowBgBrush", Color.Parse("#1FFFC34B"));
        YellowLine = Brush(owner, "YellowLineBrush", Color.Parse("#66FFC34B"));
        Cyan = Brush(owner, "CyanBrush", Color.Parse("#4BCDD7"));
        CyanBg = Brush(owner, "CyanBgBrush", Color.Parse("#1F4BCDD7"));
        CyanLine = new SolidColorBrush(Color.Parse("#734BCDD7")).ToImmutable();
        Red = Brush(owner, "RedBrush", Color.Parse("#FF5F5F"));
        RedBg = Brush(owner, "RedBgBrush", Color.Parse("#1FFF5F5F"));
        RedLine = Brush(owner, "RedLineBrush", Color.Parse("#8CFF5F5F"));
        GroupYellow = new SolidColorBrush(Color.Parse("#0FFFC34B")).ToImmutable();
        GroupCyan = new SolidColorBrush(Color.Parse("#0F4BCDD7")).ToImmutable();
    }

    public IBrush Accent { get; }

    public IBrush AccentBg { get; }

    public IBrush AccentLine { get; }

    public IBrush Yellow { get; }

    public IBrush YellowBg { get; }

    public IBrush YellowLine { get; }

    public IBrush Cyan { get; }

    public IBrush CyanBg { get; }

    public IBrush CyanLine { get; }

    public IBrush Red { get; }

    public IBrush RedBg { get; }

    public IBrush RedLine { get; }

    /// <summary>Заливка условного куска (6% цвета рамки).</summary>
    public IBrush GroupYellow { get; }

    public IBrush GroupCyan { get; }

    /// <summary>Текст, заливка и обводка плашки.</summary>
    public (IBrush Text, IBrush Fill, IBrush Line) Pill(TemplateMarkup markup, int token) =>
        markup.Bad[token] ? (Red, RedBg, RedLine)
        : markup.Tokens[token].Kind == TemplateTokenKind.Variable ? (Accent, AccentBg, AccentLine)
        : markup.Depth[token] == 0 ? (Yellow, YellowBg, YellowLine)
        : (Cyan, CyanBg, CyanLine);

    private static IBrush Brush(Control owner, string key, Color fallback) =>
        owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback).ToImmutable();
}
