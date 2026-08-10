namespace Mellow.SlopFactory.Gui.Services;

public interface IAppPreferenceStore
{
    string ReadString(string key, string defaultValue);
    void WriteString(string key, string value);
}
