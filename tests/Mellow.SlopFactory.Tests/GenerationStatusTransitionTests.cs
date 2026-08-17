using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Exercises <c>ILibraryWorkspace.AdvanceGenerationStatusAsync</c>/<c>GetGenerationStatusHistoryAsync</c>
/// directly, without <c>GenerationQueueService</c>, to keep transition-log correctness fast and
/// independent of the queue's timing-sensitive tests.
/// </summary>
public sealed class GenerationStatusTransitionTests
{
    [Fact]
    public async Task AFullLifecycleSequenceIsLoggedInOrderWithMonotonicTimestamps()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Preparing);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Submitting);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Processing);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Completed);

        var history = await workspace.GetGenerationStatusHistoryAsync(record.Id);
        Assert.Equal(
            [GenerationStatus.Queued, GenerationStatus.Preparing, GenerationStatus.Submitting, GenerationStatus.Processing, GenerationStatus.Completed],
            history.Select(entry => entry.Status).ToArray());
        for (var i = 1; i < history.Count; i++)
        {
            Assert.True(history[i].OccurredAt >= history[i - 1].OccurredAt);
        }
        Assert.All(history, entry => Assert.Equal(record.Id, entry.GenerationRecordId));
    }

    [Fact]
    public async Task AdvancingAnAlreadyTerminalRecordThrows()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Failed);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Queued));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Processing));

        var history = await workspace.GetGenerationStatusHistoryAsync(record.Id);
        Assert.Equal([GenerationStatus.Queued, GenerationStatus.Failed], history.Select(entry => entry.Status).ToArray());
    }

    [Fact]
    public async Task PositionScopedTransitionsAreDistinguishableFromAggregateLevelTransitions()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 2, workspace.Descriptor.GeneratedFolderId);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Processing, position: 0);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Processing, position: 1);

        var history = await workspace.GetGenerationStatusHistoryAsync(record.Id);
        Assert.Null(history[0].Position);
        Assert.Equal(0, history[1].Position);
        Assert.Equal(1, history[2].Position);
    }

    [Fact]
    public async Task AbandoningASubmissionOutcomeUnknownRecordFinalizesItAsFailedWithTheAbandonedReason()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Submitting);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.SubmissionOutcomeUnknown);

        var abandoned = await workspace.AbandonGenerationOutcomeAsync(record.Id);

        Assert.Equal(GenerationStatus.Failed, abandoned.Status);
        Assert.Equal(GenerationFailureReason.AbandonedByUser, abandoned.FailureReason);
        Assert.NotNull(abandoned.CompletedAt);
    }

    [Fact]
    public async Task AbandoningAPausedRecordFinalizesItAsFailed()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Paused, holdReason: GenerationHoldReason.DependencyChanged);

        var abandoned = await workspace.AbandonGenerationOutcomeAsync(record.Id);

        Assert.Equal(GenerationStatus.Failed, abandoned.Status);
        Assert.Equal(GenerationFailureReason.AbandonedByUser, abandoned.FailureReason);
    }

    [Theory]
    [InlineData(GenerationStatus.Queued)]
    [InlineData(GenerationStatus.Processing)]
    [InlineData(GenerationStatus.Completed)]
    public async Task AbandoningARecordInAnyOtherStatusThrows(GenerationStatus status)
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        if (status != GenerationStatus.Queued)
        {
            await workspace.AdvanceGenerationStatusAsync(record.Id, status);
        }

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.AbandonGenerationOutcomeAsync(record.Id));
    }

    [Fact]
    public async Task PausingWithAHoldReasonPersistsItOnTheTransitionRow()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.Paused, holdReason: GenerationHoldReason.MeteredNetwork);

        var history = await workspace.GetGenerationStatusHistoryAsync(record.Id);
        var pauseTransition = Assert.Single(history, entry => entry.Status == GenerationStatus.Paused);
        Assert.Equal(GenerationHoldReason.MeteredNetwork, pauseTransition.HoldReason);
    }
}
