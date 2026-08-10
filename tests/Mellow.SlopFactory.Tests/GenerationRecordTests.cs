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
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var imported = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "Describe this image", 1, workspace.Descriptor.GeneratedFolderId, ["A red circle."], null, sourceFileId: imported.Id);

        Assert.Equal(imported.Id, record.SourceFileId);
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Equal(imported.Id, reloaded.SourceFileId);

        await workspace.RecycleFileAsync(imported.Id);
        await workspace.PermanentlyDeleteFileAsync(imported.Id);

        var afterDeletion = await workspace.GetGenerationRecordAsync(record.Id);
        Assert.Null(afterDeletion.SourceFileId);
    }
}
