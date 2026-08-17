using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class GenerationQueueServiceTests
{
    private static async Task<(AppLibraryState Libraries, ILibraryWorkspace Workspace, GenerationQueueService Queue, FakeProviderAdapter Adapter, FakeAppPreferenceStore Preferences)> CreateHarnessAsync(string root, FakeAppPreferenceStore? preferences = null, FakeDeviceEnergyStateProvider? energy = null, TimeSpan? videoPollInterval = null, IConnectionRateLimitTracker? rateLimitTracker = null, IDeviceConnectivityStateProvider? connectivity = null, IRecoveryStagingService? recoveryStaging = null, ILibraryAvailabilityProbe? availabilityProbe = null, bool autoStart = true, IBackgroundExecutionService? backgroundExecution = null)
    {
        var libraries = new AppLibraryState(new LibraryWorkspaceFactory(), new FakeLibraryLocationService(root), new FakeRecentLibraryService(), new LibraryAvailabilityProbe(), new FakeAppPreferenceStore());
        await libraries.InitializeAsync();
        var adapter = new FakeProviderAdapter();
        preferences ??= new FakeAppPreferenceStore();
        energy ??= new FakeDeviceEnergyStateProvider();
        var queue = new GenerationQueueService(libraries, new FakeProviderAdapterResolver(adapter), new FakeSecureCredentialStore(), preferences, energy, videoPollInterval, rateLimitTracker, connectivity, recoveryStaging, availabilityProbe, backgroundExecution: backgroundExecution);
        if (autoStart) queue.Start();
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

    private static GenerationJobSnapshot Snapshot(string draftId, string modelId, string prompt, string destinationFolderId, GenerationMode mode = GenerationMode.Text, int resultCount = 1, string? sourceFileId = null) =>
        new(draftId, "Tab", mode, modelId, prompt, null, sourceFileId, resultCount, destinationFolderId, null);

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
    public async Task AConnectionReportingZeroRemainingRequestsDelaysTheNextSubmissionUntilItsResetWindowElapses()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ConnectionRateLimitTracker();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), rateLimitTracker: tracker);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        tracker.Record(connection.Id, new RateLimitObservation(DateTimeOffset.UtcNow, 5000, 0, "200ms", TimeSpan.FromMilliseconds(200), null, null, null, null));

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);

        await Task.Delay(80);
        Assert.DoesNotContain("prompt1", adapter.InvokedPrompts);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task IsConnectionAwaitingRateLimitResetReflectsTheThrottleStateUntilItElapses()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ConnectionRateLimitTracker();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), rateLimitTracker: tracker);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        tracker.Record(connection.Id, new RateLimitObservation(DateTimeOffset.UtcNow, 5000, 0, "150ms", TimeSpan.FromMilliseconds(150), null, null, null, null));

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        Assert.True(queue.IsConnectionAwaitingRateLimitReset(connection.Id));

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.False(queue.IsConnectionAwaitingRateLimitReset(connection.Id));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task IsJobAwaitingRateLimitResetIsThePerJobEquivalentOfTheConnectionLevelCheck()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ConnectionRateLimitTracker();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), rateLimitTracker: tracker);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        tracker.Record(connection.Id, new RateLimitObservation(DateTimeOffset.UtcNow, 5000, 0, "150ms", TimeSpan.FromMilliseconds(150), null, null, null, null));

        var jobId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        Assert.True(queue.IsJobAwaitingRateLimitReset(jobId));

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.False(queue.IsJobAwaitingRateLimitReset(jobId));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task IsJobAwaitingRateLimitResetIsFalseForAnUnknownOrRunningJob()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ConnectionRateLimitTracker();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), rateLimitTracker: tracker);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        Assert.False(queue.IsJobAwaitingRateLimitReset("never-enqueued"));

        var jobId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.False(queue.IsJobAwaitingRateLimitReset(jobId));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task ARemainingRequestCountAboveZeroDoesNotDelaySubmission()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ConnectionRateLimitTracker();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), rateLimitTracker: tracker);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        tracker.Record(connection.Id, new RateLimitObservation(DateTimeOffset.UtcNow, 5000, 4999, "1s", TimeSpan.FromSeconds(1), null, null, null, null));

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
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

        var secondQueue = new GenerationQueueService(libraries, new FakeProviderAdapterResolver(adapter), new FakeSecureCredentialStore(), preferences, new FakeDeviceEnergyStateProvider());

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
    public async Task SetConnectionCapClampsToThePlatformValidRange()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, _, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");

        queue.SetConnectionCap(connection.Id, GenerationQueueService.MinConnectionCap - 10);
        Assert.Equal(GenerationQueueService.MinConnectionCap, queue.GetConnectionCap(connection.Id));

        queue.SetConnectionCap(connection.Id, GenerationQueueService.MaxConnectionCap + 10);
        Assert.Equal(GenerationQueueService.MaxConnectionCap, queue.GetConnectionCap(connection.Id));
    }

    [Fact]
    public async Task SetConnectionCapPersistsAcrossServiceInstancesSharingTheSamePreferenceStore()
    {
        using var temporary = new TemporaryDirectory();
        var preferences = new FakeAppPreferenceStore();
        var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), preferences);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        queue.SetConnectionCap(connection.Id, 3);

        var secondQueue = new GenerationQueueService(libraries, new FakeProviderAdapterResolver(adapter), new FakeSecureCredentialStore(), preferences, new FakeDeviceEnergyStateProvider());

        Assert.Equal(3, secondQueue.GetConnectionCap(connection.Id));
    }

    [Fact]
    public async Task RaisingTheConnectionCapLetsMultipleJobsOnTheSameConnectionRunConcurrently()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        for (var i = 0; i < 3; i++)
        {
            queue.Enqueue(Snapshot($"draft-{i}", model.Id, $"prompt{i}", workspace.Descriptor.GeneratedFolderId), connection.Id);
        }

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 1);
        Assert.Equal(1, queue.RunningCount);
        Assert.Equal(2, queue.QueuedCount);

        queue.SetConnectionCap(connection.Id, 3);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 3);
        Assert.Equal(0, queue.QueuedCount);
        Assert.Equal(3, queue.RunningCount);
    }

    [Fact]
    public async Task EnergySaverCapActiveReflectsTheProviderState()
    {
        using var temporary = new TemporaryDirectory();
        var energy = new FakeDeviceEnergyStateProvider();
        var (_, _, queue, _, _) = await CreateHarnessAsync(temporary.Child("library"), energy: energy);

        Assert.False(queue.EnergySaverCapActive);
        Assert.Equal(queue.DeviceCap, queue.EffectiveDeviceCap);

        energy.IsEnergySaverOn = true;

        Assert.True(queue.EnergySaverCapActive);
        Assert.Equal(1, queue.EffectiveDeviceCap);
    }

    [Fact]
    public async Task EnablingEnergySaverStopsNewStartsWithoutCancellingAlreadyRunningJobs()
    {
        using var temporary = new TemporaryDirectory();
        var energy = new FakeDeviceEnergyStateProvider();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), energy: energy);
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

        energy.IsEnergySaverOn = true;
        await Task.Delay(50);

        Assert.Equal(3, queue.RunningCount);
        Assert.Equal(1, queue.QueuedCount);

        adapter.Complete("prompt0", new TextGenerationResult(["result0"], null, null));
        await WaitUntilAsync(() => queue.RunningCount == 2);
        await Task.Delay(50);

        Assert.Equal(1, queue.QueuedCount);
        Assert.DoesNotContain("prompt3", adapter.InvokedPrompts);

        energy.IsEnergySaverOn = false;

        await WaitUntilAsync(() => adapter.InvokedPrompts.Count == 4);
        Assert.Equal(0, queue.QueuedCount);
    }

    [Fact]
    public async Task GoingOfflinePausesNewSubmissionsWithoutCancellingAnAlreadyRunningJob()
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        // A second, independent connection so prompt2's block below is unambiguously attributable to
        // the device-wide connectivity latch, not the (also-true) per-connection concurrency cap.
        var secondConnection = await CreateReadyConnectionAsync(workspace, "SecondConnection");
        var secondModel = await workspace.CreateModelAsync("GPT2", secondConnection.Id, "gpt-4o", GenerationMode.Text, true);
        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));

        connectivity.IsOffline = true;
        var pausedJobId = queue.Enqueue(Snapshot("draft-2", secondModel.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), secondConnection.Id);
        await Task.Delay(50);

        Assert.True(queue.IsPausedForConnectionLost);
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);
        Assert.Equal(1, queue.RunningCount); // the already-running job is unaffected
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(pausedJobId)!.Phase);

        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        await Task.Delay(50);
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts); // still paused even though a slot freed up
    }

    [Fact]
    public async Task ResumeJobOnlyStartsTheExplicitlyApprovedPausedJob()
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider { IsOffline = true };
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Model", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var firstJobId = queue.Enqueue(Snapshot("draft-1", model.Id, "first", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var secondJobId = queue.Enqueue(Snapshot("draft-2", model.Id, "second", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await Task.Delay(50);
        connectivity.IsOffline = false;

        Assert.True(queue.ResumeJob(secondJobId));
        Assert.False(queue.ResumeJob("missing"));
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("second"));
        Assert.DoesNotContain("first", adapter.InvokedPrompts);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(firstJobId)!.Phase);

        adapter.Complete("second", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
    }

    [Fact]
    public async Task ResumeQueueForConnectionOnlyStartsTheExplicitlyApprovedConnection()
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider { IsOffline = true };
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        var firstConnection = await CreateReadyConnectionAsync(workspace, "First");
        var firstModel = await workspace.CreateModelAsync("First model", firstConnection.Id, "gpt-4o", GenerationMode.Text, true);
        var secondConnection = await CreateReadyConnectionAsync(workspace, "Second");
        var secondModel = await workspace.CreateModelAsync("Second model", secondConnection.Id, "gpt-4o", GenerationMode.Text, true);

        var firstJobId = queue.Enqueue(Snapshot("draft-1", firstModel.Id, "first", workspace.Descriptor.GeneratedFolderId), firstConnection.Id);
        var secondJobId = queue.Enqueue(Snapshot("draft-2", secondModel.Id, "second", workspace.Descriptor.GeneratedFolderId), secondConnection.Id);
        await Task.Delay(50);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(firstJobId)!.Phase);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(secondJobId)!.Phase);

        connectivity.IsOffline = false;
        queue.ResumeQueueForConnection(firstConnection.Id);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("first"));
        Assert.DoesNotContain("second", adapter.InvokedPrompts);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(secondJobId)!.Phase);

        adapter.Complete("first", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        queue.ResumeQueueForConnection(secondConnection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("second"));
        adapter.Complete("second", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
    }

    [Fact]
    public async Task ConnectionLostStaysPausedAfterConnectivityReturnsUntilResumeQueueIsCalled()
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        connectivity.IsOffline = true;
        var pausedJobId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await Task.Delay(50);
        Assert.True(queue.IsPausedForConnectionLost);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(pausedJobId)!.Phase);

        connectivity.IsOffline = false; // connectivity returns, but the pause is manual-resume-only
        await Task.Delay(50);
        Assert.DoesNotContain("prompt1", adapter.InvokedPrompts);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(pausedJobId)!.Phase);

        queue.ResumeQueue();

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.False(queue.IsPausedForConnectionLost);
    }

    [Fact]
    public async Task MeteredNetworkPolicyAllowNeverPausesSubmissions()
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider { IsMetered = true };
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.False(queue.IsPausedForMeteredNetwork);
    }

    [Theory]
    [InlineData(MeteredNetworkTransferPolicy.WifiOnly)]
    [InlineData(MeteredNetworkTransferPolicy.Ask)]
    public async Task MeteredNetworkPolicyBlockingModesPauseNewSubmissionsUntilResumed(MeteredNetworkTransferPolicy policy)
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider { IsMetered = true };
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        queue.SetMeteredNetworkPolicy(policy);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        var pausedJobId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await Task.Delay(50);

        Assert.True(queue.IsPausedForMeteredNetwork);
        Assert.DoesNotContain("prompt1", adapter.InvokedPrompts);
        Assert.Equal(GenerationJobPhase.PausedMeteredNetwork, queue.GetJobStatus(pausedJobId)!.Phase);

        queue.ResumeQueue();

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.False(queue.IsPausedForMeteredNetwork);
    }

    [Fact]
    public async Task AConnectivityTransitionClearsAnEarlierResumeQueueOverrideSoTheNextMeteredSubmissionPausesAgain()
    {
        using var temporary = new TemporaryDirectory();
        var connectivity = new FakeDeviceConnectivityStateProvider { IsMetered = true };
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        queue.SetMeteredNetworkPolicy(MeteredNetworkTransferPolicy.Ask);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await Task.Delay(50);
        queue.ResumeQueue();
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);

        // A real connectivity transition (still metered, but a distinct event) resets the override —
        // the next submission needs a fresh resume rather than inheriting the earlier one forever.
        connectivity.IsMetered = false;
        connectivity.IsMetered = true;
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await Task.Delay(50);

        Assert.True(queue.IsPausedForMeteredNetwork);
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);
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
    public async Task CancellingAQueuedJobNeverInvokesTheAdapterButFinalizesItsDurableRecord()
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
        // The immediate in-memory outcome still never fabricates a GenerationRecord — nothing was
        // ever sent to a provider — but the durable Queued row created when the job entered the
        // queue is finalized to CancelledBeforeSubmission instead of being left stranded.
        Assert.Null(queuedOutcome.Record);

        adapter.Complete("running", new TextGenerationResult(["r"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-running") is not null);
        IReadOnlyList<GenerationRecord> history = [];
        await WaitUntilAsync(() => (history = workspace.GetGenerationHistoryAsync().GetAwaiter().GetResult()).Count == 2);
        var queuedRecord = Assert.Single(history, record => record.Prompt == "queued");
        Assert.Equal(GenerationStatus.CancelledBeforeSubmission, queuedRecord.Status);
        _ = runningJobId;
    }

    [Fact]
    public async Task CancellingARunningJobTriggersItsTokenAndFinalizesItsRecordAsSubmissionOutcomeUnknown()
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
        // Whether the request reached the provider before the cancellation fired is genuinely
        // unknown — the durable record created when the job entered the queue is finalized to
        // SubmissionOutcomeUnknown immediately rather than being silently discarded, guessed at as a
        // terminal outcome, or left stranded until a restart happens to sweep it up (full Attempt
        // Reconciliation against this status is a later phase).
        GenerationRecord? record = null;
        await WaitUntilAsync(() => (record = workspace.GetGenerationHistoryAsync().GetAwaiter().GetResult().SingleOrDefault(entry => entry.Prompt == "prompt")) is { Status: GenerationStatus.SubmissionOutcomeUnknown });
        Assert.NotNull(record);
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
    public async Task EnqueueingTwiceForTheSameDraftWhileAJobIsActiveStartsASecondIndependentJob()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        queue.SetConnectionCap(connection.Id, 2);

        var firstId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var secondId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1-again", workspace.Descriptor.GeneratedFolderId), connection.Id);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, queue.RunningCount + queue.QueuedCount);
        Assert.Equal([firstId, secondId], queue.GetActiveJobIdsForDraft("draft-1"));

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1") && adapter.InvokedPrompts.Contains("prompt1-again"));
        adapter.Complete("prompt1", new TextGenerationResult(["result"], null, null));
        adapter.Complete("prompt1-again", new TextGenerationResult(["result-again"], null, null));
        await WaitUntilAsync(() => queue.GetRecentOutcomesForDraft("draft-1").Count == 2);
        Assert.Empty(queue.GetActiveJobIdsForDraft("draft-1"));
    }

    [Fact]
    public async Task CancellingOneConcurrentRunOnADraftDoesNotAffectItsOtherActiveRun()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        queue.SetConnectionCap(connection.Id, 2);

        var firstId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        var secondId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1") && adapter.InvokedPrompts.Contains("prompt2"));

        queue.Cancel(firstId);
        await WaitUntilAsync(() => queue.GetRecentOutcomesForDraft("draft-1").Count == 1);

        Assert.Equal([secondId], queue.GetActiveJobIdsForDraft("draft-1"));
        adapter.Complete("prompt2", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetRecentOutcomesForDraft("draft-1").Count == 2);
        var outcomes = queue.GetRecentOutcomesForDraft("draft-1");
        Assert.Contains(outcomes, outcome => outcome.JobId == firstId && outcome.Record is null && !outcome.CancelledBeforeSubmission);
        Assert.Contains(outcomes, outcome => outcome.JobId == secondId && outcome.Record is not null);
    }

    [Fact]
    public async Task RecentOutcomesForADraftAreCappedAtTenNewestFirst()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        for (var i = 0; i < 11; i++)
        {
            var prompt = $"prompt{i}";
            queue.Enqueue(Snapshot("draft-1", model.Id, prompt, workspace.Descriptor.GeneratedFolderId), connection.Id);
            await WaitUntilAsync(() => adapter.InvokedPrompts.Contains(prompt));
            adapter.Complete(prompt, new TextGenerationResult([$"result{i}"], null, null));
            await WaitUntilAsync(() => queue.GetRecentOutcomesForDraft("draft-1").Any(outcome => outcome.Record?.Prompt == prompt));
        }

        var outcomes = queue.GetRecentOutcomesForDraft("draft-1");
        Assert.Equal(10, outcomes.Count);
        Assert.Equal("prompt10", outcomes[0].Record!.Prompt);
        Assert.DoesNotContain(outcomes, outcome => outcome.Record?.Prompt == "prompt0");
    }

    [Fact]
    public async Task AJobWhoseModelBecameUnavailableWhileQueuedFailsLocallyWithoutInvokingTheAdapter()
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
        // The immediate in-memory outcome never fabricates a GenerationRecord for a local failure —
        // no provider call was ever made — but the durable Queued row created when the job entered
        // the queue is finalized to Failed rather than left stranded at a nonterminal status.
        Assert.Null(outcome.Record);
        Assert.NotNull(outcome.LocalErrorMessage);
        Assert.DoesNotContain("doomed", adapter.InvokedPrompts);
        GenerationRecord? doomedRecord = null;
        await WaitUntilAsync(() => (doomedRecord = workspace.GetGenerationHistoryAsync().GetAwaiter().GetResult().SingleOrDefault(record => record.Prompt == "doomed")) is not null);
        Assert.Equal(GenerationStatus.Failed, doomedRecord!.Status);
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
    public async Task RegisteringTheKeepOpenPredicateLetsALibrarySwitchAwayFromKeepActiveWorkRunning()
    {
        using var temporary = new TemporaryDirectory();
        var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        // Mirrors MainLayout.razor's real startup registration.
        libraries.RegisterKeepOpenPredicate(queue.HasActiveWorkFor);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var libraryId = workspace.Descriptor.LibraryId;

        queue.Enqueue(Snapshot("draft-running", model.Id, "running", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("running"));

        await libraries.SwitchAsync(temporary.Child("library-2"));

        // Unlike the no-predicate case above, the job keeps running rather than being cancelled, and
        // the outgoing library is tracked as a background activity instead of disposed.
        Assert.NotNull(queue.GetActiveJobIdForDraft("draft-running"));
        Assert.Contains(libraries.BackgroundLibraries, entry => entry.LibraryId == libraryId);
        Assert.True(libraries.HasActiveWorkFor(libraryId));
        Assert.Equal(1, queue.GetActiveJobCountForWorkspace(workspace));

        adapter.Complete("running", new TextGenerationResult(["result"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-running") is not null);
        Assert.NotNull(queue.GetLastOutcomeForDraft("draft-running")!.Record);

        // plan.md:430 — the background lock releases once the last operation completes.
        await WaitUntilAsync(() => !libraries.BackgroundLibraries.Any(entry => entry.LibraryId == libraryId));
        Assert.False(libraries.HasActiveWorkFor(libraryId));
    }

    [Fact]
    public async Task SwitchingBackToABackgroundLibraryReusesItsInstanceInsteadOfFailingToReacquireItsLock()
    {
        using var temporary = new TemporaryDirectory();
        var libraryOnePath = temporary.Child("library");
        var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(libraryOnePath);
        libraries.RegisterKeepOpenPredicate(queue.HasActiveWorkFor);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var originalWorkspace = workspace;

        queue.Enqueue(Snapshot("draft-running", model.Id, "running", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("running"));

        await libraries.SwitchAsync(temporary.Child("library-2"));
        Assert.NotEmpty(libraries.BackgroundLibraries);

        // Switching back must not attempt to re-acquire an exclusive lock this same process already
        // holds via the background-kept instance — it should reuse it instead of throwing.
        await libraries.SwitchAsync(libraryOnePath);
        Assert.Same(originalWorkspace, libraries.Workspace);
        Assert.Empty(libraries.BackgroundLibraries);

        adapter.Complete("running", new TextGenerationResult(["result"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-running") is not null);
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

    [Fact]
    public async Task JobCompletedFiresExactlyOnceForEveryFinishedJobAcrossSuccessFailureAndCancellation()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var modelKept = await workspace.CreateModelAsync("Kept", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var modelDoomed = await workspace.CreateModelAsync("Doomed", connection.Id, "gpt-4o-mini", GenerationMode.Text, true);
        await workspace.RecycleModelAsync(modelDoomed.Id);
        await workspace.PermanentlyDeleteModelAsync(modelDoomed.Id);
        var completions = new List<GenerationJobOutcome>();
        queue.JobCompleted += (_, outcome) => { lock (completions) completions.Add(outcome); };

        queue.Enqueue(Snapshot("draft-success", modelKept.Id, "success", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("success"));
        adapter.Complete("success", new TextGenerationResult(["r"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-success") is not null);

        var cancelJobId = queue.Enqueue(Snapshot("draft-cancel", modelKept.Id, "cancel-me", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("cancel-me"));
        queue.Cancel(cancelJobId);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-cancel") is not null);

        queue.Enqueue(Snapshot("draft-doomed", modelDoomed.Id, "doomed", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-doomed") is not null);

        await WaitUntilAsync(() => completions.Count(outcome => outcome.DraftId is "draft-success" or "draft-cancel" or "draft-doomed") == 3);
        await Task.Delay(50);

        Assert.Single(completions, outcome => outcome.DraftId == "draft-success" && outcome.Record is not null);
        Assert.Single(completions, outcome => outcome.DraftId == "draft-cancel" && outcome.Record is null && !outcome.CancelledBeforeSubmission);
        Assert.Single(completions, outcome => outcome.DraftId == "draft-doomed" && outcome.Record is null && outcome.LocalErrorMessage is not null);
        Assert.DoesNotContain("doomed", adapter.InvokedPrompts);
    }

    [Fact]
    public async Task AudioGenerationCommitsResultFilesThroughRecordMediaGenerationResult()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("TTS", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
        adapter.SetAudioResult("Read this aloud", [[0x49, 0x44, 0x33, 1, 2, 3]]);

        queue.Enqueue(Snapshot("draft-audio", model.Id, "Read this aloud", workspace.Descriptor.GeneratedFolderId, GenerationMode.Audio), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-audio") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-audio")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Completed, outcome.Record!.Status);
        Assert.Single(outcome.Record.ResultFileIds);
    }

    [Fact]
    public async Task AudioGenerationFailureIsCommittedAsALocalFailedGenerationRecord()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("TTS", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
        // Deliberately not configuring an audio result for this prompt, so GenerateAudioAsync throws.

        queue.Enqueue(Snapshot("draft-audio-fail", model.Id, "Unconfigured prompt", workspace.Descriptor.GeneratedFolderId, GenerationMode.Audio), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-audio-fail") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-audio-fail")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Failed, outcome.Record!.Status);
        Assert.Empty(outcome.Record.ResultFileIds);
    }

    [Fact]
    public async Task VideoGenerationSubmitsPersistsAndPollsUntilCompletedThenCleansUpTheAsyncJobRegistryEntry()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-job-42";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null));
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null));
        byte[] mp4SignatureBytes = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [mp4SignatureBytes], null));

        queue.Enqueue(Snapshot("draft-video", model.Id, "A cat on a skateboard", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);

        // The async job becomes visible in the pending registry while polling is in progress.
        await WaitUntilAsync(() => adapter.VideoPollCount >= 1);
        var pendingDuringPoll = await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id);
        Assert.Contains(pendingDuringPoll, job => job.ProviderJobId == "video-job-42");

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Completed, outcome.Record!.Status);
        Assert.Single(outcome.Record.ResultFileIds);
        Assert.Equal(3, adapter.VideoPollCount);

        // The registry entry is removed once the generation has been committed.
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
    }

    [Fact]
    public async Task ACompletedVideoJobWhoseDownloadFailsLeavesARecoverableAsyncJobLinkedToItsFailedPosition()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-job-download-fail";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading the completed video result failed."));

        queue.Enqueue(Snapshot("draft-video", model.Id, "A cat on a skateboard", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video") is not null);

        var outcome = queue.GetLastOutcomeForDraft("draft-video")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Failed, outcome.Record!.Status);
        var failedEntry = Assert.Single(outcome.Record.Results);
        Assert.Equal(GenerationResultStatus.Failed, failedEntry.Status);
        Assert.Contains("download", failedEntry.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // The registry row survives — linked to the exact failed position — instead of being
        // deleted like every other terminal outcome, so Refresh Provider Status has something to
        // retry against.
        var asyncJob = Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
        Assert.Equal(AsyncRemoteJobPhase.CompletedAwaitingDownload, asyncJob.Phase);
        Assert.Equal(outcome.Record.Id, asyncJob.GenerationRecordId);
        Assert.Equal(0, asyncJob.Position);
    }

    [Fact]
    public async Task RetryMissingResultDownloadCommitsTheRecoveredFileAndRemovesTheRegistryRowOnSuccess()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-job-download-fail";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading the completed video result failed."));
        queue.Enqueue(Snapshot("draft-video", model.Id, "A cat on a skateboard", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video") is not null);
        var record = queue.GetLastOutcomeForDraft("draft-video")!.Record!;
        var asyncJob = Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));

        byte[] mp4SignatureBytes = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [mp4SignatureBytes], null));

        var recovered = await queue.RetryMissingResultDownloadAsync(asyncJob.Id);

        Assert.True(recovered);
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
        var reloaded = await workspace.GetGenerationRecordAsync(record.Id);
        var committed = Assert.Single(reloaded.Results);
        Assert.Equal(GenerationResultStatus.Committed, committed.Status);
        Assert.Single(reloaded.ResultFileIds);
    }

    [Fact]
    public async Task RepeatedRefreshProviderStatusAttemptsWithinTheThrottleWindowAreRejectedWithoutPolling()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-job-throttle";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading the completed video result failed."));
        queue.Enqueue(Snapshot("draft-video", model.Id, "A cat on a skateboard", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video") is not null);
        var asyncJob = Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));

        // First attempt consumes the one queued poll result and fails again (registry row untouched);
        // a second attempt hot on its heels must be rejected locally rather than polling a second time.
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading the completed video result failed again."));
        Assert.False(await queue.RetryMissingResultDownloadAsync(asyncJob.Id));
        var pollCountAfterFirstAttempt = adapter.VideoPollCount;

        Assert.False(await queue.RetryMissingResultDownloadAsync(asyncJob.Id));
        Assert.Equal(pollCountAfterFirstAttempt, adapter.VideoPollCount);
    }

    [Fact]
    public async Task RetryMissingResultDownloadLeavesTheRegistryRowUntouchedWhenTheDownloadFailsAgain()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-job-download-fail";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading the completed video result failed."));
        queue.Enqueue(Snapshot("draft-video", model.Id, "A cat on a skateboard", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video") is not null);
        var asyncJob = Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));

        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading the completed video result failed again."));

        var recovered = await queue.RetryMissingResultDownloadAsync(asyncJob.Id);

        Assert.False(recovered);
        var stillPending = Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
        Assert.Equal(AsyncRemoteJobPhase.CompletedAwaitingDownload, stillPending.Phase);
    }

    [Fact]
    public async Task StartingTheQueueAgainstAnAlreadyOpenLibraryAutomaticallyResumesACompletedButUndownloadedResult()
    {
        using var temporary = new TemporaryDirectory();
        var libraryPath = temporary.Child("library");
        byte[] mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];

        // "First session": a video result completes at the provider but its download fails,
        // leaving a CompletedAwaitingDownload registry row, then the app "closes."
        {
            var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(libraryPath, videoPollInterval: TimeSpan.FromMilliseconds(5));
            var connection = await CreateReadyConnectionAsync(workspace, "Connection");
            var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
            adapter.NextVideoJobId = "video-resume";
            adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, "Downloading failed."));
            queue.Enqueue(Snapshot("draft-video-resume", model.Id, "A cat", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
            await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-resume") is not null);
            Assert.Single(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
            await libraries.DisposeAsync();
        }

        // "Second session": reopening the same library and starting a fresh queue (no manual
        // Refresh Provider Status call) resolves the leftover row on its own. autoStart is deferred
        // so the poll result can be queued before Start()'s resume pass runs — otherwise Start()
        // would race the adapter setup below and find nothing queued yet.
        var (_, workspace2, queue2, adapter2, _) = await CreateHarnessAsync(libraryPath, videoPollInterval: TimeSpan.FromMilliseconds(5), autoStart: false);
        adapter2.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [mp4], null));
        queue2.Start();

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while ((await workspace2.GetPendingAsyncRemoteJobsAsync()).Count > 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The leftover async job was never resumed.");
            await Task.Delay(10);
        }
        var record = Assert.Single(await workspace2.GetGenerationHistoryAsync());
        Assert.Single(record.ResultFileIds);
    }

    [Fact]
    public async Task StartingTheQueueAgainstAnAlreadyOpenLibraryAutomaticallyResumesAQueuedTextJobAfterARestartWithoutResubmittingAnInFlightOne()
    {
        using var temporary = new TemporaryDirectory();
        var libraryPath = temporary.Child("library");

        // "First session": one job is left running (blocked mid-call on the adapter) and a second
        // job on the same connection is left genuinely Queued behind it (the default per-connection
        // cap is 1), then the app "closes" mid-flight without either job ever completing.
        {
            var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(libraryPath);
            var connection = await CreateReadyConnectionAsync(workspace, "Connection");
            var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

            queue.Enqueue(Snapshot("draft-blocked", model.Id, "blocked", workspace.Descriptor.GeneratedFolderId), connection.Id);
            queue.Enqueue(Snapshot("draft-queued-restart", model.Id, "queued-restart", workspace.Descriptor.GeneratedFolderId), connection.Id);
            await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("blocked"));

            // The Submitting transition for "blocked" is written fire-and-forget (best-effort
            // telemetry, not the terminal write), so wait for it to actually land before disposing —
            // otherwise this test would race it and could observe "blocked" still durably Queued.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                var history = await workspace.GetGenerationHistoryAsync();
                var blockedStatus = history.SingleOrDefault(record => record.Prompt == "blocked")?.Status;
                var queuedStatus = history.SingleOrDefault(record => record.Prompt == "queued-restart")?.Status;
                if (blockedStatus == GenerationStatus.Submitting && queuedStatus == GenerationStatus.Queued) break;
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"The expected pre-restart durable statuses were never reached. blocked={blockedStatus} queued={queuedStatus} historyCount={history.Count}");
                await Task.Delay(10);
            }
            await libraries.DisposeAsync();
        }

        // "Second session": reopening resumes the still-Queued job on its own and runs it to
        // completion, while the job that was left mid-flight is never resubmitted (no polling
        // handle exists for a synchronous Text/Image/Audio adapter call once the process dies).
        var (_, workspace2, queue2, adapter2, _) = await CreateHarnessAsync(libraryPath, autoStart: false);
        queue2.Start();
        await WaitUntilAsync(() => adapter2.InvokedPrompts.Contains("queued-restart"), timeoutMs: 4000);
        Assert.DoesNotContain("blocked", adapter2.InvokedPrompts);
        adapter2.Complete("queued-restart", new TextGenerationResult(["resumed result"], null, null));

        GenerationRecord? resumedRecord = null;
        await WaitUntilAsync(() => (resumedRecord = workspace2.GetGenerationHistoryAsync().GetAwaiter().GetResult().SingleOrDefault(record => record.Prompt == "queued-restart")) is { Status: GenerationStatus.Completed });
        Assert.Equal(GenerationStatus.Completed, resumedRecord!.Status);

        GenerationRecord? blockedRecord = null;
        await WaitUntilAsync(() => (blockedRecord = workspace2.GetGenerationHistoryAsync().GetAwaiter().GetResult().SingleOrDefault(record => record.Prompt == "blocked")) is not null
            && blockedRecord.Status == GenerationStatus.SubmissionOutcomeUnknown);
    }

    [Fact]
    public async Task VideoGenerationReleasesItsConnectionSlotAfterSubmissionSoOtherQueuedWorkCanRunConcurrently()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromSeconds(2));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var videoModel = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        var textModel = await workspace.CreateModelAsync("Text", connection.Id, "gpt-4o", GenerationMode.Text, true);
        adapter.NextVideoJobId = "video-slot-test";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null));
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null));

        // The default per-connection concurrency cap is 1 — without releasing the video job's slot
        // after submission, this text job could not start until the video job's entire poll loop
        // (which never even resolves in this test) finished.
        var videoJobId = queue.Enqueue(Snapshot("draft-video-slot", videoModel.Id, "A cat", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        queue.Enqueue(Snapshot("draft-text-slot", textModel.Id, "concurrent-text", workspace.Descriptor.GeneratedFolderId), connection.Id);

        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("concurrent-text"), timeoutMs: 4000);

        var videoStatus = queue.GetJobStatus(videoJobId);
        Assert.NotNull(videoStatus);
        Assert.Equal(GenerationJobPhase.Monitoring, videoStatus!.Phase);

        adapter.Complete("concurrent-text", new TextGenerationResult(["result"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-text-slot") is not null);
    }

    [Fact]
    public async Task VideoGenerationFailureCommitsAFailedRecordAndRemovesTheAsyncJobRegistryEntry()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-job-99";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, "The prompt violated content policy."));

        queue.Enqueue(Snapshot("draft-video-fail", model.Id, "A forbidden prompt", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-fail") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-fail")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Failed, outcome.Record!.Status);
        Assert.Equal("The prompt violated content policy.", outcome.Record.ErrorMessage);
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
    }

    [Fact]
    public async Task VideoGenerationCancelledAfterSubmissionButBeforeAnyPollCommitsACancelledRecordWithNoResults()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromSeconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-cancel-early";

        var jobId = queue.Enqueue(Snapshot("draft-video-cancel-early", model.Id, "A cat", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        await WaitUntilAsync(() => adapter.SubmittedVideoJobIds.Count >= 1);
        queue.Cancel(jobId);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-cancel-early") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-cancel-early")!;

        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Cancelled, outcome.Record!.Status);
        Assert.Empty(outcome.Record.ResultFileIds);
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
    }

    [Fact]
    public async Task VideoGenerationCancelledAfterSomeJobsCompleteCommitsACancelledWithResultsRecordKeepingWhatFinished()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(300));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-cancel-partial";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null));
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null));

        var jobId = queue.Enqueue(Snapshot("draft-video-cancel-partial", model.Id, "Two cats on skateboards", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video, resultCount: 2), connection.Id);
        await WaitUntilAsync(() => adapter.VideoPollCount >= 2);
        queue.Cancel(jobId);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-cancel-partial") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-cancel-partial")!;

        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.CancelledWithResults, outcome.Record!.Status);
        Assert.Single(outcome.Record.ResultFileIds);
        Assert.Null(outcome.Record.ErrorMessage);
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
    }

    private static readonly byte[] Mp4SignatureBytes = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];

    // A real PNG signature + IHDR chunk declaring a 1x1 image (no IDAT/IEND needed — matches
    // LibraryWorkspaceTests.cs's own oversized-image fixture, which reaches the same dimension-aware
    // validation this needs to pass through rather than reject).
    private static readonly byte[] PngSignatureBytes = CreateMinimalPngBytes();

    private static byte[] CreateMinimalPngBytes()
    {
        var png = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 1);
        return png;
    }

    [Fact]
    public async Task VideoGenerationWithMultipleResultsSubmitsOneIndependentJobPerResultAndCommitsAllOfThem()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-multi";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null));
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null));

        queue.Enqueue(Snapshot("draft-video-multi", model.Id, "Two cats on skateboards", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video, resultCount: 2), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-multi") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-multi")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.Completed, outcome.Record!.Status);
        Assert.Equal(2, outcome.Record.ResultFileIds.Count);
        Assert.Equal(2, adapter.SubmittedVideoJobIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));
    }

    [Fact]
    public async Task VideoGenerationSumsProviderReportedCostAcrossAllJobsInTheGroup()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-cost";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null, new AsyncGenerationCost(0.25, "USD")));
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null, new AsyncGenerationCost(0.30, "USD")));

        queue.Enqueue(Snapshot("draft-video-cost", model.Id, "Two cats on skateboards", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video, resultCount: 2), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-cost") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-cost")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(0.55, outcome.Record!.ActualCost!.Value, precision: 10);
        Assert.Equal("USD", outcome.Record.ActualCostCurrency);
    }

    [Fact]
    public async Task VideoGenerationWithMultipleResultsIsPartiallyCompletedWhenOnlySomeJobsSucceed()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), videoPollInterval: TimeSpan.FromMilliseconds(5));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-partial";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null));
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, "Moderation rejected this variation."));

        queue.Enqueue(Snapshot("draft-video-partial", model.Id, "Two cats on skateboards", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video, resultCount: 2), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-partial") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-partial")!;
        Assert.NotNull(outcome.Record);
        Assert.Equal(GenerationStatus.PartiallyCompleted, outcome.Record!.Status);
        Assert.Single(outcome.Record.ResultFileIds);
        Assert.Null(outcome.Record.ErrorMessage);
        Assert.Empty(await workspace.GetAsyncRemoteJobsForConnectionAsync(connection.Id));

        // The failed job's own specific reason reaches its result entry, not just a generic
        // "no result returned" fallback — proving per-child status carries real per-job detail.
        Assert.Equal(2, outcome.Record.Results.Count);
        Assert.Equal(GenerationResultStatus.Committed, outcome.Record.Results[0].Status);
        var failedEntry = Assert.Single(outcome.Record.Results, entry => entry.Status == GenerationResultStatus.Failed);
        Assert.Equal("Moderation rejected this variation.", failedEntry.ErrorMessage);
    }

    [Fact]
    public async Task AVideoResultIsStagedForRecoveryWhenItsLibraryBecomesUnavailableDuringTheFinalCommit()
    {
        using var temporary = new TemporaryDirectory();
        var libraryPath = temporary.Child("library");
        var stagingDirectory = temporary.Child("staging");
        var registry = new FakePendingResultRegistryService();
        var recoveryStaging = new RecoveryStagingService(registry, new FakeRecoveryStagingPathProvider(stagingDirectory));
        var availabilityProbe = new FakeLibraryAvailabilityProbe();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(libraryPath, videoPollInterval: TimeSpan.FromMilliseconds(200), recoveryStaging: recoveryStaging, availabilityProbe: availabilityProbe);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
        adapter.NextVideoJobId = "video-recovery";
        adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null));

        queue.Enqueue(Snapshot("draft-video-recovery", model.Id, "A cat skateboarding", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
        // The 200ms poll interval gives this a comfortable window before the first poll (which would
        // otherwise commit successfully). Deleting the managed-files directory the commit writes
        // into (not the SQLite database or the exclusively-locked .slopfactory.lock file, both of
        // which Windows would refuse to touch while LibraryWorkspace holds them open) makes the
        // write genuinely throw an IOException without any file-locking conflicts. The availability
        // probe is a fake here rather than the real disk-based one, since the real probe doesn't
        // check the managed-files directory specifically — this test is about the staging behavior
        // once a storage exception AND an unavailable report coincide, not about reproducing every
        // real-world way a volume disconnect could be detected.
        Directory.Delete(Path.Combine(libraryPath, "media"), recursive: true);
        availabilityProbe.IsAvailableValue = false;

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-video-recovery") is not null, timeoutMs: 5000);
        var outcome = queue.GetLastOutcomeForDraft("draft-video-recovery")!;
        Assert.True(outcome.StagedForRecovery);
        Assert.Null(outcome.Record);

        var staged = Assert.Single(registry.GetAll());
        Assert.Equal(workspace.Descriptor.LibraryId, staged.LibraryId);
        Assert.Equal("draft-video-recovery", staged.DraftId);
        Assert.Equal("video/mp4", staged.MediaType);
        Assert.Equal(Mp4SignatureBytes.LongLength, staged.ByteSize);
        Assert.Equal(Mp4SignatureBytes, await recoveryStaging.ReadBytesAsync(staged.Id));
        Assert.NotNull(staged.GenerationRecordId);
        Assert.Equal(0, staged.Position);
        var stagedRecord = await workspace.GetGenerationRecordAsync(staged.GenerationRecordId!);
        Assert.Equal(GenerationStatus.AwaitingLibrary, stagedRecord.Status);
    }

    [Fact]
    public async Task StagedVideoResultsAreAutomaticallyReconciledOnceTheLibraryVolumeReturns()
    {
        using var temporary = new TemporaryDirectory();
        var libraryPath = temporary.Child("library");
        var stagingDirectory = temporary.Child("staging");
        var registry = new FakePendingResultRegistryService();
        var recoveryStaging = new RecoveryStagingService(registry, new FakeRecoveryStagingPathProvider(stagingDirectory));
        var availabilityProbe = new FakeLibraryAvailabilityProbe();
        const string draftId = "draft-video-reconcile";

        // "First session": the video result completes but the library volume is unavailable at
        // commit time, so it's staged (tagged with its generation record/position) instead of
        // discarded, then the app "closes."
        {
            var (libraries, workspace, queue, adapter, _) = await CreateHarnessAsync(libraryPath, videoPollInterval: TimeSpan.FromMilliseconds(200), recoveryStaging: recoveryStaging, availabilityProbe: availabilityProbe);
            var connection = await CreateReadyConnectionAsync(workspace, "Connection");
            var model = await workspace.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
            adapter.NextVideoJobId = "video-reconcile";
            adapter.EnqueueVideoPollResult(new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, [Mp4SignatureBytes], null));

            queue.Enqueue(Snapshot(draftId, model.Id, "A cat skateboarding", workspace.Descriptor.GeneratedFolderId, GenerationMode.Video), connection.Id);
            Directory.Delete(Path.Combine(libraryPath, "media"), recursive: true);
            availabilityProbe.IsAvailableValue = false;

            await WaitUntilAsync(() => queue.GetLastOutcomeForDraft(draftId) is not null, timeoutMs: 5000);
            Assert.True(queue.GetLastOutcomeForDraft(draftId)!.StagedForRecovery);
            Assert.Single(registry.GetAll());

            await libraries.DisposeAsync();
        }

        // The volume returns.
        Directory.CreateDirectory(Path.Combine(libraryPath, "media"));
        availabilityProbe.IsAvailableValue = true;

        // "Second session": reopening the library with the same device-wide staging registry
        // automatically reconciles the staged result into it, without any manual export/discard
        // action and without ever re-invoking the provider adapter.
        var (_, workspace2, queue2, _, _) = await CreateHarnessAsync(libraryPath, recoveryStaging: recoveryStaging, availabilityProbe: availabilityProbe, autoStart: false);
        queue2.Start();

        GenerationRecord? reconciled = null;
        await WaitUntilAsync(() => (reconciled = workspace2.GetGenerationHistoryAsync().GetAwaiter().GetResult().SingleOrDefault(record => record.Prompt == "A cat skateboarding")) is { Status: GenerationStatus.Completed });
        Assert.Single(reconciled!.ResultFileIds);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task RecoveryStagingServiceRoundTripsAndDiscardsStagedBytes()
    {
        using var temporary = new TemporaryDirectory();
        var registry = new FakePendingResultRegistryService();
        var service = new RecoveryStagingService(registry, new FakeRecoveryStagingPathProvider(temporary.Child("staging")));

        var id = await service.StageAsync("library-1", "My Library", "draft-1", [1, 2, 3, 4], "result.mp4", "video/mp4");

        var entry = Assert.Single(service.GetAll());
        Assert.Equal(id, entry.Id);
        Assert.Equal("library-1", entry.LibraryId);
        Assert.Equal("My Library", entry.LibraryDisplayName);
        Assert.Equal("result.mp4", entry.SafeFileName);
        Assert.Equal(4, entry.ByteSize);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await service.ReadBytesAsync(id));

        await service.DiscardAsync(id);
        Assert.Empty(service.GetAll());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadBytesAsync(id));
    }

    [Fact]
    public async Task RecoveryStagingServiceStreamsBytesToDiskWithoutRegisteringUntilComplete()
    {
        using var temporary = new TemporaryDirectory();
        var registry = new FakePendingResultRegistryService();
        var service = new RecoveryStagingService(registry, new FakeRecoveryStagingPathProvider(temporary.Child("staging")));
        await using var source = new MemoryStream([5, 6, 7]);

        var id = await service.StageFromStreamAsync("library-1", "My Library", "draft-1", source, "result.mp4", "video/mp4", declaredLength: 3);

        Assert.Equal(new byte[] { 5, 6, 7 }, await service.ReadBytesAsync(id));
        Assert.Equal(3, Assert.Single(service.GetAll()).ByteSize);

        await using var staged = await service.OpenReadAsync(id);
        using var copy = new MemoryStream();
        await staged.CopyToAsync(copy);
        Assert.Equal(new byte[] { 5, 6, 7 }, copy.ToArray());
    }

    [Fact]
    public async Task RecoveryStagingServiceCleansUpWhenStreamStagingIsCancelled()
    {
        using var temporary = new TemporaryDirectory();
        var stagingDirectory = temporary.Child("staging");
        var registry = new FakePendingResultRegistryService();
        var service = new RecoveryStagingService(registry, new FakeRecoveryStagingPathProvider(stagingDirectory));
        await using var source = new MemoryStream([5, 6, 7]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StageFromStreamAsync("library-1", "My Library", "draft-1", source, "result.mp4", "video/mp4", cancellationToken: cancellation.Token));

        Assert.Empty(service.GetAll());
        Assert.Empty(Directory.EnumerateFiles(stagingDirectory));
    }

    [Fact]
    public async Task RecyclingAConnectivityPausedDependencyKeepsItPausedUntilBothConditionsAreResolved()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var connectivity = new FakeDeviceConnectivityStateProvider { IsOffline = true };
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), connectivity: connectivity);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var sourceFile = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var jobId = queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId, sourceFileId: sourceFile.Id), connection.Id);
        await Task.Delay(50);
        Assert.Equal(GenerationJobPhase.PausedConnectionLost, queue.GetJobStatus(jobId)!.Phase);

        queue.NotifyFileRecycled(sourceFile.Id);
        Assert.Equal(GenerationJobPhase.DependencyRecycled, queue.GetJobStatus(jobId)!.Phase);

        connectivity.IsOffline = false;
        queue.ResumeQueue();
        Assert.Equal(GenerationJobPhase.DependencyRecycled, queue.GetJobStatus(jobId)!.Phase);
        Assert.DoesNotContain("prompt1", adapter.InvokedPrompts);

        queue.NotifyFileRestored(sourceFile.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task RecyclingASourceFilePausesAQueuedJobDependingOnItAndRestoringResumesIt()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        // Notify* only simulates the recycle-bin event; the file stays genuinely active/readable in
        // the workspace throughout, matching what would actually be true once restored for real.
        var sourceFile = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId, sourceFileId: sourceFile.Id), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        var job2Id = queue.GetActiveJobIdForDraft("draft-2")!;

        queue.NotifyFileRecycled(sourceFile.Id);
        Assert.Equal(GenerationJobPhase.DependencyRecycled, queue.GetJobStatus(job2Id)!.Phase);
        // The queue position is retained across the pause (it is the only job still in this
        // connection's queue once job1 starts running, so position 1).
        Assert.Equal(1, queue.GetJobStatus(job2Id)!.QueuePosition);

        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        // Still paused even once it would otherwise be next in line.
        await Task.Delay(50);
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);

        queue.NotifyFileRestored(sourceFile.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt2"));
        Assert.Equal(GenerationJobPhase.Running, queue.GetJobStatus(job2Id)!.Phase);
        adapter.Complete("prompt2", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
    }

    [Fact]
    public async Task ADependencyRecycledJobDoesNotBlockALaterQueuedJobOnTheSameConnectionFromRunning()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId, sourceFileId: "file-1"), connection.Id);
        queue.Enqueue(Snapshot("draft-3", model.Id, "prompt3", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));

        queue.NotifyFileRecycled("file-1");
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));

        // prompt3 must be picked up around the paused prompt2, not stalled behind it.
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt3"));
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);
        adapter.Complete("prompt3", new TextGenerationResult(["result3"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-3") is not null);
    }

    [Fact]
    public async Task AJobPausedByTwoRecycledDependenciesOnlyResumesOnceBothAreRestored()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var sourceFile = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        // A blocking job occupies the connection's only concurrency slot so draft-2 stays Queued
        // (never dequeued into Running, which would otherwise try to actually read the source file).
        // The real generated folder is used for the notify calls below too — they are pure in-memory
        // simulations of a recycle-bin event, independent of the folder's real database state, and
        // this job is allowed to run to full completion later in this test.
        var folderId = workspace.Descriptor.GeneratedFolderId;
        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", folderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", folderId, sourceFileId: sourceFile.Id), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        var jobId = queue.GetActiveJobIdForDraft("draft-2")!;

        queue.NotifyFileRecycled(sourceFile.Id);
        queue.NotifyFolderRecycled(folderId);
        Assert.Equal(GenerationJobPhase.DependencyRecycled, queue.GetJobStatus(jobId)!.Phase);

        queue.NotifyFileRestored(sourceFile.Id);
        Assert.Equal(GenerationJobPhase.DependencyRecycled, queue.GetJobStatus(jobId)!.Phase);

        queue.NotifyFolderRestored(folderId);
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt2"));
        adapter.Complete("prompt2", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
    }

    [Fact]
    public async Task PermanentlyDeletingADependencyMarksAJobNonRunnableAndRestoringSomethingElseDoesNotClearIt()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId, sourceFileId: "file-1"), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        var jobId = queue.GetActiveJobIdForDraft("draft-2")!;

        queue.NotifyFilePermanentlyDeleted("file-1");
        var status = queue.GetJobStatus(jobId)!;
        Assert.Equal(GenerationJobPhase.DependencyRecycled, status.Phase);
        Assert.True(queue.GetSnapshot().Single(entry => entry.JobId == jobId).NonRunnable);

        // Restoring an unrelated file must never clear a different job's permanent-deletion flag.
        queue.NotifyFileRestored("some-other-file");
        Assert.True(queue.GetSnapshot().Single(entry => entry.JobId == jobId).NonRunnable);
        Assert.Equal(GenerationJobPhase.DependencyRecycled, queue.GetJobStatus(jobId)!.Phase);

        // Cancelling a non-runnable, dependency-recycled job must actually remove it rather than
        // silently no-op — Cancel() only looked for an active CancellationTokenSource before this
        // phase existed, which a paused-never-started job never has.
        queue.Cancel(jobId);
        Assert.Null(queue.GetJobStatus(jobId));
        var outcome = queue.GetLastOutcomeForDraft("draft-2");
        Assert.NotNull(outcome);
        Assert.True(outcome!.CancelledBeforeSubmission);

        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task ActivelyInUseQueriesOnlyReportRunningJobsNotMerelyQueuedOnes()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, PngSignatureBytes);
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var sourceFile = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId, sourceFileId: sourceFile.Id), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));

        Assert.True(queue.IsFileActivelyInUse(sourceFile.Id));
        Assert.True(queue.IsConnectionActivelyInUse(connection.Id));
        Assert.True(queue.IsModelActivelyInUse(model.Id));

        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId, sourceFileId: "file-2"), connection.Id);
        // draft-2 is merely queued (blocked by the connection's default concurrency cap of 1) —
        // it must never count as "actively in use" the way a running job does.
        Assert.False(queue.IsFileActivelyInUse("file-2"));

        // draft-2 references a nonexistent file — fine, since it is cancelled here rather than ever
        // allowed to actually run (this test's assertions about "actively in use" are already made).
        queue.Cancel(queue.GetActiveJobIdForDraft("draft-2")!);
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task RecyclingAConnectionCascadeCancelsItsQueuedButNeverSubmittedJobs()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.Single(queue.GetQueuedJobTitlesForConnection(connection.Id));

        // The running job is untouched by the cascade — only never-submitted work is cancelled.
        queue.CancelQueuedJobsForConnection(connection.Id);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
        Assert.True(queue.GetLastOutcomeForDraft("draft-2")!.CancelledBeforeSubmission);
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);
        Assert.Equal(GenerationJobPhase.Running, queue.GetJobStatus(queue.GetActiveJobIdForDraft("draft-1")!)!.Phase);

        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task CancelWorkAndExitCancelsEveryQueuedJobAndRaisesExitConfirmed()
    {
        using var temporary = new TemporaryDirectory();
        var (libraries, workspace, queue, adapter, preferences) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        var flushCount = 0;
        libraries.Closing += () => { flushCount++; return Task.CompletedTask; };
        var coordinator = new WindowsExitCoordinator(queue, preferences, libraries);
        var exitConfirmed = false;
        coordinator.ExitConfirmed += (_, _) => exitConfirmed = true;

        await coordinator.CancelWorkAndExitAsync();

        Assert.True(exitConfirmed);
        Assert.False(coordinator.PendingDecision);
        Assert.Equal(1, flushCount);
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
        Assert.True(queue.GetLastOutcomeForDraft("draft-2")!.CancelledBeforeSubmission);
    }

    [Fact]
    public async Task KeepRunningOnlyPersistsTheRememberedChoiceWhenAskedTo()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, queue, _, preferences) = await CreateHarnessAsync(temporary.Child("library"));
        var coordinator = new WindowsExitCoordinator(queue, preferences);
        var keptRunningRaised = 0;
        coordinator.KeptRunning += (_, _) => keptRunningRaised++;
        coordinator.RequestDecision();
        Assert.True(coordinator.PendingDecision);

        coordinator.KeepRunning(remember: false);
        Assert.False(coordinator.PendingDecision);
        Assert.False(coordinator.RememberedKeepRunning);
        Assert.Equal(1, keptRunningRaised);

        coordinator.RequestDecision();
        coordinator.KeepRunning(remember: true);
        Assert.True(coordinator.RememberedKeepRunning);
        Assert.Equal(2, keptRunningRaised);
    }

    [Fact]
    public async Task ReturnToAppClearsThePendingDecisionWithoutAffectingAnyQueuedWork()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, queue, adapter, preferences) = await CreateHarnessAsync(temporary.Child("library"));
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        var coordinator = new WindowsExitCoordinator(queue, preferences);
        coordinator.RequestDecision();

        coordinator.ReturnToApp();

        Assert.False(coordinator.PendingDecision);
        Assert.Equal(GenerationJobPhase.Running, queue.GetJobStatus(queue.GetActiveJobIdForDraft("draft-1")!)!.Phase);
        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
    }

    [Fact]
    public async Task BackgroundExecutionStartsWhileAJobIsRunningAndStopsOnceItCompletes()
    {
        using var temporary = new TemporaryDirectory();
        var backgroundExecution = new FakeBackgroundExecutionService();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), backgroundExecution: backgroundExecution);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));
        Assert.True(backgroundExecution.IsRunning);

        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        Assert.False(backgroundExecution.IsRunning);
    }

    [Fact]
    public async Task BackgroundExecutionSuspensionCancelsRunningJobsAndFinalizesTheirRecordsDistinctlyFromAProviderFailure()
    {
        using var temporary = new TemporaryDirectory();
        var backgroundExecution = new FakeBackgroundExecutionService();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), backgroundExecution: backgroundExecution);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));

        backgroundExecution.RaiseSuspended();

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        var outcome = queue.GetLastOutcomeForDraft("draft-1")!;
        Assert.Null(outcome.Record);
        Assert.False(outcome.CancelledBeforeSubmission);

        GenerationRecord? record = null;
        await WaitUntilAsync(() => (record = workspace.GetGenerationHistoryAsync().GetAwaiter().GetResult().SingleOrDefault(entry => entry.Prompt == "prompt1")) is { Status: GenerationStatus.Failed });
        Assert.Equal(GenerationFailureReason.ExecutionSuspended, record!.FailureReason);
    }

    [Fact]
    public async Task BackgroundExecutionDoesNotStartForAMerelyQueuedJob()
    {
        using var temporary = new TemporaryDirectory();
        var backgroundExecution = new FakeBackgroundExecutionService();
        var (_, workspace, queue, adapter, _) = await CreateHarnessAsync(temporary.Child("library"), backgroundExecution: backgroundExecution);
        var connection = await CreateReadyConnectionAsync(workspace, "Connection");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);

        // The first job starts running (needs background execution); the second stays merely
        // Queued behind it — plan.md:269's "active transfers", not indefinite queue waiting.
        queue.Enqueue(Snapshot("draft-1", model.Id, "prompt1", workspace.Descriptor.GeneratedFolderId), connection.Id);
        queue.Enqueue(Snapshot("draft-2", model.Id, "prompt2", workspace.Descriptor.GeneratedFolderId), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt1"));

        Assert.True(backgroundExecution.IsRunning);
        Assert.DoesNotContain("prompt2", adapter.InvokedPrompts);

        adapter.Complete("prompt1", new TextGenerationResult(["result1"], null, null));
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt2"));
        adapter.Complete("prompt2", new TextGenerationResult(["result2"], null, null));
        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-2") is not null);
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

        private readonly Dictionary<string, IReadOnlyList<byte[]>> _audioResults = new(StringComparer.Ordinal);
        public void SetAudioResult(string prompt, IReadOnlyList<byte[]> bytes) { lock (_gate) _audioResults[prompt] = bytes; }
        public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return _audioResults.TryGetValue(prompt, out var bytes) ? Task.FromResult(bytes) : throw new ProviderAdapterException($"No configured audio result for '{prompt}'.");
            }
        }

        public string NextVideoJobId = "video-job-1";
        private int _videoJobCounter;
        public List<string> SubmittedVideoJobIds { get; } = [];
        private readonly Queue<AsyncGenerationPollResult> _videoPollResults = new();
        public int VideoPollCount { get; private set; }
        public void EnqueueVideoPollResult(AsyncGenerationPollResult result) { lock (_gate) _videoPollResults.Enqueue(result); }

        public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var jobId = _videoJobCounter++ == 0 ? NextVideoJobId : $"{NextVideoJobId}-{_videoJobCounter}";
                SubmittedVideoJobIds.Add(jobId);
                return Task.FromResult(new AsyncGenerationSubmission(jobId));
            }
        }

        public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                VideoPollCount++;
                if (_videoPollResults.Count == 0) throw new InvalidOperationException("The test did not configure enough queued video poll results.");
                return Task.FromResult(_videoPollResults.Dequeue());
            }
        }

        public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, TextGenerationSourceImage? sourceImage = null, GenerationSettings? settings = null, TextGenerationSourceImage? secondarySourceImage = null, TextGenerationSourceImage? tertiarySourceImage = null, CancellationToken cancellationToken = default)
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

    private sealed class FakeDeviceEnergyStateProvider : IDeviceEnergyStateProvider
    {
        private bool _isEnergySaverOn;
        public bool IsEnergySaverOn
        {
            get => _isEnergySaverOn;
            set
            {
                if (_isEnergySaverOn == value) return;
                _isEnergySaverOn = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler? Changed;
    }

    private sealed class FakeDeviceConnectivityStateProvider : IDeviceConnectivityStateProvider
    {
        private bool _isOffline;
        public bool IsOffline
        {
            get => _isOffline;
            set
            {
                if (_isOffline == value) return;
                _isOffline = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool _isMetered;
        public bool IsMetered
        {
            get => _isMetered;
            set
            {
                if (_isMetered == value) return;
                _isMetered = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? Changed;
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

    private sealed class FakePendingResultRegistryService : IPendingResultRegistryService
    {
        private readonly List<StagedResultEntry> _entries = [];
        public IReadOnlyList<StagedResultEntry> GetAll() => _entries.OrderByDescending(entry => entry.CreatedAt).ToArray();
        public void Add(StagedResultEntry entry) => _entries.Add(entry);
        public void Remove(string id) => _entries.RemoveAll(entry => entry.Id == id);
    }

    private sealed class FakeRecoveryStagingPathProvider(string directory) : IRecoveryStagingPathProvider
    {
        public string StagingDirectory => directory;
    }

    private sealed class FakeBackgroundExecutionService : IBackgroundExecutionService
    {
        public int EnsureRunningCallCount { get; private set; }
        public int StopRunningCallCount { get; private set; }
        public bool IsRunning { get; private set; }
        public void EnsureRunning(string statusText) { EnsureRunningCallCount++; IsRunning = true; }
        public void StopRunning() { StopRunningCallCount++; IsRunning = false; }
        public event EventHandler? Suspended;
        public void RaiseSuspended() => Suspended?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeLibraryAvailabilityProbe : ILibraryAvailabilityProbe
    {
        public bool IsAvailableValue { get; set; } = true;
        public bool IsAvailable(string path, string? expectedVolumeIdentity, out string failureStage)
        {
            failureStage = "root-missing";
            return IsAvailableValue;
        }
    }
}
