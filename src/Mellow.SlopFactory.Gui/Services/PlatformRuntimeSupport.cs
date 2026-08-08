using Mellow.SlopFactory.Application;

namespace Mellow.SlopFactory.Gui.Services;

internal static class PlatformRuntimeSupport
{
    public static string? GetUnsupportedMessage()
    {
#if WINDOWS
        return PlatformVersionPolicy.IsSupported(SupportedPlatform.Windows, Environment.OSVersion.Version)
            ? null
            : $"SlopFactory requires Windows 10 version 22H2 (build 19045) or Windows 11. This device is running Windows build {Environment.OSVersion.Version.Build}.";
#elif ANDROID
        return PlatformVersionPolicy.IsSupported(SupportedPlatform.Android, new Version((int)Android.OS.Build.VERSION.SdkInt, 0))
            ? null
            : $"SlopFactory requires Android 8.0 (API level 26) or later. This device reports API level {(int)Android.OS.Build.VERSION.SdkInt}.";
#else
        return "This platform is not supported by SlopFactory.";
#endif
    }
}
