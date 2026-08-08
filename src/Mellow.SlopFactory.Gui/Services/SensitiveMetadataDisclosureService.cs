namespace Mellow.SlopFactory.Gui.Services;

public interface ISensitiveMetadataDisclosureService
{
    bool IsAcknowledged { get; }
    void Acknowledge();
}

public sealed class SensitiveMetadataDisclosureService : ISensitiveMetadataDisclosureService
{
    private const string PreferenceKey = "sensitive_metadata_disclosure_acknowledged";
    public bool IsAcknowledged => Preferences.Default.Get(PreferenceKey, false);
    public void Acknowledge() => Preferences.Default.Set(PreferenceKey, true);
}
