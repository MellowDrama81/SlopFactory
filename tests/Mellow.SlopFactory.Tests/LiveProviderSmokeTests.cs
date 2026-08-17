using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Deliberate real-provider discovery smoke tests. These are skipped unless explicitly enabled;
/// they make only adapter discovery calls and never submit a generation prompt or source file.
/// </summary>
public sealed class LiveProviderSmokeTests
{
    [Fact]
    [Trait("Category", "LiveProvider")]
    public async Task RunsDiscoveryOnlyWhenExplicitlyEnabledAndCredentialed()
    {
        var settings = LiveProviderTestSettings.FromEnvironment();
        if (!settings.CanRunDiscovery) return;
        settings.RequireDiscoveryRun();

        foreach (var (providerType, configured) in settings.ConfiguredConnections)
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var adapter = CreateAdapter(providerType, client);
            var connection = new Connection(
                $"live-{providerType}",
                $"Live {providerType}",
                providerType,
                configured.BaseUrl,
                "Authorization",
                "Bearer",
                false,
                ConnectionTestStatus.Untested,
                null,
                null,
                LibraryRecordState.Active,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                TimeoutSeconds: 30);

            var result = await adapter.TestConnectionAsync(connection, configured.ApiKey);

            Assert.True(result.Success, $"{providerType} discovery failed: {result.Message}");
        }
    }

    private static IProviderAdapter CreateAdapter(ProviderType providerType, HttpClient client) => providerType switch
    {
        ProviderType.OpenAi => new OpenAiProviderAdapter(client),
        ProviderType.GenericOpenAiCompatible => new GenericOpenAiCompatibleProviderAdapter(client),
        ProviderType.OpenRouter => new OpenRouterProviderAdapter(client),
        ProviderType.DeepInfra => new DeepInfraProviderAdapter(client),
        _ => throw new ArgumentOutOfRangeException(nameof(providerType))
    };
}
