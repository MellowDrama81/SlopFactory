using System.Text.Json;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public static class IntegrityReportExporter
{
    private const string Format = "slopfactory.integrity-report/v1";

    public static string SerializeDefault(LibraryIntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var export = new DefaultIntegrityReportExport(
            Format,
            report.LibraryId,
            report.SchemaVersion,
            report.StartedAt,
            report.FinishedAt,
            report.IsComplete,
            report.WasCancelled,
            report.Findings.Select(finding => new DefaultIntegrityFindingExport(
                finding.Kind.ToString(),
                finding.RecordId,
                finding.ExpectedByteSize,
                finding.ActualByteSize,
                finding.Summary)).ToArray());
        return JsonSerializer.Serialize(export, SerializerOptions);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record DefaultIntegrityReportExport(
        string Format,
        string LibraryId,
        int SchemaVersion,
        DateTimeOffset StartedAt,
        DateTimeOffset FinishedAt,
        bool IsComplete,
        bool WasCancelled,
        IReadOnlyList<DefaultIntegrityFindingExport> Findings);

    private sealed record DefaultIntegrityFindingExport(
        string Category,
        string? RecordId,
        long? ExpectedByteSize,
        long? ActualByteSize,
        string Summary);
}
