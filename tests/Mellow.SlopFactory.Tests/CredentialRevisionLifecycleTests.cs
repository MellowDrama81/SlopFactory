using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class CredentialRevisionLifecycleTests
{
    private static async Task<(LibraryWorkspaceFactory Factory, string Root, ILibraryWorkspace Workspace, string ConnectionId)> CreateHarnessAsync(TemporaryDirectory temporary)
    {
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        var workspace = await factory.CreateAsync(root);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        return (factory, root, workspace, connection.Id);
    }

    [Fact]
    public async Task BeginCredentialCandidateMintsALedgerRow()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var revisionId = await workspace.BeginCredentialCandidateAsync(connectionId);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connectionId);
        var revision = Assert.Single(snapshot.Revisions);
        Assert.Equal(revisionId, revision.RevisionId);
        Assert.Equal(CredentialRevisionPurpose.Candidate, revision.Purpose);
        Assert.Null(snapshot.CommittedRevisionId);
    }

    [Fact]
    public async Task PromoteCredentialRevisionFlipsPurposeAndUpdatesTheConnectionPointer()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var revisionId = await workspace.BeginCredentialCandidateAsync(connectionId);
        var result = await workspace.PromoteCredentialRevisionAsync(connectionId, revisionId);

        Assert.Empty(result.SupersededRevisionIds);
        Assert.Equal(revisionId, result.Connection.CredentialRevisionId);
        Assert.True(result.Connection.HasCredential);
        Assert.False(result.Connection.CredentialRequiresRepair);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connectionId);
        var revision = Assert.Single(snapshot.Revisions);
        Assert.Equal(CredentialRevisionPurpose.Active, revision.Purpose);
        Assert.Equal(revisionId, snapshot.CommittedRevisionId);
    }

    [Fact]
    public async Task PromotingASecondRevisionSupersedesTheFirstAndDropsItFromTheLedger()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var firstRevisionId = await workspace.BeginCredentialCandidateAsync(connectionId);
        await workspace.PromoteCredentialRevisionAsync(connectionId, firstRevisionId);

        var secondRevisionId = await workspace.BeginCredentialCandidateAsync(connectionId);
        var result = await workspace.PromoteCredentialRevisionAsync(connectionId, secondRevisionId);

        Assert.Equal([firstRevisionId], result.SupersededRevisionIds);
        Assert.Equal(secondRevisionId, result.Connection.CredentialRevisionId);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connectionId);
        var revision = Assert.Single(snapshot.Revisions);
        Assert.Equal(secondRevisionId, revision.RevisionId);
    }

    [Fact]
    public async Task PromotingAnUnknownRevisionThrowsRecordNotFound()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.PromoteCredentialRevisionAsync(connectionId, "unknown-revision"));
    }

    [Fact]
    public async Task DiscardCredentialCandidateRemovesOnlyTheCandidateRow()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var activeRevisionId = await workspace.BeginCredentialCandidateAsync(connectionId);
        await workspace.PromoteCredentialRevisionAsync(connectionId, activeRevisionId);
        var candidateRevisionId = await workspace.BeginCredentialCandidateAsync(connectionId);

        await workspace.DiscardCredentialCandidateAsync(connectionId, candidateRevisionId);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connectionId);
        var revision = Assert.Single(snapshot.Revisions);
        Assert.Equal(activeRevisionId, revision.RevisionId);
    }

    [Fact]
    public async Task MarkCredentialRequiresRepairSetsTheFlagWithoutTouchingThePointerOrLedger()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var revisionId = await workspace.BeginCredentialCandidateAsync(connectionId);
        await workspace.PromoteCredentialRevisionAsync(connectionId, revisionId);

        var updated = await workspace.MarkCredentialRequiresRepairAsync(connectionId);

        Assert.True(updated.CredentialRequiresRepair);
        Assert.Equal(revisionId, updated.CredentialRevisionId);
        Assert.True(updated.HasCredential);
        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connectionId);
        Assert.Equal(revisionId, snapshot.CommittedRevisionId);
        Assert.Single(snapshot.Revisions);
    }

    [Fact]
    public async Task DeleteCredentialLedgerRowRemovesTheSpecifiedRevision()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var revisionId = await workspace.BeginCredentialCandidateAsync(connectionId);

        await workspace.DeleteCredentialLedgerRowAsync(connectionId, revisionId);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connectionId);
        Assert.Empty(snapshot.Revisions);
    }

    [Fact]
    public async Task PermanentlyDeletingAConnectionCascadesItsCredentialLedgerRows()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, workspace, connectionId) = await CreateHarnessAsync(temporary);
        await using var _ = workspace;

        var revisionId = await workspace.BeginCredentialCandidateAsync(connectionId);
        await workspace.PromoteCredentialRevisionAsync(connectionId, revisionId);
        await workspace.RecycleConnectionAsync(connectionId);

        await workspace.PermanentlyDeleteConnectionAsync(connectionId);

        var snapshots = await workspace.GetCredentialLedgerSnapshotAsync();
        Assert.DoesNotContain(snapshots, s => s.ConnectionId == connectionId);
    }
}
