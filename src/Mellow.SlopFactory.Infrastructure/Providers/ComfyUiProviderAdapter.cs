using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// ComfyUI / Comfy Cloud (confirmed live 2026-08-18 against <c>cloud.comfy.org</c> — see
/// <c>docs/developer/comfycloud-contract.md</c>, authoritative over <c>Comfy.md</c>'s pre-verification
/// text). Every real endpoint lives under an <c>/api/</c> prefix; auth is a plain <c>X-API-Key</c>
/// header with no prefix. Phase 1 only: Image mode, driven by the raw API-format workflow JSON stored
/// in <see cref="Model.ComfyWorkflowTemplate"/> (see <see cref="LibraryRules.ValidateComfyWorkflowTemplate"/>
/// and Comfy.md section 3.2 for the placeholder-token design). Text, Audio and Video generation are not
/// implemented — this adapter has no chat/speech contract at all, and video was explicitly deferred
/// (Comfy.md section 4).
/// <para>
/// <see cref="GenerateImageAsync"/> submits one job per requested result (<c>POST /api/prompt</c>),
/// polls <c>GET /api/job/{id}/status</c> until a terminal state, fetches output filenames from
/// <c>GET /api/jobs/{id}</c> (plural — the singular/history endpoints are not real, confirmed via a
/// live 404 that names the correct one), and downloads bytes via <c>GET /api/view</c>, which redirects
/// (302) to a signed, time-limited <c>storage.googleapis.com</c> URL — the same third-party-result
/// shape as OpenRouter/1min.ai, so it gets the same <see cref="ResultUrlValidator"/> host revalidation
/// and DNS-rebinding-hardened handler (<c>DependencyInjection.CreateOpenRouterHttpHandler</c>).
/// <c>outputs.*.images[].filename</c> is a server-assigned opaque hash name and is what must be passed
/// to <c>/api/view</c> — <c>display_name</c>/<c>mime_type</c>/<c>size_bytes</c> are unreliable or empty
/// and are not trusted; the real media type is taken from the downloaded response's own headers.
/// </para>
/// <para>
/// Reference-image upload (<c>POST /api/upload/image</c>, multipart field <c>image</c>) was **not**
/// exercised in the live verification pass that produced <c>comfycloud-contract.md</c> — only a
/// text-to-image workflow was tested. The path is placed under the same confirmed <c>/api/</c> prefix
/// every other Cloud endpoint uses (unprefixed ComfyUI paths were confirmed to silently return the web
/// app's HTML shell rather than erroring, so guessing an unprefixed path here would be worse than
/// guessing a prefixed one), but this specific endpoint and its response shape remain unconfirmed. Up
/// to two reference images are uploaded and substituted (<c>{{UPLOADED_IMAGE_FILENAME}}</c>/
/// <c>{{UPLOADED_IMAGE_FILENAME_2}}</c>) for dual-image-edit workflows — see
/// <see cref="Domain.ComfyBuiltInWorkflows"/> for the built-in templates that use this.
/// </para>
/// </summary>
internal sealed class ComfyUiProviderAdapter : IProviderAdapter
{
    private const string ClientId = "slopfactory";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxPollDuration = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public ComfyUiProviderAdapter(HttpClient httpClient, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.ComfyUi;

    public async Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "api/user"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) return new ConnectionTestResult(false, DescribeFailure(body, statusCode), TryGetHost(connection.BaseUrl), false);

