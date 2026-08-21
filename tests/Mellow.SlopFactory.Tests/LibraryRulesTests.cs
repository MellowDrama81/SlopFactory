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
    [InlineData(ProviderType.OpenAi, true)]
    [InlineData(ProviderType.GenericOpenAiCompatible, false)]
    [InlineData(ProviderType.OpenRouter, true)]
    [InlineData(ProviderType.OneMinAi, false)]
    [InlineData(ProviderType.DeepInfra, true)]
    [InlineData(ProviderType.Groq, true)]
    [InlineData(ProviderType.Gemini, true)]
    [InlineData(ProviderType.Mistral, false)]
    [InlineData(ProviderType.Anthropic, false)]
    [InlineData(ProviderType.Cohere, false)]
    public void SupportsAudioVoiceSelectionMatchesEachAdaptersConfirmedAudioSpeechShape(ProviderType providerType, bool expected)
    {
        Assert.Equal(expected, LibraryRules.SupportsAudioVoiceSelection(providerType));
    }

    [Theory]
    [InlineData(ProviderType.OpenAi, true)]
    [InlineData(ProviderType.GenericOpenAiCompatible, true)]
    [InlineData(ProviderType.OpenRouter, true)]
    [InlineData(ProviderType.DeepInfra, true)]
    [InlineData(ProviderType.OneMinAi, false)]
    [InlineData(ProviderType.ComfyUi, false)]
    [InlineData(ProviderType.Mistral, true)]
    [InlineData(ProviderType.Groq, true)]
    [InlineData(ProviderType.TogetherAi, true)]
    [InlineData(ProviderType.FireworksAi, true)]
    [InlineData(ProviderType.DeepSeek, true)]
    [InlineData(ProviderType.Perplexity, true)]
    [InlineData(ProviderType.XAi, true)]
    [InlineData(ProviderType.Anthropic, true)]
    [InlineData(ProviderType.Gemini, true)]
    [InlineData(ProviderType.Cohere, true)]
    [InlineData(ProviderType.AI21, true)]
    public void SupportsModelDiscoveryIsFalseOnlyForOneMinAiAndComfyUi(ProviderType providerType, bool expected)
    {
        Assert.Equal(expected, LibraryRules.SupportsModelDiscovery(providerType));
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
    [InlineData(ProviderType.OpenAi, GenerationMode.Audio)]
    [InlineData(ProviderType.GenericOpenAiCompatible, GenerationMode.Image)]
    [InlineData(ProviderType.OneMinAi, GenerationMode.Image)]
    [InlineData(ProviderType.OneMinAi, GenerationMode.Video)]
    [InlineData(ProviderType.TogetherAi, GenerationMode.Image)]
    [InlineData(ProviderType.FireworksAi, GenerationMode.Image)]
    [InlineData(ProviderType.Mistral, GenerationMode.Image)]
    [InlineData(ProviderType.Groq, GenerationMode.Image)]
    [InlineData(ProviderType.Anthropic, GenerationMode.Text)]
    [InlineData(ProviderType.Gemini, GenerationMode.Text)]
    [InlineData(ProviderType.Cohere, GenerationMode.Text)]
    public void GetInputSlotCapabilitiesReturnsNoneForEveryOtherModeProviderCombination(ProviderType providerType, GenerationMode mode)
    {
        // TogetherAi/FireworksAi have a plain images/generations-shaped endpoint (see
        // TogetherAiProviderAdapter/FireworksAiProviderAdapter's remarks) but no confirmed image-edit
        // shape, so unlike OpenAI/OpenRouter/DeepInfra they get no ReferenceImage input slot here.
        // Anthropic/Gemini/Cohere all genuinely support chat vision input, but none of their bespoke
        // adapters translate TextGenerationSourceImage into that provider's shape in this pass, so they
        // get no Text-mode ReferenceImage slot either (see GetInputSlotCapabilities's remarks).
        Assert.Empty(LibraryRules.GetInputSlotCapabilities(providerType, mode));
    }

    [Theory]
    [InlineData(ProviderType.OpenAi)]
    [InlineData(ProviderType.OpenRouter)]
    [InlineData(ProviderType.DeepInfra)]
    public void GetInputSlotCapabilitiesReturnsUpToThreeReferenceImagesForImageModeOnConfirmedProviders(ProviderType providerType)
    {
        // DeepInfra's actual per-model behavior varies (confirmed by live testing: some models keep
        // only the last of several supplied images, others genuinely use more than one with real
        // run-to-run quality variance — see DeepInfraProviderAdapter.GenerateImageAsync's remarks), but
        // this capability schema deliberately does not special-case that per model ID: a per-model
        // allowlist was tried and reverted as unmaintainable, so DeepInfra gets the same flat MaxCount
        // as OpenAI/OpenRouter and any per-model quirk surfaces as an ordinary generation outcome.
        var capabilities = LibraryRules.GetInputSlotCapabilities(providerType, GenerationMode.Image);

        var capability = Assert.Single(capabilities, item => item.Role == GenerationInputSlotRole.ReferenceImage);
        Assert.Equal(GenerationInputSlotRole.ReferenceImage, capability.Role);
        Assert.Equal(0, capability.MinCount);
        Assert.Equal(3, capability.MaxCount);
        Assert.False(capability.Required);
        if (providerType is ProviderType.OpenAi or ProviderType.DeepInfra or ProviderType.OpenRouter)
        {
            var mask = Assert.Single(capabilities, item => item.Role == GenerationInputSlotRole.Mask);
            Assert.Equal(1, mask.MaxCount);
        }
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

    [Theory]
    [InlineData(ProviderType.Anthropic)]
    [InlineData(ProviderType.Gemini)]
    public void GetGenerationSettingsCapabilitiesWithholdsPenaltyFieldsForAnthropicAndGeminiTextSinceNeitherRequestShapeHasThem(ProviderType providerType)
    {
        var capabilities = LibraryRules.GetGenerationSettingsCapabilities(providerType, GenerationMode.Text);

        Assert.Equal(
            GenerationSettingsCapability.Temperature | GenerationSettingsCapability.TopP | GenerationSettingsCapability.MaxTokens | GenerationSettingsCapability.AdvancedJson,
            capabilities);
    }

    [Fact]
    public void GetGenerationSettingsCapabilitiesReturnsEveryFieldForCohereTextSinceItsRequestShapeSupportsThem()
    {
        var capabilities = LibraryRules.GetGenerationSettingsCapabilities(ProviderType.Cohere, GenerationMode.Text);

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
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OneMinAi, GenerationMode.Image);
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
    public void ValidateSourceSlotsAcceptsASecondReferenceImageForDeepInfraImageMode()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.DeepInfra, GenerationMode.Image);
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, "file-1", 0),
            new(GenerationInputSlotRole.ReferenceImage, "file-2", 1),
        ];

        LibraryRules.ValidateSourceSlots(slots, capabilities);
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

    [Fact]
    public void ValidateSourceSlotsAcceptsPrivateMaskPairedWithItsReferenceImage()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Image);
        GenerationSourceSlot[] slots = [new(GenerationInputSlotRole.ReferenceImage, "image-1", 0), new(GenerationInputSlotRole.Mask, "image-1", 0, "mask-1")];
        LibraryRules.ValidateSourceSlots(slots, capabilities);
    }

    [Fact]
    public void ValidateSourceSlotsRejectsMaskWithoutItsReferenceImage()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Image);
        GenerationSourceSlot[] slots = [new(GenerationInputSlotRole.Mask, "image-1", 0, "mask-1")];
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }

    [Fact]
    public void ValidateSourceSlotsAcceptsASnapshotBackedReferenceImageAndItsPairedMask()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Image);
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, null, 0, SnapshotSourceGenerationId: "generation-1"),
            new(GenerationInputSlotRole.Mask, null, 0, "mask-1", "generation-1"),
        ];

        LibraryRules.ValidateSourceSlots(slots, capabilities);
    }

    [Fact]
    public void ValidateSourceSlotsRejectsASnapshotBackedMaskPairedWithAnUnrelatedSnapshotSource()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Image);
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, null, 0, SnapshotSourceGenerationId: "generation-1"),
            new(GenerationInputSlotRole.Mask, null, 0, "mask-1", "generation-2"),
        ];

        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }

    [Fact]
    public void ValidateSourceSlotsRejectsASlotNamingBothALiveFileAndASnapshotSource()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Text);
        GenerationSourceSlot[] slots = [new(GenerationInputSlotRole.ReferenceImage, "file-1", 0, SnapshotSourceGenerationId: "generation-1")];

        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }

    [Fact]
    public void ValidateSourceSlotsRejectsASlotNamingNeitherALiveFileNorASnapshotSource()
    {
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.OpenAi, GenerationMode.Text);
        GenerationSourceSlot[] slots = [new(GenerationInputSlotRole.ReferenceImage, null, 0)];

        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateSourceSlots(slots, capabilities));
    }

    [Fact]
    public void ValidateNoDuplicateSourceSlotFilesIgnoresSnapshotBackedSlotsWithNoLiveFileToCompare()
    {
        GenerationSourceSlot[] slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, null, 0, SnapshotSourceGenerationId: "generation-1"),
            new(GenerationInputSlotRole.ReferenceImage, null, 1, SnapshotSourceGenerationId: "generation-1"),
        ];

        LibraryRules.ValidateNoDuplicateSourceSlotFiles(slots);
    }

    [Fact]
    public void GetInputSlotCapabilitiesReturnsAtMostTwoReferenceImagesForComfyUiImageMode()
    {
        // ComfyUi's real per-slot capability lives in the model's own workflow JSON, which this flat
        // (provider, mode) switch cannot see — this is a fixed upper bound (Comfy.md section 3.3,
        // option (a)), not a claim every ComfyUi workflow accepts two reference images. 2 matches the
        // highest count any built-in workflow template (ComfyBuiltInWorkflows) actually uses.
        var capabilities = LibraryRules.GetInputSlotCapabilities(ProviderType.ComfyUi, GenerationMode.Image);

        var capability = Assert.Single(capabilities);
        Assert.Equal(GenerationInputSlotRole.ReferenceImage, capability.Role);
        Assert.Equal(0, capability.MinCount);
        Assert.Equal(2, capability.MaxCount);
        Assert.False(capability.Required);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateComfyWorkflowTemplateRejectsAMissingValue(string? value)
    {
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate(value, GenerationMode.Image));
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateRejectsInvalidJson()
    {
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate("not json", GenerationMode.Image));
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateRejectsAJsonArray()
    {
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate("""["3"]""", GenerationMode.Image));
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateRejectsANonNumericNodeKey()
    {
        const string json = """{"clip":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}}}""";
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate(json, GenerationMode.Image));
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateRejectsANodeMissingClassTypeOrInputs()
    {
        const string json = """{"3":{"inputs":{"text":"{{PROMPT}}"}}}""";
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate(json, GenerationMode.Image));
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateRejectsAWorkflowWithNoPromptPlaceholder()
    {
        const string json = """{"3":{"class_type":"CLIPTextEncode","inputs":{"text":"a fixed prompt"}}}""";
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate(json, GenerationMode.Image));
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateAcceptsAWellFormedWorkflow()
    {
        const string json = """{"3":{"class_type":"KSampler","inputs":{"seed":{{SEED}}}},"6":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}}}""";

        var result = LibraryRules.ValidateComfyWorkflowTemplate(json, GenerationMode.Image);

        Assert.Equal(json, result);
    }

    [Theory]
    [InlineData("57:8")]
    [InlineData("48:35:43")]
    public void ValidateComfyWorkflowTemplateAcceptsColonSeparatedSubgraphNodeKeys(string nodeKey)
    {
        // Real Comfy Cloud API-format exports use compound "parent:child" node IDs for nodes nested
        // inside a subgraph (confirmed against a real export) — not just bare integers.
        var json = "{\"" + nodeKey + "\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{PROMPT}}\"}}}";

        var result = LibraryRules.ValidateComfyWorkflowTemplate(json, GenerationMode.Image);

        Assert.Equal(json, result);
    }

    [Fact]
    public void ValidateComfyWorkflowTemplateRejectsAColonSeparatedKeyWithANonNumericSegment()
    {
        const string json = """{"57:clip":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}}}""";
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateComfyWorkflowTemplate(json, GenerationMode.Image));
    }

    [Fact]
    public void EveryBuiltInComfyWorkflowValidates()
    {
        foreach (var workflow in ComfyBuiltInWorkflows.All)
        {
            var result = LibraryRules.ValidateComfyWorkflowTemplate(workflow.WorkflowTemplate, GenerationMode.Image);
            Assert.Equal(workflow.WorkflowTemplate, result);
        }
    }

    [Fact]
    public void EveryBuiltInComfyWorkflowDeclaresAConsistentReferenceImageCount()
    {
        foreach (var workflow in ComfyBuiltInWorkflows.All)
        {
            var expectedFilenameTokenCount = workflow.WorkflowTemplate.Contains("{{UPLOADED_IMAGE_FILENAME_2}}", StringComparison.Ordinal) ? 2
                : workflow.WorkflowTemplate.Contains("{{UPLOADED_IMAGE_FILENAME}}", StringComparison.Ordinal) ? 1
                : 0;
            Assert.Equal(expectedFilenameTokenCount, workflow.ReferenceImageCount);
        }
    }
}
