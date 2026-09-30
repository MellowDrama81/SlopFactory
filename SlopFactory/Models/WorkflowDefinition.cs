namespace SlopFactory.Models;

/// <summary>Describes one image supplied by the user to a workflow.</summary>
public sealed record WorkflowImageInput(bool AcceptsMask = false, string? Purpose = null);

/// <summary>Describes the image-oriented inputs and result of a workflow.</summary>
public sealed record WorkflowCapabilities(
    bool RequiresMask,
    int MinImages,
    int MaxImages,
    IReadOnlyList<WorkflowImageInput>? ImageInputs = null,
    string? OutputImagePurpose = null,
    string? ImageGenerationModel = null);

public sealed record WorkflowDefinition(
    string Id,
    string DisplayName,
    string Description,
    string GraphFile,
    WorkflowCapabilities Capabilities,
    bool IsBuiltIn);
