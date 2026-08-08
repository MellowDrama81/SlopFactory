namespace Mellow.SlopFactory.Application;

public enum SupportedPlatform
{
    Windows,
    Android
}

public static class PlatformVersionPolicy
{
    public static readonly Version MinimumWindowsVersion = new(10, 0, 19045);
    public const int MinimumAndroidApiLevel = 26;

    public static bool IsSupported(SupportedPlatform platform, Version version) => platform switch
    {
        SupportedPlatform.Windows => version >= MinimumWindowsVersion,
        SupportedPlatform.Android => version.Major >= MinimumAndroidApiLevel,
        _ => false
    };
}
