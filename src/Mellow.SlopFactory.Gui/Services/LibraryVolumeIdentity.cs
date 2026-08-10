namespace Mellow.SlopFactory.Gui.Services;

using System.Runtime.InteropServices;
using System.Text;

public static class LibraryVolumeIdentity
{
    public static string? ForPath(string path)
    {
        try
        {
#if ANDROID
            var context = Android.App.Application.Context;
            var storage = context.GetSystemService(Android.Content.Context.StorageService) as Android.OS.Storage.StorageManager;
            var volume = storage?.GetStorageVolume(new Java.IO.File(Path.GetFullPath(path)));
            if (volume is not null) return volume.IsPrimary ? "android-volume:primary" : $"android-volume:{volume.Uuid ?? "unidentified-secondary"}";
#endif
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return null;
            var drive = new DriveInfo(root);
            if (OperatingSystem.IsWindows())
            {
                var volumeName = new StringBuilder(261);
                var fileSystemName = new StringBuilder(261);
                if (GetVolumeInformation(root, volumeName, volumeName.Capacity, out var serial, out _, out _, fileSystemName, fileSystemName.Capacity))
                {
                    return $"windows-volume:{serial:X8}:{fileSystemName}";
                }
            }
            return $"volume:{drive.DriveType}:{drive.DriveFormat}:{drive.VolumeLabel}:{root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()}";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }


#pragma warning disable CA1838 // GetVolumeInformationW requires writable character buffers.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
#pragma warning restore CA1838
}
