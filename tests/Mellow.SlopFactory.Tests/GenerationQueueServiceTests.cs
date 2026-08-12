using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class GenerationQueueServiceTests
{
    private static async Task<(AppLibraryState Libraries, ILibraryWorkspace Workspace, GenerationQueueService Queue, FakeProviderAdapter Adapter, FakeAppPreferenceStore Preferences)> CreateHarnessAsync(string root, FakeAppPreferenceStore? preferences = null)
    {
        var libraries = new AppLibraryState(new LibraryWorkspaceFactory(), new FakeLibraryLocationService(root), new FakeRecentLibraryService(), new LibraryAvailabilityProbe(), new FakeAppPreferenceStore());
        await libraries.InitializeAsync();
        var adapter = new FakeProviderAdapter();
        preferences ??= new FakeAppPreferenceStore();
        var queue = new GenerationQueueService(libraries, new FakeProviderAdapterResolver(adapter), new FakeSecureCredentialStore(), preferences);
        queue.Start();
        return (libraries, libraries.Workspace!, queue, adapter, preferences);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static GenerationJobSnapshot Snapshot(string draftId, string modelId, string prompt, string destinationFolderId) =>
        new(draftId, "Tab", GenerationMode.Text, modelId, prompt, null, null, 1, destinationFolderId, null);

    private static async Task<Connection> CreateReadyConnectionAsync(ILibraryWorkspace workspace, string label)
    {
        var connection = await workspace.CreateConnectionAsync(label, ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var revisionId = await workspace.BeginCredentialCandidateAsync(connection.Id);
        return (await workspace.PromoteCredentialRevisionAsync(connection.Id, revisionId)).Connection;
    }

    [Fact]
    public async Task EnqueueingMultipleJobsOnOneConnectionRunsThemInFifoSubmissionOrder()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-3", model.Id, "prompt3", workspace.Descriptor.GeneratedFolderId), connection.Id);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt2"));
        Assert.DoesNotContain("prompt3", adapter.InvokedPrompts);
        adapter.Complete("prompt2", new TextGenerationResult(["result2"], null, null));

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt3"));
        adapter.Complete("prompt3", new TextGenerationResult(["result3"], null, null));

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-3") is not null);
        Assert.Equal(["prompt1", "prompt2", "prompt3"], adapter.InvokedPrompts);
    }

    [Fact]
    public async Task PerConnectionConcurrencyIsOneEvenWithManyJobsQueued()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        for (var i = 0; i < 5; i++)
        {
            queue.Enqueue(Snapshot($"draft-{i}", model.Id, $"prompt{i}", workspace.Descriptor.GeneratedFolderId), connection.Id);
        }

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 1);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(1, queue.RunningCount);
            adapter.Complete($"prompt{i}", new TextGenerationResult([$"result{i}"], null, null));
            if (i < 4) await WaitUntilAsync(() => adapter.InvokedPrompts.Count == i + 2);
        }

        await WaitUntilAsync(() => queue.RunningCount == 0);
    }

    [Fact]
    public async Task DeviceWideCapLimitsTotalRunningAcrossConnections()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connections = new List<(Connection Connection, Model Model)>();
        for (var i = 0; i < 4; i++)
        {
            var connection = await CreateReadyConnectionAsync(workspace, $"Connection{i}");
            var model = await workspace.CreateModelAsync($"GPT{i}", connection.Id, "gpt-4o", GenerationMode.Text, true);
            connections.Add((connection, model));
        }

        for (var i = 0; i < 4; i++)
        {
            queue.Enqueue(Snapshot($"draft-{i}", connections[i].Model.Id, $"prompt{i}", workspace.Descriptor.GeneratedFolderId), connections[i].Connection.Id);
        }

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 3);
        Assert.Equal(3, queue.RunningCount);
        Assert.Equal(1, queue.QueuedCount);

        adapter.Complete("prompt0", new TextGenerationResult(["result0"], null, null));

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 4);
        Assert.Equal(0, queue.QueuedCount);
    }

    [Fact]
    public async Task SetDeviceCapClampsToThePlatformValidRange()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, queue, _, _) = await CreateHarnessAsync(temporary.Child("library"));

        queue.SetDeviceCap(GenerationQueueService.MinDeviceCap - 10);
        Assert.Equal(GenerationQueueService.MinDeviceCap, queue.DeviceCap);

        queue.SetDeviceCap(GenerationQueueService.MaxDeviceCap + 10);
        Assert.Equal(GenerationQueueService.MaxDeviceCap, queue.DeviceCap);
    }

    [Fact]
    public async Task SetDeviceCapPersistsAcrossServiceInstancesSharingTheSamePreferenceStore()
    {
        using var temporary = new TemporaryDirectory();
        var preferences = new FakeAppPreferenceStore();
        var (libraries, _, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), preferences);
        queue.SetDeviceCap(1);

        var secondQueue = new GenerationQueueService(libraries, new FakeProviderAdapterResolver(adapter), new FakeSecureCredentialStore(), preferences);

        Assert.Equal(1, secondQueue.DeviceCap);
    }

    [Fact]
    public async Task RaisingTheDeviceCapImmediatelyStartsAnAdditionalWaitingJob()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connections = new List<(Connection Connection, Model Model)>();
        for (var i = 0; i < 4; i++)
        {
            var connection = await CreateReadyConnectionAsync(workspace, $"Connection{i}");
            var model = await workspace.CreateModelAsync($"GPT{i}", connection.Id, "gpt-4o", GenerationMode.Text, true);
            connections.Add((connection, model));
        }

        for (var i = 0; i < 4; i++)
        {
            queue.Enqueue(Snapshot($"draft-{i}", connections[i].Model.Id, $"prompt{i}", workspace.Descriptor.GeneratedFolderId), connections[i].Connection.Id);
        }

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 3);
        Assert.Equal(1, queue.QueuedCount);

        queue.SetDeviceCap(4);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 4);
        Assert.Equal(0, queue.QueuedCount);
        Assert.Equal(4, queue.RunningCount);
    }

    [Fact]
    public async Task RoundRobinGivesASecondConnectionATurnRatherThanLettingABacklogConnectionHogAllStartedSlots()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connectionA = await CreateReadyConnectionAsync(workspace, "ConnectionA");
        var modelA = await workspace.CreateModelAsync("GPT-A", connectionA.Id, "gpt-4o", GenerationMode.Text, true);
        var connectionB = await CreateReadyConnectionAsync(workspace, "ConnectionB");
        var modelB = await workspace.CreateModelAsync("GPT-B", connectionB.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-a1", modelA.Id, "a1", workspace.Descriptor.GeneratedFolderId), connectionA.Id);
        queue.Enqueue(Snapshot("draft-a2", modelA.Id, "a2", workspace.Descriptor.GeneratedFolderId), connectionA.Id);
        queue.Enqueue(Snapshot("draft-a3", modelA.Id, "a3", workspace.Descriptor.GeneratedFolderId), connectionA.Id);
        queue.Enqueue(Snapshot("draft-b1", modelB.Id, "b1", workspace.Descriptor.GeneratedFolderId), connectionB.Id);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("b1"));
        Assert.Contains("a1", adapter.InvokedPrompts);
        Assert.DoesNotContain("a2", adapter.InvokedPrompts);
        Assert.DoesNotContain("a3", adapter.InvokedPrompts);
        Assert.Equal(2, queue.RunningCount);

        adapter.Complete("a1", new TextGenerationResult(["ra1"], null, null));
        adapter.Complete("b1", new TextGenerationResult(["rb1"], null, null));
        await WaitUntilAsync(() => queue.RunningCount == 0 || adapter.InvokedPrompts.Contains("a2"));
    }

    [Fact]
    public async Task CursorPicksUpAConnectionsNewArrivalAfterItsQueueHadEmptiedMidRotation()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connectionA = await CreateReadyConnectionAsync(workspace, "ConnectionA");
        var modelA = await workspace.CreateModelAsync("GPT-A", connectionA.Id, "gpt-4o", GenerationMode.Text, true);
        var connectionB = await CreateReadyConnectionAsync(workspace, "ConnectionB");
        var modelB = await workspace.CreateModelAsync("GPT-B", connectionB.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-a1", modelA.Id, "a1", workspace.Descriptor.GeneratedFolderId), connectionA.Id);
        queue.Enqueue(Snapshot("draft-b1", modelB.Id, "b1", workspace.Descriptor.GeneratedFolderId), connectionB.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("a1") && adapter.InvokedPrompts.Contains("b1"));

        adapter.Complete("a1", new TextGenerationResult(["ra1"], null, null));
        await WaitUntilAsync(() => queue.RunningCount == 1);

        queue.Enqueue(Snapshot("draft-a2", modelA.Id, "a2", workspace.Descriptor.GeneratedFolderId), connectionA.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("a2"));
    }

    [Fact]
    public async Task CancellingAQueuedJobNeverInvokesTheAdapterAndRecordsNoGenerationHistory()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var runningJobId = queue.Enqueue(Snapshot("draft-running", model.Id, "running", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var queuedJobId = queue.Enqueue(Snapshot("draft-queued", model.Id, "queued", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("running"));

        queue.Cancel(queuedJobId);
        Assert.DoesNotContain("queued", adapter.InvokedPrompts);
        var queuedOutcome = queue.GetLastOutcomeForDraft("draft-queued");
        Assert.NotNull(queuedOutcome);
        Assert.True(queuedOutcome!.CancelledBeforeSubmission);
        Assert.Null(queuedOutcome.Record);

        adapter.Complete("running", new TextGenerationResult(["r"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-running") is not null);
        var history = await workspace.GetGenerationHistoryAsync();
        Assert.Single(history);
        _ = runningJobId;
    }

    [Fact]
    public async Task CancellingARunningJobTriggersItsTokenAndProducesNoGenerationRecord()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var jobId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt"));

        queue.Cancel(jobId);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-1")!;
        Assert.Null(outcome.Record);
        Assert.False(outcome.CancelledBeforeSubmission);
        Assert.Empty(await workspace.GetGenerationHistoryAsync());
    }

    [Fact]
    public async Task SuccessfulJobCommitsAGenerationRecordAndRemainsTheDraftsLastOutcomeUntilSuperseded()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);

        var firstOutcome = queue.GetLastOutcomeForDraft("draft-1")!;
        Assert.NotNull(firstOutcome.Record);
        var history = await workspace.GetGenerationHistoryAsync();
        Assert.Contains(history, record => record.Id == firstOutcome.Record!.Id);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt2"));
        adapter.Complete("prompt2", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1")!.JobId != firstOutcome.JobId);

        var secondOutcome = queue.GetLastOutcomeForDraft("draft-1")!;
        Assert.NotEqual(firstOutcome.Record!.Id, secondOutcome.Record!.Id);
    }

    [Fact]
    public async Task EnqueueingTwiceForTheSameDraftWhileAJobIsActiveIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var firstId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var secondId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1-again", workspace.Descriptor.GeneratedFolderId), connection.Id);

        Assert.Equal(firstId, secondId);
        Assert.Equal(1, queue.RunningCount + queue.QueuedCount);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        adapter.Complete("prompt1", new TextGenerationResult(["result"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task AJobWhoseModelBecameUnavailableWhileQueuedFailsLocallyWithoutFabricatingARecord()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var modelKept = await workspace.CreateModelAsync("Kept", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var modelDoomed = await workspace.CreateModelAsync("Doomed", connection.Id, "gpt-4o-mini", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-kept", modelKept.Id, "keep", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-doomed", modelDoomed.Id, "doomed", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("keep"));

        await workspace.RecycleModelAsync(modelDoomed.Id);
        await workspace.PermanentlyDeleteModelAsync(modelDoomed.Id);

        adapter.Complete("keep", new TextGenerationResult(["ok"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-doomed") is not null);

        var outcome = queue.GetLastOutcomeForDraft("draft-doomed")!;
        Assert.Null(outcome.Record);
        Assert.NotNull(outcome.LocalErrorMessage);
        Assert.DoesNotContain("doomed", adapter.InvokedPrompts);
        var history = await workspace.GetGenerationHistoryAsync();
        Assert.DoesNotContain(history, record => record.Prompt == "doomed");
    }

    [Fact]
    public async Task LibrarySwitchDropsQueuedAndCancelsRunningJobsTiedToTheOutgoingWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-running", model.Id, "running", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-queued", model.Id, "queued", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("running"));
        Assert.Equal(1, queue.QueuedCount);

        await libraries.SwitchAsync(temporary.Child("library-2"));

        Assert.Equal(0, queue.QueuedCount);
        await WaitUntilAsync(() => queue.RunningCount == 0);
        Assert.Null(queue.GetActiveJobIdForDraft("draft-running"));
        Assert.Null(queue.GetActiveJobIdForDraft("draft-queued"));
    }

    [Fact]
    public async Task GetSnapshotReflectsQueuedAndRunningJobsAcrossConnections()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var runningJobId = queue.Enqueue(Snapshot("draft-running", model.Id, "running", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var queuedJobId = queue.Enqueue(Snapshot("draft-queued", model.Id, "queued", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("running"));

        var snapshot = queue.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        var runningEntry = Assert.Single(snapshot, entry => entry.JobId == runningJobId);
        Assert.Equal(GenerationJobPhase.Running, runningEntry.Phase);
        Assert.Null(runningEntry.QueuePosition);
        var queuedEntry = Assert.Single(snapshot, entry => entry.JobId == queuedJobId);
        Assert.Equal(GenerationJobPhase.Queued, queuedEntry.Phase);
        Assert.Equal(1, queuedEntry.QueuePosition);

        adapter.Complete("running", new TextGenerationResult(["r"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-running") is not null);
    }

    [Fact]
    public async Task ReorderQueuedJobsRewritesOrderAndRejectsAMismatchedSet()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var runningJobId = queue.Enqueue(Snapshot("draft-running", model.Id, "running", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var secondJobId = queue.Enqueue(Snapshot("draft-2", model.Id, "second", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var thirdJobId = queue.Enqueue(Snapshot("draft-3", model.Id, "third", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("running"));

        queue.ReorderQueuedJobs(connection.Id, [thirdJobId, secondJobId]);
        var reordered = queue.GetSnapshot().Where(entry => entry.Phase == GenerationJobPhase.Queued).OrderBy(entry => entry.QueuePosition ?? 0).ToList();
        Assert.Equal([thirdJobId, secondJobId], reordered.Select(entry => entry.JobId).ToArray());

        Assert.Throws<InvalidOperationException>(() => queue.ReorderQueuedJobs(connection.Id, [secondJobId]));
        Assert.Throws<InvalidOperationException>(() => queue.ReorderQueuedJobs(connection.Id, [secondJobId, "unknown-id"]));

        adapter.Complete("running", new TextGenerationResult(["r"], null, null));
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("third"));
        adapter.Complete("third", new TextGenerationResult(["r3"], null, null));
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("second"));
        adapter.Complete("second", new TextGenerationResult(["r2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
        _ = runningJobId;
    }

    private sealed class FakeProviderAdapter : IProviderAdapter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<TextGenerationResult>> _gates = new(StringComparer.Ordinal);
        public List<string> InvokedPrompts { get; } = [];

        public ProviderType ProviderType => ProviderType.OpenAi;

        public void Complete(string prompt, TextGenerationResult result)
        {
            lock (_gate) _gates[prompt].TrySetResult(result);
        }

        public Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, TextGenerationSourceImage? sourceImage = null, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<TextGenerationResult> tcs;
            lock (_gate)
            {
                InvokedPrompts.Add(prompt);
                tcs = _gates.TryGetValue(prompt, out var existing) ? existing : _gates[prompt] = new TaskCompletionSource<TextGenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
    }

    private sealed class FakeProviderAdapterResolver(FakeProviderAdapter adapter) : IProviderAdapterResolver
    {
        public IProviderAdapter Resolve(ProviderType providerType) => adapter;
    }

    private sealed class FakeSecureCredentialStore : ISecureCredentialStore
    {
        public Task<string?> GetActiveAsync(string libraryId, string connectionId, string revisionId) => Task.FromResult<string?>("test-api-key");
        public Task SetActiveAsync(string libraryId, string connectionId, string revisionId, string value) => Task.CompletedTask;
        public Task RemoveActiveAsync(string libraryId, string connectionId, string revisionId) => Task.CompletedTask;
        public Task<string?> GetCandidateAsync(string libraryId, string connectionId, string revisionId) => Task.FromResult<string?>("test-api-key");
        public Task SetCandidateAsync(string libraryId, string connectionId, string revisionId, string value) => Task.CompletedTask;
        public Task RemoveCandidateAsync(string libraryId, string connectionId, string revisionId) => Task.CompletedTask;
        public Task<string?> GetLegacyAsync(string libraryId, string connectionId) => Task.FromResult<string?>("test-api-key");
        public Task RemoveLegacyAsync(string libraryId, string connectionId) => Task.CompletedTask;
    }

    private sealed class FakeLibraryLocationService(string defaultPath) : ILibraryLocationService
    {
        public string DefaultPath => defaultPath;
        public bool IsAllowedPath(string path) => true;
    }

    private sealed class FakeRecentLibraryService : IRecentLibraryService
    {
        public IReadOnlyList<RecentLibrary> GetAll() => [];
        public void RecordOpened(LibraryDescriptor descriptor) { }
        public void RecordFailure(string path, string displayName, string? libraryId, RememberedLibraryState state, string failureStage, string diagnosticId) { }
        public void ValidateNoOverlap(string candidatePath) { }
    }

    private sealed class FakeAppPreferenceStore : IAppPreferenceStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string ReadString(string key, string defaultValue) => _values.TryGetValue(key, out var value) ? value : defaultValue;
        public void WriteString(string key, string value) => _values[key] = value;
    }
}
