using ZetPDF.Core.Utilities;

namespace ZetPDF.Core.Tests;

public class HyphenJoinTests
{
    [Theory]
    [InlineData("przeno-\r\nszone", "przenoszone")]
    [InlineData("przeno-\nszone", "przenoszone")]
    [InlineData("biało-czerwony", "biało-czerwony")]
    [InlineData("biało- czerwony", "biało- czerwony")]
    [InlineData("2020-\r\n2021", "2020-\r\n2021")]
    [InlineData("słowo -\r\nnastępne", "słowo -\r\nnastępne")]
    [InlineData("koniec-\r\n", "koniec-\r\n")]
    [InlineData("", "")]
    public void Joins_only_letter_hyphen_line_break_letter(string text, string expected)
    {
        var joined = HyphenJoin.Join(text, out var map);
        Assert.Equal(expected, joined);
        Assert.Equal(joined.Length, map.Length);
        for (int i = 0; i < map.Length; i++) Assert.Equal(joined[i], text[map[i]]);
    }
}
