using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class GenerationNotificationCoordinatorTests
{
    private static async Task<(GenerationQueueService Queue, ILibraryWorkspace Workspace, FakeProviderAdapter Adapter, FakeNotificationService Notifications, AppLifecycleState Lifecycle, GenerationNotificationCoordinator Coordinator)> CreateHarnessAsync(string root, bool enabled = true, bool startCoordinator = true)
    {
        var libraries = new AppLibraryState(new LibraryWorkspaceFactory(), new FakeLibraryLocationService(root), new FakeRecentLibraryService(), new LibraryAvailabilityProbe(), new FakeAppPreferenceStore());
        await libraries.InitializeAsync();
        var adapter = new FakeProviderAdapter();
        var queue = new GenerationQueueService(libraries, new FakeProviderAdapterResolver(adapter), new FakeSecureCredentialStore(), new FakeAppPreferenceStore(), new FakeDeviceEnergyStateProvider());
        queue.Start();
        var notifications = new FakeNotificationService();
        var lifecycle = new AppLifecycleState();
        var preferences = new FakeAppPreferenceStore();
        var coordinator = new GenerationNotificationCoordinator(queue, lifecycle, notifications, preferences);
        if (enabled) Assert.True(await coordinator.SetEnabledAsync(true));
        if (startCoordinator) coordinator.Start();
        return (queue, libraries.Workspace!, adapter, notifications, lifecycle, coordinator);
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

    private static async Task<(Connection Connection, Model Model)> CreateReadyModelAsync(ILibraryWorkspace workspace)
    {
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var revisionId = await workspace.BeginCredentialCandidateAsync(connection.Id);
        var ready = (await workspace.PromoteCredentialRevisionAsync(connection.Id, revisionId)).Connection;
        var model = await workspace.CreateModelAsync("GPT", ready.Id, "gpt-4o", GenerationMode.Text, true);
        return (ready, model);
    }

    [Fact]
    public async Task SetEnabledAsyncTrueRequestsPermissionAndPersistsOnlyWhenGranted()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, _, notifications, _, coordinator) = await CreateHarnessAsync(temporary.Child("library"), enabled: false);
        Assert.False(coordinator.Enabled);
        Assert.Equal(0, notifications.PermissionRequests);

        notifications.GrantPermission = false;
        Assert.False(await coordinator.SetEnabledAsync(true));
        Assert.Equal(1, notifications.PermissionRequests);
        Assert.False(coordinator.Enabled);

        notifications.GrantPermission = true;
        Assert.True(await coordinator.SetEnabledAsync(true));
        Assert.Equal(2, notifications.PermissionRequests);
        Assert.True(coordinator.Enabled);
    }

    [Fact]
    public async Task SetEnabledAsyncFalseNeverRequestsPermission()
    {
        using var temporary = new TemporaryDirectory();
        var (_, _, _, notifications, _, coordinator) = await CreateHarnessAsync(temporary.Child("library"), enabled: false);

        Assert.True(await coordinator.SetEnabledAsync(false));
        Assert.Equal(0, notifications.PermissionRequests);
        Assert.False(coordinator.Enabled);
    }

    [Fact]
    public async Task ABackgroundedCompletedJobRaisesNotifyRequestedWithTheGenerationRecord()
    {
        using var temporary = new TemporaryDirectory();
        var (queue, workspace, adapter, _, lifecycle, coordinator) = await CreateHarnessAsync(temporary.Child("library"));
        var (connection, model) = await CreateReadyModelAsync(workspace);
        lifecycle.SetForeground(false);
        var notified = new List<GenerationRecord>();
        coordinator.NotifyRequested += (_, record) => notified.Add(record);

        queue.Enqueue(new GenerationJobSnapshot("draft-1", "Tab", GenerationMode.Text, model.Id, "prompt", null, 1, workspace.Descriptor.GeneratedFolderId, null), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt"));
        adapter.Complete("prompt", new TextGenerationResult(["result"], null, null));

        await WaitUntilAsync(() => notified.Count == 1);
        Assert.Equal("GPT", notified[0].ModelLabel);
    }

    [Fact]
    public async Task ACompletedJobWhileForegroundedDoesNotNotify()
    {
        using var temporary = new TemporaryDirectory();
        var (queue, workspace, adapter, _, lifecycle, coordinator) = await CreateHarnessAsync(temporary.Child("library"));
        var (connection, model) = await CreateReadyModelAsync(workspace);
        lifecycle.SetForeground(true);
        var notified = new List<GenerationRecord>();
        coordinator.NotifyRequested += (_, record) => notified.Add(record);

        queue.Enqueue(new GenerationJobSnapshot("draft-1", "Tab", GenerationMode.Text, model.Id, "prompt", null, 1, workspace.Descriptor.GeneratedFolderId, null), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt"));
        adapter.Complete("prompt", new TextGenerationResult(["result"], null, null));

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        await Task.Delay(50);
        Assert.Empty(notified);
    }

    [Fact]
    public async Task ACompletedJobWhileDisabledDoesNotNotify()
    {
        using var temporary = new TemporaryDirectory();
        var (queue, workspace, adapter, _, lifecycle, coordinator) = await CreateHarnessAsync(temporary.Child("library"), enabled: false);
        var (connection, model) = await CreateReadyModelAsync(workspace);
        lifecycle.SetForeground(false);
        var notified = new List<GenerationRecord>();
        coordinator.NotifyRequested += (_, record) => notified.Add(record);

        queue.Enqueue(new GenerationJobSnapshot("draft-1", "Tab", GenerationMode.Text, model.Id, "prompt", null, 1, workspace.Descriptor.GeneratedFolderId, null), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt"));
        adapter.Complete("prompt", new TextGenerationResult(["result"], null, null));

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        await Task.Delay(50);
        Assert.Empty(notified);
    }

    [Fact]
    public async Task ABackgroundedCompletedJobWhoseRecordPageIsCurrentlyVisibleDoesNotNotify()
    {
        using var temporary = new TemporaryDirectory();
        // Coordinator.Start() is deferred so the test's JobCompleted handler (which learns the freshly
        // minted record id and marks it "visible") can subscribe first; both handlers run synchronously
        // off the same JobCompleted?.Invoke call, in subscription order, so the coordinator always sees
        // an up-to-date VisibleGenerationRecordId for the very outcome it is currently handling.
        var (queue, workspace, adapter, _, lifecycle, coordinator) = await CreateHarnessAsync(temporary.Child("library"), startCoordinator: false);
        var (connection, model) = await CreateReadyModelAsync(workspace);
        lifecycle.SetForeground(false);
        var notified = new List<GenerationRecord>();
        coordinator.NotifyRequested += (_, record) => notified.Add(record);
        queue.JobCompleted += (_, outcome) => coordinator.VisibleGenerationRecordId = outcome.Record?.Id;
        coordinator.Start();

        queue.Enqueue(new GenerationJobSnapshot("draft-1", "Tab", GenerationMode.Text, model.Id, "prompt", null, 1, workspace.Descriptor.GeneratedFolderId, null), connection.Id);
        await WaitUntilAsync(() => adapter.InvokedPrompts.Contains("prompt"));
        adapter.Complete("prompt", new TextGenerationResult(["result"], null, null));

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-1") is not null);
        await Task.Delay(50);
        Assert.Empty(notified);
    }

    [Fact]
    public async Task ALocalPreSubmissionFailureNeverProducesANotificationBecauseItHasNoGenerationRecord()
    {
        using var temporary = new TemporaryDirectory();
        var (queue, workspace, adapter, _, lifecycle, coordinator) = await CreateHarnessAsync(temporary.Child("library"));
        var (connection, model) = await CreateReadyModelAsync(workspace);
        lifecycle.SetForeground(false);
        var notified = new List<GenerationRecord>();
        coordinator.NotifyRequested += (_, record) => notified.Add(record);

        await workspace.RecycleModelAsync(model.Id);
        await workspace.PermanentlyDeleteModelAsync(model.Id);
        queue.Enqueue(new GenerationJobSnapshot("draft-doomed", "Tab", GenerationMode.Text, model.Id, "doomed", null, 1, workspace.Descriptor.GeneratedFolderId, null), connection.Id);

        await WaitUntilAsync(() => queue.GetLastOutcomeForDraft("draft-doomed") is not null);
        await Task.Delay(50);
        Assert.Empty(notified);
        Assert.DoesNotContain("doomed", adapter.InvokedPrompts);
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public bool GrantPermission = true;
        public int PermissionRequests { get; private set; }
        public List<(string RecordId, string Title, string Body)> Shown { get; } = [];
#pragma warning disable CS0067
        public event EventHandler<string>? Tapped;
#pragma warning restore CS0067

        public Task<bool> RequestPermissionAsync()
        {
            PermissionRequests++;
            return Task.FromResult(GrantPermission);
        }

        public void Show(string recordId, string title, string body) => Shown.Add((recordId, title, body));
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
        public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default)
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
        public bool IsEnergySaverOn { get; set; }
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
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
