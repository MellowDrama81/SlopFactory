using System.Text.Json.Serialization;

namespace SlopFactory.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SplitNode), "split")]
[JsonDerivedType(typeof(TabGroupNode), "tabs")]
public abstract record DockNode;
public sealed record SplitNode(string Id, DockAxis Axis, double Ratio, DockNode First, DockNode Second) : DockNode;
public sealed record TabGroupNode(string Id, IReadOnlyList<DockPanel> Panels, string ActiveId) : DockNode;
public enum DockAxis { Horizontal, Vertical }
public sealed record DockPanel(string Id, string Title, string Icon, string Accent, string Kind, string? Context = null, string? Location = null, string? Owner = null, string? WorkflowId = null, Dictionary<string, string>? InputValues = null);
public sealed record TabActivation(string GroupId, string PanelId);
public sealed record GenerationWorkflowSelection(string PanelId, string WorkflowId);
public sealed record GenerationInputChange(string PanelId, string Name, string Value);
