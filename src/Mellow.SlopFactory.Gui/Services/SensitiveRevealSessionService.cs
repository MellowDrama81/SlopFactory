namespace Mellow.SlopFactory.Gui.Services;

public interface ISensitiveRevealSessionService
{
    bool IsRevealed(string metadataId);
    void Toggle(string metadataId);
    void Clear();
}

public sealed class SensitiveRevealSessionService : ISensitiveRevealSessionService, IDisposable
{
    private readonly AppLibraryState _libraries;
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);
    private string? _libraryId;

    public SensitiveRevealSessionService(AppLibraryState libraries)
    {
        _libraries = libraries;
        _libraryId = libraries.Workspace?.Descriptor.LibraryId;
        libraries.Changed += OnLibraryChanged;
    }

    public bool IsRevealed(string metadataId) => _revealed.Contains(metadataId);
    public void Toggle(string metadataId) { if (!_revealed.Add(metadataId)) _revealed.Remove(metadataId); }
    public void Clear() => _revealed.Clear();

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        var current = _libraries.Workspace?.Descriptor.LibraryId;
        if (!string.Equals(current, _libraryId, StringComparison.Ordinal) || current is null) _revealed.Clear();
        _libraryId = current;
    }

    public void Dispose() => _libraries.Changed -= OnLibraryChanged;
}
