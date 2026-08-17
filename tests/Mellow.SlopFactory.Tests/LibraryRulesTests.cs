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

    [Fact]
    public void AdvancedGenerationSettingsAcceptACompactJsonObject()
    {
        var settings = LibraryRules.ValidateGenerationSettings(new GenerationSettings(AdvancedJson: "{ \"response_format\" : { \"type\" : \"json_object\" } }"));

        Assert.Equal("{ \"response_format\" : { \"type\" : \"json_object\" } }", settings.AdvancedJson);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"temperature\":0.2}")]
    [InlineData("{\"model\":\"other\"}")]
    [InlineData("{\"response_format\":")]
    [InlineData("{\"logprobs\":\"yes\"}")]
    [InlineData("{\"stop\":[\"END\",1]}")]
    public void AdvancedGenerationSettingsRejectInvalidOrManagedFields(string json)
    {
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateGenerationSettings(new GenerationSettings(AdvancedJson: json)));
    }

    [Fact]
    public void AdvancedGenerationSettingsPreviewRedactsCredentialLikeValues()
    {
        var preview = LibraryRules.BuildAdvancedGenerationSettingsPreview(new GenerationSettings(AdvancedJson: "{\"response_format\":{\"type\":\"json_object\"},\"api_key\":\"secret-value\",\"nested\":{\"accessToken\":\"another-secret\"}}"));

        Assert.NotNull(preview);
        Assert.Contains("json_object", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", preview, StringComparison.Ordinal);
        Assert.Equal(2, preview!.Split("[redacted]", StringSplitOptions.None).Length - 1);
    }
}
