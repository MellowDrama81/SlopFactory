using Mellow.SlopFactory.Domain;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class CostSummaryCalculatorTests
{
    private static GenerationRecord Record(string modelLabel, ProviderType providerType, GenerationMode mode, DateTimeOffset createdAt, double? actualCost, string? currency = "USD", GenerationStatus status = GenerationStatus.Completed, LibraryRecordState state = LibraryRecordState.Active) =>
        new("id-" + Guid.NewGuid().ToString("N"), "model-id", modelLabel, "provider-model-id", providerType, mode, "prompt", null, 1, status, null,
            "folder-id", createdAt, createdAt, [], ActualCost: actualCost, ActualCostCurrency: currency, State: state);

    [Fact]
    public void ApplyFiltersIncludesRecycledRecordsByDefaultBecauseRecyclingDoesNotUndoIncurredCost()
    {
        var active = Record("Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.10, state: LibraryRecordState.Active);
        var recycled = Record("Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.20, state: LibraryRecordState.Recycled);

        var included = CostSummaryCalculator.ApplyFilters([active, recycled]);
        Assert.Equal(2, included.Count);

        var excluded = CostSummaryCalculator.ApplyFilters([active, recycled], excludeRecycled: true);
        Assert.Equal(active.Id, Assert.Single(excluded).Id);
    }

    [Fact]
    public void ApplyFiltersExcludesRecordsWithNoReportedCost()
    {
        var withCost = Record("Video Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.25);
        var withoutCost = Record("Text Model", ProviderType.OpenAi, GenerationMode.Text, DateTimeOffset.UtcNow, null);

        var filtered = CostSummaryCalculator.ApplyFilters([withCost, withoutCost]);

        Assert.Equal(withCost.Id, Assert.Single(filtered).Id);
    }

    [Fact]
    public void ApplyFiltersNarrowsByProviderModeModelAndDateRange()
    {
        var target = Record("Video Model", ProviderType.OpenRouter, GenerationMode.Video, new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), 0.25);
        var wrongProvider = Record("Video Model", ProviderType.DeepInfra, GenerationMode.Video, new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), 0.10);
        var wrongMode = Record("Image Model", ProviderType.OpenRouter, GenerationMode.Image, new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), 0.05);
        var wrongModel = Record("Other Video Model", ProviderType.OpenRouter, GenerationMode.Video, new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), 0.30);
        var outsideDateRange = Record("Video Model", ProviderType.OpenRouter, GenerationMode.Video, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), 0.40);
        var all = new[] { target, wrongProvider, wrongMode, wrongModel, outsideDateRange };

        var filtered = CostSummaryCalculator.ApplyFilters(all,
            dateFromInclusive: new DateOnly(2026, 6, 1), dateToInclusive: new DateOnly(2026, 6, 30),
            providerType: ProviderType.OpenRouter, mode: GenerationMode.Video, modelLabel: "Video Model");

        Assert.Equal(target.Id, Assert.Single(filtered).Id);
    }

    [Fact]
    public void ApplyFiltersIncludesFailedAndPartiallyCompletedRecordsBecauseCostIsIndependentOfOutcome()
    {
        var failed = Record("Video Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.10, status: GenerationStatus.Failed);
        var partial = Record("Video Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.15, status: GenerationStatus.PartiallyCompleted);

        var filtered = CostSummaryCalculator.ApplyFilters([failed, partial]);

        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void GroupTotalsSumsByKeyAndKeepsDifferentCurrenciesSeparate()
    {
        var records = new[]
        {
            Record("Model A", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.25, "USD"),
            Record("Model A", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.30, "USD"),
            Record("Model A", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 1.00, "EUR"),
            Record("Model B", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.50, "USD"),
        };

        var groups = CostSummaryCalculator.GroupTotals(records, record => record.ModelLabel);

        Assert.Equal(3, groups.Count);
        var modelAUsd = Assert.Single(groups, g => g.Key == "Model A" && g.Currency == "USD");
        Assert.Equal(2, modelAUsd.RecordCount);
        Assert.Equal(0.55, modelAUsd.TotalCost, precision: 10);
        var modelAEur = Assert.Single(groups, g => g.Key == "Model A" && g.Currency == "EUR");
        Assert.Equal(1.00, modelAEur.TotalCost, precision: 10);
        var modelB = Assert.Single(groups, g => g.Key == "Model B");
        Assert.Equal(0.50, modelB.TotalCost, precision: 10);
    }

    [Fact]
    public void GroupTotalsOrdersDescendingByTotalCost()
    {
        var records = new[]
        {
            Record("Cheap Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.05),
            Record("Expensive Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 5.00),
            Record("Mid Model", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 1.00),
        };

        var groups = CostSummaryCalculator.GroupTotals(records, record => record.ModelLabel);

        Assert.Equal(["Expensive Model", "Mid Model", "Cheap Model"], groups.Select(g => g.Key).ToArray());
    }

    [Fact]
    public void GroupTotalsTreatsAMissingCurrencyAsAnUnknownGroupRatherThanCrashing()
    {
        var records = new[] { Record("Model A", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.25, currency: null) };

        var groups = CostSummaryCalculator.GroupTotals(records, record => record.ModelLabel);

        Assert.Equal("Unknown", Assert.Single(groups).Currency);
    }

    [Fact]
    public void GroupTotalsByCurrencyProducesOneGrandTotalPerCurrency()
    {
        var records = new[]
        {
            Record("Model A", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 0.25, "USD"),
            Record("Model B", ProviderType.DeepInfra, GenerationMode.Image, DateTimeOffset.UtcNow, 0.75, "USD"),
            Record("Model C", ProviderType.OpenRouter, GenerationMode.Video, DateTimeOffset.UtcNow, 2.00, "EUR"),
        };

        var groups = CostSummaryCalculator.GroupTotalsByCurrency(records);

        Assert.Equal(2, groups.Count);
        Assert.Equal(1.00, Assert.Single(groups, g => g.Currency == "USD").TotalCost, precision: 10);
        Assert.Equal(2.00, Assert.Single(groups, g => g.Currency == "EUR").TotalCost, precision: 10);
    }
}
