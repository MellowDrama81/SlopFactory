using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ConnectionModelTests
{
    [Fact]
    public async Task CreateConnectionValidatesLabelBaseUrlAndEnforcesUniqueLabels()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var connection = await workspace.CreateConnectionAsync("OpenAI Primary", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        Assert.Equal("OpenAI Primary", connection.Label);
        Assert.Equal("https://api.openai.com/v1", connection.BaseUrl);
        Assert.Equal(ConnectionTestStatus.Untested, connection.LastTestStatus);
        Assert.False(connection.HasCredential);
        Assert.True(connection.IsUnverified);

        await Assert.ThrowsAsync<NameConflictException>(() => workspace.CreateConnectionAsync("OpenAI Primary", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer"));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateConnectionAsync("Bad URL", ProviderType.OpenAi, "not-a-url", "Authorization", "Bearer"));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateConnectionAsync("Insecure", ProviderType.GenericOpenAiCompatible, "http://example.com", "Authorization", "Bearer"));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateConnectionAsync("Embedded Credential", ProviderType.GenericOpenAiCompatible, "https://user:pass@example.com", "Authorization", "Bearer"));

        var localConnection = await workspace.CreateConnectionAsync("Local", ProviderType.GenericOpenAiCompatible, "http://localhost:1234/", "Authorization", "Bearer");
        Assert.Equal("http://localhost:1234", localConnection.BaseUrl);
    }

    [Fact]
    public async Task UpdatingAConnectionResetsItsTestStatusWithoutAffectingDependentModels()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        connection = await workspace.SetConnectionTestResultAsync(connection.Id, true, "ok");
        Assert.Equal(ConnectionTestStatus.Success, connection.LastTestStatus);

        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var updated = await workspace.UpdateConnectionAsync(connection.Id, "Renamed Connection", connection.BaseUrl, connection.CredentialHeaderName, connection.AuthPrefix);
        Assert.Equal(ConnectionTestStatus.Untested, updated.LastTestStatus);
        Assert.Null(updated.LastTestedAt);

        var reloadedModel = await workspace.GetModelAsync(model.Id);
        Assert.Equal("GPT", reloadedModel.Label);
        Assert.Equal(connection.Id, reloadedModel.ConnectionId);
    }

    [Fact]
    public async Task RecyclingAConnectionCascadesToItsModelsAndRestoreReversesIt()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        await workspace.RecycleConnectionAsync(connection.Id);
        Assert.Empty(await workspace.GetActiveConnectionsAsync());
        Assert.Empty(await workspace.GetActiveModelsAsync());
        Assert.Single(await workspace.GetRecycledModelsAsync());

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RestoreModelAsync(model.Id));

        await workspace.RestoreConnectionAsync(connection.Id);
        Assert.Single(await workspace.GetActiveConnectionsAsync());
        Assert.Single(await workspace.GetActiveModelsAsync());
    }

    [Fact]
    public async Task PermanentlyDeletingAConnectionCascadesToItsRecycledModels()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        await workspace.RecycleConnectionAsync(connection.Id);

        await workspace.PermanentlyDeleteConnectionAsync(connection.Id);

        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetConnectionAsync(connection.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetModelAsync(model.Id));
    }

    [Fact]
    public async Task ModelLabelsAreUniqueAndModelsRequireAnActiveConnection()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        await Assert.ThrowsAsync<NameConflictException>(() => workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o-mini", GenerationMode.Text, true));

        await workspace.RecycleConnectionAsync(connection.Id);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateModelAsync("Another", connection.Id, "gpt-4o", GenerationMode.Text, true));
    }

    [Fact]
    public async Task ModelCatalogueRefreshPersistsEntriesAndFailedRefreshMarksPossiblyStaleWithoutClearingThem()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");

        var empty = await workspace.GetModelCatalogueAsync(connection.Id);
        Assert.Null(empty.RetrievedAt);
        Assert.False(empty.PossiblyStale);
        Assert.Empty(empty.Entries);

        var discovered = new[] { new ProviderModelInfo("gpt-4o", "GPT-4o"), new ProviderModelInfo("gpt-4o-mini", null) };
        var refreshed = await workspace.RefreshModelCatalogueAsync(connection.Id, discovered);
        Assert.NotNull(refreshed.RetrievedAt);
        Assert.False(refreshed.PossiblyStale);
        Assert.Equal(2, refreshed.Entries.Count);
        Assert.Contains(refreshed.Entries, entry => entry.ProviderModelId == "gpt-4o" && entry.DisplayLabel == "GPT-4o");
        Assert.Contains(refreshed.Entries, entry => entry.ProviderModelId == "gpt-4o-mini" && entry.DisplayLabel is null);

        var failed = await workspace.MarkModelCatalogueRefreshFailedAsync(connection.Id);
        Assert.Equal(refreshed.RetrievedAt, failed.RetrievedAt);
        Assert.True(failed.PossiblyStale);
        Assert.Equal(2, failed.Entries.Count);

        var reRefreshed = await workspace.RefreshModelCatalogueAsync(connection.Id, [new ProviderModelInfo("gpt-4o", "GPT-4o")]);
        Assert.False(reRefreshed.PossiblyStale);
        Assert.Equal("gpt-4o", Assert.Single(reRefreshed.Entries).ProviderModelId);
    }

    [Fact]
    public async Task ConnectionTimeoutOverrideIsValidatedAndPersistedIndependentlyOfTestStatus()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        Assert.Null(connection.TimeoutSeconds);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateConnectionAsync("Too Short", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer", 1));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateConnectionAsync("Too Long", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer", 601));

        var withTimeout = await workspace.UpdateConnectionAsync(connection.Id, connection.Label, connection.BaseUrl, connection.CredentialHeaderName, connection.AuthPrefix, 30);
        Assert.Equal(30, withTimeout.TimeoutSeconds);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.Equal(30, reloaded.TimeoutSeconds);

        var backToDefault = await workspace.UpdateConnectionAsync(connection.Id, connection.Label, connection.BaseUrl, connection.CredentialHeaderName, connection.AuthPrefix, null);
        Assert.Null(backToDefault.TimeoutSeconds);
    }

    [Fact]
    public async Task AdditionalConnectionHeadersAreValidatedAndPersisted()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var withHeaders = await workspace.CreateConnectionAsync("Gateway", ProviderType.GenericOpenAiCompatible, "https://gateway.example.com", "Authorization", "Bearer",
            additionalHeaders: [new ConnectionHeader("X-Organization", "org_123"), new ConnectionHeader("X-Trace-Id", "abc")]);
        Assert.Equal(2, withHeaders.AdditionalHeaders!.Count);
        Assert.Contains(withHeaders.AdditionalHeaders!, header => header.Name == "X-Organization" && header.Value == "org_123");

        var reloaded = await workspace.GetConnectionAsync(withHeaders.Id);
        Assert.Equal(2, reloaded.AdditionalHeaders!.Count);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.UpdateConnectionAsync(withHeaders.Id, withHeaders.Label, withHeaders.BaseUrl, withHeaders.CredentialHeaderName, withHeaders.AuthPrefix,
            additionalHeaders: [new ConnectionHeader("Authorization", "Bearer other-key")]));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.UpdateConnectionAsync(withHeaders.Id, withHeaders.Label, withHeaders.BaseUrl, withHeaders.CredentialHeaderName, withHeaders.AuthPrefix,
            additionalHeaders: [new ConnectionHeader("Host", "example.com")]));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.UpdateConnectionAsync(withHeaders.Id, withHeaders.Label, withHeaders.BaseUrl, withHeaders.CredentialHeaderName, withHeaders.AuthPrefix,
            additionalHeaders: [new ConnectionHeader("X-Dup", "1"), new ConnectionHeader("x-dup", "2")]));

        var cleared = await workspace.UpdateConnectionAsync(withHeaders.Id, withHeaders.Label, withHeaders.BaseUrl, withHeaders.CredentialHeaderName, withHeaders.AuthPrefix, additionalHeaders: []);
        Assert.Empty(cleared.AdditionalHeaders!);
    }

    [Fact]
    public async Task GenericModalitySettingsDefaultToAllEnabledAndValidateRelativePathOverrides()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var connection = await workspace.CreateConnectionAsync("Gateway", ProviderType.GenericOpenAiCompatible, "https://gateway.example.com", "Authorization", "Bearer");
        Assert.True(connection.GenericModalitySettings!.ModelsEnabled);
        Assert.True(connection.GenericModalitySettings!.TextGenerationEnabled);
        Assert.True(connection.GenericModalitySettings!.ImageGenerationEnabled);
        Assert.Null(connection.GenericModalitySettings!.ModelsPathOverride);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.UpdateConnectionAsync(connection.Id, connection.Label, connection.BaseUrl, connection.CredentialHeaderName, connection.AuthPrefix,
            genericModalitySettings: new GenericConnectionModalitySettings(true, "https://evil.example.com/models", true, null, true, null)));

        var updated = await workspace.UpdateConnectionAsync(connection.Id, connection.Label, connection.BaseUrl, connection.CredentialHeaderName, connection.AuthPrefix,
            genericModalitySettings: new GenericConnectionModalitySettings(true, "v2/models", false, null, true, null));
        Assert.Equal("v2/models", updated.GenericModalitySettings!.ModelsPathOverride);
        Assert.False(updated.GenericModalitySettings!.TextGenerationEnabled);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.Equal("v2/models", reloaded.GenericModalitySettings!.ModelsPathOverride);
        Assert.False(reloaded.GenericModalitySettings!.TextGenerationEnabled);
    }

    [Fact]
    public async Task ProviderTypeCanOnlyChangeWithNoDependentModelsAndResetsModalitySettings()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var connection = await workspace.CreateConnectionAsync("Gateway", ProviderType.GenericOpenAiCompatible, "https://gateway.example.com", "Authorization", "Bearer",
            genericModalitySettings: new GenericConnectionModalitySettings(true, "v2/models", false, null, true, null));
        var model = await workspace.CreateModelAsync("Local", connection.Id, "local-model", GenerationMode.Text, true);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ChangeConnectionProviderTypeAsync(connection.Id, ProviderType.OpenAi));

        await workspace.RecycleModelAsync(model.Id);
        var changed = await workspace.ChangeConnectionProviderTypeAsync(connection.Id, ProviderType.OpenAi);
        Assert.Equal(ProviderType.OpenAi, changed.ProviderType);
        Assert.True(changed.GenericModalitySettings!.ModelsEnabled);
        Assert.Null(changed.GenericModalitySettings!.ModelsPathOverride);
        Assert.Equal(ConnectionTestStatus.Untested, changed.LastTestStatus);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.Equal(ProviderType.OpenAi, reloaded.ProviderType);
    }

    [Fact]
    public async Task ChangingProviderModelIdOrModeMarksModelAndItsActiveSavedSettingsNeedsReview()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var savedSetting = await workspace.CreateSavedSettingAsync("My preset", model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);
        Assert.False(model.NeedsReview);
        Assert.False(savedSetting.NeedsReview);

        var relabelled = await workspace.UpdateModelAsync(model.Id, "Renamed GPT", model.ProviderModelId, model.Mode, model.SupportsSystemInstructions);
        Assert.False(relabelled.NeedsReview);
        var afterLabelOnly = await workspace.GetSavedSettingAsync(savedSetting.Id);
        Assert.False(afterLabelOnly.NeedsReview);

        var changedProviderModelId = await workspace.UpdateModelAsync(model.Id, relabelled.Label, "gpt-4o-mini", relabelled.Mode, relabelled.SupportsSystemInstructions);
        Assert.True(changedProviderModelId.NeedsReview);
        var reloadedSetting = await workspace.GetSavedSettingAsync(savedSetting.Id);
        Assert.True(reloadedSetting.NeedsReview);

        var reviewed = await workspace.MarkModelReviewedAsync(model.Id);
        Assert.False(reviewed.NeedsReview);
        var settingAfterReview = await workspace.GetSavedSettingAsync(savedSetting.Id);
        Assert.False(settingAfterReview.NeedsReview);

        var changedMode = await workspace.UpdateModelAsync(model.Id, reviewed.Label, reviewed.ProviderModelId, GenerationMode.Image, reviewed.SupportsSystemInstructions);
        Assert.True(changedMode.NeedsReview);
    }
}
