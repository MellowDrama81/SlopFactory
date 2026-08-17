namespace Mellow.SlopFactory.Gui.Services;

/// <summary>Real, MAUI Essentials-backed <see cref="IExportJournalStorage"/>. Kept in its own file
/// (separate from <see cref="ExportCleanupJournalService"/> and the interfaces it implements) purely
/// so the service's own logic can be linked into the plain (non-MAUI) test project without dragging
/// in a <c>Preferences</c> reference that project has no package for.</summary>
internal sealed class PreferencesExportJournalStorage : IExportJournalStorage
{
    private const string PreferenceKey = "export_cleanup_journal_v1";
    public string GetJournalJson() => Preferences.Default.Get(PreferenceKey, string.Empty);
    public void SetJournalJson(string json) => Preferences.Default.Set(PreferenceKey, json);
}

/// <summary>Real, MAUI Essentials-backed <see cref="IExportJournalSecretStore"/>. See
/// <see cref="PreferencesExportJournalStorage"/>'s remarks for why this is a separate file.</summary>
internal sealed class SecureStorageExportJournalSecretStore : IExportJournalSecretStore
{
    private const string SecretKey = "export-journal-secret";
    public Task<string?> GetSecretAsync() => SecureStorage.Default.GetAsync(SecretKey);
    public Task SetSecretAsync(string value) => SecureStorage.Default.SetAsync(SecretKey, value);
}
