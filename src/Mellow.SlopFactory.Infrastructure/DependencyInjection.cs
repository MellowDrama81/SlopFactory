using Microsoft.Extensions.DependencyInjection;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Infrastructure.Providers;

namespace Mellow.SlopFactory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSlopFactoryInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ILibraryWorkspaceFactory, LibraryWorkspaceFactory>();
        // HttpClient's own timeout is disabled here so OpenAiCompatibleProtocol.SendAsync is the single place that enforces
        // a timeout; otherwise a default HttpClient timeout could fire as a bare OperationCanceledException indistinguishable
        // from user-initiated generation cancellation.
        services.AddHttpClient<OpenAiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<GenericOpenAiCompatibleProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<OpenAiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<GenericOpenAiCompatibleProviderAdapter>());
        services.AddSingleton<IProviderAdapterResolver, ProviderAdapterResolver>();
        return services;
    }
}

