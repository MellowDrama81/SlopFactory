using Mellow.SlopFactory.Domain;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class LiveProviderTestSettingsTests
{
    [Fact]
    public void DisabledConfigurationSkipsBeforeAnyCredentialOrNetworkUse()
    {
        var settings = LiveProviderTestSettings.FromEnvironment(_ => null);

        var exception = Assert.Throws<Xunit.SkipException>(() => settings.RequireDiscoveryRun());

        Assert.Contains(LiveProviderTestSettings.EnableVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledConfigurationRequiresAPositiveBudgetBeforeAnyRun()
    {
        var values = new Dictionary<string, string?>
        {
            [LiveProviderTestSettings.EnableVariable] = "true",
            [LiveProviderTestSettings.BudgetVariable] = "0",
            ["SLOPFACTORY_LIVE_OPENAI_API_KEY"] = "fixture-key"
        };
        var settings = LiveProviderTestSettings.FromEnvironment(name => values.TryGetValue(name, out var value) ? value : null);

        var exception = Assert.Throws<Xunit.SkipException>(() => settings.RequireDiscoveryRun());

        Assert.Contains(LiveProviderTestSettings.BudgetVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledBudgetedConfigurationExposesOnlyConfiguredProviders()
    {
        var values = new Dictionary<string, string?>
        {
            [LiveProviderTestSettings.EnableVariable] = "TRUE",
            [LiveProviderTestSettings.BudgetVariable] = "1.50",
            ["SLOPFACTORY_LIVE_OPENROUTER_API_KEY"] = "fixture-key"
        };
        var settings = LiveProviderTestSettings.FromEnvironment(name => values.TryGetValue(name, out var value) ? value : null);

        settings.RequireDiscoveryRun();

        var connection = Assert.Single(settings.ConfiguredConnections);
        Assert.Equal(ProviderType.OpenRouter, connection.Key);
        Assert.Equal("https://openrouter.ai/api/v1", connection.Value.BaseUrl);
    }

    [Fact]
    public void BillableRunSkipsWhenItsMaximumCostExceedsTheApprovedBudget()
    {
        var values = new Dictionary<string, string?>
        {
            [LiveProviderTestSettings.EnableVariable] = "true",
            [LiveProviderTestSettings.BudgetVariable] = "0.10",
            ["SLOPFACTORY_LIVE_OPENAI_API_KEY"] = "fixture-key"
        };
        var settings = LiveProviderTestSettings.FromEnvironment(name => values.TryGetValue(name, out var value) ? value : null);

        var exception = Assert.Throws<Xunit.SkipException>(() => settings.RequireBillableRun(0.11m));

        Assert.Contains("no billable request was sent", exception.Message, StringComparison.Ordinal);
    }
}
