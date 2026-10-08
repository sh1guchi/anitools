using System.Text;
using Anitools.Core.Parsing;

namespace Anitools.Core.Tests.Parsing;

public sealed class TextUtilsTests
{
    [Fact]
    public void Lower_and_casefold_match_python_for_every_character() =>
        GoldenAssert.All("str_casing", input =>
        {
            var s = new Rune(input.GetInt32()).ToString();
            return new[] { TextUtils.Lower(s), TextUtils.CaseFold(s) };
        });

    [Fact]
    public void IsSpace_matches_python_isspace()
    {
        // [hex(c) for c in range(0x110000) if chr(c).isspace()] — Python 3.13
        int[] python =
        [
            0x9, 0xa, 0xb, 0xc, 0xd, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002,
            0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
        ];
        var actual = Enumerable.Range(0, 0x10000).Where(c => TextUtils.IsSpace((char)c)).ToArray();
        Assert.Equal(python, actual);
    }

    [Theory]
    [InlineData("+2", "2")]
    [InlineData(" 7 ", "7")]
    [InlineData("１２", "12")]
    [InlineData("1_0", "10")]
    [InlineData("٢", "2")]
    [InlineData("٣_٤", "34")]
    [InlineData("-0", "0")]
    [InlineData("\u20072", "2")]
    [InlineData("007", "7")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
    public void ParseInt_accepts_what_python_int_accepts(string input, string expected) =>
        Assert.Equal(expected, TextUtils.IntString(input));

    [Theory]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1_")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1.5")]
    [InlineData("\u22125")]
    [InlineData("\x1f3\x1c")]
    [InlineData("0x10")]
    [InlineData("1 2")]
    [InlineData("²")]
    public void ParseInt_rejects_what_python_int_rejects(string input) =>
        Assert.Throws<FormatException>(() => TextUtils.ParseInt(input));

    [Theory]
    [InlineData(".mkv", ".mkv", "")]
    [InlineData("Title.", "Title", ".")]
    [InlineData("a.b.", "a.b", ".")]
    [InlineData("...", "...", "")]
    [InlineData("Title -.mkv", "Title -", ".mkv")]
    [InlineData("   Title - 05.mkv   ", "   Title - 05", ".mkv   ")]
    [InlineData("..Title..", "..Title.", ".")]
    [InlineData("Title", "Title", "")]
    [InlineData("a..b", "a.", ".b")]
    [InlineData(".a.b", ".a", ".b")]
    [InlineData("", "", "")]
    [InlineData(@"D:\anime.x\Title", @"D:\anime.x\Title", "")]
    public void SplitExt_matches_os_path_splitext(string path, string root, string ext) =>
        Assert.Equal((root, ext), TextUtils.SplitExt(path));

    [Theory]
    [InlineData(".mkv", ".mkv")]
    [InlineData("Title.", "Title.")]
    [InlineData("a.b.", "a.b.")]
    [InlineData("...", "...")]
    [InlineData("Title -.mkv", "Title -")]
    [InlineData("   Title - 05.mkv   ", "   Title - 05")]
    [InlineData("..Title..", "..Title..")]
    [InlineData("a..b", "a.")]
    [InlineData(".a.b", ".a")]
    [InlineData("x.", "x.")]
    [InlineData("", "")]
    [InlineData(@"D:\anime\Title - 05.mkv", "Title - 05")]
    public void Stem_matches_purepath_stem(string path, string stem) =>
        Assert.Equal(stem, TextUtils.Stem(path));

    [Fact]
    public void CompareCodePoints_orders_like_python_sorted()
    {
        // sorted(['\uff21', '\U0001F600', 'a', '\ud7ff']) → ['a', '\ud7ff', 'Ａ', '😀']
        string[] input = ["\uff21", "\U0001F600", "a", "\ud7ff"];
        Assert.Equal(["a", "\ud7ff", "\uff21", "\U0001F600"], input.Order(TextUtils.CodePointComparer));
        Assert.True(TextUtils.CompareCodePoints("ab", "abc") < 0);
        Assert.Equal(0, TextUtils.CompareCodePoints("ж", "ж"));
    }

    [Fact]
    public void Len_counts_code_points() => Assert.Equal(3, TextUtils.Len("a\U0001F600b"));
}
