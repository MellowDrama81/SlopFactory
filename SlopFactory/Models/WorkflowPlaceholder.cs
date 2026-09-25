namespace SlopFactory.Models;

public sealed record WorkflowPlaceholder(string Name, string Type)
{
    public bool IsSeed => Type.Equals("seed", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("SEED", StringComparison.OrdinalIgnoreCase);
    public bool IsMaskedImage => Type.Equals("image", StringComparison.OrdinalIgnoreCase) &&
        Name.Contains("MASKED", StringComparison.OrdinalIgnoreCase);

    public string Label => Name switch
    {
        "PROMPT" => "Prompt",
        "UPLOADED_IMAGE_FILENAME" => "Image 1",
        "UPLOADED_MASKED_IMAGE_FILENAME" => "Masked image",
        _ when Name.StartsWith("UPLOADED_IMAGE_FILENAME_", StringComparison.Ordinal) =>
            $"Image {Name["UPLOADED_IMAGE_FILENAME_".Length..]}",
        _ => string.Join(' ', Name.Split('_')).ToLowerInvariant()
    };
}
