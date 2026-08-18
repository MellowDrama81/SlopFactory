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
    [InlineData(ProviderType.OpenAi, false)]
    [InlineData(ProviderType.GenericOpenAiCompatible, false)]
    [InlineData(ProviderType.OpenRouter, false)]
    [InlineData(ProviderType.OneMinAi, false)]
    [InlineData(ProviderType.DeepInfra, true)]
    public void SupportsAudioVoiceSelectionIsTrueOnlyForDeepInfra(ProviderType providerType, bool expected)
    {
        Assert.Equal(expected, LibraryRules.SupportsAudioVoiceSelection(providerType));
    }

    [Fact]
    public void EstimateGenerationCostReturnsNullWithoutPricing()
    {
        Assert.Null(LibraryRules.EstimateGenerationCost(null, 100, null, "source", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EstimateGenerationCostWithoutAMaxTokensCapHasNoReliableUpperBound()
    {
        var pricing = new ProviderModelPricing(0.00000045m, 0.0000032m, "USD");
        var effectiveAt = DateTimeOffset.UtcNow;

        var estimate = LibraryRules.EstimateGenerationCost(pricing, 1000, null, "OpenRouter", effectiveAt);

        Assert.NotNull(estimate);
        Assert.Equal(0.00000045m * 1000, estimate!.LowerBound);
        Assert.Equal(estimate.LowerBound, estimate.UpperBound);
        Assert.False(estimate.HasReliableUpperBound);
        Assert.Equal("USD", estimate.Currency);
        Assert.Equal("OpenRouter", estimate.Source);
        Assert.Equal(effectiveAt, estimate.EffectiveAt);
    }

    [Fact]
    public void EstimateGenerationCostWithAMaxTokensCapReportsAReliableUpperBound()
    {
        var pricing = new ProviderModelPricing(0.00000045m, 0.0000032m, "USD");

        var estimate = LibraryRules.EstimateGenerationCost(pricing, 1000, 500, "OpenRouter", DateTimeOffset.UtcNow);

        Assert.NotNull(estimate);
        var expectedLower = 0.00000045m * 1000;
        var expectedUpper = expectedLower + 0.0000032m * 500;
        Assert.Equal(expectedLower, estimate!.LowerBound);
        Assert.Equal(expectedUpper, estimate.UpperBound);
        Assert.True(estimate.HasReliableUpperBound);
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

    [Theory]
    [InlineData(ProviderType.OpenAi)]
    [InlineData(ProviderType.GenericOpenAiCompatible)]
    [InlineData(ProviderType.OpenRouter)]
    [InlineData(ProviderType.DeepInfra)]
    public void GetGenerationSettingsCapabilitiesReturnsEveryFieldForTextModeOnOpenAiCompatibleProviders(ProviderType providerType)
    {
        var capabilities = LibraryRules.GetGenerationSettingsCapabilities(providerType, GenerationMode.Text);

        Assert.Equal(
            GenerationSettingsCapability.Temperature | GenerationSettingsCapability.TopP | GenerationSettingsCapability.MaxTokens
                | GenerationSettingsCapability.FrequencyPenalty | GenerationSettingsCapability.PresencePenalty | GenerationSettingsCapability.AdvancedJson,
            capabilities);
    }

    [Fact]
    public void GetGenerationSettingsCapabilitiesReturnsNoneForOneMinAiTextBecauseItsAdapterIgnoresEveryField()
    {
        Assert.Equal(GenerationSettingsCapability.None, LibraryRules.GetGenerationSettingsCapabilities(ProviderType.OneMinAi, GenerationMode.Text));
    }

    [Theory]
    [InlineData(ProviderType.OpenAi, GenerationMode.Image)]
    [InlineData(ProviderType.OpenAi, GenerationMode.Audio)]
    [InlineData(ProviderType.OpenAi, GenerationMode.Video)]
    [InlineData(ProviderType.DeepInfra, GenerationMode.Video)]
    [InlineData(ProviderType.OneMinAi, GenerationMode.Image)]
    public void GetGenerationSettingsCapabilitiesReturnsNoneForEveryNonTextModeOnAnyProvider(ProviderType providerType, GenerationMode mode)
    {
        Assert.Equal(GenerationSettingsCapability.None, LibraryRules.GetGenerationSettingsCapabilities(providerType, mode));
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