        var status = TryGetStringProperty(body, "status");
        return new ConnectionTestResult(true, string.IsNullOrEmpty(status) ? "Connected." : $"Connected (account status: {status}).", TryGetHost(connection.BaseUrl), false);
    }

    public Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, GenerationMode? mode = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Model discovery is not available for ComfyUI: there is no comparable model catalogue, only an installed-node/checkpoint listing. Set the provider model ID to the workflow's own checkpoint/model identifier manually.");

    public Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Text generation is not implemented for ComfyUI: only Image mode is supported today.");

    public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Audio generation is not implemented for ComfyUI: only Image mode is supported today.");

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not implemented for ComfyUI: only Image mode is supported today.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not implemented for ComfyUI: only Image mode is supported today.");

    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one image result must be requested.");
        if (string.IsNullOrWhiteSpace(model.ComfyWorkflowTemplate)) throw new ProviderAdapterException("This ComfyUI model has no workflow template configured.");

        // The same reference images (at most two — see LibraryRules.GetInputSlotCapabilities) are
        // uploaded once and reused across every result below, mirroring OneMinAiProviderAdapter's
        // GenerateTextAsync: the source images don't change per result, only the seed does.
        var uploadedImageFilenames = new List<string>(sourceImages?.Count ?? 0);
        if (sourceImages is { Count: > 0 })
        {
            foreach (var sourceImage in sourceImages)
            {
                uploadedImageFilenames.Add(await UploadImageAsync(connection, apiKey, sourceImage, cancellationToken).ConfigureAwait(false));
            }
        }

        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            var seed = Random.Shared.NextInt64(0, long.MaxValue);
            var workflowJson = SubstitutePlaceholders(model.ComfyWorkflowTemplate, prompt, seed, uploadedImageFilenames);
            var promptId = await SubmitPromptAsync(connection, apiKey, workflowJson, cancellationToken).ConfigureAwait(false);
            await WaitForCompletionAsync(connection, apiKey, promptId, cancellationToken).ConfigureAwait(false);
            var (filename, subfolder, type) = await GetFirstOutputImageAsync(connection, apiKey, promptId, cancellationToken).ConfigureAwait(false);
            results.Add(await DownloadOutputAsync(connection, apiKey, filename, subfolder, type, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Substitutes the first uploaded image into <c>{{UPLOADED_IMAGE_FILENAME}}</c> and, when
    /// a second reference image was supplied, the second into <c>{{UPLOADED_IMAGE_FILENAME_2}}</c> — the
    /// token naming convention every built-in dual-image-edit workflow template uses (see
    /// <see cref="Domain.ComfyBuiltInWorkflows"/>). At most two reference images are ever supplied
    /// (<see cref="Domain.LibraryRules.GetInputSlotCapabilities"/> caps ComfyUi's Image-mode
    /// <c>ReferenceImage</c> slot at 2), so no further indices are substituted.</summary>
    private static string SubstitutePlaceholders(string template, string prompt, long seed, List<string> uploadedImageFilenames)
    {
        var result = template.Replace("{{PROMPT}}", JsonEncodedText.Encode(prompt).ToString(), StringComparison.Ordinal);
        result = result.Replace("{{SEED}}", seed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (uploadedImageFilenames.Count > 0)
        {
            result = result.Replace("{{UPLOADED_IMAGE_FILENAME}}", JsonEncodedText.Encode(uploadedImageFilenames[0]).ToString(), StringComparison.Ordinal);
        }
        if (uploadedImageFilenames.Count > 1)
        {
            result = result.Replace("{{UPLOADED_IMAGE_FILENAME_2}}", JsonEncodedText.Encode(uploadedImageFilenames[1]).ToString(), StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Confirmed live: <c>POST /api/prompt</c>, body <c>{"prompt": &lt;workflow&gt;,
    /// "client_id": "..."}</c>, immediate response <c>{"node_errors":{},"prompt_id":"..."}</c>. A
    /// non-empty <c>node_errors</c> was never exercised live (the test workflow was valid on the first
    /// attempt) but is treated as a submission failure rather than ignored.</summary>
    private async Task<string> SubmitPromptAsync(Connection connection, string? apiKey, string workflowJson, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "api/prompt"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var body = $"{{\"prompt\":{workflowJson},\"client_id\":{JsonSerializerLiteral(ClientId)}}}";
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, responseBody) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(responseBody, statusCode));

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("node_errors", out var nodeErrors) && nodeErrors.ValueKind == JsonValueKind.Object && nodeErrors.EnumerateObject().Any())
            {
                throw new ProviderAdapterException($"The workflow had node errors: {nodeErrors}.");
            }

            if (!document.RootElement.TryGetProperty("prompt_id", out var promptIdElement) || promptIdElement.ValueKind != JsonValueKind.String || promptIdElement.GetString() is not { Length: > 0 } promptId)
            {
                throw new ProviderAdapterException("The provider accepted the workflow but did not return a job ID.");
            }

            return promptId;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's job submission response was not valid JSON.");
        }
    }

    /// <summary>Confirmed live: <c>GET /api/job/{id}/status</c> returns a small status summary (no
    /// output filenames) with a <c>status</c> field. Only <c>"preparing"</c> (in progress) and
    /// <c>"success"</c> (terminal) were actually observed live; the documented failure vocabulary
    /// (<c>error</c>, <c>non_retryable_error</c>, <c>lost</c>, <c>cancelled</c>) and in-progress states
    /// (<c>queued_waiting</c>, <c>executing</c>) are treated as failed/processing respectively by name
    /// but were not exercised.</summary>
    private async Task WaitForCompletionAsync(Connection connection, string? apiKey, string promptId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MaxPollDuration;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, $"api/job/{Uri.EscapeDataString(promptId)}/status"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

            var status = TryGetStringProperty(body, "status");
            switch (status)
            {
                case "success":
                    return;
                case "error" or "non_retryable_error" or "lost" or "cancelled":
                    throw new ProviderAdapterException($"The provider reported the job as '{status}'.");
                default:
                    if (DateTimeOffset.UtcNow >= deadline) throw new ProviderAdapterException($"The job did not complete within {MaxPollDuration.TotalMinutes:N0} minutes.");
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Confirmed live: <c>GET /api/jobs/{id}</c> (plural — the singular/history endpoints
    /// return a 404 that names this one instead). <c>outputs</c> is keyed by node ID; this takes the
    /// first <c>images[]</c> entry found across all output nodes, in node-key enumeration order — a
    /// workflow whose <c>SaveImage</c> node batches more than one image per job only contributes its
    /// first image to this app's per-result model (see this class's remarks).</summary>
    private async Task<(string Filename, string Subfolder, string Type)> GetFirstOutputImageAsync(Connection connection, string? apiKey, string promptId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, $"api/jobs/{Uri.EscapeDataString(promptId)}"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderAdapterException("The completed job did not report any outputs.");
            }

            foreach (var node in outputs.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object || !node.Value.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) continue;
                foreach (var image in images.EnumerateArray())
                {
                    if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("filename", out var filenameElement) || filenameElement.ValueKind != JsonValueKind.String || filenameElement.GetString() is not { Length: > 0 } filename) continue;
                    var subfolder = image.TryGetProperty("subfolder", out var subfolderElement) && subfolderElement.ValueKind == JsonValueKind.String ? subfolderElement.GetString() ?? "" : "";
                    var type = image.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() ?? "output" : "output";
                    return (filename, subfolder, type);
                }
            }

            throw new ProviderAdapterException("The completed job did not produce any image outputs.");
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's job details response was not valid JSON.");
        }
    }

    /// <summary>Confirmed live: <c>GET /api/view?filename=&amp;subfolder=&amp;type=</c> returns a 302
    /// redirect to a signed, time-limited <c>storage.googleapis.com</c> URL — a genuinely different
    /// host from the connection's own, so every hop (including this first same-origin request, for
    /// consistency) is revalidated via <see cref="ResultUrlValidator"/> before connecting.</summary>
    private async Task<byte[]> DownloadOutputAsync(Connection connection, string? apiKey, string filename, string subfolder, string type, CancellationToken cancellationToken)
    {
        var query = $"filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        if (!Uri.TryCreate(OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, $"api/view?{query}"), UriKind.Absolute, out var currentUri))
        {
            throw new ProviderAdapterException("The provider's connection base URL could not be parsed.");
        }

        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            await ResultUrlValidator.ValidateHostAsync(currentUri, _resolveHost, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (IsConnectionOrigin(connection.BaseUrl, currentUri))
            {
                OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
                OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            }

            var (succeeded, statusCode, bytes, redirectLocation, mediaType, digestHeaders) = await OpenAiCompatibleProtocol.SendForBytesWithRedirectAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (succeeded)
            {
                if (!(string.IsNullOrWhiteSpace(mediaType) || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ProviderAdapterException($"The completed result declared unexpected media type '{mediaType}'.");
                }

                OpenAiCompatibleProtocol.VerifySha256Digest(bytes, digestHeaders);
                if (bytes.Length == 0) throw new ProviderAdapterException("The provider returned an empty result.");
                return bytes;
            }

            if (statusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirectLocation is null || !Uri.TryCreate(currentUri, redirectLocation, out currentUri)) throw new ProviderAdapterException("The result download redirect had no valid target URL.");
                continue;
            }

            throw new ProviderAdapterException($"Downloading the completed result failed: {OpenAiCompatibleProtocol.DescribeFailure(statusCode)}");
        }

        throw new ProviderAdapterException("The result download exceeded the maximum redirect count.");
    }

    /// <summary>Unconfirmed — see this class's remarks. Best-guess shape: multipart/form-data, single
    /// field named "image", response <c>{"name": "&lt;filename&gt;", ...}</c> per ComfyUI's documented
    /// native <c>/upload/image</c> contract, called under the confirmed <c>/api/</c> prefix.</summary>
    private async Task<string> UploadImageAsync(Connection connection, string? apiKey, TextGenerationSourceImage image, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "api/upload/image"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(image.Bytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.MediaType);
        content.Add(imageContent, "image", $"source{ImageFileExtension(image.MediaType)}");
        request.Content = content;

        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

        var uploadedName = TryGetStringProperty(body, "name");
        if (string.IsNullOrEmpty(uploadedName)) throw new ProviderAdapterException("The provider's image upload response did not include a usable filename.");
        return uploadedName;
    }

    private static string ImageFileExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".bin"
    };

    private static bool IsConnectionOrigin(string connectionBaseUrl, Uri resultUri) =>
        Uri.TryCreate(connectionBaseUrl, UriKind.Absolute, out var connectionUri) &&
        string.Equals(connectionUri.Scheme, resultUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(connectionUri.Host, resultUri.Host, StringComparison.OrdinalIgnoreCase) &&
        connectionUri.Port == resultUri.Port;

    private static string? TryGetStringProperty(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string JsonSerializerLiteral(string value) => $"\"{JsonEncodedText.Encode(value)}\"";

    /// <summary>Comfy Cloud's confirmed error shapes: <c>{"code":"...","message":"..."}</c> (e.g. the
    /// <c>GET /api/user</c> 401) and <c>{"error":{"message":"...","type":"..."}}</c> (e.g. a wrong-path
    /// 404). Falls back to the generic status-code description when the body isn't either shape.</summary>
    private static string DescribeFailure(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String && messageElement.GetString() is { Length: > 0 } message)
            {
                var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : null;
                return string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
            }

            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var nestedMessage) && nestedMessage.ValueKind == JsonValueKind.String &&
                nestedMessage.GetString() is { Length: > 0 } nested)
            {
                return nested;
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic description below.
        }

        return OpenAiCompatibleProtocol.DescribeFailure(statusCode);
    }

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
