namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// A test-only seam for simulating a crash at two points in <see cref="GenerationQueueService"/>'s
/// execution path that the existing technique (deleting a library's <c>media</c> directory to force a
/// real <see cref="IOException"/>) cannot safely reach: the SQLite reads
/// (<c>GetActiveModelsAsync</c>/<c>GetActiveConnectionsAsync</c>) performed while a job is
/// <c>Preparing</c>, and the post-commit async-remote-job cleanup step for a video generation. Both
/// share the same exclusively-locked SQLite connection the rest of a real test harness needs to keep
/// working, so forcing a real I/O failure there would either break unrelated library operations or
/// require tearing down the whole workspace. Production code always uses
/// <see cref="NullGenerationFaultInjector"/>, which does nothing.
/// </summary>
public interface IGenerationFaultInjector
{
    /// <summary>Called immediately before <c>GetActiveModelsAsync</c>/<c>GetActiveConnectionsAsync</c>
    /// run while a job is <c>Preparing</c>.</summary>
    Task BeforePrepareReadAsync(CancellationToken cancellationToken);

    /// <summary>Called immediately before each post-commit async-remote-job cleanup step (linking or
    /// deleting a completed video job's device-wide registry row) once the generation record itself
    /// has already committed successfully. A thrown exception here must never affect the already-
    /// committed outcome — this seam only proves that guarantee, not any new behavior.</summary>
    Task BeforePostCommitCleanupAsync(CancellationToken cancellationToken);
}

public sealed class NullGenerationFaultInjector : IGenerationFaultInjector
{
    public static readonly NullGenerationFaultInjector Instance = new();

    public Task BeforePrepareReadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task BeforePostCommitCleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
