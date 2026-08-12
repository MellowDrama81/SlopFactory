namespace Mellow.SlopFactory.Gui.Services;

public interface INotificationService
{
    Task<bool> RequestPermissionAsync();
    void Show(string recordId, string title, string body);
    event EventHandler<string>? Tapped;
}
