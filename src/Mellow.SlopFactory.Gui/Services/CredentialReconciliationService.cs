using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class CredentialReconciliationService
{
    private readonly AppLibraryState _libraries;
    private readonly ISecureCredentialStore _credentials;
    private readonly object _gate = new();
    private string? _lastReconciledLibraryId;
    private bool _started;

    public CredentialReconciliationService(AppLibraryState libraries, ISecureCredentialStore credentials)
    {
        _libraries = libraries;
        _credentials = credentials;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _libraries.Changed += OnLibraryChanged;
        _ = ReconcileIfNeededAsync();
    }

    private void OnLibraryChanged(object? sender, EventArgs args) => _ = ReconcileIfNeededAsync();

    private async Task ReconcileIfNeededAsync()
    {
        var workspace = _libraries.Workspace;
        if (workspace is null) return;
        var libraryId = workspace.Descriptor.LibraryId;
        lock (_gate)
        {
            if (_lastReconciledLibraryId == libraryId) return;
            _lastReconciledLibraryId = libraryId;
        }
        try
        {
            await ReconcileAsync(workspace, libraryId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
        {
            lock (_gate) { if (_lastReconciledLibraryId == libraryId) _lastReconciledLibraryId = null; }
        }
    }

    internal async Task ReconcileAsync(ILibraryWorkspace workspace, string libraryId)
    {
        var snapshots = await workspace.GetCredentialLedgerSnapshotAsync().ConfigureAwait(false);
        foreach (var snapshot in snapshots)
        {
            foreach (var candidate in snapshot.Revisions.Where(revision => revision.Purpose == CredentialRevisionPurpose.Candidate))
            {
                await workspace.DeleteCredentialLedgerRowAsync(snapshot.ConnectionId, candidate.RevisionId).ConfigureAwait(false);
                await _credentials.RemoveCandidateAsync(libraryId, snapshot.ConnectionId, candidate.RevisionId).ConfigureAwait(false);
            }

            var hasActiveRevision = snapshot.Revisions.Any(revision => revision.Purpose == CredentialRevisionPurpose.Active);
            if (snapshot.HasCredential && snapshot.CommittedRevisionId is null && !hasActiveRevision)
            {
                await AdoptLegacyCredentialAsync(workspace, libraryId, snapshot.ConnectionId).ConfigureAwait(false);
                continue;
            }

            if (snapshot.CommittedRevisionId is { } committedRevisionId)
            {
                var activeRow = snapshot.Revisions.FirstOrDefault(revision => revision.Purpose == CredentialRevisionPurpose.Active && revision.RevisionId == committedRevisionId);
                var activeValue = activeRow is null ? null : await _credentials.GetActiveAsync(libraryId, snapshot.ConnectionId, committedRevisionId).ConfigureAwait(false);
                if (activeRow is null || activeValue is null)
                {
                    await workspace.MarkCredentialRequiresRepairAsync(snapshot.ConnectionId).ConfigureAwait(false);
                    continue;
                }

                foreach (var superseded in snapshot.Revisions.Where(revision => revision.Purpose == CredentialRevisionPurpose.Active && revision.RevisionId != committedRevisionId))
                {
                    await workspace.DeleteCredentialLedgerRowAsync(snapshot.ConnectionId, superseded.RevisionId).ConfigureAwait(false);
                    await _credentials.RemoveActiveAsync(libraryId, snapshot.ConnectionId, superseded.RevisionId).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task AdoptLegacyCredentialAsync(ILibraryWorkspace workspace, string libraryId, string connectionId)
    {
        var legacyValue = await _credentials.GetLegacyAsync(libraryId, connectionId).ConfigureAwait(false);
        if (legacyValue is null)
        {
            await workspace.MarkCredentialRequiresRepairAsync(connectionId).ConfigureAwait(false);
            return;
        }

        var revisionId = await workspace.BeginCredentialCandidateAsync(connectionId).ConfigureAwait(false);
        await _credentials.SetActiveAsync(libraryId, connectionId, revisionId, legacyValue).ConfigureAwait(false);
        var verified = await _credentials.GetActiveAsync(libraryId, connectionId, revisionId).ConfigureAwait(false);
        if (verified != legacyValue)
        {
            await workspace.DiscardCredentialCandidateAsync(connectionId, revisionId).ConfigureAwait(false);
            await _credentials.RemoveActiveAsync(libraryId, connectionId, revisionId).ConfigureAwait(false);
            await workspace.MarkCredentialRequiresRepairAsync(connectionId).ConfigureAwait(false);
            return;
        }

        await workspace.PromoteCredentialRevisionAsync(connectionId, revisionId).ConfigureAwait(false);
        await _credentials.RemoveLegacyAsync(libraryId, connectionId).ConfigureAwait(false);
    }
}
