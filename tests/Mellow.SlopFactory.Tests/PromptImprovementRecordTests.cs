using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class PromptImprovementRecordTests
{
    [Fact]
    public async Task SuccessfulAttemptPersistsCandidatesAndTokenUsage()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordPromptImprovementAttemptAsync(model.Id, "a raw prompt", "make it punchier", "v1", ["candidate one", "candidate two"], null, 12, 7);

        Assert.Equal(GenerationStatus.Completed, record.Status);
        Assert.Equal("GPT", record.ModelLabel);
        Assert.Equal("gpt-4o", record.ProviderModelId);
        Assert.Equal(ProviderType.OpenAi, record.ProviderType);
        Assert.Equal("a raw prompt", record.RawPrompt);
        Assert.Equal("make it punchier", record.Guidance);
        Assert.Equal("v1", record.TemplateVersion);
        Assert.Equal(2, record.Candidates.Count);
        Assert.Equal("candidate one", record.Candidates[0]);
        Assert.Equal(12, record.PromptTokens);
        Assert.Equal(7, record.CompletionTokens);
        Assert.Null(record.ErrorMessage);

        var history = await workspace.GetPromptImprovementHistoryAsync();
        var reloaded = Assert.Single(history);
        Assert.Equal(record.Id, reloaded.Id);
        Assert.Equal(2, reloaded.Candidates.Count);
    }

    [Fact]
    public async Task FailedAndRetriedAttemptsAreRecordedSeparately()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var failed = await workspace.RecordPromptImprovementAttemptAsync(model.Id, "a raw prompt", null, "v1", null, "The provider reported a server error.");
        Assert.Equal(GenerationStatus.Failed, failed.Status);
        Assert.Empty(failed.Candidates);
        Assert.Equal("The provider reported a server error.", failed.ErrorMessage);

        var retried = await workspace.RecordPromptImprovementAttemptAsync(model.Id, "a raw prompt", null, "v1", ["candidate one"], null);
        Assert.Equal(GenerationStatus.Completed, retried.Status);
        Assert.NotEqual(failed.Id, retried.Id);

        var history = await workspace.GetPromptImprovementHistoryAsync();
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task AcceptedImprovementRecordLinksToTheGenerationRecordAndSurvivesModelDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var improvementModel = await workspace.CreateModelAsync("Improver", connection.Id, "gpt-4o-mini", GenerationMode.Text, true);
        var outputModel = await workspace.CreateModelAsync("Output", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var improvement = await workspace.RecordPromptImprovementAttemptAsync(improvementModel.Id, "raw", null, "v1", ["improved prompt"], null);
        var generation = await workspace.RecordTextGenerationResultAsync(outputModel.Id, "improved prompt", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null, promptImprovementRecordId: improvement.Id);

        Assert.Equal(improvement.Id, generation.PromptImprovementRecordId);
        var reloadedGeneration = await workspace.GetGenerationRecordAsync(generation.Id);
        Assert.Equal(improvement.Id, reloadedGeneration.PromptImprovementRecordId);

        await workspace.RecycleModelAsync(improvementModel.Id);
        await workspace.PermanentlyDeleteModelAsync(improvementModel.Id);

        var improvementHistory = await workspace.GetPromptImprovementHistoryAsync();
        var survivingImprovement = Assert.Single(improvementHistory);
        Assert.Null(survivingImprovement.ModelId);
        Assert.Equal("Improver", survivingImprovement.ModelLabel);

        var stillLinkedGeneration = await workspace.GetGenerationRecordAsync(generation.Id);
        Assert.Equal(improvement.Id, stillLinkedGeneration.PromptImprovementRecordId);
    }

    [Fact]
    public async Task RawPromptAndGuidanceAreBoundedTo1MiBOfUtf8Text()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var oversized = new string('a', LibraryRules.MaximumGenerationTextUtf8Bytes + 1);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RecordPromptImprovementAttemptAsync(model.Id, oversized, null, "v1", ["candidate"], null));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RecordPromptImprovementAttemptAsync(model.Id, "raw prompt", oversized, "v1", ["candidate"], null));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RecordPromptImprovementAttemptAsync(model.Id, "raw prompt", null, "v1", ["a fine candidate", oversized], null));
    }
}
