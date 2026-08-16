using Mellow.SlopFactory.Gui.Services;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class DiagnosticsLoggerTests
{
    private sealed class FakeAppPreferenceStore : IAppPreferenceStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string ReadString(string key, string defaultValue) => _values.TryGetValue(key, out var value) ? value : defaultValue;
        public void WriteString(string key, string value) => _values[key] = value;
    }

    [Fact]
    public void LoggingAnEntryMakesItReadableAndClearRemovesEverything()
    {
        using var temporary = new TemporaryDirectory();
        var logger = new DiagnosticsLogger(temporary.Child("diagnostics"), new FakeAppPreferenceStore());

        logger.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow, "Generation.Completed", LocalRecordId: "record-1", HttpStatusCode: 200));
        logger.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow, "Generation.Failed", SanitizedError: "The connection is unavailable."));

        var entries = logger.ReadAll();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Generation.Completed", entries[0].OperationType);
        Assert.Equal("record-1", entries[0].LocalRecordId);
        Assert.Equal(200, entries[0].HttpStatusCode);
        Assert.Equal("The connection is unavailable.", entries[1].SanitizedError);

        logger.Clear();
        Assert.Empty(logger.ReadAll());
    }

    [Fact]
    public void EntriesOlderThan30DaysAreRemovedOnTheNextLogCall()
    {
        using var temporary = new TemporaryDirectory();
        var logger = new DiagnosticsLogger(temporary.Child("diagnostics"), new FakeAppPreferenceStore());
        logger.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow.AddDays(-31), "Old.Entry"));

        logger.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow, "New.Entry"));

        var entries = logger.ReadAll();
        var single = Assert.Single(entries);
        Assert.Equal("New.Entry", single.OperationType);
    }

    [Fact]
    public void OrdinaryLoggingUnderTheRealCapNeverEvictsAnything()
    {
        using var temporary = new TemporaryDirectory();
        var logger = new DiagnosticsLogger(temporary.Child("diagnostics"), new FakeAppPreferenceStore());
        for (var i = 0; i < 5; i++)
        {
            logger.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow.AddMinutes(i), $"Entry-{i}"));
        }

        Assert.Equal(5, logger.ReadAll().Count);
    }

    [Fact]
    public void OldestEntriesAreRemovedFirstOnceTheRollingCapIsExceeded()
    {
        using var temporary = new TemporaryDirectory();
        // An artificially tiny cap (a handful of entries' worth) makes eviction reachable without
        // writing anywhere near the real 50 MB in a unit test.
        var logger = new DiagnosticsLogger(temporary.Child("diagnostics"), new FakeAppPreferenceStore(), maxTotalBytesOverride: 300);
        for (var i = 0; i < 10; i++)
        {
            logger.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow.AddMinutes(i), $"Entry-{i}"));
        }

        var entries = logger.ReadAll();
        Assert.True(entries.Count < 10, "Expected the tiny cap to have evicted at least one entry.");
        Assert.DoesNotContain(entries, entry => entry.OperationType == "Entry-0");
        Assert.Contains(entries, entry => entry.OperationType == "Entry-9");
    }

    [Fact]
    public void ReadingBackAllFieldsRoundTripsExactly()
    {
        using var temporary = new TemporaryDirectory();
        var logger = new DiagnosticsLogger(temporary.Child("diagnostics"), new FakeAppPreferenceStore());
        var entry = new DiagnosticLogEntry(
            DateTimeOffset.UtcNow,
            "Connection.Test",
            ProviderType: "OpenAi",
            LocalRecordId: "conn-1",
            HttpStatusCode: 429,
            ProviderRequestId: "req-abc",
            RetryCount: 2,
            SanitizedError: "Rate limited.",
            DurationMs: 1234,
            IsVerbose: true,
            IsCrash: false);

        logger.Log(entry);

        Assert.Equal(entry, Assert.Single(logger.ReadAll()));
    }

    [Fact]
    public void EnablingVerboseActivatesItForApproximatelyOneHour()
    {
        var preferences = new FakeAppPreferenceStore();
        var logger = new DiagnosticsLogger("unused", preferences);

        logger.EnableVerbose();

        Assert.True(logger.VerboseEnabled);
        Assert.NotNull(logger.VerboseExpiresAt);
        var remaining = logger.VerboseExpiresAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(remaining.TotalMinutes, 55, 65);
    }

    [Fact]
    public void ReactivatingVerboseWhileAlreadyActiveDoesNotExtendItsDeadline()
    {
        var preferences = new FakeAppPreferenceStore();
        var logger = new DiagnosticsLogger("unused", preferences);
        var originalExpiry = DateTimeOffset.UtcNow.AddMinutes(30);
        preferences.WriteString("slopfactory.diagnostics.verboseexpiresat", originalExpiry.ToString("o"));

        logger.EnableVerbose();

        Assert.Equal(originalExpiry, logger.VerboseExpiresAt);
    }

    [Fact]
    public void VerboseIsDisabledOncePastItsExpiry()
    {
        var preferences = new FakeAppPreferenceStore();
        var logger = new DiagnosticsLogger("unused", preferences);
        preferences.WriteString("slopfactory.diagnostics.verboseexpiresat", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"));

        Assert.False(logger.VerboseEnabled);
    }

    [Fact]
    public void FirstSessionStartLeavesNoCrashMarkAndCreatesASessionMarker()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Child("diagnostics");
        var logger = new DiagnosticsLogger(directory, new FakeAppPreferenceStore());

        logger.MarkSessionStarted();

        Assert.False(logger.DidNotCloseNormallyLastSession);
        Assert.True(File.Exists(Path.Combine(directory, "session.marker")));
        Assert.Empty(logger.ReadAll());
    }

    [Fact]
    public void AMissingSessionEndMarkerIsDetectedAsAnUncleanShutdownOnTheNextStart()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Child("diagnostics");
        var firstRun = new DiagnosticsLogger(directory, new FakeAppPreferenceStore());
        firstRun.MarkSessionStarted();
        // No MarkSessionEndedNormally() call — simulates a crash/kill.

        var secondRun = new DiagnosticsLogger(directory, new FakeAppPreferenceStore());
        secondRun.MarkSessionStarted();

        Assert.True(secondRun.DidNotCloseNormallyLastSession);
        var crashEntry = Assert.Single(secondRun.ReadAll());
        Assert.True(crashEntry.IsCrash);
        Assert.Equal("Application.UncleanShutdownDetected", crashEntry.OperationType);
    }

    [Fact]
    public void AGracefulSessionEndPreventsTheNextStartFromDetectingACrash()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Child("diagnostics");
        var firstRun = new DiagnosticsLogger(directory, new FakeAppPreferenceStore());
        firstRun.MarkSessionStarted();
        firstRun.MarkSessionEndedNormally();

        var secondRun = new DiagnosticsLogger(directory, new FakeAppPreferenceStore());
        secondRun.MarkSessionStarted();

        Assert.False(secondRun.DidNotCloseNormallyLastSession);
        Assert.Empty(secondRun.ReadAll());
    }

    [Fact]
    public void DisableVerboseClearsAnActivePeriod()
    {
        var preferences = new FakeAppPreferenceStore();
        var logger = new DiagnosticsLogger("unused", preferences);
        logger.EnableVerbose();

        logger.DisableVerbose();

        Assert.False(logger.VerboseEnabled);
        Assert.Null(logger.VerboseExpiresAt);
    }
}
