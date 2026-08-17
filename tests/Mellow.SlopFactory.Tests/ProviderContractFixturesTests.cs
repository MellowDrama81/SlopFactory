using System.Text.Json;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ProviderContractFixturesTests
{
    public static IEnumerable<object[]> BoundJsonFixtures()
    {
        yield return ["OpenAI-compatible models", ProviderContractFixtures.OpenAiCompatibleModelsResponseV1
            .Replace("__MODEL_ID__", "fixture-model")
            .Replace("__MODEL_LABEL__", "Fixture Model")];
        yield return ["OpenAI-compatible chat request", ProviderContractFixtures.OpenAiCompatibleChatCompletionRequestV1
            .Replace("__MODEL_ID__", "fixture-model")
            .Replace("__RESULT_COUNT__", "2")
            .Replace("__PROMPT__", "fixture prompt")];
        yield return ["OpenAI-compatible chat response", ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1
            .Replace("__CONTENT__", "fixture response")
            .Replace("__PROMPT_TOKENS__", "4")
            .Replace("__COMPLETION_TOKENS__", "3")];
        yield return ["OpenAI-compatible image request", ProviderContractFixtures.OpenAiCompatibleImageGenerationRequestV1
            .Replace("__MODEL_ID__", "fixture-model")
            .Replace("__PROMPT__", "fixture prompt")
            .Replace("__RESULT_COUNT__", "1")];
        yield return ["OpenAI-compatible image response", ProviderContractFixtures.OpenAiCompatibleImageGenerationResponseV1
            .Replace("__BASE64__", "AQID")];
        yield return ["OpenRouter image request", ProviderContractFixtures.OpenRouterImageRequestV1
            .Replace("__MODEL_ID__", "fixture-model")
            .Replace("__PROMPT__", "fixture prompt")
            .Replace("__RESULT_COUNT__", "1")];
        yield return ["OpenRouter image response", ProviderContractFixtures.OpenRouterImageResponseV1
            .Replace("__BASE64__", "AQID")];
        yield return ["OpenRouter audio request", ProviderContractFixtures.OpenRouterAudioSpeechRequestV1
            .Replace("__MODEL_ID__", "fixture-model")
            .Replace("__PROMPT__", "fixture prompt")];
        yield return ["OpenRouter video submit request", ProviderContractFixtures.OpenRouterVideoSubmitRequestV1
            .Replace("__MODEL_ID__", "fixture-model")
            .Replace("__PROMPT__", "fixture prompt")];
        yield return ["OpenRouter video submit response", ProviderContractFixtures.OpenRouterVideoSubmitResponseV1
            .Replace("__JOB_ID__", "fixture-job")
            .Replace("__POLLING_URL__", "https://provider.test/v1/videos/fixture-job")];
        yield return ["OpenRouter video poll processing", ProviderContractFixtures.OpenRouterVideoPollProcessingV1];
        yield return ["OpenRouter video poll completed", ProviderContractFixtures.OpenRouterVideoPollCompletedV1
            .Replace("__JOB_ID__", "fixture-job")
            .Replace("__CONTENT_URL__", "https://provider.test/v1/results/fixture-job")];
        yield return ["OpenRouter video poll completed with cost", ProviderContractFixtures.OpenRouterVideoPollCompletedWithCostV1
            .Replace("__JOB_ID__", "fixture-job")
            .Replace("__CONTENT_URL__", "https://provider.test/v1/results/fixture-job")
            .Replace("__COST__", "0.25")];
        yield return ["OpenRouter video poll failed", ProviderContractFixtures.OpenRouterVideoPollFailedV1
            .Replace("__ERROR_MESSAGE__", "fixture failure")];
        yield return ["DeepInfra chat response", ProviderContractFixtures.DeepInfraChatCompletionResponseV1
            .Replace("__CONTENT__", "fixture response")];
        yield return ["DeepInfra image response", ProviderContractFixtures.DeepInfraImageResponseV1
            .Replace("__BASE64__", "AQID")];
    }

    [Theory]
    [MemberData(nameof(BoundJsonFixtures))]
    public void EveryBoundFixtureIsValidSanitizedJson(string _, string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
        Assert.DoesNotContain("__", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }
}
