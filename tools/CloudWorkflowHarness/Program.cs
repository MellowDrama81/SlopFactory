using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

var root = Directory.GetCurrentDirectory();
var key = Environment.GetEnvironmentVariable("COMFY_API_KEY");
var server = new Uri((Environment.GetEnvironmentVariable("COMFY_SERVER_URL") ?? "https://cloud.comfy.org").TrimEnd('/') + "/");
var selectedIds = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
var retry = args.Contains("--retry", StringComparer.OrdinalIgnoreCase);
var distinctReferences = args.Contains("--distinct-references", StringComparer.OrdinalIgnoreCase);
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("COMFY_API_KEY is required; no Cloud request was made.");
    return 2;
}

using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
client.DefaultRequestHeaders.Add("X-API-Key", key);
var reportPath = Path.Combine(root, "workflow-cloud-execution.json");
var report = File.Exists(reportPath)
    ? JsonSerializer.Deserialize<List<WorkflowResult>>(await File.ReadAllTextAsync(reportPath)) ?? []
    : [];
var cloudQuotaLimited = false;
var referenceFixtures = new (string Label, byte[] Color)[]
{
    ("red", [220, 38, 38]), ("orange", [234, 88, 12]), ("yellow", [202, 138, 4]),
    ("green", [22, 163, 74]), ("cyan", [8, 145, 178]), ("blue", [37, 99, 235]),
    ("purple", [126, 34, 206]), ("pink", [219, 39, 119]), ("brown", [120, 53, 15]),
    ("gray", [75, 85, 99])
};
var images = new List<string>();
if (distinctReferences)
{
    for (var index = 0; index < referenceFixtures.Length; index++)
    {
        var fixture = referenceFixtures[index];
        images.Add(await UploadAsync($"reference-{index + 1}-{fixture.Label}.png", CreatePng(256, false, fixture.Color)));
    }
}
else images.Add(await UploadAsync("reference.png", CreatePng(256, false)));
var mask = await UploadAsync("mask.png", CreatePng(256, true));

foreach (var metadataPath in Directory.EnumerateFiles(Path.Combine(root, "Workflows"), "*.meta.json").OrderBy(path => path))
{
    using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
    var id = metadata.RootElement.GetProperty("id").GetString()!;
    if (metadata.RootElement.TryGetProperty("cloudCompatible", out var cloudCompatible) && cloudCompatible.ValueKind == JsonValueKind.False) continue;
    if (selectedIds.Count > 0 && !selectedIds.Contains(id)) continue;
    if (!retry && report.Any(item => item.Id == id && (item.ExecutionSucceeded || item.ExecutionStatus is not null || item.Error is not null))) continue;
    report.RemoveAll(item => item.Id == id);
    var graphPath = Path.Combine(root, "Workflows", metadata.RootElement.GetProperty("graphFile").GetString()!);
    var sourceGraph = await File.ReadAllTextAsync(graphPath);
    var result = new WorkflowResult
    {
        Id = id,
        ReferenceImagesUploaded = true,
        ReferenceBindings = DescribeReferenceBindings(sourceGraph, distinctReferences)
    };
    report.Add(result);
    await PersistAsync();
    try
    {
        var graph = Populate(sourceGraph, images, mask);
        using var prompt = JsonDocument.Parse(graph);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/prompt"));
        request.Content = JsonContent.Create(new { prompt = prompt.RootElement, client_id = Guid.NewGuid().ToString(), extra_data = new { api_key_comfy_org = key } });
        using var submitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, submitTimeout.Token);
        var body = await response.Content.ReadAsStringAsync();
        result.SubmissionAccepted = response.IsSuccessStatusCode;
        result.SubmissionStatus = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        using var submitted = JsonDocument.Parse(body);
        result.PromptId = submitted.RootElement.GetProperty("prompt_id").GetString();
        await PersistAsync();
        result.ExecutionStatus = await WaitForCompletionAsync(result.PromptId!);
        result.ExecutionSucceeded = result.ExecutionStatus is "success" or "completed";
        if (result.ExecutionSucceeded) result.OutputCount = await GetOutputCountAsync(result.PromptId!);
        if (!result.ExecutionSucceeded) result.Error = await GetExecutionErrorAsync(result.PromptId!);
    }
    catch (Exception ex)
    {
        result.Error ??= ex.Message;
    }
    await PersistAsync();
    Console.WriteLine($"{id}: {(result.ExecutionSucceeded ? "passed" : result.Error ?? result.ExecutionStatus ?? "failed")}");
    if (result.ExecutionStatus == "queued_limited")
    {
        cloudQuotaLimited = true;
        Console.Error.WriteLine("Cloud execution quota is limited; stopping before additional workflows are queued.");
        break;
    }
}

return cloudQuotaLimited ? 3 : report.All(item => item.ExecutionSucceeded) ? 0 : 1;

async Task<string> UploadAsync(string filename, byte[] bytes)
{
    using var form = new MultipartFormDataContent();
    form.Add(new ByteArrayContent(bytes), "image", filename);
    form.Add(new StringContent("input"), "type");
    using var response = await client.PostAsync(new Uri(server, "api/upload/image"), form);
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var name = document.RootElement.GetProperty("name").GetString()!;
    var subfolder = document.RootElement.TryGetProperty("subfolder", out var value) ? value.GetString() : null;
    return string.IsNullOrWhiteSpace(subfolder) ? name : $"{subfolder.TrimEnd('/', '\\')}/{name}";
}

