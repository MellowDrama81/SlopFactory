using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class GenerationRecordTests
{
    [Fact]
    public async Task RecordingAOneShotTextGenerationLogsAQueuedThenTerminalTransitionPair()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null);

        var history = await workspace.GetGenerationStatusHistoryAsync(record.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(GenerationStatus.Queued, history[0].Status);
        Assert.Equal(GenerationStatus.Completed, history[1].Status);
        Assert.True(history[1].OccurredAt >= history[0].OccurredAt);
    }

    [Fact]
    public async Task AdvancingAGenerationStatusPersistsHoldAndFailureReasonsThroughReload()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var created = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        Assert.Equal(GenerationStatus.Queued, created.Status);

        var paused = await workspace.AdvanceGenerationStatusAsync(created.Id, GenerationStatus.Paused, holdReason: GenerationHoldReason.ConnectionLost);
        Assert.Equal(GenerationStatus.Paused, paused.Status);
        Assert.Equal(GenerationHoldReason.ConnectionLost, paused.HoldReason);
        Assert.Null(paused.CompletedAt);

        var failed = await workspace.AdvanceGenerationStatusAsync(created.Id, GenerationStatus.Failed, failureReason: GenerationFailureReason.RemoteJobUnavailable);
        Assert.Equal(GenerationStatus.Failed, failed.Status);
        Assert.Equal(GenerationFailureReason.RemoteJobUnavailable, failed.FailureReason);
        Assert.NotNull(failed.CompletedAt);

        var reloaded = await workspace.GetGenerationRecordAsync(created.Id);
        Assert.Equal(GenerationStatus.Failed, reloaded.Status);
        Assert.Equal(GenerationFailureReason.RemoteJobUnavailable, reloaded.FailureReason);

        var history = await workspace.GetGenerationStatusHistoryAsync(created.Id);
        Assert.Equal([GenerationStatus.Queued, GenerationStatus.Paused, GenerationStatus.Failed], history.Select(entry => entry.Status).ToArray());

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.AdvanceGenerationStatusAsync(created.Id, GenerationStatus.Processing));
    }

    [Fact]
    public async Task FinalizingAQueuedGenerationRecordUpdatesTheSameRowInsteadOfCreatingASecondOne()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var queued = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId);
        var finalized = await workspace.RecordTextGenerationResultAsync(model.Id, "prompt", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null, existingGenerationRecordId: queued.Id);

        Assert.Equal(queued.Id, finalized.Id);
        Assert.Equal(GenerationStatus.Completed, finalized.Status);
        Assert.Equal(queued.CreatedAt, finalized.CreatedAt);

        var history = await workspace.GetGenerationHistoryAsync();
        Assert.Single(history);
        var statusHistory = await workspace.GetGenerationStatusHistoryAsync(queued.Id);
        Assert.Equal([GenerationStatus.Queued, GenerationStatus.Completed], statusHistory.Select(entry => entry.Status).ToArray());
    }

    [Fact]
    public async Task RecordingASuccessfulTextGenerationCommitsFilesAndHistory()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 2, workspace.Descriptor.GeneratedFolderId, ["First result", "Second result"], null);

        Assert.Equal(GenerationStatus.Completed, record.Status);
        Assert.Equal(2, record.ResultFileIds.Count);
        Assert.Equal("GPT", record.ModelLabel);
        Assert.Equal("gpt-4o", record.ProviderModelId);
        Assert.Equal(ProviderType.OpenAi, record.ProviderType);
        Assert.Null(record.ErrorMessage);

        var firstFile = await workspace.GetFileAsync(record.ResultFileIds[0]);
        Assert.Equal(FileOrigin.Generated, firstFile.Origin);
        Assert.Equal("text/markdown", firstFile.MediaType);
        Assert.Equal(workspace.Descriptor.GeneratedFolderId, firstFile.FolderId);
        var content = await workspace.ReadTextFileAsync(firstFile.Id);
        Assert.Equal("First result", content.Content);

        var secondFile = await workspace.GetFileAsync(record.ResultFileIds[1]);
        Assert.NotEqual(firstFile.DisplayName, secondFile.DisplayName);

        var history = await workspace.GetGenerationHistoryAsync();
        var historyEntry = Assert.Single(history);
        Assert.Equal(record.Id, historyEntry.Id);
        Assert.Equal(2, historyEntry.ResultFileIds.Count);

        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(record.ResultFileIds, reloaded.ResultFileIds);
    }

    [Fact]
    public async Task PermanentlyDeletingAGenerationResultFileClearsItsResultReferenceWithoutRemovingTheHistoryRecord()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 2, workspace.Descriptor.GeneratedFolderId, ["First result", "Second result"], null);
        var firstFileId = record.ResultFileIds[0];
        var secondFileId = record.ResultFileIds[1];

        await workspace.RecycleFileAsync(firstFileId);
        await workspace.PermanentlyDeleteFileAsync(firstFileId);

        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.DoesNotContain(firstFileId, reloaded.ResultFileIds);
        Assert.Contains(secondFileId, reloaded.ResultFileIds);
        Assert.Equal(2, reloaded.ResultCount);
        Assert.Equal(GenerationStatus.Completed, reloaded.Status);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(firstFileId));

        var tombstone = Assert.Single(reloaded.TombstonedResults);
        Assert.Equal("text/markdown", tombstone.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(tombstone.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(tombstone.ContentHash));
    }

    [Fact]
    public async Task RecyclingRestoringAndPermanentlyDeletingAGenerationRecordNeverTouchesItsFiles()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null);
        var resultFileId = record.ResultFileIds[0];
        Assert.Equal(LibraryRecordState.Active, record.State);

        await workspace.RecycleGenerationRecordAsync(record.Id);
        var recycled = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(LibraryRecordState.Recycled, recycled.State);
        Assert.NotNull(recycled.RecycledAt);
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(resultFileId)).State);

        await workspace.RestoreGenerationRecordAsync(record.Id);
        var restored = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(LibraryRecordState.Active, restored.State);
        Assert.Single(await workspace.GetGenerationHistoryAsync());

        await workspace.RecycleGenerationRecordAsync(record.Id);
        await workspace.PermanentlyDeleteGenerationRecordAsync(record.Id);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetGenerationRecordAsync(record.Id));
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(resultFileId)).State);
    }

    [Fact]
    public async Task OnlyARecycledGenerationRecordCanBeRestoredOrPermanentlyDeleted()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RestoreGenerationRecordAsync(record.Id));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.PermanentlyDeleteGenerationRecordAsync(record.Id));

        await workspace.RecycleGenerationRecordAsync(record.Id);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RecycleGenerationRecordAsync(record.Id));
    }

    [Fact]
    public async Task RecordingAFailedTextGenerationCreatesNoFilesButKeepsHistory()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, null, "Authentication failed.");

        Assert.Equal(GenerationStatus.Failed, record.Status);
        Assert.Empty(record.ResultFileIds);
        Assert.Equal("Authentication failed.", record.ErrorMessage);
        Assert.Empty(await workspace.GetActiveFilesAsync());

        var history = await workspace.GetGenerationHistoryAsync();
        Assert.Single(history);
    }

    [Fact]
    public async Task PartiallyCommittedTextGenerationLeavesTheEarlierResultFileIntactWithNoOrphanedHistoryRecord()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        await Assert.ThrowsAsync<LibraryValidationException>(() =>
            workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 2, workspace.Descriptor.GeneratedFolderId, ["First result", "\uD800"], null));

        var committed = Assert.Single(await workspace.GetActiveFilesAsync());
        Assert.Equal(FileOrigin.Generated, committed.Origin);
        var content = await workspace.ReadTextFileAsync(committed.Id);
        Assert.Equal("First result", content.Content);
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root, ".staging")), path => path.EndsWith(".generating", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingMidLoopDuringATextGenerationCommitLeavesTheEarlierResultFileIntactWithNoOrphanedHistoryRecord()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        using var cancellation = new CancellationTokenSource();
        var texts = new CancelAfterFirstItem<string>(["First result", "Second result"], cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workspace.RecordTextGenerationResultAsync(model.Id, "Write two haiku", 2, workspace.Descriptor.GeneratedFolderId, texts, null, cancellationToken: cancellation.Token));

        var committed = Assert.Single(await workspace.GetActiveFilesAsync());
        Assert.Equal(FileOrigin.Generated, committed.Origin);
        var content = await workspace.ReadTextFileAsync(committed.Id);
        Assert.Equal("First result", content.Content);
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root, ".staging")), path => path.EndsWith(".generating", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordingATextGenerationWithSystemInstructionsPersistsAndReloadsThem()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null, "Respond only in French.");

        Assert.Equal("Respond only in French.", record.SystemInstructions);

        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal("Respond only in French.", reloaded.SystemInstructions);

        var withoutInstructions = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null);
        Assert.Null(withoutInstructions.SystemInstructions);
    }

    [Fact]
    public async Task RecordingATextGenerationWithGenerationSettingsPersistsAndReloadsThem()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var settings = new GenerationSettings(0.7, 0.9, 500, 0.5, -0.5, "{\"response_format\":{\"type\":\"json_object\"}}");

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null, settings: settings);

        Assert.Equal(settings, record.Settings);

        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(settings, reloaded.Settings);

        var withoutSettings = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null);
        Assert.Equal(GenerationSettings.Empty, withoutSettings.Settings);
    }

    [Theory]
    [InlineData(2.1, null, null, null, null)]
    [InlineData(null, -0.1, null, null, null)]
    [InlineData(null, null, 0, null, null)]
    [InlineData(null, null, null, 2.1, null)]
    [InlineData(null, null, null, null, -2.1)]
    public async Task RecordingATextGenerationRejectsOutOfRangeGenerationSettings(double? temperature, double? topP, int? maxTokens, double? frequencyPenalty, double? presencePenalty)
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var settings = new GenerationSettings(temperature, topP, maxTokens, frequencyPenalty, presencePenalty);

        await Assert.ThrowsAsync<LibraryValidationException>(() =>
            workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null, settings: settings));
    }

    [Fact]
    public async Task RecordingATextGenerationWithTokenUsagePersistsAndReloadsIt()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null, promptTokens: 12, completionTokens: 34);

        Assert.Equal(12, record.PromptTokens);
        Assert.Equal(34, record.CompletionTokens);

        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(12, reloaded.PromptTokens);
        Assert.Equal(34, reloaded.CompletionTokens);

        var history = Assert.Single(await workspace.GetGenerationHistoryAsync());
        Assert.Equal(12, history.PromptTokens);
        Assert.Equal(34, history.CompletionTokens);
    }

    private static readonly byte[] PngSignatureBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public async Task RecordingASuccessfulImageGenerationCommitsFilesWithDetectedMediaType()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Image Model", connection.Id, "gpt-image-1", GenerationMode.Image, false);

        var record = await workspace.RecordImageGenerationResultAsync(model.Id, "A watercolor fox", 1, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null);

        Assert.Equal(GenerationStatus.Completed, record.Status);
        Assert.Equal(GenerationMode.Image, record.Mode);
        var file = await workspace.GetFileAsync(Assert.Single(record.ResultFileIds));
        Assert.Equal(FileOrigin.Generated, file.Origin);
        Assert.Equal("image/png", file.MediaType);
        Assert.EndsWith(".png", file.ManagedName, StringComparison.Ordinal);
        Assert.Equal(PngSignatureBytes.LongLength, file.ByteSize);
    }

    private static readonly byte[] Mp3SignatureBytes = [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21];
    private static readonly byte[] Mp4SignatureBytes = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];

    [Fact]
    public async Task RecordingASuccessfulAudioGenerationCommitsFilesWithDetectedMediaType()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);

        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 1, workspace.Descriptor.GeneratedFolderId, [Mp3SignatureBytes], null);

        Assert.Equal(GenerationStatus.Completed, record.Status);
        Assert.Equal(GenerationMode.Audio, record.Mode);
        var file = await workspace.GetFileAsync(Assert.Single(record.ResultFileIds));
        Assert.Equal("audio/mpeg", file.MediaType);
        Assert.EndsWith(".mp3", file.ManagedName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordingASuccessfulVideoGenerationCommitsFilesWithDetectedMediaType()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Video Model", connection.Id, "google/veo-3.1", GenerationMode.Video, false);

        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "A cat on a skateboard", 1, workspace.Descriptor.GeneratedFolderId, [Mp4SignatureBytes], null);

        Assert.Equal(GenerationStatus.Completed, record.Status);
        Assert.Equal(GenerationMode.Video, record.Mode);
        var file = await workspace.GetFileAsync(Assert.Single(record.ResultFileIds));
        Assert.Equal("video/mp4", file.MediaType);
        Assert.EndsWith(".mp4", file.ManagedName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioGenerationResultWhoseBytesDoNotMatchTheExpectedMediaCategoryIsNotCommitted()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);

        // The bytes actually decode as a PNG image, not audio — simulating a provider response that
        // cannot be validated as the expected media category.
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 1, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null);

        Assert.Equal(GenerationStatus.Failed, record.Status);
        Assert.Empty(record.ResultFileIds);
        Assert.Empty(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task VideoGenerationResultWhoseBytesDoNotMatchTheExpectedMediaCategoryIsNotCommitted()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Video Model", connection.Id, "google/veo-3.1", GenerationMode.Video, false);

        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "A cat on a skateboard", 1, workspace.Descriptor.GeneratedFolderId, [Mp3SignatureBytes], null);

        Assert.Equal(GenerationStatus.Failed, record.Status);
        Assert.Empty(record.ResultFileIds);
        Assert.Empty(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task AMixedBatchOfValidAndMismatchedAudioResultsCommitsOnlyTheValidOnesAsPartiallyCompleted()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);

        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 2, workspace.Descriptor.GeneratedFolderId, [Mp3SignatureBytes, PngSignatureBytes], null);

        Assert.Equal(GenerationStatus.PartiallyCompleted, record.Status);
        var file = await workspace.GetFileAsync(Assert.Single(record.ResultFileIds));
        Assert.Equal("audio/mpeg", file.MediaType);

        Assert.Equal(2, record.Results.Count);
        var committed = Assert.Single(record.Results, entry => entry.Status == GenerationResultStatus.Committed);
        Assert.Equal(0, committed.Position);
        Assert.Equal(file.Id, committed.FileId);
        Assert.Null(committed.ErrorMessage);
        // PNG bytes aren't recognized as an error document/authentication page, so the mismatch
        // awaits an explicit Retain-as-Unverified-Binary/Discard decision rather than an automatic Failed.
        var pending = Assert.Single(record.Results, entry => entry.Status == GenerationResultStatus.PendingReview);
        Assert.Equal(1, pending.Position);
        Assert.Null(pending.FileId);
        Assert.Contains("did not match the expected media type", pending.ErrorMessage, StringComparison.Ordinal);
        var pendingReviews = await workspace.GetPendingUnverifiedResultsAsync(record.Id);
        var pendingReview = Assert.Single(pendingReviews);
        Assert.Equal(1, pendingReview.Position);
        Assert.Equal(PngSignatureBytes.LongLength, pendingReview.ByteSize);
    }

    [Fact]
    public async Task AResultRecognizedAsAJsonErrorDocumentIsDiscardedAutomaticallyRatherThanHeldForReview()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
        var errorDocumentBytes = System.Text.Encoding.UTF8.GetBytes("""{"error":{"message":"Invalid API key","type":"authentication_error"}}""");

        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 1, workspace.Descriptor.GeneratedFolderId, [errorDocumentBytes], null);

        Assert.Equal(GenerationStatus.Failed, record.Status);
        var failed = Assert.Single(record.Results);
        Assert.Equal(GenerationResultStatus.Failed, failed.Status);
        Assert.Empty(await workspace.GetPendingUnverifiedResultsAsync(record.Id));
    }

    [Fact]
    public async Task RetainingAnUnverifiedResultCommitsAnExportOnlyBinFileAndClearsTheReviewQueue()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 1, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null);
        Assert.Equal(GenerationResultStatus.PendingReview, Assert.Single(record.Results).Status);

        var retained = await workspace.RetainUnverifiedResultAsync(record.Id, 0);

        Assert.Equal(FileOrigin.UnverifiedProviderOutput, retained.Origin);
        Assert.Equal("application/octet-stream", retained.MediaType);
        Assert.EndsWith(".bin", retained.ManagedName, StringComparison.Ordinal);
        Assert.Equal(PngSignatureBytes.LongLength, retained.ByteSize);
        Assert.Equal(LibraryRecordState.Active, retained.State);
        // Export-only: never previewable (media type forced to opaque octet-stream
        // regardless of the mismatched bytes' own detected type) and never opened externally.
        Assert.Equal(BuiltInPreviewKind.Unsupported, BuiltInPreviewCapabilities.ForMediaType(retained.MediaType));

        Assert.Empty(await workspace.GetPendingUnverifiedResultsAsync(record.Id));
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        var committed = Assert.Single(reloaded.Results);
        Assert.Equal(GenerationResultStatus.Committed, committed.Status);
        Assert.Equal(retained.Id, committed.FileId);
        Assert.Null(committed.ErrorMessage);

        Assert.Equal(ExternalOpenSafety.BlockedUnverifiedContent, ContentActionPolicy.GetExternalOpenSafety(retained));
    }

    [Fact]
    public async Task DiscardingAnUnverifiedResultRemovesTheStagedBytesAndLeavesTheResultFailed()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 1, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null);

        await workspace.DiscardUnverifiedResultAsync(record.Id, 0);

        Assert.Empty(await workspace.GetPendingUnverifiedResultsAsync(record.Id));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ".pending-review")));
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        var failed = Assert.Single(reloaded.Results);
        Assert.Equal(GenerationResultStatus.Failed, failed.Status);
        Assert.Null(failed.FileId);
        Assert.Empty(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task ImportMissingResultCommitsALateRecoveredVideoIntoItsFailedPosition()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Video Model", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        // Simulates a video job that completed at the provider but whose download failed —
        // committed with no files and a childErrorMessage, exactly like
        // GenerationQueueService.ExecuteVideoGenerationAsync does for AsyncGenerationPollOutcome
        // .CompletedDownloadFailed.
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "A cat on a skateboard", 1, workspace.Descriptor.GeneratedFolderId, null, null, childErrorMessages: ["Downloading the completed video result failed."]);
        Assert.Equal(GenerationResultStatus.Failed, Assert.Single(record.Results).Status);

        var imported = await workspace.ImportMissingResultAsync(record.Id, 0, Mp4SignatureBytes);

        Assert.Equal(FileOrigin.Generated, imported.Origin);
        Assert.Equal("video/mp4", imported.MediaType);
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        var committed = Assert.Single(reloaded.Results);
        Assert.Equal(GenerationResultStatus.Committed, committed.Status);
        Assert.Equal(imported.Id, committed.FileId);
        Assert.Null(committed.ErrorMessage);
        Assert.Equal(imported.Id, Assert.Single(reloaded.ResultFileIds));
    }

    [Fact]
    public async Task ImportMissingResultRejectsBytesThatDoNotMatchTheExpectedMediaType()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Video Model", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "A cat on a skateboard", 1, workspace.Descriptor.GeneratedFolderId, null, null, childErrorMessages: ["download failed"]);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ImportMissingResultAsync(record.Id, 0, PngSignatureBytes));

        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(GenerationResultStatus.Failed, Assert.Single(reloaded.Results).Status);
    }

    [Fact]
    public async Task ImportMissingResultRejectsAPositionThatIsNotAwaitingImport()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Video Model", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "A cat on a skateboard", 1, workspace.Descriptor.GeneratedFolderId, [Mp4SignatureBytes], null);
        Assert.Equal(GenerationResultStatus.Committed, Assert.Single(record.Results).Status);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ImportMissingResultAsync(record.Id, 0, Mp4SignatureBytes));
    }

    [Fact]
    public async Task AShortfallBeyondWhatTheProviderReturnedGetsAGenericPerPositionFailedEntry()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);

        // Only 1 byte array returned for a 3-result request — 2 positions never got any attempt.
        var record = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 3, workspace.Descriptor.GeneratedFolderId, [Mp3SignatureBytes], null);

        Assert.Equal(3, record.Results.Count);
        Assert.Equal(GenerationResultStatus.Committed, record.Results[0].Status);
        Assert.Equal(GenerationResultStatus.Failed, record.Results[1].Status);
        Assert.Equal(GenerationResultStatus.Failed, record.Results[2].Status);
        Assert.Equal("The provider did not return a result for this position.", record.Results[1].ErrorMessage);
        Assert.Equal("The provider did not return a result for this position.", record.Results[2].ErrorMessage);
    }

    [Fact]
    public async Task PerPositionResultsSurviveARoundTripThroughGetGenerationRecordAsync()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Audio Model", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);

        var created = await workspace.RecordMediaGenerationResultAsync(model.Id, "Read this aloud", 2, workspace.Descriptor.GeneratedFolderId, [Mp3SignatureBytes, PngSignatureBytes], null);
        var reloaded = await workspace.GetGenerationRecordAsync(created.Id);

        Assert.Equal(2, reloaded.Results.Count);
        Assert.Equal(GenerationResultStatus.Committed, reloaded.Results[0].Status);
        Assert.Equal(GenerationResultStatus.PendingReview, reloaded.Results[1].Status);
        Assert.Contains("did not match the expected media type", reloaded.Results[1].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledImageGenerationCommitLeavesNoOrphanedStagingFileOrHistoryRecord()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Image Model", connection.Id, "gpt-image-1", GenerationMode.Image, false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workspace.RecordImageGenerationResultAsync(model.Id, "A watercolor fox", 1, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null, null, cancellationToken: cancellation.Token));

        Assert.Empty(await workspace.GetActiveFilesAsync());
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root, ".staging")), path => path.EndsWith(".generating", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingMidLoopDuringAnImageGenerationCommitRollsBackEarlierResultFiles()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Image Model", connection.Id, "gpt-image-1", GenerationMode.Image, false);
        using var cancellation = new CancellationTokenSource();
        var images = new CancelAfterFirstItem<byte[]>([PngSignatureBytes, PngSignatureBytes], cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workspace.RecordImageGenerationResultAsync(model.Id, "Two watercolor foxes", 2, workspace.Descriptor.GeneratedFolderId, images, null, null, cancellationToken: cancellation.Token));

        Assert.Empty(await workspace.GetActiveFilesAsync());
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root, ".staging")), path => path.EndsWith(".generating", StringComparison.Ordinal));
    }

    private sealed class CancelAfterFirstItem<T>(IReadOnlyList<T> inner, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public T this[int index] => inner[index];
        public int Count => inner.Count;

        public IEnumerator<T> GetEnumerator()
        {
            var index = 0;
            foreach (var item in inner)
            {
                if (index == 1) cancellation.Cancel();
                index++;
                yield return item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task RecordingAFailedImageGenerationCreatesNoFilesButKeepsHistory()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Image Model", connection.Id, "gpt-image-1", GenerationMode.Image, false);

        var record = await workspace.RecordImageGenerationResultAsync(model.Id, "A watercolor fox", 1, workspace.Descriptor.GeneratedFolderId, null, "The provider reported a server error.");

        Assert.Equal(GenerationStatus.Failed, record.Status);
        Assert.Empty(record.ResultFileIds);
        Assert.Equal("The provider reported a server error.", record.ErrorMessage);
        Assert.Empty(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task RecordingATextGenerationWithASourceImagePersistsAndClearsOnPermanentDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var sourcePath = temporary.Child("source.png");
        var secondarySourcePath = temporary.Child("secondary.png");
        var tertiarySourcePath = temporary.Child("tertiary.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        await File.WriteAllBytesAsync(secondarySourcePath, [.. PngSignatureBytes, 1]);
        await File.WriteAllBytesAsync(tertiarySourcePath, [.. PngSignatureBytes, 2]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var imported = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var secondaryImported = Assert.Single(await workspace.ImportAsync([secondarySourcePath], workspace.Descriptor.RootFolderId)).File!;
        var tertiaryImported = Assert.Single(await workspace.ImportAsync([tertiarySourcePath], workspace.Descriptor.RootFolderId)).File!;

        IReadOnlyList<GenerationSourceSlot> slots =
        [
            new(GenerationInputSlotRole.ReferenceImage, imported.Id, 0),
            new(GenerationInputSlotRole.ReferenceImage, secondaryImported.Id, 1),
            new(GenerationInputSlotRole.ReferenceImage, tertiaryImported.Id, 2),
        ];
        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Describe this image", 1, workspace.Descriptor.GeneratedFolderId, ["A red circle."], null, sourceSlots: slots);

        Assert.Contains(record.SourceSlots, slot => slot.FileId == imported.Id && slot.Order == 0);
        Assert.Contains(record.SourceSlots, slot => slot.FileId == secondaryImported.Id && slot.Order == 1);
        Assert.Contains(record.SourceSlots, slot => slot.FileId == tertiaryImported.Id && slot.Order == 2);
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Contains(reloaded.SourceSlots, slot => slot.FileId == imported.Id && slot.Order == 0);
        Assert.Contains(reloaded.SourceSlots, slot => slot.FileId == secondaryImported.Id && slot.Order == 1);
        Assert.Contains(reloaded.SourceSlots, slot => slot.FileId == tertiaryImported.Id && slot.Order == 2);

        await workspace.RecycleFileAsync(imported.Id);
        await workspace.PermanentlyDeleteFileAsync(imported.Id);
        await workspace.RecycleFileAsync(secondaryImported.Id);
        await workspace.PermanentlyDeleteFileAsync(secondaryImported.Id);
        await workspace.RecycleFileAsync(tertiaryImported.Id);
        await workspace.PermanentlyDeleteFileAsync(tertiaryImported.Id);

        var afterDeletion = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Empty(afterDeletion.SourceSlots);

        var primarySnapshot = Assert.Single(afterDeletion.SourceSlotSnapshots, snapshot => snapshot.Order == 0);
        Assert.Null(primarySnapshot.FileId);
        Assert.Equal(imported.DisplayName, primarySnapshot.Identity.DisplayName);
        Assert.Equal(imported.MediaType, primarySnapshot.Identity.MediaType);
        Assert.Equal(imported.ContentHash, primarySnapshot.Identity.ContentHash);

        var secondarySnapshot = Assert.Single(afterDeletion.SourceSlotSnapshots, snapshot => snapshot.Order == 1);
        Assert.Null(secondarySnapshot.FileId);
        Assert.Equal(secondaryImported.DisplayName, secondarySnapshot.Identity.DisplayName);
        Assert.Equal(secondaryImported.MediaType, secondarySnapshot.Identity.MediaType);
        Assert.Equal(secondaryImported.ContentHash, secondarySnapshot.Identity.ContentHash);

        var tertiarySnapshot = Assert.Single(afterDeletion.SourceSlotSnapshots, snapshot => snapshot.Order == 2);
        Assert.Null(tertiarySnapshot.FileId);
        Assert.Equal(tertiaryImported.DisplayName, tertiarySnapshot.Identity.DisplayName);
        Assert.Equal(tertiaryImported.MediaType, tertiarySnapshot.Identity.MediaType);
        Assert.Equal(tertiaryImported.ContentHash, tertiarySnapshot.Identity.ContentHash);
    }

    [Fact]
    public async Task RecordingATextGenerationRejectsTheSameSourceFileSelectedInMoreThanOneSlot()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var imported = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        IReadOnlyList<GenerationSourceSlot> duplicateSlots =
        [
            new(GenerationInputSlotRole.ReferenceImage, imported.Id, 0),
            new(GenerationInputSlotRole.ReferenceImage, imported.Id, 2),
        ];
        await Assert.ThrowsAsync<LibraryValidationException>(() =>
            workspace.RecordTextGenerationResultAsync(model.Id, "Describe this image", 1, workspace.Descriptor.GeneratedFolderId, ["A red circle."], null, sourceSlots: duplicateSlots));
    }

    [Fact]
    public async Task PromptAndSystemInstructionsAreBoundedTo1MiBOfUtf8Text()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var oversized = new string('a', LibraryRules.MaximumGenerationTextUtf8Bytes + 1);
        var atLimit = new string('a', LibraryRules.MaximumGenerationTextUtf8Bytes);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RecordTextGenerationResultAsync(model.Id, oversized, 1, workspace.Descriptor.GeneratedFolderId, ["result"], null));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RecordTextGenerationResultAsync(model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null, systemInstructions: oversized));

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, atLimit, 1, workspace.Descriptor.GeneratedFolderId, ["result"], null);
        Assert.Equal(GenerationStatus.Completed, record.Status);
    }

    [Fact]
    public async Task FewerCommittedResultsThanRequestedIsPartiallyCompleted()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write three haiku", 3, workspace.Descriptor.GeneratedFolderId, ["Only one came back"], null);

        Assert.Equal(GenerationStatus.PartiallyCompleted, record.Status);
        Assert.Single(record.ResultFileIds);
        Assert.Equal(3, record.ResultCount);

        var imageModel = await workspace.CreateModelAsync("Imagen", connection.Id, "gpt-image-1", GenerationMode.Image, false);
        var imageRecord = await workspace.RecordImageGenerationResultAsync(imageModel.Id, "Two cats", 2, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null);
        Assert.Equal(GenerationStatus.PartiallyCompleted, imageRecord.Status);
        Assert.Single(imageRecord.ResultFileIds);
    }

    [Fact]
    public async Task SafetyBlockedCountPersistsAndClearsToZeroWhenNotSupplied()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write two haiku", 2, workspace.Descriptor.GeneratedFolderId, ["Only one came back"], null, safetyBlockedCount: 1);

        Assert.Equal(1, record.SafetyBlockedCount);
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(1, reloaded.SafetyBlockedCount);

        var withoutBlocking = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, ["Result"], null);
        Assert.Equal(0, withoutBlocking.SafetyBlockedCount);
    }

    [Fact]
    public async Task EveryChoiceSafetyBlockedProducesAFailedRecordWithNoCommittedFiles()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, [], null, safetyBlockedCount: 1);

        Assert.Equal(GenerationStatus.Failed, record.Status);
        Assert.Empty(record.ResultFileIds);
        Assert.Equal(1, record.SafetyBlockedCount);
        Assert.Null(record.ErrorMessage);
    }
}
