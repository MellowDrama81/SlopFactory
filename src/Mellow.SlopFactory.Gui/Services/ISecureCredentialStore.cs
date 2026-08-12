namespace Mellow.SlopFactory.Gui.Services;

public interface ISecureCredentialStore
{
    Task<string?> GetActiveAsync(string libraryId, string connectionId, string revisionId);
    Task SetActiveAsync(string libraryId, string connectionId, string revisionId, string value);
    Task RemoveActiveAsync(string libraryId, string connectionId, string revisionId);
    Task<string?> GetCandidateAsync(string libraryId, string connectionId, string revisionId);
    Task SetCandidateAsync(string libraryId, string connectionId, string revisionId, string value);
    Task RemoveCandidateAsync(string libraryId, string connectionId, string revisionId);
    Task<string?> GetLegacyAsync(string libraryId, string connectionId);
    Task RemoveLegacyAsync(string libraryId, string connectionId);
}
