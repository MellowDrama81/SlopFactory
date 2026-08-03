using Microsoft.Extensions.DependencyInjection;
using Mellow.SlopFactory.Application;

namespace Mellow.SlopFactory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSlopFactoryInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ILibraryWorkspaceFactory, LibraryWorkspaceFactory>();
        return services;
    }
}

