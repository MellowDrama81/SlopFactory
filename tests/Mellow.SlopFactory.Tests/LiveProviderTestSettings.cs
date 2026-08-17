using System.Globalization;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Explicit opt-in configuration for billable, live-provider smoke tests. It is deliberately kept
/// in the test project: production code never reads these environment variables or makes a
/// provider request without a user submission.
/// </summary>
internal sealed class LiveProviderTestSettings
{
    public const string EnableVariable = "SLOPFACTORY_LIVE_PROVIDER_TESTS";
    public const string BudgetVariable = "SLOPFACTORY_LIVE_TEST_BUDGET_USD";

    private readonly IReadOnlyDictionary<ProviderType, LiveProviderConnection> _connections;

    private LiveProviderTestSettings(bool enabled, decimal? budgetUsd, IReadOnlyDictionary<ProviderType, LiveProviderConnection> connections)
    {
        Enabled = enabled;
        BudgetUsd = budgetUsd;
        _connections = connections;
    }

    public bool Enabled { get; }

    public decimal? BudgetUsd { get; }

    public IEnumerable<KeyValuePair<ProviderType, LiveProviderConnection>> ConfiguredConnections => _connections;

    public bool CanRunDiscovery => Enabled && BudgetUsd is > 0 && _connections.Count > 0;

    public static LiveProviderTestSettings FromEnvironment(Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var enabled = string.Equals(readEnvironment(EnableVariable), "true", StringComparison.OrdinalIgnoreCase);
        var budgetText = readEnvironment(BudgetVariable);
        var budget = decimal.TryParse(budgetText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedBudget)
            ? parsedBudget
            : (decimal?)null;

        var connections = new Dictionary<ProviderType, LiveProviderConnection>();
        AddIfCredentialed(connections, ProviderType.OpenAi, "https://api.openai.com/v1",
            readEnvironment("SLOPFACTORY_LIVE_OPENAI_API_KEY"));
        AddIfCredentialed(connections, ProviderType.OpenRouter, "https://openrouter.ai/api/v1",
            readEnvironment("SLOPFACTORY_LIVE_OPENROUTER_API_KEY"));
        AddIfCredentialed(connections, ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai",
            readEnvironment("SLOPFACTORY_LIVE_DEEPINFRA_API_KEY"));

        var genericBaseUrl = readEnvironment("SLOPFACTORY_LIVE_GENERIC_BASE_URL");
        var genericApiKey = readEnvironment("SLOPFACTORY_LIVE_GENERIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(genericBaseUrl) && !string.IsNullOrWhiteSpace(genericApiKey))
        {
            AddIfCredentialed(connections, ProviderType.GenericOpenAiCompatible, genericBaseUrl, genericApiKey);
        }

        return new LiveProviderTestSettings(enabled, budget, connections);
    }

    public void RequireDiscoveryRun()
    {
        if (!Enabled) throw Xunit.Sdk.SkipException.ForSkip($"Set {EnableVariable}=true to run live-provider tests.");
        if (BudgetUsd is not > 0) throw Xunit.Sdk.SkipException.ForSkip($"Set {BudgetVariable} to a positive USD budget before running live-provider tests.");
        if (_connections.Count == 0) throw Xunit.Sdk.SkipException.ForSkip("No live-provider credentials are configured; no live request was sent.");
    }

    public void RequireBillableRun(decimal maximumCostUsd)
    {
        RequireDiscoveryRun();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCostUsd);
        if (BudgetUsd < maximumCostUsd)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"The configured {BudgetVariable} is lower than the required maximum of {maximumCostUsd.ToString(CultureInfo.InvariantCulture)} USD; no billable request was sent.");
        }
    }

    private static void AddIfCredentialed(Dictionary<ProviderType, LiveProviderConnection> connections, ProviderType providerType, string baseUrl, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            connections.Add(providerType, new LiveProviderConnection(baseUrl, apiKey));
        }
    }
}

internal sealed record LiveProviderConnection(string BaseUrl, string ApiKey);
