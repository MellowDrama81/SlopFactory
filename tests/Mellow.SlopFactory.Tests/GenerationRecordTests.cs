using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class GenerationRecordTests
{
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
        var settings = new GenerationSettings(0.7, 0.9, 500, 0.5, -0.5);

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
            workspace.RecordImageGenerationResultAsync(model.Id, "A watercolor fox", 1, workspace.Descriptor.GeneratedFolderId, [PngSignatureBytes], null, null, cancellation.Token));

        Assert.Empty(await workspace.GetActiveFilesAsync());
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root, ".staging")), path => path.EndsWith(".generating", StringComparison.Ordinal));
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

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Describe this image", 1, workspace.Descriptor.GeneratedFolderId, ["A red circle."], null, sourceFileId: imported.Id, secondarySourceFileId: secondaryImported.Id, tertiarySourceFileId: tertiaryImported.Id);

        Assert.Equal(imported.Id, record.SourceFileId);
        Assert.Equal(secondaryImported.Id, record.SecondarySourceFileId);
        Assert.Equal(tertiaryImported.Id, record.TertiarySourceFileId);
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(imported.Id, reloaded.SourceFileId);
        Assert.Equal(secondaryImported.Id, reloaded.SecondarySourceFileId);
        Assert.Equal(tertiaryImported.Id, reloaded.TertiarySourceFileId);

        await workspace.RecycleFileAsync(imported.Id);
        await workspace.PermanentlyDeleteFileAsync(imported.Id);
        await workspace.RecycleFileAsync(secondaryImported.Id);
        await workspace.PermanentlyDeleteFileAsync(secondaryImported.Id);
        await workspace.RecycleFileAsync(tertiaryImported.Id);
        await workspace.PermanentlyDeleteFileAsync(tertiaryImported.Id);

        var afterDeletion = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Null(afterDeletion.SourceFileId);
        Assert.NotNull(afterDeletion.SourceFileTombstone);
        Assert.Equal(imported.DisplayName, afterDeletion.SourceFileTombstone!.DisplayName);
        Assert.Equal(imported.MediaType, afterDeletion.SourceFileTombstone.MediaType);
        Assert.Equal(imported.ContentHash, afterDeletion.SourceFileTombstone.ContentHash);

        Assert.Null(afterDeletion.SecondarySourceFileId);
        Assert.NotNull(afterDeletion.SecondarySourceFileTombstone);
        Assert.Equal(secondaryImported.DisplayName, afterDeletion.SecondarySourceFileTombstone!.DisplayName);
        Assert.Equal(secondaryImported.MediaType, afterDeletion.SecondarySourceFileTombstone.MediaType);
        Assert.Equal(secondaryImported.ContentHash, afterDeletion.SecondarySourceFileTombstone.ContentHash);

        Assert.Null(afterDeletion.TertiarySourceFileId);
        Assert.NotNull(afterDeletion.TertiarySourceFileTombstone);
        Assert.Equal(tertiaryImported.DisplayName, afterDeletion.TertiarySourceFileTombstone!.DisplayName);
        Assert.Equal(tertiaryImported.MediaType, afterDeletion.TertiarySourceFileTombstone.MediaType);
        Assert.Equal(tertiaryImported.ContentHash, afterDeletion.TertiarySourceFileTombstone.ContentHash);
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

        await Assert.ThrowsAsync<LibraryValidationException>(() =>
            workspace.RecordTextGenerationResultAsync(model.Id, "Describe this image", 1, workspace.Descriptor.GeneratedFolderId, ["A red circle."], null, sourceFileId: imported.Id, tertiarySourceFileId: imported.Id));
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
