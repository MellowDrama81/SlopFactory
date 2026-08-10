using Microsoft.Extensions.DependencyInjection;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

internal sealed class ProviderAdapterResolver : IProviderAdapterResolver
{
    private readonly IServiceProvider _services;

    public ProviderAdapterResolver(IServiceProvider services)
    {
        _services = services;
    }

    public IProviderAdapter Resolve(ProviderType providerType) =>
        _services.GetServices<IProviderAdapter>().FirstOrDefault(adapter => adapter.ProviderType == providerType)
        ?? throw new NotSupportedException($"No provider adapter is registered for {providerType}.");
}
