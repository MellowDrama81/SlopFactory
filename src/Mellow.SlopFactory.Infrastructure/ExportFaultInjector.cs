namespace Mellow.SlopFactory.Infrastructure;

/// <summary>
/// A test-only seam for simulating a crash at a specific point in the atomic export write path
/// (<c>LibraryWorkspace</c>'s shared temp-then-rename-then-journal helper), so tests can prove the
/// export cleanup journal recovers correctly regardless of exactly where the process died. Pure C#
/// (no MAUI dependency) since it lives entirely in Infrastructure — production code always uses
/// <see cref="Null"/>, which does nothing.
/// </summary>
internal interface IExportFaultInjector
{
    /// <summary>Called immediately before the temporary export file is created.</summary>
    Task BeforeTempCreationAsync(CancellationToken cancellationToken);

    /// <summary>Called immediately before the atomic rename that commits the temp file to its final
    /// destination.</summary>
    Task BeforeAtomicCommitAsync(CancellationToken cancellationToken);

    /// <summary>Called immediately before the journal entry for a just-committed export is
    /// removed.</summary>
    Task BeforeJournalRemovalAsync(CancellationToken cancellationToken);
}

internal sealed class NullExportFaultInjector : IExportFaultInjector
{
    public static readonly NullExportFaultInjector Instance = new();

    public Task BeforeTempCreationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task BeforeAtomicCommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task BeforeJournalRemovalAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
