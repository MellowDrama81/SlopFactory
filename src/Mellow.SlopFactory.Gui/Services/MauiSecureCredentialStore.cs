namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiSecureCredentialStore : ISecureCredentialStore
{
    public Task<string?> GetActiveAsync(string libraryId, string connectionId, string revisionId) => GetAsync(ActiveKey(libraryId, connectionId, revisionId));
    public Task SetActiveAsync(string libraryId, string connectionId, string revisionId, string value) => SecureStorage.Default.SetAsync(ActiveKey(libraryId, connectionId, revisionId), value);
    public Task RemoveActiveAsync(string libraryId, string connectionId, string revisionId)
    {
        _ = SecureStorage.Default.Remove(ActiveKey(libraryId, connectionId, revisionId));
        return Task.CompletedTask;
    }

    public Task<string?> GetCandidateAsync(string libraryId, string connectionId, string revisionId) => GetAsync(CandidateKey(libraryId, connectionId, revisionId));
    public Task SetCandidateAsync(string libraryId, string connectionId, string revisionId, string value) => SecureStorage.Default.SetAsync(CandidateKey(libraryId, connectionId, revisionId), value);
    public Task RemoveCandidateAsync(string libraryId, string connectionId, string revisionId)
    {
        _ = SecureStorage.Default.Remove(CandidateKey(libraryId, connectionId, revisionId));
        return Task.CompletedTask;
    }

    public Task<string?> GetLegacyAsync(string libraryId, string connectionId) => GetAsync(LegacyKey(libraryId, connectionId));
    public Task RemoveLegacyAsync(string libraryId, string connectionId)
    {
        _ = SecureStorage.Default.Remove(LegacyKey(libraryId, connectionId));
        return Task.CompletedTask;
    }

    private static async Task<string?> GetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string ActiveKey(string libraryId, string connectionId, string revisionId) => $"connection:{libraryId}:{connectionId}:{revisionId}";
    private static string CandidateKey(string libraryId, string connectionId, string revisionId) => $"connection-candidate:{libraryId}:{connectionId}:{revisionId}";
    private static string LegacyKey(string libraryId, string connectionId) => $"connection:{libraryId}:{connectionId}";
}
