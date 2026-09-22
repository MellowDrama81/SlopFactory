using Android.App; using Android.Runtime;
namespace SlopFactory;
[Application] public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership) { protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp(); }
