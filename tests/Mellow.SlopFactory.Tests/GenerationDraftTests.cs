using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class GenerationDraftTests
{
    private static readonly byte[] PngSignatureBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public async Task CreateDraftAsyncDefaultsDestinationFolderAndResultCount()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var draft = await workspace.CreateDraftAsync();

        Assert.Null(draft.CustomTitle);
        Assert.Null(draft.ModelId);
        Assert.Equal(string.Empty, draft.Prompt);
        Assert.Null(draft.SystemInstructions);
        Assert.Null(draft.SourceFileId);
        Assert.Equal(1, draft.ResultCount);
        Assert.Equal(workspace.Descriptor.GeneratedFolderId, draft.DestinationFolderId);
        Assert.Null(draft.ImprovementModelId);
        Assert.Null(draft.ImprovementGuidance);

        var second = await workspace.CreateDraftAsync();
        Assert.True(second.TabOrder > draft.TabOrder);

        var all = await workspace.GetDraftsAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ReplaceDraftStateRoundTripsEveryFieldAndCanClearTheCustomTitle()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var draft = await workspace.CreateDraftAsync();

        var settings = new GenerationSettings(0.7, 0.9, 500, 0.5, -0.5);
        var updated = await workspace.ReplaceDraftStateAsync(draft.Id, "My Tab", model.Id, "Write a haiku", "Respond in French.", null, 3, workspace.Descriptor.GeneratedFolderId, model.Id, "Be concise.", settings);

        Assert.Equal("My Tab", updated.CustomTitle);
        Assert.Equal(model.Id, updated.ModelId);
        Assert.Equal("Write a haiku", updated.Prompt);
        Assert.Equal("Respond in French.", updated.SystemInstructions);
        Assert.Equal(3, updated.ResultCount);
        Assert.Equal(model.Id, updated.ImprovementModelId);
        Assert.Equal("Be concise.", updated.ImprovementGuidance);
        Assert.Equal(settings, updated.Settings);

        var reloaded = await workspace.GetDraftAsync(draft.Id);
        Assert.Equal("My Tab", reloaded.CustomTitle);
        Assert.Equal(settings, reloaded.Settings);

        var resetToAutomaticTitle = await workspace.ReplaceDraftStateAsync(draft.Id, null, model.Id, "Write a haiku", null, null, 3, workspace.Descriptor.GeneratedFolderId, null, null);
        Assert.Null(resetToAutomaticTitle.CustomTitle);
        Assert.Null(resetToAutomaticTitle.SystemInstructions);
        Assert.Null(resetToAutomaticTitle.ImprovementModelId);
        Assert.Null(resetToAutomaticTitle.ImprovementGuidance);
        Assert.Equal(GenerationSettings.Empty, resetToAutomaticTitle.Settings);
    }

    [Fact]
    public async Task DuplicateDraftCopiesFieldsExceptTitleAndLandsAdjacentInTabOrder()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var draft = await workspace.CreateDraftAsync();
        var settings = new GenerationSettings(0.7, 0.9, 500, 0.5, -0.5);
        draft = await workspace.ReplaceDraftStateAsync(draft.Id, "Original", model.Id, "Write a haiku", null, null, 2, workspace.Descriptor.GeneratedFolderId, null, null, settings);
        var trailing = await workspace.CreateDraftAsync();

        var duplicate = await workspace.DuplicateDraftAsync(draft.Id);

        Assert.Null(duplicate.CustomTitle);
        Assert.Equal(model.Id, duplicate.ModelId);
        Assert.Equal("Write a haiku", duplicate.Prompt);
        Assert.Equal(2, duplicate.ResultCount);
        Assert.Equal(draft.TabOrder + 1, duplicate.TabOrder);
        Assert.Equal(settings, duplicate.Settings);

        var reloadedTrailing = await workspace.GetDraftAsync(trailing.Id);
        Assert.Equal(trailing.TabOrder + 1, reloadedTrailing.TabOrder);
    }

    [Fact]
    public async Task DeleteDraftRemovesItAndASecondDeleteThrows()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var draft = await workspace.CreateDraftAsync();

        await workspace.DeleteDraftAsync(draft.Id);

        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetDraftAsync(draft.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.DeleteDraftAsync(draft.Id));
    }

    [Fact]
    public async Task PermanentlyDeletingAReferencedModelOrFileClearsTheDraftsReferenceInsteadOfThrowing()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var improvementModel = await workspace.CreateModelAsync("Improver", connection.Id, "gpt-4o-mini", GenerationMode.Text, true);
        var imported = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var draft = await workspace.CreateDraftAsync();
        draft = await workspace.ReplaceDraftStateAsync(draft.Id, null, model.Id, "Describe this image", null, imported.Id, 1, workspace.Descriptor.GeneratedFolderId, improvementModel.Id, "Be vivid.");

        await workspace.RecycleModelAsync(model.Id);
        await workspace.PermanentlyDeleteModelAsync(model.Id);
        await workspace.RecycleModelAsync(improvementModel.Id);
        await workspace.PermanentlyDeleteModelAsync(improvementModel.Id);
        await workspace.RecycleFileAsync(imported.Id);
        await workspace.PermanentlyDeleteFileAsync(imported.Id);

        var reloaded = await workspace.GetDraftAsync(draft.Id);
        Assert.Null(reloaded.ModelId);
        Assert.Null(reloaded.ImprovementModelId);
        Assert.Null(reloaded.SourceFileId);
    }

    [Fact]
    public async Task ReorderDraftsRewritesTabOrderAndRejectsAMismatchedSet()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var first = await workspace.CreateDraftAsync();
        var second = await workspace.CreateDraftAsync();
        var third = await workspace.CreateDraftAsync();

        var reordered = await workspace.ReorderDraftsAsync([third.Id, first.Id, second.Id]);

        Assert.Equal([third.Id, first.Id, second.Id], reordered.Select(draft => draft.Id).ToArray());
        Assert.Equal([0, 1, 2], reordered.Select(draft => draft.TabOrder).ToArray());
        var reloaded = await workspace.GetDraftsAsync();
        Assert.Equal([third.Id, first.Id, second.Id], reloaded.OrderBy(draft => draft.TabOrder).Select(draft => draft.Id).ToArray());

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReorderDraftsAsync([first.Id, second.Id]));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReorderDraftsAsync([first.Id, second.Id, "unknown-id"]));
    }

    [Fact]
    public async Task DraftTextFieldsAreBoundedTo1MiBOfUtf8Text()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var draft = await workspace.CreateDraftAsync();
        var oversized = new string('a', LibraryRules.MaximumGenerationTextUtf8Bytes + 1);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReplaceDraftStateAsync(draft.Id, null, null, oversized, null, null, 1, workspace.Descriptor.GeneratedFolderId, null, null));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReplaceDraftStateAsync(draft.Id, null, null, "a prompt", oversized, null, 1, workspace.Descriptor.GeneratedFolderId, null, null));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReplaceDraftStateAsync(draft.Id, null, null, "a prompt", null, null, 1, workspace.Descriptor.GeneratedFolderId, null, oversized));
    }

    [Theory]
    [InlineData(-0.1, null, null, null, null)]
    [InlineData(2.1, null, null, null, null)]
    [InlineData(null, -0.1, null, null, null)]
    [InlineData(null, 1.1, null, null, null)]
    [InlineData(null, null, 0, null, null)]
    [InlineData(null, null, null, -2.1, null)]
    [InlineData(null, null, null, 2.1, null)]
    [InlineData(null, null, null, null, -2.1)]
    [InlineData(null, null, null, null, 2.1)]
    public async Task ReplaceDraftStateRejectsOutOfRangeGenerationSettings(double? temperature, double? topP, int? maxTokens, double? frequencyPenalty, double? presencePenalty)
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var draft = await workspace.CreateDraftAsync();
        var settings = new GenerationSettings(temperature, topP, maxTokens, frequencyPenalty, presencePenalty);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReplaceDraftStateAsync(draft.Id, null, null, "a prompt", null, null, 1, workspace.Descriptor.GeneratedFolderId, null, null, settings));
    }
}
