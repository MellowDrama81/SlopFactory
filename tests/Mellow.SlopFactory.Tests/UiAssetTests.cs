using Xunit;
using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Tests;

public sealed class UiAssetTests
{
    [Fact]
    public void ResponsiveAndFocusStylesCoverDesktopTouchAndHighContrast()
    {
        var css = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "wwwroot", "css", "app.css");

        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (pointer: coarse)", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemePreferencePersistsAndTheShellUpdatesImmediately()
    {
        var service = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "ThemePreferenceService.cs");
        var layout = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Layout", "MainLayout.razor");

        Assert.Contains("private const string PreferenceKey = \"slopfactory.theme\"", service, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Get(PreferenceKey", service, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Set(PreferenceKey, preference.ToString())", service, StringComparison.Ordinal);
        Assert.Contains("ThemePreference.System", service, StringComparison.Ordinal);
        Assert.Contains("ThemePreference.Light", service, StringComparison.Ordinal);
        Assert.Contains("ThemePreference.Dark", service, StringComparison.Ordinal);
        Assert.Contains("Changed?.Invoke(this, EventArgs.Empty)", service, StringComparison.Ordinal);
        Assert.Contains("@Theme.CssClass", layout, StringComparison.Ordinal);
        Assert.Contains("Theme.Changed += OnPendingChanged", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void RememberedLibrariesPersistVolumeIdentityAndRejectPathReuseOnAnotherVolume()
    {
        var recentLibraries = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "RecentLibraryService.cs");
        var availability = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "LibraryAvailabilityProbe.cs");
        var state = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "AppLibraryState.cs");

        Assert.Contains("string? VolumeIdentity = null", recentLibraries, StringComparison.Ordinal);
        Assert.Contains("LibraryVolumeIdentity.ForPath(path)", recentLibraries, StringComparison.Ordinal);
        Assert.Contains("expectedVolumeIdentity", availability, StringComparison.Ordinal);
        Assert.Contains("failureStage = \"volume-mismatch\"", availability, StringComparison.Ordinal);
        Assert.Contains("_availability.IsAvailable(path, remembered.VolumeIdentity", state, StringComparison.Ordinal);
        Assert.Contains("_availability.IsAvailable(remembered.Path, remembered.VolumeIdentity", state, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailabilityProbeRejectsAnExistingPathWhenItsRememberedVolumeDoesNotMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"slopfactory-volume-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var available = new LibraryAvailabilityProbe().IsAvailable(root, "different-volume-identity", out var failureStage);

            Assert.False(available);
            Assert.Equal("volume-mismatch", failureStage);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void ActiveLibraryAvailabilityLossClosesSafelyAndPreservesItsRememberedLocation()
    {
        var state = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "AppLibraryState.cs");
        var watcher = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "ManagedContentWatchService.cs");

