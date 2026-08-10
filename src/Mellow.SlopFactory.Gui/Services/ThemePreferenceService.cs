namespace Mellow.SlopFactory.Gui.Services;

public enum ThemePreference { System, Light, Dark }

public interface IThemePreferenceService
{
    ThemePreference Preference { get; }
    string CssClass { get; }
    event EventHandler? Changed;
    void SetPreference(ThemePreference preference);
}

internal sealed class ThemePreferenceService : IThemePreferenceService
{
    private const string PreferenceKey = "slopfactory.theme";
    public ThemePreferenceService()
    {
        Preference = Enum.TryParse<ThemePreference>(Preferences.Default.Get(PreferenceKey, nameof(ThemePreference.System)), ignoreCase: true, out var preference)
            ? preference
            : ThemePreference.System;
    }
    public ThemePreference Preference { get; private set; }
    public string CssClass => Preference switch { ThemePreference.Light => "theme-light", ThemePreference.Dark => "theme-dark", _ => "theme-system" };
    public event EventHandler? Changed;
    public void SetPreference(ThemePreference preference)
    {
        if (Preference == preference) return;
        Preference = preference;
        Preferences.Default.Set(PreferenceKey, preference.ToString());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
