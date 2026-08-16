namespace Mellow.SlopFactory.Gui.Services;

/// <summary>Device-wide (not per-library) folder for temporarily staging a provider result that
/// completed while its destination library's volume was disconnected (plan.md:323-324). Abstracted
/// for testability the same way as the other MAUI-only platform services in this folder.</summary>
public interface IRecoveryStagingPathProvider
{
    string StagingDirectory { get; }
}
