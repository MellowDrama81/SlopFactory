namespace SlopFactory.Models;

public abstract record DockNode;
public sealed record SplitNode(string Id, DockAxis Axis, double Ratio, DockNode First, DockNode Second) : DockNode;
public sealed record TabGroupNode(string Id, IReadOnlyList<DockPanel> Panels, string ActiveId) : DockNode;
public enum DockAxis { Horizontal, Vertical }
public sealed record DockPanel(string Id, string Title, string Icon, string Accent, string Kind, string? Context = null);
public sealed record TabActivation(string GroupId, string PanelId);
