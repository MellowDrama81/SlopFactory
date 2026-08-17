using System.Net;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task OpenRouterHandlerRejectsAPrivateResolvedAddressBeforeOpeningAConnection()
    {
        using var handler = DependencyInjection.CreateOpenRouterHttpHandler((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://rebound.example.test/result"));

        Assert.Contains("disallowed network address", exception.Message, StringComparison.Ordinal);
    }
}
