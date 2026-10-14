using Pairnets.Core;
using Xunit;

namespace Pairnets.Tests.Unit;

public sealed class PlainTextTests
{
    [Theory]
    [InlineData("LAPTOP\u200B-2", "LAPTOP-2")]
    [InlineData("\u202Egnirts", "gnirts")]
    [InlineData("a\u00ADb\u061Cc\u180Ed", "abcd")]
    [InlineData("x\u200B\u200C\u200D\u200E\u200Fy", "xy")]
    [InlineData("x\u202A\u202B\u202C\u202D\u202Ey", "xy")]
    [InlineData("x\u2060\u2061\u2062\u2063\u2064y", "xy")]
    [InlineData("x\u2066\u2067\u2068\u2069y", "xy")]
    [InlineData("\uFEFFname", "name")]
    [InlineData("a\r\nb\tc\u0000d", "abcd")]
    [InlineData("a\u2028b\u2029c", "abc")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("Büro-PC 日本語", "Büro-PC 日本語")]
    public void RemovesHiddenAndDirectionCharacters(string input, string expected) => Assert.Equal(expected, PlainText.Clean(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\u200B\u200E  \u202E")]
    public void NothingUsableGivesEmpty(string? input) => Assert.Equal(string.Empty, PlainText.Clean(input));

    [Fact]
    public void CutsToTheLengthWithoutSplittingACharacter()
    {
        Assert.Equal(new string('a', 64), PlainText.Clean(new string('a', 200)));
        var emoji = "\U0001F600"; // two UTF-16 units
        var cut = PlainText.Clean(new string('a', 63) + emoji, 64);
        Assert.Equal(new string('a', 63), cut);
        Assert.Equal("ab", PlainText.Clean("ab  cd", 4));
    }

    [Fact]
    public void RemovesTagCharactersThatHideInsideText()
    {
        Assert.Equal("ab", PlainText.Clean("a\U000E0041\U000E007Fb"));
    }

    [Fact]
    public void TheJoinNoticeNamesTheAddressWhenTheNestSaidIt()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("LAPTOP-2 (Windows) wants to join from 203.0.113.7", new Pairnets.Core.Client.JoinRequest("ABCD-EFGH", "LAPTOP-2", "Windows", now, "203.0.113.7").Title);
        Assert.Equal("LAPTOP-2 (Windows) wants to join", new Pairnets.Core.Client.JoinRequest("ABCD-EFGH", "LAPTOP-2", "Windows", now).Title);
    }
}
