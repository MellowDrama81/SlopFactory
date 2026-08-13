using Mellow.SlopFactory.Domain;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class LibraryRulesTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("abcdefgh", 2)]
    [InlineData("abcdefghi", 3)]
    public void EstimateTokenCountRoundsUpAndNeverReturnsZeroForNonEmptyText(string? text, int expected)
    {
        Assert.Equal(expected, LibraryRules.EstimateTokenCount(text));
    }
}
