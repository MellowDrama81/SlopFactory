using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class SavedGenerationSettingTests
{
    [Fact]
    public async Task CreateSavedSettingSnapshotsModelAndEnforcesUniqueTitles()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 2, workspace.Descriptor.GeneratedFolderId);

        Assert.Equal("My Preset", saved.Title);
        Assert.Equal(model.Id, saved.ModelId);
        Assert.Equal("GPT", saved.ModelLabel);
        Assert.Equal(GenerationMode.Text, saved.Mode);
        Assert.Equal(LibraryRecordState.Active, saved.State);

        await Assert.ThrowsAsync<NameConflictException>(() => workspace.CreateSavedSettingAsync("My Preset", model.Id, "Different prompt", 1, workspace.Descriptor.GeneratedFolderId));

        var updated = await workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, "Renamed Preset", model.Id, "Updated prompt", 3, workspace.Descriptor.GeneratedFolderId);
        Assert.Equal("Renamed Preset", updated.Title);
        Assert.Equal("Updated prompt", updated.Prompt);
        Assert.Equal(3, updated.ResultCount);
        Assert.Equal(saved.Revision + 1, updated.Revision);

        var active = await workspace.GetActiveSavedSettingsAsync();
        Assert.Single(active);
        var reloaded = await workspace.GetSavedSettingAsync(saved.Id);
        Assert.Equal("Renamed Preset", reloaded.Title);
    }

    [Fact]
    public async Task SavedSettingPersistsAndUpdatesSystemInstructions()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId, "Respond only in French.");
        Assert.Equal("Respond only in French.", saved.SystemInstructions);

        var reloaded = await workspace.GetSavedSettingAsync(saved.Id);
        Assert.Equal("Respond only in French.", reloaded.SystemInstructions);

        var cleared = await workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, saved.Prompt, saved.ResultCount, saved.DestinationFolderId);
        Assert.Null(cleared.SystemInstructions);
    }

    [Fact]
    public async Task SavedSettingPersistsAndClearsSourceFileId()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var imported = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Describe this image", 1, workspace.Descriptor.GeneratedFolderId, null, imported.Id);
        Assert.Equal(imported.Id, saved.SourceFileId);

        var reloaded = await workspace.GetSavedSettingAsync(saved.Id);
        Assert.Equal(imported.Id, reloaded.SourceFileId);

        var cleared = await workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, saved.Prompt, saved.ResultCount, saved.DestinationFolderId);
        Assert.Null(cleared.SourceFileId);
    }

    [Fact]
    public async Task RecycleRestoreAndPermanentlyDeleteASavedSettingDirectly()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleSavedSettingAsync(saved.Id);
        Assert.Empty(await workspace.GetActiveSavedSettingsAsync());
        Assert.Single(await workspace.GetRecycledSavedSettingsAsync());

        await workspace.RestoreSavedSettingAsync(saved.Id);
        Assert.Single(await workspace.GetActiveSavedSettingsAsync());

        await workspace.RecycleSavedSettingAsync(saved.Id);
        await workspace.PermanentlyDeleteSavedSettingAsync(saved.Id);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetSavedSettingAsync(saved.Id));
    }

    [Fact]
    public async Task ARecycledSavedSettingCanBeRestoredAndImmediatelyUpdatedWithNewFieldsInOneFlow()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleSavedSettingAsync(saved.Id);
        var recycled = await workspace.GetSavedSettingAsync(saved.Id);
        Assert.Equal(LibraryRecordState.Recycled, recycled.State);

        await workspace.RestoreSavedSettingAsync(saved.Id);
        var updated = await workspace.UpdateSavedSettingAsync(saved.Id, recycled.Revision, "My Preset", model.Id, "A new prompt after restore", 4, workspace.Descriptor.GeneratedFolderId);

        Assert.Equal(LibraryRecordState.Active, (await workspace.GetSavedSettingAsync(saved.Id)).State);
        Assert.Equal("A new prompt after restore", updated.Prompt);
        Assert.Equal(4, updated.ResultCount);
        Assert.Equal(recycled.Revision + 1, updated.Revision);
    }

    [Fact]
    public async Task RestoringARecycledSavedSettingWhoseTitleNowCollidesWithAnActiveOneThrowsANameConflict()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.RecycleSavedSettingAsync(saved.Id);
        await workspace.CreateSavedSettingAsync("My Preset", model.Id, "A different prompt", 1, workspace.Descriptor.GeneratedFolderId);

        await Assert.ThrowsAsync<NameConflictException>(() => workspace.RestoreSavedSettingAsync(saved.Id));
    }

    [Fact]
    public async Task GetSavedSettingThrowsRecordNotFoundAfterPermanentDeletionSoASourceTabCanDetectItWasDeletedRatherThanJustRecycled()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleSavedSettingAsync(saved.Id);
        await workspace.PermanentlyDeleteSavedSettingAsync(saved.Id);

        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetSavedSettingAsync(saved.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.RestoreSavedSettingAsync(saved.Id));
    }

    [Fact]
    public async Task RecyclingAModelCascadesToItsSavedSettingsAndRestoreReversesIt()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleModelAsync(model.Id);
        Assert.Empty(await workspace.GetActiveSavedSettingsAsync());
        var recycled = Assert.Single(await workspace.GetRecycledSavedSettingsAsync());
        Assert.Equal(saved.Id, recycled.Id);

        await workspace.RestoreModelAsync(model.Id);
        Assert.Single(await workspace.GetActiveSavedSettingsAsync());
    }

    [Fact]
    public async Task PermanentlyDeletingAModelCascadesToItsSavedSettings()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleModelAsync(model.Id);
        await workspace.PermanentlyDeleteModelAsync(model.Id);

        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetSavedSettingAsync(saved.Id));
    }

    [Fact]
    public async Task RecyclingAConnectionCascadesThroughModelsToSavedSettings()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleConnectionAsync(connection.Id);
        Assert.Empty(await workspace.GetActiveSavedSettingsAsync());
        Assert.Single(await workspace.GetRecycledSavedSettingsAsync());

        await workspace.RestoreConnectionAsync(connection.Id);
        Assert.Single(await workspace.GetActiveSavedSettingsAsync());

        await workspace.RecycleConnectionAsync(connection.Id);
        await workspace.PermanentlyDeleteConnectionAsync(connection.Id);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetSavedSettingAsync(saved.Id));
    }

    [Fact]
    public async Task SavedSettingPromptAndSystemInstructionsAreBoundedTo1MiBOfUtf8Text()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var oversized = new string('a', LibraryRules.MaximumGenerationTextUtf8Bytes + 1);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateSavedSettingAsync("Too Long", model.Id, oversized, 1, workspace.Descriptor.GeneratedFolderId));

        var saved = await workspace.CreateSavedSettingAsync("Preset", model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, systemInstructions: oversized));
    }

    [Fact]
    public async Task CreatingASavedSettingStartsAtRevisionOne()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);

        Assert.Equal(1, saved.Revision);
    }

    [Fact]
    public async Task UpdatingWithAStaleRevisionThrowsAConflictCarryingTheCurrentRecordWithoutWriting()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);
        var updatedElsewhere = await workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, "Changed by another tab", saved.ResultCount, saved.DestinationFolderId);

        var exception = await Assert.ThrowsAsync<SavedSettingRevisionConflictException>(() =>
            workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, "Changed by this tab", saved.ResultCount, saved.DestinationFolderId));

        Assert.Equal(updatedElsewhere.Revision, exception.Current.Revision);
        Assert.Equal("Changed by another tab", exception.Current.Prompt);
        var reloaded = await workspace.GetSavedSettingAsync(saved.Id);
        Assert.Equal("Changed by another tab", reloaded.Prompt);
    }

    [Fact]
    public async Task OverwritingAfterAConflictSucceedsUsingTheFreshCurrentRevision()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("My Preset", model.Id, "Write a haiku", 1, workspace.Descriptor.GeneratedFolderId);
        var updatedElsewhere = await workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, "Changed by another tab", saved.ResultCount, saved.DestinationFolderId);
        var conflict = await Assert.ThrowsAsync<SavedSettingRevisionConflictException>(() =>
            workspace.UpdateSavedSettingAsync(saved.Id, saved.Revision, saved.Title, model.Id, "Changed by this tab", saved.ResultCount, saved.DestinationFolderId));

        var overwritten = await workspace.UpdateSavedSettingAsync(saved.Id, conflict.Current.Revision, saved.Title, model.Id, "Changed by this tab", saved.ResultCount, saved.DestinationFolderId);

        Assert.Equal("Changed by this tab", overwritten.Prompt);
        Assert.Equal(updatedElsewhere.Revision + 1, overwritten.Revision);
    }
}
