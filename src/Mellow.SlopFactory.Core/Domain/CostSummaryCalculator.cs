namespace Mellow.SlopFactory.Domain;

/// <summary>One grouping key's aggregated provider-reported cost. Grouped separately per currency
/// rather than assuming a single one, since nothing prevents two different providers or connections
/// reporting cost in different currencies from appearing in the same library.</summary>
public sealed record CostSummaryGroup(string Key, string Currency, int RecordCount, double TotalCost);

/// <summary>
/// Pure aggregation over already-loaded generation history, mirroring the fully client-side
/// filtering convention <c>GenerationHistory.razor</c> already uses (no server-side query). Kept
/// separate from the Razor page specifically so the aggregation logic itself is unit-testable —
/// this codebase has no Blazor component test harness, so anything with real behavior worth
/// verifying belongs in a plain class rather than a page's code-behind.
/// </summary>
public static class CostSummaryCalculator
{
    /// <summary>Records with a known provider-reported cost, narrowed by the same filter
    /// dimensions <c>GenerationHistory.razor</c> already exposes (status is intentionally omitted —
    /// a cost summary cares what was spent regardless of whether the run fully succeeded).
    /// Recycled records are included by default (<paramref name="excludeRecycled"/> is false) per
    /// plan.md:1615 — "recycling does not undo incurred usage" — with an opt-out filter, not an
    /// opt-in one, so a recycled generation's real cost is never silently dropped from the total
    /// unless the user specifically asks to see only active history.</summary>
    public static IReadOnlyList<GenerationRecord> ApplyFilters(
        IReadOnlyList<GenerationRecord> records,
        DateOnly? dateFromInclusive = null,
        DateOnly? dateToInclusive = null,
        ProviderType? providerType = null,
        GenerationMode? mode = null,
        string? modelLabel = null,
        bool excludeRecycled = false)
    {
        return records
            .Where(record => record.ActualCost is not null)
            .Where(record => !excludeRecycled || record.State != LibraryRecordState.Recycled)
            .Where(record => dateFromInclusive is not { } from || DateOnly.FromDateTime(record.CreatedAt.ToLocalTime().DateTime) >= from)
            .Where(record => dateToInclusive is not { } to || DateOnly.FromDateTime(record.CreatedAt.ToLocalTime().DateTime) <= to)
            .Where(record => providerType is null || record.ProviderType == providerType.Value)
            .Where(record => mode is null || record.Mode == mode.Value)
            .Where(record => string.IsNullOrEmpty(modelLabel) || string.Equals(record.ModelLabel, modelLabel, StringComparison.Ordinal))
            .ToArray();
    }

    public static IReadOnlyList<CostSummaryGroup> GroupTotals(IReadOnlyList<GenerationRecord> records, Func<GenerationRecord, string> keySelector)
    {
        return records
            .GroupBy(record => (Key: keySelector(record), Currency: record.ActualCostCurrency ?? "Unknown"))
            .Select(group => new CostSummaryGroup(group.Key.Key, group.Key.Currency, group.Count(), group.Sum(record => record.ActualCost ?? 0)))
            .OrderByDescending(group => group.TotalCost)
            .ToArray();
    }

    /// <summary>Grand totals per currency — kept separate rather than summed together, since adding
    /// amounts in different currencies would silently produce a meaningless number.</summary>
    public static IReadOnlyList<CostSummaryGroup> GroupTotalsByCurrency(IReadOnlyList<GenerationRecord> records) =>
        GroupTotals(records, _ => "Total");
}
