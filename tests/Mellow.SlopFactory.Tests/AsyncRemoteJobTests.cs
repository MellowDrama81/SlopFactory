using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class AsyncRemoteJobTests
{
    [Fact]
    public async Task CreateAsyncRemoteJobPersistsSubmittedPhaseAndIsReturnedAsPending()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OneMinAi, "https://api.1min.ai", "API-KEY", "");
        var draft = await workspace.CreateDraftAsync();

        var job = await workspace.CreateAsyncRemoteJobAsync(draft.Id, ProviderType.OneMinAi, connection.Id, "remote-job-1", "idem-key-1", DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(draft.Id, job.DraftId);
        Assert.Equal(ProviderType.OneMinAi, job.ProviderType);
        Assert.Equal(connection.Id, job.ConnectionId);
        Assert.Equal("remote-job-1", job.ProviderJobId);
        Assert.Equal(AsyncRemoteJobPhase.Submitted, job.Phase);
        Assert.Equal("idem-key-1", job.IdempotencyKey);
        Assert.Null(job.LastPolledAt);

        var pending = await workspace.GetPendingAsyncRemoteJobsAsync();
        Assert.Equal(job.Id, Assert.Single(pending).Id);
    }

    [Fact]
    public async Task UpdatingPhaseToTerminalRemovesJobFromPendingButLeavesItReadableUntilDeleted()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
        var draft = await workspace.CreateDraftAsync();
        var job = await workspace.CreateAsyncRemoteJobAsync(draft.Id, ProviderType.OpenRouter, connection.Id, "remote-job-2", null, null);

        var processing = await workspace.UpdateAsyncRemoteJobPhaseAsync(job.Id, AsyncRemoteJobPhase.Processing, DateTimeOffset.UtcNow);
        Assert.Equal(AsyncRemoteJobPhase.Processing, processing.Phase);
        Assert.NotNull(processing.LastPolledAt);
        Assert.Equal(job.Id, Assert.Single(await workspace.GetPendingAsyncRemoteJobsAsync()).Id);

        var completed = await workspace.UpdateAsyncRemoteJobPhaseAsync(job.Id, AsyncRemoteJobPhase.Completed, DateTimeOffset.UtcNow);
        Assert.Equal(AsyncRemoteJobPhase.Completed, completed.Phase);
        Assert.Empty(await workspace.GetPendingAsyncRemoteJobsAsync());
        Assert.Equal(job.Id, Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id)).Id);

        await workspace.DeleteAsyncRemoteJobAsync(job.Id);
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
    }

    [Fact]
    public async Task MonitoringPausedJobsRemainInPendingRegistryForLaterReconciliation()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai", "Authorization", "Bearer");
        var draft = await workspace.CreateDraftAsync();
        var job = await workspace.CreateAsyncRemoteJobAsync(draft.Id, ProviderType.DeepInfra, connection.Id, "remote-job-3", null, DateTimeOffset.UtcNow.AddMinutes(-1));

        var paused = await workspace.UpdateAsyncRemoteJobPhaseAsync(job.Id, AsyncRemoteJobPhase.MonitoringPaused, DateTimeOffset.UtcNow);

        Assert.Equal(AsyncRemoteJobPhase.MonitoringPaused, paused.Phase);
        Assert.Equal(job.Id, Assert.Single(await workspace.GetPendingAsyncRemoteJobsAsync()).Id);
    }

    [Fact]
    public async Task DeletingADraftDoesNotCascadeDeleteItsAsyncRemoteJobBecauseSubmittedWorkOutlivesTheTabDraft()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OneMinAi, "https://api.1min.ai", "API-KEY", "");
        var draft = await workspace.CreateDraftAsync();
        var job = await workspace.CreateAsyncRemoteJobAsync(draft.Id, ProviderType.OneMinAi, connection.Id, "remote-job-4", null, null);

        await workspace.DeleteDraftAsync(draft.Id);

        Assert.Equal(job.Id, Assert.Single(await workspace.GetPendingAsyncRemoteJobsAsync()).Id);
    }

    [Fact]
    public async Task UpdatingOrDeletingAnUnknownAsyncRemoteJobThrowsRecordNotFound()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.UpdateAsyncRemoteJobPhaseAsync("missing", AsyncRemoteJobPhase.Processing));
    }
}
