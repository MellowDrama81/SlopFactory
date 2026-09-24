using System.Text.Json.Serialization;

namespace SlopFactory.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SplitNode), "split")]
[JsonDerivedType(typeof(TabGroupNode), "tabs")]
public abstract record DockNode;
public sealed record SplitNode(string Id, DockAxis Axis, double Ratio, DockNode First, DockNode Second) : DockNode;
public sealed record TabGroupNode(string Id, IReadOnlyList<DockPane> Panes, string ActiveId) : DockNode;
public enum DockAxis { Horizontal, Vertical }
public sealed record DockPane(string Id, string Title, string Icon, string Accent, string Kind, string? Context = null, string? Location = null, string? Owner = null, string? WorkflowId = null, Dictionary<string, string>? InputValues = null, string? OutputFolder = null, List<string>? OutputTags = null, string? PanelFolder = null);
public sealed record TabActivation(string GroupId, string PaneId);
public sealed record GenerationWorkflowSelection(string PaneId, string WorkflowId);
public sealed record GenerationInputChange(string PaneId, string Name, string Value);
public sealed record GenerationOutputFolderChange(string PaneId, string Folder);
public sealed record GenerationOutputTagsChange(string PaneId, List<string> Tags);