static string Populate(string graph, IReadOnlyList<string> images, string mask) => Regex.Replace(graph, @"\{\{(?<name>[A-Z][A-Z0-9_]*):(?<type>[a-z]+)\}\}", match => match.Groups["type"].Value switch
{
    "seed" => "1",
    "image" => match.Groups["name"].Value.Contains("MASK", StringComparison.Ordinal) ? mask : images[ReferenceImageIndex(match.Groups["name"].Value, images.Count)],
    "string" => "A simple colorful geometric shape on a plain background.",
    "int" => "1",
    "float" => "1.0",
    _ => throw new InvalidOperationException($"Unsupported placeholder {match.Value}.")
});

static int ReferenceImageIndex(string placeholderName, int imageCount)
{
    var suffix = Regex.Match(placeholderName, @"_(?<index>[0-9]+)$");
    return Math.Clamp(suffix.Success ? int.Parse(suffix.Groups["index"].Value) - 1 : 0, 0, imageCount - 1);
}

static IReadOnlyList<string> DescribeReferenceBindings(string graph, bool distinctReferences) => Regex.Matches(graph, @"\{\{(?<name>[A-Z][A-Z0-9_]*):image\}\}")
    .Select(match =>
    {
        var name = match.Groups["name"].Value;
        return $"{name}={(name.Contains("MASK", StringComparison.Ordinal) ? "mask" : distinctReferences ? $"reference-{ReferenceImageIndex(name, 10) + 1}" : "reference")}";
    })
    .Distinct(StringComparer.Ordinal)
    .ToArray();

static byte[] CreatePng(int size, bool mask, byte[]? color = null)
{
    var raw = new byte[size * (size * 3 + 1)];
    var offset = 0;
    for (var y = 0; y < size; y++)
    {
        raw[offset++] = 0; // PNG's “no filter” byte.
        for (var x = 0; x < size; x++)
        {
            var white = mask && x < size / 2;
            raw[offset++] = white ? (byte)255 : mask ? (byte)0 : color?[0] ?? (byte)100;
            raw[offset++] = white ? (byte)255 : mask ? (byte)0 : color?[1] ?? (byte)149;
            raw[offset++] = white ? (byte)255 : mask ? (byte)0 : color?[2] ?? (byte)237;
        }
    }
    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(raw);
    using var png = new MemoryStream();
    png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    WritePngChunk(png, "IHDR", [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
        (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 8, 2, 0, 0, 0]);
    WritePngChunk(png, "IDAT", compressed.ToArray());
    WritePngChunk(png, "IEND", []);
    return png.ToArray();
}

static void WritePngChunk(Stream destination, string type, byte[] data)
{
    WriteUInt32(destination, (uint)data.Length);
    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    destination.Write(typeBytes);
    destination.Write(data);
    var crc = 0xffffffffu;
    foreach (var value in typeBytes.Concat(data))
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
    }
    WriteUInt32(destination, ~crc);
}

static void WriteUInt32(Stream destination, uint value)
{
    destination.WriteByte((byte)(value >> 24));
    destination.WriteByte((byte)(value >> 16));
    destination.WriteByte((byte)(value >> 8));
    destination.WriteByte((byte)value);
}

async Task<string> WaitForCompletionAsync(string promptId)
{
    // A provider workflow that remains executing for two minutes is recorded as
    // timed out so it cannot prevent the rest of the catalog from being tested.
    for (var attempt = 0; attempt < 60; attempt++)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        using var statusTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var status = await client.GetAsync(new Uri(server, $"api/job/{Uri.EscapeDataString(promptId)}/status"), statusTimeout.Token);
        status.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        var value = document.RootElement.GetProperty("status").GetString() ?? "unknown";
        if (value is "success" or "completed" or "error" or "non_retryable_error" or "lost" or "cancelled" or "failed" or "queued_limited") return value;
    }
    return "timed_out";
}

async Task<string?> GetExecutionErrorAsync(string promptId)
{
    using var detail = await client.GetAsync(new Uri(server, $"api/jobs/{Uri.EscapeDataString(promptId)}"));
    if (!detail.IsSuccessStatusCode) return $"Cloud returned {(int)detail.StatusCode} while reading job detail.";
    using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
    return document.RootElement.TryGetProperty("execution_error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("exception_message", out var message)
        ? message.GetString() : null;
}

async Task<int> GetOutputCountAsync(string promptId)
{
    using var detail = await client.GetAsync(new Uri(server, $"api/jobs/{Uri.EscapeDataString(promptId)}"));
    detail.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
    if (!document.RootElement.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object) return 0;
    return outputs.EnumerateObject().Sum(node => node.Value.ValueKind == JsonValueKind.Object && node.Value.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
        ? images.GetArrayLength() : 0);
}

Task PersistAsync() => File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

sealed class WorkflowResult
{
    public required string Id { get; init; }
    public bool ReferenceImagesUploaded { get; init; }
    public IReadOnlyList<string> ReferenceBindings { get; init; } = [];
    public bool SubmissionAccepted { get; set; }
    public int SubmissionStatus { get; set; }
    public string? PromptId { get; set; }
    public string? ExecutionStatus { get; set; }
    public bool ExecutionSucceeded { get; set; }
    public int OutputCount { get; set; }
    public string? Error { get; set; }
}
