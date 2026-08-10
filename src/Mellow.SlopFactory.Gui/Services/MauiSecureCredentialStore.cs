namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiSecureCredentialStore : ISecureCredentialStore
{
    public async Task<string?> GetAsync(string libraryId, string connectionId)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key(libraryId, connectionId)).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task SetAsync(string libraryId, string connectionId, string value) => SecureStorage.Default.SetAsync(Key(libraryId, connectionId), value);

    public Task RemoveAsync(string libraryId, string connectionId)
    {
        _ = SecureStorage.Default.Remove(Key(libraryId, connectionId));
        return Task.CompletedTask;
    }

    private static string Key(string libraryId, string connectionId) => $"connection:{libraryId}:{connectionId}";
}
