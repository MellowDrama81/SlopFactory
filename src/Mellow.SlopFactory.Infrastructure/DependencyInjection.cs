using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Infrastructure.Providers;

namespace Mellow.SlopFactory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSlopFactoryInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ILibraryWorkspaceFactory, LibraryWorkspaceFactory>();
        services.AddSingleton<IConnectionRateLimitTracker, ConnectionRateLimitTracker>();
        // HttpClient's own timeout is disabled here so OpenAiCompatibleProtocol.SendAsync is the single place that enforces
        // a timeout; otherwise a default HttpClient timeout could fire as a bare OperationCanceledException indistinguishable
        // from user-initiated generation cancellation.
        services.AddHttpClient<OpenAiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<GenericOpenAiCompatibleProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<OpenRouterProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateOpenRouterHttpHandler);
        services.AddHttpClient<DeepInfraProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        // 1min.ai's image/audio results are fetched from a third-party (S3) temporaryUrl, the same
        // cross-origin-result-download shape as OpenRouter's video results, so it gets the same
        // DNS-rebinding-hardened handler rather than relying solely on the request-time
        // ResultUrlValidator check.
        services.AddHttpClient<OneMinAiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateOpenRouterHttpHandler);
        // ComfyUI's GET /api/view redirects to a signed storage.googleapis.com URL, the same
        // cross-origin-result-download shape as OpenRouter/1min.ai, so it gets the same handler.
        services.AddHttpClient<ComfyUiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateOpenRouterHttpHandler);
        // The following seven are all directly OpenAI-compatible (providers.md's "Directly
        // OpenAI-compatible" section) — no cross-origin result-download shape confirmed for any of
        // them, so they use the default handler like OpenAiProviderAdapter/DeepInfraProviderAdapter.
        services.AddHttpClient<MistralProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<GroqProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<TogetherAiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<FireworksAiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<DeepSeekProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<PerplexityProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        // xAI's images/generations returns a hosted result URL (Grok Imagine), the same cross-origin
        // result-download shape as OpenRouter/1min.ai/ComfyUI, so it gets the same handler.
        services.AddHttpClient<XAiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateOpenRouterHttpHandler);
        // Bespoke shapes (not OpenAI-compatible) — same default handler, no cross-origin result-download shape.
        services.AddHttpClient<AnthropicProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<GoogleGeminiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<CohereProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<AI21ProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<OpenAiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<GenericOpenAiCompatibleProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<OpenRouterProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<DeepInfraProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<OneMinAiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<ComfyUiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<MistralProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<GroqProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<TogetherAiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<FireworksAiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<DeepSeekProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<PerplexityProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<XAiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<AnthropicProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<GoogleGeminiProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<CohereProviderAdapter>());
        services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<AI21ProviderAdapter>());
        services.AddSingleton<IProviderAdapterResolver, ProviderAdapterResolver>();
        return services;
    }

    // OpenRouter returns provider-hosted result URLs. Resolve each hostname here and connect to the
    // validated address directly so DNS cannot switch a previously validated public host to a
    // private address between validation and connection. The original host remains in the request
    // URI, preserving normal HTTPS SNI and certificate validation.
    private static SocketsHttpHandler CreateOpenRouterHttpHandler() => CreateOpenRouterHttpHandler(Dns.GetHostAddressesAsync);

    internal static SocketsHttpHandler CreateOpenRouterHttpHandler(Func<string, CancellationToken, Task<IPAddress[]>> resolveHost) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) => ConnectToValidatedPublicAddressAsync(context, resolveHost, cancellationToken)
    };

    private static async ValueTask<Stream> ConnectToValidatedPublicAddressAsync(SocketsHttpConnectionContext context, Func<string, CancellationToken, Task<IPAddress[]>> resolveHost, CancellationToken cancellationToken)
    {
        var addresses = await resolveHost(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var permittedAddresses = addresses.Where(address => !ResultUrlValidator.IsBlockedAddress(address)).ToArray();
        if (permittedAddresses.Length == 0)
        {
            throw new HttpRequestException($"The host '{context.DnsEndPoint.Host}' resolved to a disallowed network address.");
        }

        Exception? lastFailure = null;
        foreach (var address in permittedAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (exception is OperationCanceledException) throw;
                lastFailure = exception;
            }
        }

        throw new HttpRequestException($"Could not connect to the result host '{context.DnsEndPoint.Host}'.", lastFailure);
    }
}
