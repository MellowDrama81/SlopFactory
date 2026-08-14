using System.Net;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ResultUrlValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    public void IsBlockedAddressReturnsTrueForLoopbackPrivateLinkLocalAndMulticastAddresses(string address)
    {
        Assert.True(ResultUrlValidator.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("2606:4700:4700::1111")]
    public void IsBlockedAddressReturnsFalseForOrdinaryPublicAddresses(string address)
    {
        Assert.False(ResultUrlValidator.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task ValidateHostAsyncThrowsForANonHttpsUri()
    {
        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            ResultUrlValidator.ValidateHostAsync(new Uri("http://example.test/result"), (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }), CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateHostAsyncThrowsWhenResolutionReturnsABlockedAddress()
    {
        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            ResultUrlValidator.ValidateHostAsync(new Uri("https://example.test/result"), (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") }), CancellationToken.None));

        Assert.Contains("disallowed network address", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateHostAsyncThrowsWhenResolutionReturnsNoAddresses()
    {
        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            ResultUrlValidator.ValidateHostAsync(new Uri("https://example.test/result"), (_, _) => Task.FromResult(Array.Empty<IPAddress>()), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateHostAsyncSucceedsForAnHttpsUriResolvingToOnlyPublicAddresses()
    {
        await ResultUrlValidator.ValidateHostAsync(new Uri("https://example.test/result"), (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), CancellationToken.None);
    }

    [Fact]
    public async Task ValidateHostAsyncThrowsAProviderAdapterExceptionWhenResolutionFails()
    {
        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            ResultUrlValidator.ValidateHostAsync(new Uri("https://nonexistent.invalid/result"), (_, _) => throw new System.Net.Sockets.SocketException(), CancellationToken.None));
    }
}
