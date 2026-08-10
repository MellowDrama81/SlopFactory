using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public static class ContentActionPolicy
{
    private static readonly HashSet<string> ActiveMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-msdownload", "application/x-dosexec", "application/x-sh", "application/x-bat",
        "application/vnd.microsoft.portable-executable", "application/x-executable", "text/x-shellscript", "text/x-script"
    };

    private static readonly HashSet<string> PotentiallyActiveMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "text/html", "application/xhtml+xml", "image/svg+xml",
        "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint", "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/x-ole-storage"
    };

    public static bool CanUseManagedContent(FileRecord file) =>
        file.State == LibraryRecordState.Active && file.ContentState is FileContentState.Healthy or FileContentState.Replaced;

    public static ExternalOpenSafety GetExternalOpenSafety(FileRecord file)
    {
        if (!CanUseManagedContent(file)) return ExternalOpenSafety.BlockedUnavailableContent;
        if (ActiveMediaTypes.Contains(file.MediaType)) return ExternalOpenSafety.BlockedActiveContent;
        return PotentiallyActiveMediaTypes.Contains(file.MediaType)
            ? ExternalOpenSafety.RequiresWarning
            : ExternalOpenSafety.Allowed;
    }
}
