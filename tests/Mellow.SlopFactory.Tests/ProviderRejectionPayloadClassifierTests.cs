using System.Text;
using Mellow.SlopFactory.Domain;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ProviderRejectionPayloadClassifierTests
{
    [Fact]
    public void EmptyBytesAreNotARecognizedRejection()
    {
        Assert.False(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload([], "application/octet-stream"));
    }

    [Fact]
    public void RealMediaBytesAreNotARecognizedRejection()
    {
        byte[] pngSignatureBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        Assert.False(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(pngSignatureBytes, "image/png"));
    }

    [Fact]
    public void ArbitraryUnrecognizedBinaryIsNotARecognizedRejection()
    {
        byte[] randomBytes = [0x01, 0x02, 0x03, 0x04, 0x05, 0xFF, 0xFE, 0xAB];
        Assert.False(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(randomBytes, "application/octet-stream"));
    }

    [Fact]
    public void JsonWithAnErrorKeyIsARecognizedRejection()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"error":{"message":"Invalid API key","type":"authentication_error"}}""");
        Assert.True(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, "application/json"));
    }

    [Fact]
    public void JsonWithAnErrorsKeyIsARecognizedRejection()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"errors":["rate limit exceeded"]}""");
        Assert.True(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, "application/octet-stream"));
    }

    [Fact]
    public void JsonWithoutAnErrorKeyIsNotARecognizedRejection()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"status":"ok","message":"unrelated"}""");
        Assert.False(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, "application/json"));
    }

    [Fact]
    public void MalformedJsonIsNotARecognizedRejection()
    {
        var bytes = Encoding.UTF8.GetBytes("{not valid json");
        Assert.False(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, "application/octet-stream"));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Sign in</body></html>")]
    [InlineData("<html><head></head><body>blocked</body></html>")]
    [InlineData("  \r\n<!doctype HTML>\n<html></html>")]
    public void HtmlContentIsARecognizedRejection(string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        Assert.True(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, "application/octet-stream"));
    }

    [Fact]
    public void DeclaredHtmlMediaTypeIsARecognizedRejectionEvenWithoutADoctype()
    {
        var bytes = Encoding.UTF8.GetBytes("<div>Please sign in</div>");
        Assert.True(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, "text/html"));
    }

    [Fact]
    public void BytesLargerThanTheInspectionCeilingAreNeverInspectedAsText()
    {
        var oversized = new byte[100_000];
        Encoding.UTF8.GetBytes("""{"error":"too big to matter"}""").CopyTo(oversized, 0);
        Assert.False(ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(oversized, "application/octet-stream"));
    }
}
