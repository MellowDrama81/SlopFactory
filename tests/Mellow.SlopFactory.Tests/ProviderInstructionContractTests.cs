using System.Text.Json;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ProviderInstructionContractTests
{
    [Fact]
    public void OpenAiCompatibleRequestKeepsSystemAndUserInstructionsInSeparateDocumentedMessages()
    {
        var body = OpenAiCompatibleProtocol.BuildChatCompletionRequestBody(
            "fixture-model",
            "user instruction",
            2,
            "system instruction");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var messages = root.GetProperty("messages");

        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal("fixture-model", root.GetProperty("model").GetString());
        Assert.Equal(2, root.GetProperty("n").GetInt32());
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system instruction", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user instruction", messages[1].GetProperty("content").GetString());
        Assert.DoesNotContain("developer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("history", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenAiCompatibleRequestOmitsAnEmptySystemInstructionInsteadOfAddingAnEmptyOrHiddenMessage()
    {
        var body = OpenAiCompatibleProtocol.BuildChatCompletionRequestBody(
            "fixture-model",
            "user instruction",
            1,
            "  ");

        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");

        Assert.Single(messages.EnumerateArray());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.DoesNotContain("system", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiCompatibleRequestCapturesSourceBytesAtBuildTime()
    {
        byte[] sourceBytes = [1, 2, 3];
        var source = new TextGenerationSourceImage("image/png", sourceBytes);

        var snapshot = OpenAiCompatibleProtocol.BuildChatCompletionRequestBody(
            "fixture-model",
            "user instruction",
            1,
            sourceImage: source);
        sourceBytes[0] = 255;

        Assert.Contains("data:image/png;base64,AQID", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("/wID", snapshot, StringComparison.Ordinal);
    }
}
