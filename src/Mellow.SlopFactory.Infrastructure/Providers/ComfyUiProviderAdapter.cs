using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
/// polls <c>GET /api/jobs/{id}</c> (plural — the singular/history endpoints are not real, confirmed via
/// a live 404 that names the correct one) until a terminal state, reads output filenames from that same
/// response, and downloads bytes via <c>GET /api/view</c>, which redirects
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
    // /api/object_info is roughly 10 MB on Cloud, so retain the successful (or unavailable) snapshot
    // for this adapter instance and connection rather than downloading it for every generation.
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlySet<string>?>>> _cloudNodeInventories = new(StringComparer.Ordinal);

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

        await PreflightBuiltInCloudWorkflowAsync(connection, apiKey, model.ComfyWorkflowTemplate, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Best-effort only: Cloud's object-info inventory is large, experimental and can vary by
    /// worker. It is therefore consulted only for an exact built-in template, cached per connection,
    /// and skipped if Cloud does not return a usable catalog. A positive missing-node result is still
    /// valuable: it prevents a costly submission whose graph cannot be constructed.</summary>
    private async Task PreflightBuiltInCloudWorkflowAsync(Connection connection, string? apiKey, string workflowTemplate, CancellationToken cancellationToken)
    {
        if (!IsComfyCloud(connection.BaseUrl)) return;
        var builtIn = ComfyBuiltInWorkflows.All.FirstOrDefault(workflow => string.Equals(workflow.WorkflowTemplate, workflowTemplate, StringComparison.Ordinal));
        if (builtIn is null || builtIn.Requirements.NodeTypes.Count == 0) return;

        var inventory = await _cloudNodeInventories.GetOrAdd(
            connection.Id,
            _ => new Lazy<Task<IReadOnlySet<string>?>>(() => GetCloudNodeInventoryAsync(connection, apiKey, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication)).Value.ConfigureAwait(false);
        if (inventory is null) return;

        var missing = builtIn.Requirements.NodeTypes.Where(nodeType => !inventory.Contains(nodeType)).ToArray();
        if (missing.Length > 0)
        {
            throw new ProviderAdapterException($"This Comfy Cloud worker does not advertise the required node type(s) for '{builtIn.Name}': {string.Join(", ", missing)}. Cloud's inventory is experimental and worker-dependent; try again later or choose a worker/account where these nodes are available.");
        }
    }

    private async Task<IReadOnlySet<string>?> GetCloudNodeInventoryAsync(Connection connection, string? apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "api/object_info"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            var (isSuccess, _, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) return null;

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.EnumerateObject().Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static bool IsComfyCloud(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "cloud.comfy.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ComfyUI workflows receive a mask through the alpha channel of their first uploaded image: core
    /// <c>LoadImage</c> exposes that alpha channel as its MASK output. This preserves the app's normal
    /// private-mask picker while keeping the submitted workflow portable between Comfy Cloud and local
    /// ComfyUI. Opaque pixels in the private mask become transparent/editable pixels in the upload.
    /// </summary>
    public Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages, TextGenerationSourceImage? mask, CancellationToken cancellationToken = default)
    {
        if (mask is null) return GenerateImageAsync(connection, model, apiKey, prompt, resultCount, sourceImages, cancellationToken);
        if (sourceImages is not { Count: > 0 }) throw new ProviderAdapterException("A mask requires a source image.");

        var alphaMaskedSources = sourceImages.ToList();
        alphaMaskedSources[0] = ApplyMaskAsTransparency(sourceImages[0], mask);
        return GenerateImageAsync(connection, model, apiKey, prompt, resultCount, alphaMaskedSources, cancellationToken);
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

    private static TextGenerationSourceImage ApplyMaskAsTransparency(TextGenerationSourceImage source, TextGenerationSourceImage mask)
    {
        try
        {
            using var sourceImage = Image.Load<Rgba32>(source.Bytes);
            using var maskImage = Image.Load<Rgba32>(mask.Bytes);
            if (sourceImage.Width != maskImage.Width || sourceImage.Height != maskImage.Height)
                throw new ProviderAdapterException("The mask dimensions must match its source image.");

            for (var y = 0; y < sourceImage.Height; y++)
            for (var x = 0; x < sourceImage.Width; x++)
            {
                var pixel = sourceImage[x, y];
                pixel.A = (byte)(pixel.A * (255 - maskImage[x, y].A) / 255);
                sourceImage[x, y] = pixel;
            }

            using var output = new MemoryStream();
            sourceImage.SaveAsPng(output);
            return new TextGenerationSourceImage("image/png", output.ToArray());
        }
        catch (ProviderAdapterException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidImageContentException or UnknownImageFormatException or NotSupportedException)
        {
            throw new ProviderAdapterException("The source image or mask could not be converted into an alpha-masked PNG.");
        }
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
                throw new ProviderAdapterException(DescribeNodeErrors(nodeErrors, workflowJson));
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

    /// <summary>Turns Comfy's per-node validation payload into an actionable error. Cloud commonly
    /// reports an unavailable checkpoint or ControlNet as a terse <c>"Value not in list"</c> on a
    /// loader node; pairing that with the submitted workflow identifies the real problem without
    /// requiring the user to inspect raw JSON.</summary>
    private static string DescribeNodeErrors(JsonElement nodeErrors, string workflowJson)
    {
        Dictionary<string, string?>? nodeTypes = null;
        try
        {
            using var workflow = JsonDocument.Parse(workflowJson);
            nodeTypes = workflow.RootElement.EnumerateObject()
                .Where(node => node.Value.ValueKind == JsonValueKind.Object && node.Value.TryGetProperty("class_type", out _))
                .ToDictionary(node => node.Name, node => node.Value.GetProperty("class_type").GetString(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // The server's error is still more useful than discarding it if a custom workflow was
            // somehow modified into invalid JSON after local validation.
        }

        var descriptions = new List<string>();
        foreach (var node in nodeErrors.EnumerateObject())
        {
            var messages = node.Value.ValueKind == JsonValueKind.Object && node.Value.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? errors.EnumerateArray()
                    .Where(error => error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    .Select(error => error.GetProperty("message").GetString())
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Cast<string>()
                    .ToArray()
                : [];
            var nodeType = nodeTypes is not null && nodeTypes.TryGetValue(node.Name, out var knownType) && !string.IsNullOrWhiteSpace(knownType)
                ? $" ({knownType})"
                : string.Empty;
            var message = messages.Length > 0 ? string.Join("; ", messages) : "Cloud rejected this node's configuration";
            if (nodeType.EndsWith("Loader)", StringComparison.Ordinal) && messages.Any(item => item.Contains("Value not in list", StringComparison.OrdinalIgnoreCase)))
            {
                message += ". The selected model file is not available to this Cloud worker; import it or choose a filename offered by the loader.";
            }
            descriptions.Add($"node {node.Name}{nodeType}: {message}");
        }

        return $"The workflow was rejected: {string.Join(" | ", descriptions)}.";
    }

    /// <summary>Polls the confirmed Cloud job-details endpoint. Cloud no longer exposes the old
    /// singular <c>/api/job/{id}/status</c> route; its current lifecycle includes <c>in_progress</c>
    /// and uses <c>completed</c> as the successful terminal status.</summary>
    private async Task WaitForCompletionAsync(Connection connection, string? apiKey, string promptId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MaxPollDuration;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, $"api/jobs/{Uri.EscapeDataString(promptId)}"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

            var status = TryGetStringProperty(body, "status");
            switch (status)
            {
                case "success" or "completed":
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
