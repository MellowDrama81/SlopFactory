namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiRecoveryStagingPathProvider : IRecoveryStagingPathProvider
{
    public string StagingDirectory => Path.Combine(FileSystem.Current.AppDataDirectory, "recovery-staging");
}
