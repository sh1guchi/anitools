using Anitools.Core.Templates;

namespace Anitools.Core.Tests.Templates;

/// <summary>Движок шаблонов — как tplTokenize / validateTemplate / tplRender в core.js Anime Uploader, без HTML.</summary>
public sealed class TextTemplateTests
{
    private static readonly string[] Names = ["название", "серия", "сезон", "суффикс"];

    [Fact]
    public void Tokens_with_positions()
    {
        var (tokens, errors) = TextTemplate.Tokenize("{название} - {серия}{?суффикс}.{суффикс}{/}");

        Assert.Empty(errors);
        Assert.Equal(
        [
            new TemplateToken(TemplateTokenKind.Variable, 0, 10, "название"),
            new TemplateToken(TemplateTokenKind.Text, 10, 13, " - "),
            new TemplateToken(TemplateTokenKind.Variable, 13, 20, "серия"),
            new TemplateToken(TemplateTokenKind.Condition, 20, 30, "суффикс"),
            new TemplateToken(TemplateTokenKind.Text, 30, 31, "."),
            new TemplateToken(TemplateTokenKind.Variable, 31, 40, "суффикс"),
            new TemplateToken(TemplateTokenKind.End, 40, 43, "/"),
        ], tokens);
    }

    [Fact]
    public void Double_brace_is_a_literal_brace_and_lone_brace_is_an_error()
    {
        var (tokens, errors) = TextTemplate.Tokenize("a {{b} c");
        Assert.Equal([new TemplateToken(TemplateTokenKind.Text, 0, 8, "a {b} c")], tokens);
        Assert.Empty(errors);

        (tokens, errors) = TextTemplate.Tokenize("a { b");
        Assert.Equal([new TemplateToken(TemplateTokenKind.Text, 0, 5, "a { b")], tokens);
        Assert.Equal([new TemplateIssue(2, 3, "Одиночная «{» — для обычной скобки пиши {{")], errors);

        // «}» сама по себе — просто текст
        Assert.Empty(TextTemplate.Tokenize("a } b").Errors);
        Assert.Equal("{x}", TextTemplate.Render(TextTemplate.Escape("{x}"), _ => "!"));
    }

    [Fact]
    public void Conditions_and_comparisons()
    {
        var tokens = TextTemplate.Tokenize("{?сезон≠1}{? сезон != 1 }{?часть}").Tokens;
        Assert.Equal(
        [
            new TemplateToken(TemplateTokenKind.Condition, 0, 10, "сезон", "1"),
            new TemplateToken(TemplateTokenKind.Condition, 10, 25, "сезон", "1"),
            new TemplateToken(TemplateTokenKind.Condition, 25, 33, "часть"),
        ], tokens);

        var (_, errors) = TextTemplate.Tokenize("x{?сезон=1}y");
        Assert.Equal([new TemplateIssue(1, 11, "Непонятное условие «{?сезон=1}» — пиши {?переменная} или {?сезон≠1}")], errors);
    }

    [Fact]
    public void Validation_reports_every_problem_in_order()
    {
        var issues = TextTemplate.Validate("{/}{сзн} {?сезон}x {", Names);

        Assert.Equal(
        [
            new TemplateIssue(0, 3, "Лишний {/} — условие не открыто"),
            new TemplateIssue(3, 8, "Неизвестная переменная {сзн}"),
            new TemplateIssue(9, 17, "Условие не закрыто — нужен {/}"),
            new TemplateIssue(19, 20, "Одиночная «{» — для обычной скобки пиши {{"),
        ], issues);
        Assert.Empty(TextTemplate.Validate("{название} - {серия}{?суффикс}.{суффикс}{/}", Names));
        Assert.Equal(
            [new TemplateIssue(TextTemplate.MaxLength, TextTemplate.MaxLength + 1, $"Шаблон длиннее {TextTemplate.MaxLength} символов")],
            TextTemplate.Validate(new string('x', TextTemplate.MaxLength + 1), Names));
    }

    [Theory]
    [InlineData("", "Show - 05")]
    [InlineData("1", "Show - 05")]
    [InlineData("01", "Show - 05")]
    [InlineData("2", "Show S2 - 05")]
    [InlineData("Клоун", "Show SКлоун - 05")]
    public void Season_comparison_hides_the_first_season(string season, string expected) =>
        Assert.Equal(expected, TextTemplate.Render("{название}{?сезон≠1} S{сезон}{/} - {серия}", Values(("название", "Show"), ("серия", "05"), ("сезон", season))));

    [Fact]
    public void Nested_conditions_and_soft_rendering()
    {
        var values = Values(("а", "1"), ("б", ""));
        Assert.Equal("<1>", TextTemplate.Render("<{?а}{а}{?б}+{б}{/}{/}>", values));
        Assert.Equal("<>", TextTemplate.Render("<{?б}{?а}{а}{/}!{/}>", values));

        // как tplRender: лишний {/} пропускается, незакрытое условие тянется до конца, неизвестное — пусто
        Assert.Equal("x1", TextTemplate.Render("x{/}{а}", values));
        Assert.Equal("x", TextTemplate.Render("x{?б}tail", values));
        Assert.Equal("[]", TextTemplate.Render("[{нет}]", values));
    }

    [Fact]
    public void Uses_and_pairs()
    {
        const string src = "{?сезон≠1}S{сезон}{?а}{/}{/}{/}";
        Assert.True(TextTemplate.Uses(src, "сезон"));
        Assert.True(TextTemplate.Uses(src, "а"));
        Assert.False(TextTemplate.Uses(src, "серия"));

        var tokens = TextTemplate.Tokenize(src).Tokens;
        var pairs = TextTemplate.Pairs(tokens);
        Assert.Equal(5, pairs[0]); // {?сезон≠1} … второй {/}
        Assert.Equal(0, pairs[5]);
        Assert.Equal(4, pairs[3]); // {?а}{/}
        Assert.False(pairs.ContainsKey(6)); // лишний {/}
    }

    [Theory]
    [InlineData("To Be Hero X", "точки", "To.Be.Hero.X")]
    [InlineData("Re Zero - Season 2", "точки", "Re.Zero.Season.2")]
    [InlineData("Mr. Robot  ", "точки", "Mr.Robot")]
    [InlineData("1", "00", "01")]
    [InlineData("12", "00", "12")]
    [InlineData("7", "000", "007")]
    [InlineData("Клоун", "00", "Клоун")]
    [InlineData("", "00", "")]
    public void Formats_for_release_style_names(string value, string format, string expected)
    {
        Assert.Equal(expected, TextTemplate.Apply(value, format));
        Assert.Equal(expected, TextTemplate.Render($"{{x:{format}}}", _ => value));
    }

    [Fact]
    public void Formats_are_part_of_the_variable_token_and_checked()
    {
        Assert.Equal(
            [new TemplateToken(TemplateTokenKind.Variable, 0, 10, "сезон", Format: "00")],
            TextTemplate.Tokenize("{сезон:00}").Tokens);
        Assert.Empty(TextTemplate.Validate("{название:точки}.S{сезон:00}E{серия:000}", Names));
        Assert.Equal(
            ["Непонятное «:2» — можно {сезон:точки} (слова через точку) или {сезон:00} (нули до двух цифр)", "Неизвестная переменная {сзн}"],
            TextTemplate.Validate("{сезон:2}{сзн:00}", Names).Select(i => i.Message));
        Assert.True(TextTemplate.Uses("{сезон:00}", "сезон"));
    }

    private static Func<string, string?> Values(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;
}