        Assert.Contains("public async Task CloseUnavailableLibraryAsync", state, StringComparison.Ordinal);
        Assert.Contains("RememberedLibraryState.Unavailable", state, StringComparison.Ordinal);
        Assert.Contains("Workspace = null", state, StringComparison.Ordinal);
        Assert.Contains("Its remembered location was preserved", state, StringComparison.Ordinal);
        Assert.Contains("new Timer(CheckAvailability", watcher, StringComparison.Ordinal);
        Assert.Contains("CloseUnavailableLibraryAsync(workspace, stage)", watcher, StringComparison.Ordinal);
        Assert.Contains("IntegrityScanRecommendationReason.UnsafeVolumeRemoval", watcher, StringComparison.Ordinal);
        Assert.Contains("ReopenAvailableLibraryAsync", watcher, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptRememberedLibrariesExposeRecoveryActionsWithoutAnAutomaticRepairAction()
    {
        var settings = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "LibrarySettings.razor");

        Assert.Contains("RetryActiveLibraryAsync", settings, StringComparison.Ordinal);
        Assert.Contains("RelinkRecentAsync", settings, StringComparison.Ordinal);
        Assert.Contains("ForgetRecentAsync", settings, StringComparison.Ordinal);
        Assert.Contains("OpenRememberedLocationAsync", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("RepairLibraryAsync", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageIntegrityRecommendationIsTriggeredForEveryDefinedNonDestructiveCondition()
    {
        var watcher = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "ManagedContentWatchService.cs");
        var layout = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Layout", "MainLayout.razor");

        Assert.Contains("IntegrityScanRecommendationReason.WatcherOverflow", watcher, StringComparison.Ordinal);
        Assert.Contains("IntegrityScanRecommendationReason.StorageInconsistency", watcher, StringComparison.Ordinal);
        Assert.Contains("IntegrityScanRecommendationReason.UnsafeVolumeRemoval", watcher, StringComparison.Ordinal);
        Assert.Contains("ScanRecommendation.Defer", layout, StringComparison.Ordinal);
        Assert.Contains("StartFromLibrarySettings", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("RunIntegrityScanAsync", watcher, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionRecoveryNoticeAppearsForDirtyDraftsAndCanBeDismissed()
    {
        var layout = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Layout", "MainLayout.razor");
        var strings = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");

        Assert.Contains("LibraryState.Changed += OnPendingChanged", layout, StringComparison.Ordinal);
        Assert.Contains("LibraryState.DirtyDraftIds.Count > 0", layout, StringComparison.Ordinal);
        Assert.Contains("DirtyDraftsDetected", layout, StringComparison.Ordinal);
        Assert.Contains("LibraryState.DismissDirtyDrafts", layout, StringComparison.Ordinal);
        Assert.Contains("name=\"DirtyDraftsDetected\"", strings, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidManifestAndStorageGuidanceExcludeBackupAndBroadPermissions()
    {
        var manifest = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Platforms", "Android", "AndroidManifest.xml");
        var settings = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "LibrarySettings.razor");
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");

        Assert.Contains("android:allowBackup=\"false\"", manifest, StringComparison.Ordinal);
        Assert.Contains("android:fullBackupContent=\"false\"", manifest, StringComparison.Ordinal);
        Assert.Contains("android.permission.INTERNET", manifest, StringComparison.Ordinal);
        foreach (var forbiddenPermission in new[] { "MANAGE_EXTERNAL_STORAGE", "READ_MEDIA_", "READ_EXTERNAL_STORAGE", "WRITE_EXTERNAL_STORAGE", "CAMERA", "RECORD_AUDIO", "READ_CONTACTS", "ACCESS_FINE_LOCATION" })
        {
            Assert.DoesNotContain(forbiddenPermission, manifest, StringComparison.Ordinal);
        }
        Assert.Contains("Strings[\"AndroidStorageRetention\"]", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"AndroidStoragePermissions\"]", settings, StringComparison.Ordinal);
        Assert.Contains("OpenApplicationSettings", settings, StringComparison.Ordinal);
        Assert.Contains("SlopFactory data is excluded from Android backup", resources, StringComparison.Ordinal);
    }

    [Fact]
    public void HomePassesTheIncludeHiddenChoiceToItsImportInventory()
    {
        var home = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Home.razor");

        Assert.Contains("@bind:after=\"RefreshImportInventoryForCurrentSelectionAsync\"", home, StringComparison.Ordinal);
        Assert.Contains("includeHiddenFiles: _includeHiddenFiles", home, StringComparison.Ordinal);
        Assert.Contains("_pendingImports.RemoveAll", home, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportReviewExposesExplicitDuplicateChoicesForEveryExistingRecordState()
    {
        var home = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Home.razor");

        Assert.Contains("Strings[\"Skip\"]", home, StringComparison.Ordinal);
        Assert.Contains("Strings[\"ImportAnyway\"]", home, StringComparison.Ordinal);
        Assert.Contains("LibraryRecordState.Recycled", home, StringComparison.Ordinal);
        Assert.Contains("Strings[\"RestoreExisting\",", home, StringComparison.Ordinal);
        Assert.Contains("Strings[\"PendingDeletionCannotRestore\"]", home, StringComparison.Ordinal);
    }

    [Fact]
    public void TechnicalMetadataAndThumbnailExtractionAreBackgroundAndCancellationAware()
    {
        var details = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "FileDetails.razor");
        var previews = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "PreviewCacheService.cs");

        Assert.Contains("_technicalMetadataLoading", details, StringComparison.Ordinal);
        Assert.Contains("ReadingTechnicalMetadata", details, StringComparison.Ordinal);
        Assert.Contains("CancelTechnicalMetadata", details, StringComparison.Ordinal);
        Assert.Contains("new CancellationTokenSource()", details, StringComparison.Ordinal);
        Assert.Contains("GetSystemMetadataAsync(Id, cancellationToken)", details, StringComparison.Ordinal);
        Assert.Contains("GetImageThumbnailAsync(ILibraryWorkspace workspace, FileRecord file, CancellationToken cancellationToken", previews, StringComparison.Ordinal);
        Assert.Contains("_workerGate.WaitAsync(cancellationToken)", previews, StringComparison.Ordinal);
        Assert.Contains("WriteCachedAsync(path, thumbnail, cancellationToken)", previews, StringComparison.Ordinal);
        var home = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Home.razor");
        Assert.Contains("GeneratingThumbnails", home, StringComparison.Ordinal);
        Assert.Contains("CancelThumbnailLoading", home, StringComparison.Ordinal);
        Assert.Contains("_thumbnailCancellation?.Cancel()", home, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveMetadataEditAndFilterInputsAreMaskedAndDoNotOfferTextAssistance()
    {
        var details = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "FileDetails.razor");
        var home = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Home.razor");

        foreach (var component in new[] { details, home })
        {
            Assert.Contains("type=\"password\"", component, StringComparison.Ordinal);
            Assert.Contains("autocorrect=\"off\"", component, StringComparison.Ordinal);
            Assert.Contains("autocapitalize=\"off\"", component, StringComparison.Ordinal);
            Assert.Contains("spellcheck=\"false\"", component, StringComparison.Ordinal);
        }

        Assert.Contains("autocomplete=\"new-password\"", details, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"off\"", home, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Strings[\"SensitiveMetadataValue\"]\"", details, StringComparison.Ordinal);
        Assert.Contains("id=\"metadata-filter-value\" type=\"password\"", home, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveRevealSessionClearsForAppShutdownAndLibraryLifecycleChanges()
    {
        var app = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "App.xaml.cs");
        var service = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "SensitiveRevealSessionService.cs");

        Assert.Contains("window.Stopped += (_, _) => _sensitiveReveals.Clear()", app, StringComparison.Ordinal);
        Assert.Contains("window.Destroying += (_, _) => _sensitiveReveals.Clear()", app, StringComparison.Ordinal);
        Assert.Contains("libraries.Changed += OnLibraryChanged", service, StringComparison.Ordinal);
        Assert.Contains("current is null) _revealed.Clear()", service, StringComparison.Ordinal);
    }

    [Fact]
    public void RecursiveFolderImportUsesPlatformPickersAndPreservesRelativeFolders()
    {
        var actions = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Services", "PlatformFileActionService.cs");
        var home = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Home.razor");
        var activity = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Platforms", "Android", "MainActivity.cs");

        Assert.Contains("new Windows.Storage.Pickers.FolderPicker", actions, StringComparison.Ordinal);
        Assert.Contains("AddWindowsFolder(folder.Path, folder.Name, includeHiddenFiles", actions, StringComparison.Ordinal);
        Assert.Contains("Intent.ActionOpenDocumentTree", activity, StringComparison.Ordinal);
        Assert.Contains("StageAndroidTreeAsync(activity, tree, includeHiddenFiles", actions, StringComparison.Ordinal);
        Assert.Contains("new IncomingImportItem(info.FullName, info.Name, info.Length, false, relative)", actions, StringComparison.Ordinal);
        Assert.Contains("StageAndQueueAsync(source, name, current.Relative", actions, StringComparison.Ordinal);
        Assert.Contains("ResolveImportFolderAsync", home, StringComparison.Ordinal);
        Assert.Contains("item.RelativeFolder", home, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogFocusHelperRestoresFocusAfterTheDialogCloses()
    {
        var script = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "wwwroot", "ui.js");

        Assert.Contains("MutationObserver", script, StringComparison.Ordinal);
        Assert.Contains("dialogAdded", script, StringComparison.Ordinal);
        Assert.Contains("dialogRemoved", script, StringComparison.Ordinal);
        Assert.Contains("returnFocus.focus()", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Components", "Pages", "FileDetails.razor")]
    [InlineData("Components", "Pages", "RecycleBin.razor")]
    [InlineData("Components", "Pages", "Home.razor")]
    [InlineData("Components", "Pages", "LibrarySettings.razor")]
    [InlineData("Components", "Pages", "Connections.razor")]
    [InlineData("Components", "Pages", "Models.razor")]
    [InlineData("Components", "Pages", "SavedSettings.razor")]
    [InlineData("Components", "Pages", "Generate.razor")]
    [InlineData("Components", "Pages", "GenerationHistory.razor")]
    [InlineData("Components", "Pages", "GenerationHistoryDetail.razor")]
    public void AppearAndDisappearConfirmationPanelsUseTheDialogRoleSoFocusIsRestoredOnClose(params string[] componentPath)
    {
        var component = ReadRepositoryFile(["src", "Mellow.SlopFactory.Gui", .. componentPath]);

        Assert.Contains("role=\"dialog\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"group\"", component, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationShellAndLibrarySettingsUseLocalizableResources()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var layout = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Layout", "MainLayout.razor");
        var settings = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "LibrarySettings.razor");

        Assert.Contains("name=\"NavLibrarySettings\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"Theme\"", resources, StringComparison.Ordinal);
        Assert.Contains("IStringLocalizer<UiStrings>", layout, StringComparison.Ordinal);
        Assert.Contains("IStringLocalizer<UiStrings>", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"NavLibrarySettings\"]", layout, StringComparison.Ordinal);
        Assert.Contains("Strings[\"Theme\"]", settings, StringComparison.Ordinal);
        Assert.Contains("name=\"DeviceWideSubmissionCap\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"DeviceWideSubmissionCapHelp\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"SaveDeviceCap\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"DeviceCapSaved\"", resources, StringComparison.Ordinal);
        Assert.Contains("Strings[\"DeviceWideSubmissionCap\"]", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"DeviceWideSubmissionCapHelp\"]", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"SaveDeviceCap\"]", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"DeviceCapSaved\"]", settings, StringComparison.Ordinal);
        Assert.Contains("name=\"GenerationNotificationsHeading\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"GenerationNotificationsHelp\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"EnableGenerationNotifications\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"GenerationNotificationsEnabled\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"GenerationNotificationsDisabled\"", resources, StringComparison.Ordinal);
        Assert.Contains("name=\"NotificationPermissionDenied\"", resources, StringComparison.Ordinal);
        Assert.Contains("Strings[\"GenerationNotificationsHeading\"]", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"GenerationNotificationsHelp\"]", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"EnableGenerationNotifications\"]", settings, StringComparison.Ordinal);
        Assert.Contains("NotificationCoordinator.Enabled", settings, StringComparison.Ordinal);
        Assert.Contains("GenerationQueueService.MinDeviceCap", settings, StringComparison.Ordinal);
        Assert.Contains("GenerationQueueService.MaxDeviceCap", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"IntegrityProgress\",", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"IssueManifestInvalid\"]", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Integrity scan finished.", settings, StringComparison.Ordinal);
        Assert.Contains("Strings[\"IncomingImportsPending\",", layout, StringComparison.Ordinal);
        Assert.Contains("Strings[\"ManagedContentNeedsReview\"]", layout, StringComparison.Ordinal);
        Assert.Contains("name=\"GenerationQueueActivity\"", resources, StringComparison.Ordinal);
        Assert.Contains("Strings[\"GenerationQueueActivity\",", layout, StringComparison.Ordinal);
        Assert.Contains("name=\"EnergySaverCapActive\"", resources, StringComparison.Ordinal);
        Assert.Contains("Strings[\"EnergySaverCapActive\",", layout, StringComparison.Ordinal);
        Assert.Contains("name=\"NavQueue\"", resources, StringComparison.Ordinal);
        Assert.Contains("Strings[\"NavQueue\"]", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("shared or dropped item(s) are waiting", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Managed content needs review</strong>", layout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Components", "Layout", "MainLayout.razor")]
    [InlineData("Components", "Pages", "LibrarySettings.razor")]
    [InlineData("Components", "Pages", "Home.razor")]
    [InlineData("Components", "Pages", "FileDetails.razor")]
    [InlineData("Components", "Pages", "RecycleBin.razor")]
    [InlineData("Components", "Pages", "Connections.razor")]
    [InlineData("Components", "Pages", "ConnectionEdit.razor")]
    [InlineData("Components", "Pages", "Models.razor")]
    [InlineData("Components", "Pages", "ModelEdit.razor")]
    [InlineData("Components", "Pages", "Generate.razor")]
    [InlineData("Components", "Pages", "GenerationHistory.razor")]
    [InlineData("Components", "Pages", "GenerationHistoryDetail.razor")]
    [InlineData("Components", "Pages", "GenerationQueue.razor")]
    [InlineData("Components", "Pages", "SavedSettings.razor")]
    public void LocalizationTargetComponentsInjectTheUiStringLocalizer(params string[] componentPath)
    {
        var component = ReadRepositoryFile(["src", "Mellow.SlopFactory.Gui", .. componentPath]);

        Assert.Contains("@inject IStringLocalizer<UiStrings> Strings", component, StringComparison.Ordinal);
    }

    [Fact]
    public void RecycleBinUsesLocalizedLabelsForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var recycleBin = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "RecycleBin.razor");

        foreach (var key in new[] { "RecycleBinPageTitle", "RecentlyDeleted", "RestoreSelected", "RetryPermanentDeletion", "ItemsProcessedSuccessfully", "CascadeSummary", "SavedSetting", "CascadeSummaryConnection", "CascadeSummaryModel", "NoDependents", "GenerationRecordKind" })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", recycleBin, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Recycle bin<", ">Recently deleted<", "Retry permanent deletion", "Delete permanently" })
        {
            Assert.DoesNotContain(literal, recycleBin, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HomeUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var home = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Home.razor");

        foreach (var key in new[]
                 {
                     "LibraryPageTitle", "OpeningLibrary", "ValidatingLocalStorage", "LibrarySummary", "PermissionBlocksImport",
                     "ImportInventorySummary", "ImportProgress", "ImportSummary", "RestoreExisting", "OpenImportedFile",
                     "DisplayName", "SaveChanges", "CopyName", "FolderPath", "UnknownFolder", "SortBy", "LoadingFiles",
                     "BrowseResultSummary", "SelectedAcrossResultPages", "SelectFile", "WhyFileMatched", "FileResultPages", "PageOf",
                     "BulkDestinationFolder", "BulkMetadataSetPreview", "BulkMetadataSensitivityPreview", "BulkDuplicateComplete", "BulkActionComplete",
                     "DefaultCopyName", "FileDuplicated"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", home, StringComparison.Ordinal);
        }

        foreach (var literal in new[]
                 {
                     ">Opening your library<", ">Validating local storage", "A document-provider permission is blocking",
                     "Size unavailable until import", "Open imported file", ">Display name<", ">Save changes<", ">Copy name<",
                     ">Location<", "Unknown folder", "Import files into SlopFactory", ">Loading files", "Why this file matched",
                     "File result pages", "selected across result pages", "Destination folder", "Bulk duplicate complete", "Bulk action complete"
                 })
        {
            Assert.DoesNotContain(literal, home, StringComparison.Ordinal);
        }

        var markup = home[..home.IndexOf("@code", StringComparison.Ordinal)];
        var rawVisibleText = System.Text.RegularExpressions.Regex.Matches(markup, ">([^<]+)<")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0 && !value.Contains('\n') && !value.Contains('\r') && !value.Contains('"') && !value.Contains('=') && System.Text.RegularExpressions.Regex.IsMatch(value, "[A-Za-z]") && !value.Contains('@'))
            .ToArray();

        Assert.Empty(rawVisibleText);
    }

    [Fact]
    public void UiStringResourcesHaveUniqueKeys()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var keys = System.Text.RegularExpressions.Regex.Matches(resources, "<data name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CultureSpecificUiStringFixtureOverridesValuesWithoutUsingResourceKeys()
    {
        var fixture = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.en-AU.resx");

        Assert.Contains("name=\"NavLibrary\"", fixture, StringComparison.Ordinal);
        Assert.Contains("<value>Library (AU)</value>", fixture, StringComparison.Ordinal);
        Assert.Contains("<value>Library (AU) · SlopFactory</value>", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("<value>NavLibrary</value>", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void FileDetailsUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var details = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "FileDetails.razor");

        foreach (var key in new[]
                 {
                     "FileDetailsPageTitle", "OpenInAnotherApp", "OpenTemporaryCopy", "ExtensionMismatch", "ManagedContent",
                     "BuiltInViewingVerifiesContent", "InspectChangedBytes", "ChangedByteInspection", "ReplacementAcceptedDescription",
                     "CandidateRestoresOriginal", "ContentVerificationComplete", "ExportCompleted", "ChangedBytesExported", "ExternalOpenComplete",
                     "RotateImage", "ImagePreviewAlt", "ReadOnlyTextSummary", "Searching", "TextSearchMatchSummary",
                     "ExternalLinkDestinationNotice", "TextTooLargeForEditor", "PreserveSourceFormat", "DefaultEditedCopyName",
                     "ReadingTechnicalMetadata", "ImageDimensions", "ProvenanceChainDescription", "ConcealedSensitiveMetadataSummary",
                     "LinkSummary", "MetadataSaved", "LinkCreated", "SensitiveValueCopied", "UnavailableFileIdentifier",
                     "Candidate", "PlaybackRateHalf", "PlaybackRateDouble"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", details, StringComparison.Ordinal);
        }

        foreach (var literal in new[]
                 {
                     "<PageTitle>File details", "Built-in viewing rechecks", "Inspect changed bytes", "Changed-byte inspection",
                     "Only the first 1,048,576 characters are shown.", "The current bytes were accepted as a permanent replacement",
                     "Export completed and the destination bytes", "Changed bytes exported for recovery", "verified temporary read-only copy",
                     "Rotate 90", "Preview of @_file.DisplayName", "Read-only ·", "First 200 available for navigation",
                     "Another application will receive this destination", "too large to open in the built-in editor", "Preserve source format",
                     "Reading bounded technical metadata", "This read-only chain follows", "Metadata saved.", "Link created.", "Sensitive value copied",
                     ">Candidate<", ">0.5x<", ">2x<"
                 })
        {
            Assert.DoesNotContain(literal, details, StringComparison.Ordinal);
        }

        var markup = details[..details.IndexOf("@code", StringComparison.Ordinal)];
        var rawVisibleText = System.Text.RegularExpressions.Regex.Matches(markup, ">([^<]+)<")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0 && !value.Contains('\n') && !value.Contains('\r') && !value.Contains('"') && !value.Contains('=') && System.Text.RegularExpressions.Regex.IsMatch(value, "[A-Za-z]") && !value.Contains('@'))
            .ToArray();

        Assert.Empty(rawVisibleText);
    }

    [Fact]
    public void ConnectionsUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var connections = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Connections.razor");

        foreach (var key in new[]
                 {
                     "ConnectionsPageTitle", "ConnectionsEyebrow", "ConnectionsHeading", "ConnectionsDescription", "AddConnection",
                     "NoConnections", "ConnectionSummary", "ConfirmRecycleConnection", "RecycleConnectionWarning",
                     "ProviderOpenAi", "ProviderGenericOpenAiCompatible", "ConnectionVerified", "ConnectionTestFailedStatus",
                     "ConnectionUnverified", "ConnectionRecycled",
                     "ConnectionCredentialsRequired", "InsecureConnectionBadge", "ConnectionCredentialRequiresRepair",
                     "RecycledItemsMovedToBin", "NavRecycleBin"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", connections, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Connections<", "No connections yet.", "Add connection", "Delete permanently" })
        {
            Assert.DoesNotContain(literal, connections, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(connections);
    }

    [Fact]
    public void ConnectionEditUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var connectionEdit = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "ConnectionEdit.razor");

        foreach (var key in new[]
                 {
                     "AddConnection", "EditConnection", "ProviderType", "BaseUrl", "AdvancedConnectionSettings",
                     "CredentialHeaderName", "AuthPrefix", "ApiKey", "CredentialStored", "ReplaceApiKey",
                     "TestConnection", "TestingConnection", "Save", "ConnectionTestSucceeded", "ConnectionTestFailed",
                     "InsecureHttpBaseUrlWarning", "ConnectionTimeoutSeconds", "ConnectionTimeoutHelp", "AdditionalHeaders",
                     "AdditionalHeadersHelp", "ModalitySettingsHeading", "ModalitySettingsDescription", "ModelsModality",
                     "TextGenerationModality", "ImageGenerationModality", "ResolvedEndpoint", "ProviderTypeLockedWhileModelsExist",
                     "CredentialRequiresRepairBanner", "KeepExistingKey", "SaveNewKeyAsUnverified", "SaveNewKeyAsUnverifiedWarning",
                     "ConnectionMaxConcurrency", "ConnectionMaxConcurrencyHelp", "SaveConnectionMaxConcurrency", "ConnectionMaxConcurrencySaved"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", connectionEdit, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Add connection<", ">Edit connection<", "Test connection", "Credential stored" })
        {
            Assert.DoesNotContain(literal, connectionEdit, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(connectionEdit);
    }

    [Fact]
    public void ModelsUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var models = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Models.razor");

        foreach (var key in new[]
                 {
                     "ModelsPageTitle", "ModelsEyebrow", "ModelsHeading", "ModelsDescription", "AddModel",
                     "AddConnectionFirst", "NoModels", "ModelSummary", "ConfirmRecycleModel", "RecycleModelWarning",
                     "ModeText", "ModeImage", "ModelRecycled",
                     "ModelNotCurrentlyListed", "NeedsReview", "RecycledItemsMovedToBin", "NavRecycleBin"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", models, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Models<", "No models yet.", "Add model" })
        {
            Assert.DoesNotContain(literal, models, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(models);
    }

    [Fact]
    public void ModelEditUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var modelEdit = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "ModelEdit.razor");

        foreach (var key in new[]
                 {
                     "AddModel", "EditModel", "AddConnectionFirst", "Connection", "GenerationMode", "ModeText", "ModeImage",
                     "ProviderModelId", "LoadModels", "LoadingModels", "NoModelsDiscovered", "DiscoveredModels",
                     "SelectDiscoveredModel", "SupportsSystemInstructions", "Save", "ModelCatalogueLastRefreshed",
                     "ModelCatalogueNeverRefreshed", "ModelCataloguePossiblyStale", "ModelCatalogueStale",
                     "ModelNeedsReview", "MarkAsReviewed", "ModelChangeAffectsSavedSettings", "ConfirmModelChange",
                     "ModelMarkedReviewed", "TextResultFormat", "TextResultFormatMarkdown", "TextResultFormatPlainText"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", modelEdit, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Add model<", ">Edit model<", "Load models", "Supports system instructions" })
        {
            Assert.DoesNotContain(literal, modelEdit, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(modelEdit);
    }

    [Fact]
    public void GenerateUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var generate = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Generate.razor");

        foreach (var key in new[]
                 {
                     "GeneratePageTitle", "GenerateEyebrow", "GenerateHeading", "GenerateDescription", "AddModelFirst",
                     "Model", "Prompt", "ResultCount", "Generate", "Generating", "GenerationCompleted", "GenerationFailed",
                     "OpenGeneratedFile", "ConnectionUnavailableForModel", "ModeText", "ModeImage", "SaveSettingsHeading",
                     "SaveSettingsDescription", "SettingsTitle", "SaveSettingsAction", "SavedSettingsSaved", "SavedSettingsModelUnavailable",
                     "SavedSettingsHeading", "SystemInstructions", "TokenUsage", "EstimatedTokenCount", "SourceImageSlot1", "SourceImageSlot2", "SourceImageSlot3",
                     "NoSourceImage", "SourceImageSlotUsed", "DuplicateSourceSelectionError",
                     "CancelGeneration", "GenerationCancelledByUser", "PromptImprovementHeading", "PromptImprovementDescription",
                     "ImprovementModel", "NoImprovementModel", "ImprovementGuidance", "ImprovePromptAction", "ImprovingPrompt",
                     "NoImprovementCandidates", "UseThisCandidate", "GenerationBlockedCredentialsRequired", "AddApiKey",
                     "ConnectionUnverifiedWarning", "ModelDoesNotSupportSystemInstructions", "AllModelsNeedReview", "ModelsHeading",
                     "GenerationPartiallyCompleted", "GenerationPartiallyCompletedDetail", "SafetyBlockedResultsDetail", "TabTitle", "UntitledDraftTitle",
                     "ResetTabTitle", "DuplicateTab", "MoveTabLeft", "MoveTabRight", "CloseTab", "ConfirmCloseTab", "CancelCloseTab",
                     "CloseTabConfirmMessage", "AddDraftTab", "DraftSaving", "DraftSaved", "DraftNotSaved", "RetrySaveAction",
                     "SaveSettingsAndCloseTab", "SaveAndCloseTab", "BackToCloseOptions", "QueuePosition",
                     "RunsHeading", "ActiveRunsCount",
                     "GenerationCancelledBeforeSubmission", "DraftModelUnavailable", "CredentialRequiresRepairBanner",
                     "SaveSettingsAsAction", "SavedSettingsConflictTitle", "SavedSettingsConflictMessage", "OverwriteSavedSettings",
                     "ReviewChangesSummary", "ReviewChangesNoDifferences", "ReviewChangesYourVersionColumn", "ReviewChangesSavedVersionColumn",
                     "ReviewChangesModelField", "ReviewChangesPromptField", "ReviewChangesSystemInstructionsField", "ReviewChangesSourceFileField",
                     "ReviewChangesSecondarySourceFileField", "ReviewChangesTertiarySourceFileField",
                     "ReviewChangesDestinationField", "ReviewChangesResultCountField", "ReviewChangesNoValue", "UnknownFile",
                     "TabSwitcherCompactLabel", "ManageTabs", "SearchTabs", "CloseTabSwitcher", "RenameTabAction",
                     "SavedSettingsRecycledTitle", "SavedSettingsRecycledMessage", "RestoreAndSaveSettings", "SavedSettingsSourceDeleted",
                     "DraftModelRecycled", "RestoreModelAction", "DraftModelDeleted", "ChooseReplacementModel", "CostUnknownNotice",
                     "GenerationSettingsHeading", "Temperature", "TemperatureHelp", "TopP", "TopPHelp", "MaxTokens", "MaxTokensHelp",
                     "FrequencyPenalty", "FrequencyPenaltyHelp", "PresencePenalty", "PresencePenaltyHelp",
                     "ReviewChangesTemperatureField", "ReviewChangesTopPField", "ReviewChangesMaxTokensField",
                     "ReviewChangesFrequencyPenaltyField", "ReviewChangesPresencePenaltyField", "UseProviderDefault"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", generate, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Generate<", "Add a model before generating", "Generation completed", "Generation failed", "Save these settings" })
        {
            Assert.DoesNotContain(literal, generate, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(generate);
    }

    [Fact]
    public void GenerateOffersAnAndroidCompactTabSwitcherWithASearchableManagementList()
    {
        var generate = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "Generate.razor");
        var css = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "wwwroot", "css", "app.css");

        Assert.Contains("OperatingSystem.IsAndroid()", generate, StringComparison.Ordinal);
        Assert.Contains("<Virtualize", generate, StringComparison.Ordinal);
        Assert.Contains("_switcherFilter", generate, StringComparison.Ordinal);
        Assert.Contains("DuplicateDraftAsync(draft.Id)", generate, StringComparison.Ordinal);
        Assert.Contains("BeginSwitcherRename(draft)", generate, StringComparison.Ordinal);
        Assert.Contains("MoveDraftAsync(draft.Id,", generate, StringComparison.Ordinal);
        Assert.Contains("BeginCloseDraft(draft.Id)", generate, StringComparison.Ordinal);
        Assert.Contains(".tab-strip { display: flex; flex-wrap: nowrap; overflow-x: auto;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationHistoryUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var history = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "GenerationHistory.razor");

        foreach (var key in new[]
                 {
                     "GenerationHistoryPageTitle", "GenerationHistoryHeading", "GenerationHistoryDescription", "NoGenerationHistory",
                     "GenerationRecordSummary", "GenerationCompleted", "GenerationFailed", "UseAgain", "TokenUsage",
                     "FilterByStatus", "FilterByMode", "FilterByModel", "NoFilteredGenerationHistory", "ViewDetails",
                     "PromptImprovementHistoryHeading", "PromptImprovementHistoryDescription", "ImprovementGuidance", "ShowImprovementCandidates",
                     "FilterByProvider", "FilterByDateFrom", "FilterByDateTo", "ProviderOpenAi", "ProviderGenericOpenAiCompatible",
                     "GenerationPartiallyCompleted", "Recycle", "ConfirmRecycleGenerationRecord", "RecycleGenerationRecordWarning",
                     "Cancel", "RecycledItemsMovedToBin", "NavRecycleBin"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", history, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Generation history<", "No generations yet." })
        {
            Assert.DoesNotContain(literal, history, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(history);
    }

    [Fact]
    public void GenerationHistoryDetailUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var detail = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "GenerationHistoryDetail.razor");

        foreach (var key in new[]
                 {
                     "GenerationHistoryDetailPageTitle", "GenerationHistoryDetailHeading", "BackToGenerationHistory",
                     "GenerationRecordSummary", "GenerationCompleted", "GenerationFailed", "GenerationPartiallyCompleted",
                     "GenerationPartiallyCompletedDetail", "SafetyBlockedResultsDetail", "ShowSystemInstructions", "TokenUsage", "SourceImageSlotUsed",
                     "OpenGeneratedFile", "PromptImprovementUsed", "UseAgain", "Recycle", "ConfirmRecycleGenerationRecord",
                     "RecycleGenerationRecordWarning", "Cancel", "SourceFilePermanentlyDeletedSlot", "ResultFilePermanentlyDeleted",
                     "ResultPendingReview", "RetainAsUnverifiedBinary", "DiscardUnverifiedResult"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", detail, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Generation details<", "Back to generation history<" })
        {
            Assert.DoesNotContain(literal, detail, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(detail);
    }

    [Fact]
    public void GenerationQueueUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var queue = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "GenerationQueue.razor");

        foreach (var key in new[]
                 {
                     "QueuePageTitle", "QueueHeading", "QueueDescription", "QueueEmpty", "GenerateEyebrow", "Generating",
                     "QueuePosition", "MoveTabLeft", "MoveTabRight", "CancelGeneration", "NavGenerate", "Unknown",
                     "EnergySaverCapActive"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", queue, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Generation queue<", "Nothing is queued or running<" })
        {
            Assert.DoesNotContain(literal, queue, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(queue);
    }

    [Fact]
    public void SavedSettingsUsesLocalizedResourcesForAllApplicationOwnedUiText()
    {
        var resources = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Resources", "UiStrings.resx");
        var savedSettings = ReadRepositoryFile("src", "Mellow.SlopFactory.Gui", "Components", "Pages", "SavedSettings.razor");

        foreach (var key in new[]
                 {
                     "SavedSettingsPageTitle", "SavedSettingsHeading", "SavedSettingsDescription", "NewGeneration", "NoSavedSettings",
                     "SavedSettingSummary", "UseSettings", "ConfirmRecycleSavedSettings", "RecycleSavedSettingsWarning",
                     "SavedSettingsRecycled", "NeedsReview", "RecycledItemsMovedToBin", "NavRecycleBin"
                 })
        {
            Assert.Contains($"name=\"{key}\"", resources, StringComparison.Ordinal);
            Assert.Contains($"Strings[\"{key}\"", savedSettings, StringComparison.Ordinal);
        }

        foreach (var literal in new[] { ">Saved settings<", "No saved settings yet." })
        {
            Assert.DoesNotContain(literal, savedSettings, StringComparison.Ordinal);
        }

        AssertNoRawVisibleMarkupText(savedSettings);
    }

    private static void AssertNoRawVisibleMarkupText(string component)
    {
        var markup = component[..component.IndexOf("@code", StringComparison.Ordinal)];
        var rawVisibleText = System.Text.RegularExpressions.Regex.Matches(markup, ">([^<]+)<")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0 && !value.Contains('\n') && !value.Contains('\r') && !value.Contains('"') && !value.Contains('=') && System.Text.RegularExpressions.Regex.IsMatch(value, "[A-Za-z]") && !value.Contains('@'))
            .ToArray();

        Assert.Empty(rawVisibleText);
    }

    private static string ReadRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
