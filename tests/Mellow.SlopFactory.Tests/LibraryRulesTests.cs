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

    [Theory]
    [InlineData(ProviderType.OpenAi, GenerationMode.Text)]
    [InlineData(ProviderType.DeepInfra, GenerationMode.Text)]
    [InlineData(ProviderType.OpenRouter, GenerationMode.Text)]
    [InlineData(ProviderType.OneMinAi, GenerationMode.Text)]
    public void GetInputSlotCapabilitiesReturnsUpToThreeReferenceImagesForTextModeOnAnyProvider(ProviderType providerType, GenerationMode mode)
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(providerType, mode);

        var capability = Assert.Single(capabilities);
        Assert.Equal(GenerationInputSlotRole.ReferenceImage, capability.Role);
        Assert.Equal(0, capability.MinCount);
        Assert.Equal(3, capability.MaxCount);
        Assert.False(capability.Required);
    }

    [Fact]
    public void GetInputSlotCapabilitiesReturnsFirstFrameOnlyForDeepInfraVideo()
    {
        var deepInfraCapabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.DeepInfra, GenerationMode.Video);
        var capability = Assert.Single(deepInfraCapabilities);
        Assert.Equal(GenerationInputSlotRole.FirstFrame, capability.Role);
        Assert.Equal(0, capability.MinCount);
        Assert.Equal(1, capability.MaxCount);

        Assert.Empty(LibraryRules.GetInputSlotCapabilities(ProviderType.OpenRouter, GenerationMode.Video));
    }

    [Theory]
    [InlineData(ProviderType.OpenAi, GenerationMode.Image)]
    [InlineData(ProviderType.OpenAi, GenerationMode.Audio)]
    [InlineData(ProviderType.DeepInfra, GenerationMode.Image)]
    [InlineData(ProviderType.OneMinAi, GenerationMode.Video)]
    public void GetInputSlotCapabilitiesReturnsNoneForEveryOtherModeProviderCombination(ProviderType providerType, GenerationMode mode)
    {
        Assert.Empty(LibraryRules.GetInputSlotCapabilities(providerType, mode));
    }

    [Fact]
    public void ValidateSourceSlotsAcceptsAssignmentsWithinDeclaredCapabilities()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Text);
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, "file-1", 0),
            new(GenerationInputSlotRole.ReferenceImage, "file-2", 1),
        ];

        LibraryRules.ValidateSourceSlots(slots, capabilities);
    }

    [Fact]
    public void ValidateSourceSlotsRejectsARoleTheModelDoesNotAccept()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Image);
        GenerationSourceSlot[] slots = [new(GenerationInputSlotRole.ReferenceImage, "file-1", 0)];

        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }

    [Fact]
    public void ValidateSourceSlotsRejectsExceedingARolesMaxCount()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.DeepInfra, GenerationMode.Video);
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.FirstFrame, "file-1", 0),
            new(GenerationInputSlotRole.FirstFrame, "file-2", 1),
        ];

        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }

    [Fact]
    public void ValidateSourceSlotsRejectsTheSameFileSelectedInMoreThanOneSlot()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Text);
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, "file-1", 0),
            new(GenerationInputSlotRole.ReferenceImage, "file-1", 1),
        ];

        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }
}
