using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Mellow.SlopFactory.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Every other test in this project resolves adapters through a hand-written
/// <c>FakeProviderAdapterResolver</c> that trivially always returns one fake adapter — none of them
/// actually exercise the real <see cref="ProviderAdapterResolver"/> with more than one adapter
/// registered, so a regression that returned the wrong adapter for a given <see cref="ProviderType"/>
/// (a real cross-adapter isolation risk once four adapters share one DI container) would go
/// unnoticed. This proves the real resolver, wired exactly as the application wires it, picks the
/// correct adapter for each of the four supported provider types and never a different one.
/// </summary>
public sealed class ProviderAdapterResolverTests
{
    private static IProviderAdapterResolver BuildResolver()
    {
        var services = new ServiceCollection();
        services.AddSlopFactoryInfrastructure();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IProviderAdapterResolver>();
    }

    [Theory]
    [InlineData(ProviderType.OpenAi, typeof(OpenAiProviderAdapter))]
    [InlineData(ProviderType.GenericOpenAiCompatible, typeof(GenericOpenAiCompatibleProviderAdapter))]
    [InlineData(ProviderType.OpenRouter, typeof(OpenRouterProviderAdapter))]
    [InlineData(ProviderType.DeepInfra, typeof(DeepInfraProviderAdapter))]
    public void ResolveReturnsExactlyTheAdapterRegisteredForEachProviderType(ProviderType providerType, Type expectedAdapterType)
    {
        var resolver = BuildResolver();

        var adapter = resolver.Resolve(providerType);

        Assert.IsType(expectedAdapterType, adapter);
        Assert.Equal(providerType, adapter.ProviderType);
    }

    [Fact]
    public void ResolvingAllFourProviderTypesNeverReturnsTheSameAdapterInstanceTwice()
    {
        var resolver = BuildResolver();

        var adapters = new[] { ProviderType.OpenAi, ProviderType.GenericOpenAiCompatible, ProviderType.OpenRouter, ProviderType.DeepInfra }
            .Select(resolver.Resolve)
            .ToArray();

        Assert.Equal(4, adapters.Select(adapter => adapter.GetType()).Distinct().Count());
    }

    [Fact]
    public void ResolvingAnUnregisteredProviderTypeThrowsRatherThanSilentlyFallingBackToAnotherAdapter()
    {
        var resolver = BuildResolver();

        var exception = Assert.Throws<NotSupportedException>(() => resolver.Resolve((ProviderType)(-1)));
        Assert.Contains("No provider adapter is registered", exception.Message, StringComparison.Ordinal);
    }
}
