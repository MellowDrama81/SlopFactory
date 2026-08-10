using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public enum RememberedLibraryState { Available, Unavailable, Corrupt }

public sealed record RecentLibrary(string LibraryId, string DisplayName, string Path, DateTimeOffset LastOpenedAt, string? VolumeIdentity = null, RememberedLibraryState State = RememberedLibraryState.Available, string? FailureStage = null, string? DiagnosticId = null);

public interface ILibraryLocationService
{
    string DefaultPath { get; }
    bool IsAllowedPath(string path);
}

public interface IRecentLibraryService
{
    IReadOnlyList<RecentLibrary> GetAll();
    void RecordOpened(LibraryDescriptor descriptor);
    void RecordFailure(string path, string displayName, string? libraryId, RememberedLibraryState state, string failureStage, string diagnosticId);
    void ValidateNoOverlap(string candidatePath);
}

public static class PlatformRuntimeSupport
{
    public static string? GetUnsupportedMessage() => null;
}
