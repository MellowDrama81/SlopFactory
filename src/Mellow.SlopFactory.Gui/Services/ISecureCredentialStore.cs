namespace Mellow.SlopFactory.Gui.Services;

public interface ISecureCredentialStore
{
    Task<string?> GetAsync(string libraryId, string connectionId);
    Task SetAsync(string libraryId, string connectionId, string value);
    Task RemoveAsync(string libraryId, string connectionId);
}
