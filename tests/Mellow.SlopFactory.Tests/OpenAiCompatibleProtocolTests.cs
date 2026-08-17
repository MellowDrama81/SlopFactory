using System.Net;
using System.Security.Cryptography;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class OpenAiCompatibleProtocolTests
{
    [Fact]
    public async Task ReadResponseBytesAsyncRejectsAnOversizedDeclaredResponse()
    {
        using var content = new ByteArrayContent([1, 2, 3, 4]);

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => OpenAiCompatibleProtocol.ReadResponseBytesAsync(content, CancellationToken.None, maximumBytes: 3));

        Assert.Contains("download limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResponseBytesAsyncRejectsAnOversizedResponseWithoutContentLength()
    {
        using var content = new UnknownLengthContent([1, 2, 3, 4]);

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => OpenAiCompatibleProtocol.ReadResponseBytesAsync(content, CancellationToken.None, maximumBytes: 3));

        Assert.Contains("download limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifySha256DigestAcceptsMatchingContentDigest()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var digest = Convert.ToBase64String(SHA256.HashData(bytes));

        OpenAiCompatibleProtocol.VerifySha256Digest(bytes, [$"sha-256=:{digest}:"]);
    }

    [Fact]
    public void VerifySha256DigestRejectsMismatchedContentDigest()
    {
        var exception = Assert.Throws<ProviderAdapterException>(() => OpenAiCompatibleProtocol.VerifySha256Digest([1, 2, 3], ["sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:"]));

        Assert.Contains("did not match", exception.Message, StringComparison.Ordinal);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
