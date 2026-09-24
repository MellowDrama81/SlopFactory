using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed record GeneratedOutput(string Path, bool IsImage);
public sealed record GenerationRunState(bool IsRunning, string Status, string? PromptId, IReadOnlyList<GeneratedOutput> Outputs);

public sealed class ComfyGenerationService(ComfyConnectionSettings settings, WorkflowTemplateService templates,
    ComfyAssetReferenceStore assetReferences)
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    private readonly Dictionary<string, GenerationRunState> jobs = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public event Action<string>? Changed;

    public GenerationRunState? GetState(string paneId)
    {
        lock (gate) return jobs.GetValueOrDefault(paneId);
    }

    public bool Start(string paneId, string projectFolder, WorkflowDefinition workflow, IReadOnlyDictionary<string, string> inputValues,
        string? outputFolder, IReadOnlyList<string> outputTags)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(paneId, out var existing) && existing.IsRunning) return false;
            jobs[paneId] = new(true, "Preparing workflow…", null, []);
        }
        Changed?.Invoke(paneId);
        var values = new Dictionary<string, string>(inputValues, StringComparer.Ordinal);
        var serverUrl = settings.ServerUrl.Trim();
        var apiKey = settings.ApiKey.Trim();
        var connectionId = string.IsNullOrWhiteSpace(settings.ConnectionId) ? serverUrl : settings.ConnectionId.Trim();
        var tags = outputTags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _ = ExecuteAsync(paneId, projectFolder, workflow, values, serverUrl, apiKey, connectionId, outputFolder, tags);
        return true;
    }

    private void Update(string paneId, bool running, string status, string? promptId = null, IReadOnlyList<GeneratedOutput>? outputs = null)
    {
        lock (gate)
        {
            var previous = jobs[paneId];
            jobs[paneId] = new(running, status, promptId ?? previous.PromptId, outputs ?? previous.Outputs);
        }
        Changed?.Invoke(paneId);
    }

    private async Task ExecuteAsync(string paneId, string projectFolder, WorkflowDefinition workflow,
        Dictionary<string, string> values, string serverUrl, string apiKey, string connectionId,
        string? outputFolder, IReadOnlyList<string> outputTags)
    {
        try
        {
            if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var server) ||
                server.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Configure a valid Comfy connection URL before running.");
            var cloud = server.Host.Equals("cloud.comfy.org", StringComparison.OrdinalIgnoreCase);
            if (cloud && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("The Comfy Cloud connection needs an API key.");
            if (!Directory.Exists(projectFolder))
                throw new DirectoryNotFoundException("The selected project folder is unavailable.");
            var outputDirectory = ResolveOutputDirectory(projectFolder, outputFolder);

            var placeholders = await templates.GetPlaceholdersAsync(workflow);
            foreach (var placeholder in placeholders.Where(item => !item.IsSeed))
                if (!values.TryGetValue(placeholder.Name, out var value) || string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException($"Choose or enter {placeholder.Label.ToLowerInvariant()}.");
            Directory.CreateDirectory(outputDirectory);

            var uploaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var placeholder in placeholders.Where(item => item.Type == "image"))
            {
                var assetPath = ResolveAssetPath(projectFolder, values[placeholder.Name]);
                if (!uploaded.TryGetValue(assetPath, out var remoteName))
                {
                    var existing = await assetReferences.GetValidAsync(projectFolder, assetPath, connectionId, serverUrl);
                    if (existing is not null)
                    {
                        remoteName = existing.WorkflowFilename;
                        Update(paneId, true, $"Reusing {Path.GetFileName(assetPath)} on Comfy…");
                    }
                    else
                    {
                        Update(paneId, true, $"Uploading {Path.GetFileName(assetPath)}…");
                        var uploadedImage = await UploadImageAsync(server, cloud, apiKey, assetPath);
                        await assetReferences.SetAsync(projectFolder, assetPath, connectionId, serverUrl,
                            uploadedImage.Filename, uploadedImage.Subfolder, uploadedImage.Type);
                        remoteName = uploadedImage.WorkflowFilename;
                    }
                    uploaded[assetPath] = remoteName;
                }
                values[placeholder.Name] = remoteName;
            }

            var prepared = await templates.PrepareAsync(workflow, values);
            Update(paneId, true, "Submitting workflow to Comfy…");
            var promptId = await SubmitAsync(server, cloud, apiKey, prepared.GraphJson);
            Update(paneId, true, "Queued…", promptId);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            JsonElement outputs = default;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (cloud)
                {
                    using var status = await ReadJsonAsync(server, "api/job/" + Uri.EscapeDataString(promptId) + "/status", apiKey, timeout.Token);
                    var value = status.RootElement.GetProperty("status").GetString() ?? string.Empty;
                    if (value is "success" or "completed")
                    {
                        using var detail = await ReadJsonAsync(server, "api/jobs/" + Uri.EscapeDataString(promptId), apiKey, timeout.Token);
                        if (detail.RootElement.TryGetProperty("outputs", out var cloudOutputs))
                            outputs = cloudOutputs.Clone();
                        break;
                    }
                    if (value is "error" or "non_retryable_error" or "lost" or "cancelled" or "failed")
                    {
                        using var detail = await ReadJsonAsync(server, "api/jobs/" + Uri.EscapeDataString(promptId), apiKey, timeout.Token);
                        var reason = detail.RootElement.TryGetProperty("execution_error", out var error) &&
                            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("exception_message", out var message)
                            ? message.GetString() : null;
                        throw new InvalidOperationException(reason ?? $"Comfy job {value}.");
                    }
                    Update(paneId, true, value is "executing" or "in_progress" ? "Running…" : "Queued…");
                }
                else
                {
                    using var history = await ReadJsonAsync(server, "history/" + Uri.EscapeDataString(promptId), apiKey, timeout.Token);
                    if (history.RootElement.TryGetProperty(promptId, out var entry))
                    {
                        if (entry.TryGetProperty("status", out var status))
                        {
                            var state = status.TryGetProperty("status_str", out var statusStr) ? statusStr.GetString() : null;
                            if (state == "error") throw new InvalidOperationException(GetExecutionError(status));
                            if (status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True)
                            {
                                if (entry.TryGetProperty("outputs", out var localOutputs))
                                    outputs = localOutputs.Clone();
                                break;
                            }
                        }
                    }
                    Update(paneId, true, "Queued or running…");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }

            Update(paneId, true, "Downloading results…");
            var saved = await SaveOutputsAsync(server, cloud, apiKey, projectFolder, outputDirectory, workflow.Id,
                connectionId, serverUrl, outputs, outputTags);
            Update(paneId, false, saved.Count == 0 ? "Complete. The workflow produced no downloadable files." :
                $"Complete. Saved {saved.Count} result{(saved.Count == 1 ? "" : "s")} to project assets.", outputs: saved);
        }
        catch (OperationCanceledException)
        {
            Update(paneId, false, "Timed out waiting for the Comfy job. It may still be running on the server.");
        }
        catch (Exception ex)
        {
            Update(paneId, false, $"Generation failed: {ex.Message}");
        }
    }

    private static string ResolveAssetPath(string projectFolder, string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(projectFolder, "assets")) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(projectFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new InvalidOperationException($"Project image is missing or outside assets: {relativePath}");
        return fullPath;
    }

    private async Task<ComfyAssetReference> UploadImageAsync(Uri server, bool cloud, string apiKey, string path)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        form.Add(new StreamContent(stream), "image", Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        form.Add(new StringContent("input"), "type");
        using var request = MakeRequest(HttpMethod.Post, server, Route(cloud, "upload/image"), apiKey);
        request.Content = form;
        using var response = await client.SendAsync(request);
        await EnsureSuccessAsync(response);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var name = result.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Comfy did not return an uploaded filename.");
        var subfolder = result.RootElement.TryGetProperty("subfolder", out var folder) ? folder.GetString() ?? "" : "";
        var type = result.RootElement.TryGetProperty("type", out var fileType) ? fileType.GetString() ?? "input" : "input";
        return new(name, subfolder, type, "", "");
    }

    private async Task<string> SubmitAsync(Uri server, bool cloud, string apiKey, string graphJson)
    {
        var payload = new JsonObject
        {
            ["prompt"] = JsonNode.Parse(graphJson),
            ["client_id"] = Guid.NewGuid().ToString()
        };
        if (cloud && apiKey.Length > 0)
            payload["extra_data"] = new JsonObject { ["api_key_comfy_org"] = apiKey };
        using var request = MakeRequest(HttpMethod.Post, server, Route(cloud, "prompt"), apiKey);
        request.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(request);
        await EnsureSuccessAsync(response);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return result.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new InvalidOperationException("Comfy did not return a prompt ID.");
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri server, string route, string apiKey, CancellationToken cancellationToken)
    {
        using var request = MakeRequest(HttpMethod.Get, server, route, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string ResolveOutputDirectory(string projectFolder, string? outputFolder)
    {
        var folder = string.IsNullOrWhiteSpace(outputFolder) ? "generated" : outputFolder.Trim();
        if (Path.IsPathRooted(folder)) throw new InvalidOperationException("Choose a folder within project assets, not an absolute path.");
        var assetsRoot = Path.GetFullPath(Path.Combine(projectFolder, "assets"));
        if (folder == ".") return assetsRoot;
        var segments = folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.EndsWith('.') ||
            segment.EndsWith(' ') || segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("Enter a valid folder path relative to project assets.");
        var destination = Path.GetFullPath(Path.Combine([assetsRoot, .. segments]));
        if (!destination.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output folder must be inside project assets.");
        return destination;
    }

    private async Task<IReadOnlyList<GeneratedOutput>> SaveOutputsAsync(Uri server, bool cloud, string apiKey,
        string projectFolder, string outputDirectory, string workflowId, string connectionId, string serverUrl,
        JsonElement outputs, IReadOnlyList<string> outputTags)
    {
        var files = new List<GeneratedOutput>();
        if (outputs.ValueKind != JsonValueKind.Object) return files;
        foreach (var node in outputs.EnumerateObject())
        foreach (var category in new[] { "images", "gifs", "video", "videos", "audio" })
        {
            if (node.Value.ValueKind != JsonValueKind.Object) continue;
            if (!node.Value.TryGetProperty(category, out var items)) continue;
            var entries = items.ValueKind switch
            {
                JsonValueKind.Array => items.EnumerateArray().ToArray(),
                JsonValueKind.Object => [items],
                _ => []
            };
            foreach (var item in entries)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("filename", out var nameProperty)) continue;
                var remoteName = nameProperty.GetString();
                if (string.IsNullOrWhiteSpace(remoteName)) continue;
                var subfolder = item.TryGetProperty("subfolder", out var sub) ? sub.GetString() ?? "" : "";
                var type = item.TryGetProperty("type", out var outputType) ? outputType.GetString() ?? "output" : "output";
                var query = $"filename={Uri.EscapeDataString(remoteName)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
                using var request = MakeRequest(HttpMethod.Get, server, Route(cloud, "view") + "?" + query, apiKey);
                using var response = await client.SendAsync(request);
                HttpResponseMessage download = response;
                if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect)
                {
                    var redirect = response.Headers.Location ?? throw new InvalidOperationException("Comfy returned an output redirect without a location.");
                    // The signed storage URL authenticates itself; never forward the Comfy API key.
                    download = await client.GetAsync(redirect.IsAbsoluteUri ? redirect : new Uri(server, redirect));
                }
                try
                {
                    await EnsureSuccessAsync(download);
                    Directory.CreateDirectory(outputDirectory);
                    var extension = Path.GetExtension(remoteName).ToLowerInvariant();
                    if (extension.Length is < 2 or > 8 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.')) extension = ".bin";
                    var filePath = Path.Combine(outputDirectory, $"{SafeFilePart(workflowId)}_{Guid.NewGuid():N}{extension}");
                    await using (var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write))
                        await download.Content.CopyToAsync(file);
                    await assetReferences.SetAsync(projectFolder, filePath, connectionId, serverUrl,
                        remoteName, subfolder, type, outputTags);
                    files.Add(new(filePath, extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"));
                }
                finally { if (!ReferenceEquals(download, response)) download.Dispose(); }
            }
        }
        return files;
    }

    private static string GetExecutionError(JsonElement status)
    {
        if (status.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (var message in messages.EnumerateArray().Reverse())
                if (message.ValueKind == JsonValueKind.Array && message.GetArrayLength() > 1 &&
                    message[1].ValueKind == JsonValueKind.Object &&
                    message[1].TryGetProperty("exception_message", out var detail))
                    return detail.GetString() ?? "Comfy reported an execution error.";
        return "Comfy reported an execution error.";
    }

    private static string Route(bool cloud, string path) => cloud ? "api/" + path : path;

    private static string SafeFilePart(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Take(40).ToArray());

    private static HttpRequestMessage MakeRequest(HttpMethod method, Uri server, string route, string apiKey)
    {
        var request = new HttpRequestMessage(method, new Uri(server, route));
        if (apiKey.Length > 0) request.Headers.Add("X-API-Key", apiKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 500) body = body[..500] + "…";
        throw new HttpRequestException($"Comfy returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }
}
