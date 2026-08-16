namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// One diagnostic log entry. Deliberately a closed set of narrow, structured fields rather than a
/// free-text message — plan.md:171-176 prohibits API keys, authorization headers, raw or improved
/// prompts, system instructions, prompt-improvement guidance, source/generated-file contents, signed
/// result URLs, provider moderation categories/descriptions, and sensitive user-metadata values from
/// ever appearing in diagnostics. A structured record with no arbitrary-text field makes most of
/// that impossible to violate by construction rather than relying on every call site remembering to
/// sanitize a string; <see cref="SanitizedError"/> is the one free-text field plan.md permits
/// ("sanitized errors" only), and callers are still responsible for not passing raw provider/user
/// content through it.
/// </summary>
public sealed record DiagnosticLogEntry(
    DateTimeOffset Timestamp,
    string OperationType,
    string? ProviderType = null,
    string? LocalRecordId = null,
    int? HttpStatusCode = null,
    string? ProviderRequestId = null,
    int? RetryCount = null,
    string? SanitizedError = null,
    long? DurationMs = null,
    bool IsVerbose = false,
    bool IsCrash = false);

public interface IDiagnosticsLogger
{
    /// <summary>Appends an entry, then enforces the 30-day age limit and the 50 MB device-wide
    /// rolling cap (plan.md:167-170) — whichever removes an entry first, applied together on every
    /// write rather than on a separate schedule.</summary>
    void Log(DiagnosticLogEntry entry);

    /// <summary>Every currently retained entry, oldest first.</summary>
    IReadOnlyList<DiagnosticLogEntry> ReadAll();

    /// <summary>Clears every retained entry (plan.md:178's "view, clear and export").</summary>
    void Clear();

    /// <summary>True while verbose logging is active and not yet expired (plan.md:180-181).</summary>
    bool VerboseEnabled { get; }

    /// <summary>When the current verbose period ends, or null if verbose logging is off. Persisted
    /// so the expiry survives an application restart per plan.md:181.</summary>
    DateTimeOffset? VerboseExpiresAt { get; }

    /// <summary>Activates verbose logging for exactly one hour from now (plan.md:180-181) —
    /// re-activating does not extend an already-running period past its original deadline through
    /// activity, matching "revert ... without extending the deadline through activity."</summary>
    void EnableVerbose();

    void DisableVerbose();

    /// <summary>
    /// True once per process lifetime, computed the first time <see cref="MarkSessionStarted"/> is
    /// called: whether the previous session's marker was still present, meaning it never reached a
    /// graceful shutdown (plan.md:184-185). Call <see cref="MarkSessionStarted"/> once at startup
    /// before reading this.
    /// </summary>
    bool DidNotCloseNormallyLastSession { get; }

    /// <summary>
    /// Call once at application startup, before anything else touches this logger. Detects whether
    /// the previous session's marker is still present (crash/kill, never a graceful exit) — if so,
    /// logs a crash entry (<see cref="DiagnosticLogEntry.IsCrash"/>) and sets
    /// <see cref="DidNotCloseNormallyLastSession"/> — then writes a fresh marker for this session.
    /// </summary>
    void MarkSessionStarted();

    /// <summary>Call from the app's own graceful-exit path once it is genuinely about to terminate
    /// normally (plan.md:184's crash detection only fires when this was never called last time).</summary>
    void MarkSessionEndedNormally();
}
