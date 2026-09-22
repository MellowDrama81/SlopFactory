namespace SlopFactory.Services;

public sealed class ComfyConnectionSettings
{
    private const string ServerUrlKey = "comfy.connection.serverUrl";
    private const string ApiKeyKey = "comfy.connection.apiKey";
    private const string LegacyServerUrlKey = "comfy.connection.selfHostedUrl";
    private const string LegacyApiKeyKey = "comfy.connection.cloudApiKey";

    public string ServerUrl { get; set; } = "http://127.0.0.1:8188";
    public string ApiKey { get; set; } = string.Empty;

    public async Task LoadAsync()
    {
        ServerUrl = Preferences.Default.Get(ServerUrlKey,
            Preferences.Default.Get(LegacyServerUrlKey, ServerUrl));

        try
        {
            ApiKey = await SecureStorage.Default.GetAsync(ApiKeyKey)
                ?? await SecureStorage.Default.GetAsync(LegacyApiKeyKey)
                ?? string.Empty;
        }
        catch (Exception)
        {
            // Some platforms may not offer secure storage until the app is fully provisioned.
            ApiKey = string.Empty;
        }
    }

    public async Task SaveAsync()
    {
        Preferences.Default.Set(ServerUrlKey, ServerUrl.Trim());

        if (string.IsNullOrWhiteSpace(ApiKey))
            SecureStorage.Default.Remove(ApiKeyKey);
        else
            await SecureStorage.Default.SetAsync(ApiKeyKey, ApiKey.Trim());
    }
}
