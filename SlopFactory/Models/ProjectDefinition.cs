namespace SlopFactory.Models;

public sealed record ProjectDefinition(string FolderPath, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FolderPath : Name;
}
