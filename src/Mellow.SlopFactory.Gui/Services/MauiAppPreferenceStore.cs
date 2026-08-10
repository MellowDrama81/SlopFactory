namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiAppPreferenceStore : IAppPreferenceStore
{
    public string ReadString(string key, string defaultValue) => Preferences.Default.Get(key, defaultValue);
    public void WriteString(string key, string value) => Preferences.Default.Set(key, value);
}
