using System.Net;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ComfyUiProviderAdapterTests
{
    private const string MinimalWorkflow = """{"3":{"class_type":"KSampler","inputs":{"seed":{{SEED}}}},"6":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}}}""";

    private static Connection CreateConnection(string baseUrl = "https://cloud.comfy.org") =>
        new("connection-1", "Test Connection", ProviderType.ComfyUi, baseUrl, "X-API-Key", "", false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static Model CreateModel(string? workflowTemplate = MinimalWorkflow) =>
        new("model-1", "connection-1", "Test Model", "unused", GenerationMode.Image, false, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            false, TextResultFormat.Markdown, workflowTemplate);

    private static Task<IPAddress[]> PublicAddressResolver(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    [Fact]
    public async Task TestConnectionAsyncSucceedsAndReportsAccountStatus()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://cloud.comfy.org/api/user", request.RequestUri!.ToString());
            Assert.Equal("secret-key", request.Headers.GetValues("X-API-Key").Single());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"EcyUgBJwS4cnZATlg2tLrDchYbn1","status":"active"}""");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));

        var result = await adapter.TestConnectionAsync(CreateConnection(), "secret-key");

        Assert.True(result.Success);
        Assert.Contains("active", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsyncSurfacesTheProviderErrorCodeAndMessageOnAnInvalidKey()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Unauthorized, """{"code":"UNAUTHORIZED","message":"Unauthorized"}"""));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));

        var result = await adapter.TestConnectionAsync(CreateConnection(), "bad-key");

        Assert.False(result.Success);
        Assert.Contains("UNAUTHORIZED", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListModelsAsyncThrowsSinceThereIsNoComparableModelCatalogue()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent: ComfyUI has no comparable model catalogue."));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.ListModelsAsync(CreateConnection(), "secret-key"));
    }

    [Fact]
    public async Task GenerateTextAsyncThrowsSinceOnlyImageModeIsImplemented()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateTextAsync(CreateConnection(), CreateModel(), "secret-key", "prompt", 1));
    }

    [Fact]
    public async Task GenerateAudioAsyncThrowsSinceOnlyImageModeIsImplemented()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(CreateConnection(), CreateModel(), "secret-key", "prompt", 1));
    }

    [Fact]
    public async Task SubmitVideoGenerationAsyncThrowsSinceOnlyImageModeIsImplemented()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(CreateConnection(), CreateModel(), "secret-key", "prompt"));
    }

    [Fact]
    public async Task PollVideoGenerationAsyncThrowsSinceOnlyImageModeIsImplemented()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.PollVideoGenerationAsync(CreateConnection(), "secret-key", "job-id"));
    }

    [Fact]
    public async Task GenerateImageAsyncThrowsWhenTheModelHasNoWorkflowTemplate()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent without a workflow template."));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(CreateConnection(), CreateModel(null), "secret-key", "A fox", 1));
    }

    [Fact]
    public async Task GenerateImageAsyncSubmitsPollsAndDownloadsTheCompletedResult()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        string? submittedWorkflow = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://cloud.comfy.org/api/prompt")
            {
                submittedWorkflow = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"node_errors":{},"prompt_id":"job-1"}""");
            }
            if (url == "https://cloud.comfy.org/api/job/job-1/status")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"success"}""");
            }
            if (url == "https://cloud.comfy.org/api/jobs/job-1")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"id":"job-1","status":"completed","outputs":{"9":{"images":[{"filename":"abc123.png","subfolder":"","type":"output","display_name":"result.png"}]}}}""");
            }
            if (url == "https://cloud.comfy.org/api/view?filename=abc123.png&subfolder=&type=output")
            {
                return FakeHttpMessageHandler.Redirect(HttpStatusCode.Found, "https://storage.googleapis.com/comfy-cloud-assets/abc123.png?signed=1");
            }
            if (url == "https://storage.googleapis.com/comfy-cloud-assets/abc123.png?signed=1")
            {
                Assert.False(request.Headers.Contains("X-API-Key"));
                return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
            }
            throw new InvalidOperationException($"Unexpected request to {url}.");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler), PublicAddressResolver);

        var images = await adapter.GenerateImageAsync(CreateConnection(), CreateModel(), "secret-key", "A watercolor fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
        Assert.NotNull(submittedWorkflow);
        Assert.Contains("\"text\":\"A watercolor fox\"", submittedWorkflow, StringComparison.Ordinal);
        Assert.Contains("\"client_id\":\"slopfactory\"", submittedWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("{{SEED}}", submittedWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("{{PROMPT}}", submittedWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateImageAsyncKeepsPollingWhileTheJobIsPreparing()
    {
        byte[] pngBytes = [1, 2, 3];
        var statusCallCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://cloud.comfy.org/api/prompt") return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"node_errors":{},"prompt_id":"job-1"}""");
            if (url == "https://cloud.comfy.org/api/job/job-1/status")
            {
                statusCallCount++;
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, statusCallCount == 1 ? """{"id":"job-1","status":"preparing"}""" : """{"id":"job-1","status":"success"}""");
            }
            if (url == "https://cloud.comfy.org/api/jobs/job-1")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"completed","outputs":{"9":{"images":[{"filename":"abc.png","subfolder":"","type":"output"}]}}}""");
            }
            return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler), PublicAddressResolver);

        var images = await adapter.GenerateImageAsync(CreateConnection(), CreateModel(), "secret-key", "A fox", 1);

        Assert.Equal(2, statusCallCount);
        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task GenerateImageAsyncThrowsWhenTheJobReportsAnErrorStatus()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://cloud.comfy.org/api/prompt") return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"node_errors":{},"prompt_id":"job-1"}""");
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"error"}""");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(CreateConnection(), CreateModel(), "secret-key", "A fox", 1));
        Assert.Contains("'error'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateImageAsyncThrowsWhenTheWorkflowSubmissionHasNodeErrors()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
            """{"node_errors":{"6":{"errors":[{"message":"Required input is missing"}]}},"prompt_id":"job-1"}"""));
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(CreateConnection(), CreateModel(), "secret-key", "A fox", 1));
        Assert.Contains("node errors", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateImageAsyncUploadsTheSourceImageAndSubstitutesItsFilenameIntoTheWorkflow()
    {
        const string workflowWithImage = """{"6":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}},"10":{"class_type":"LoadImage","inputs":{"image":"{{UPLOADED_IMAGE_FILENAME}}"}},"3":{"class_type":"KSampler","inputs":{"seed":{{SEED}}}}}""";
        byte[] pngBytes = [1, 2, 3];
        byte[] sourceBytes = [9, 9, 9];
        string? submittedWorkflow = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://cloud.comfy.org/api/upload/image")
            {
                Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.ToString(), StringComparison.Ordinal);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("name=image; filename=source.png", body, StringComparison.Ordinal);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"name":"uploaded-abc.png","subfolder":"","type":"input"}""");
            }
            if (url == "https://cloud.comfy.org/api/prompt")
            {
                submittedWorkflow = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"node_errors":{},"prompt_id":"job-1"}""");
            }
            if (url == "https://cloud.comfy.org/api/job/job-1/status") return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"success"}""");
            if (url == "https://cloud.comfy.org/api/jobs/job-1")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"completed","outputs":{"9":{"images":[{"filename":"out.png","subfolder":"","type":"output"}]}}}""");
            }
            return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        TextGenerationSourceImage[] sourceImages = [new("image/png", sourceBytes)];

        var images = await adapter.GenerateImageAsync(CreateConnection(), CreateModel(workflowWithImage), "secret-key", "A fox", 1, sourceImages);

        Assert.Equal(pngBytes, Assert.Single(images));
        Assert.NotNull(submittedWorkflow);
        Assert.Contains("\"image\":\"uploaded-abc.png\"", submittedWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateImageAsyncUploadsTwoSourceImagesAndSubstitutesBothIndexedFilenameTokens()
    {
        const string workflowWithTwoImages = """{"6":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}},"10":{"class_type":"LoadImage","inputs":{"image":"{{UPLOADED_IMAGE_FILENAME}}"}},"11":{"class_type":"LoadImage","inputs":{"image":"{{UPLOADED_IMAGE_FILENAME_2}}"}},"3":{"class_type":"KSampler","inputs":{"seed":{{SEED}}}}}""";
        byte[] pngBytes = [1, 2, 3];
        var uploadCount = 0;
        string? submittedWorkflow = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://cloud.comfy.org/api/upload/image")
            {
                uploadCount++;
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"name":"uploaded-{{uploadCount}}.png","subfolder":"","type":"input"}""");
            }
            if (url == "https://cloud.comfy.org/api/prompt")
            {
                submittedWorkflow = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"node_errors":{},"prompt_id":"job-1"}""");
            }
            if (url == "https://cloud.comfy.org/api/job/job-1/status") return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"success"}""");
            if (url == "https://cloud.comfy.org/api/jobs/job-1")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"job-1","status":"completed","outputs":{"9":{"images":[{"filename":"out.png","subfolder":"","type":"output"}]}}}""");
            }
            return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 1, 1]), new("image/png", [2, 2, 2])];

        var images = await adapter.GenerateImageAsync(CreateConnection(), CreateModel(workflowWithTwoImages), "secret-key", "A fox", 1, sourceImages);

        Assert.Equal(pngBytes, Assert.Single(images));
        Assert.Equal(2, uploadCount);
        Assert.NotNull(submittedWorkflow);
        Assert.Contains("\"10\":{\"class_type\":\"LoadImage\",\"inputs\":{\"image\":\"uploaded-1.png\"}}", submittedWorkflow, StringComparison.Ordinal);
        Assert.Contains("\"11\":{\"class_type\":\"LoadImage\",\"inputs\":{\"image\":\"uploaded-2.png\"}}", submittedWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateImageAsyncSubmitsOneJobPerRequestedResultWithADifferentSeedEachTime()
    {
        byte[] pngBytes = [1, 2, 3];
        var submittedWorkflows = new List<string>();
        var jobCounter = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://cloud.comfy.org/api/prompt")
            {
                jobCounter++;
                submittedWorkflows.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"node_errors":{},"prompt_id":"job-{{jobCounter}}"}""");
            }
            if (url.StartsWith("https://cloud.comfy.org/api/job/", StringComparison.Ordinal)) return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"id":"job-{{jobCounter}}","status":"success"}""");
            if (url.StartsWith("https://cloud.comfy.org/api/jobs/", StringComparison.Ordinal))
            {
                var body = "{\"id\":\"job-" + jobCounter + "\",\"status\":\"completed\",\"outputs\":{\"9\":{\"images\":[{\"filename\":\"out" + jobCounter + ".png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}";
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, body);
            }
            return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
        });
        var adapter = new ComfyUiProviderAdapter(new HttpClient(handler), PublicAddressResolver);

        var images = await adapter.GenerateImageAsync(CreateConnection(), CreateModel(), "secret-key", "A fox", 2);

        Assert.Equal(2, images.Count);
        Assert.Equal(2, submittedWorkflows.Count);
        Assert.NotEqual(submittedWorkflows[0], submittedWorkflows[1]); // different random seed per job
    }
}
