using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

/// <summary>
/// A device-local, authenticated, versioned record of in-flight external export temporary objects,
/// so a crash between creating a temporary export object and committing or
/// cleaning it up leaves something a later <see cref="SweepAsync"/> can safely recover — never by
/// guessing, only by reverifying the exact identity that was journaled before the crash. Defined in
/// Core (no platform dependency) because the atomic-export code that calls it
/// (<c>LibraryWorkspace.ExportCoreAsync</c>) lives in Infrastructure, which cannot reference the MAUI
/// Essentials APIs (<c>SecureStorage</c>/<c>Preferences</c>) the real implementation needs — that
/// implementation lives in the Gui project instead and is threaded in as an optional collaborator,
/// exactly like <see cref="IConnectionRateLimitTracker"/>. A caller with no journal (e.g. every
/// existing test, and any workspace opened without one) simply doesn't get crash-recovery tracking
/// for its exports — the export itself still works exactly as before.
/// </summary>
public interface IExportCleanupJournal
{
    /// <summary>Durably records that a temporary export object is about to be created, before it
    /// exists. Returns the new entry's operation ID.</summary>
    Task<string> RecordPlannedAsync(ExportCleanupObjectType objectType, string parentPath, string opaqueName, string targetIdentity, CancellationToken cancellationToken = default);

    /// <summary>Marks a previously planned entry as durably created — called immediately after the
    /// temporary object's creation succeeds.</summary>
    Task ConfirmAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>Removes an entry once its temporary object has been committed (renamed to its final
    /// destination) or explicitly cleaned up. Safe to call for an unknown ID (no-op).</summary>
    Task RemoveAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>Recovers orphaned entries left behind by a crash. For each authenticated entry (an
    /// entry whose stored authentication cannot be verified is silently dropped from consideration —
    /// never acted on): a <see cref="ExportCleanupObjectType.LocalTempFile"/> whose target no longer
    /// exists is simply removed (nothing to clean up); one whose target exists and still matches the
    /// journaled identity exactly is deleted and then removed; one whose target exists but no longer
    /// matches (different type, or otherwise altered) is left in place, never deleted. A
    /// <see cref="ExportCleanupObjectType.AndroidDocumentUri"/> entry is always left in place — see
    /// this interface's own remarks on why. Returns every entry left in the journal after the sweep
    /// (i.e. every entry sweeping did not resolve), for diagnostics/display.</summary>
    Task<IReadOnlyList<ExportCleanupEntry>> SweepAsync(CancellationToken cancellationToken = default);
}
