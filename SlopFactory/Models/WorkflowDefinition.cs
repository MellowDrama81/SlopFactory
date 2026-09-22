namespace SlopFactory.Models;

public sealed record WorkflowCapabilities(bool RequiresMask, int MinImages, int MaxImages);

public sealed record WorkflowDefinition(
    string Id,
    string DisplayName,
    string Description,
    string GraphFile,
    WorkflowCapabilities Capabilities,
    bool IsBuiltIn);
