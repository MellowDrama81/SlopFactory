# Milestone 1 completion checklist

> Current release status is owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md). This file is retained as Milestone 1 implementation history.

This checklist replaces broad remaining Milestone 1 themes with independently completable units. An item is complete only when its stated automated verification passes; platform-labelled items also require the applicable device check in [manual_tests.md](manual_tests.md).

## Platform shell and baseline UI

- [x] Enforce the documented minimum Windows and Android versions at startup, show a blocking unsupported-version message, and verify the decision logic with platform-version unit tests.
- [ ] Add device-local Follow System, Light, and Dark theme settings that apply immediately and
      survive restart. The implementation is in place — `IThemePreferenceService`/`ThemePreferenceService`
      persists the choice via MAUI `Preferences` under a dedicated key, `MainLayout.razor` applies
      `Theme.CssClass` immediately on change, `LibrarySettings.razor`'s **Appearance** panel exposes
      the Follow System/Light/Dark picker, and `app.css` defines the default-dark/`.theme-light`/
      `@media (prefers-color-scheme)` rules backing all three choices — and `UiAssetTests.ThemePreferencePersistsAndTheShellUpdatesImmediately`
      locks in the persistence key, all three enum values and the change-event wiring at the source
      level. This item stays unchecked only because it also requires the
      [MT-01](manual_tests.md#mt-01--theme-persistence-and-windows-high-contrast) device pass per
      this checklist's rule that platform-labelled items need the applicable device check too.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) section 15.
- [ ] Make all primary library workflows usable at phone width, tablet width, and desktop width
      without clipped controls; verify with fixed viewport UI tests and
      [MT-02](manual_tests.md#mt-02--responsive-layout-and-touch-interaction). `app.css` already has
      the responsive breakpoints (900px/720px/420px) and a `pointer: coarse` touch-target rule,
      checked by `UiAssetTests.ResponsiveAndFocusStylesCoverDesktopTouchAndHighContrast`, but that
      test only confirms the CSS rules exist in source — there is no rendered, fixed-viewport UI
      test (no bUnit/Playwright/Selenium harness exists in this repository at all), so "verify with
      fixed viewport UI tests" is not actually satisfied yet; that would mean adopting a new test
      technology, which is a real scope/dependency decision this checklist item cannot resolve on
      its own.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) sections 13 and 15.
- [ ] Add visible keyboard focus, keyboard activation, and focus restoration for primary
      library/recycle-bin controls. Keyboard focus/activation was already covered (every interactive
      control across the app is a real `<button>`/`<a>`, so the existing `:focus-visible` outline and
      native Enter/Space activation in `app.css` already apply everywhere; `ui.js` already
      implements a `MutationObserver`-based focus-restoration helper that focuses the first control
      of an appearing `[role="dialog"]` element and restores focus to the invoking control when it's
      removed). What was missing — and is now fixed — is that every appear/disappear confirmation
      panel across the app (`RecycleBin.razor`, `Home.razor`'s bulk-action panel,
      `LibrarySettings.razor`'s five confirm blocks, `Connections.razor`/`Models.razor`/`SavedSettings.razor`'s
      recycle/permanent-delete confirms, and `Generate.razor`'s tab-close confirms) used `role="group"`
      or no role at all instead of `role="dialog"`, so the existing JS helper never actually fired for
      any of them. They now all use `role="dialog"`, and
      `UiAssetTests.AppearAndDisappearConfirmationPanelsUseTheDialogRoleSoFocusIsRestoredOnClose`
      locks this in across every affected page. This stays unchecked only because it also requires
      the [MT-03](manual_tests.md#mt-03--windows-keyboard-and-focus-recovery) device/keyboard pass,
      and because "verify with keyboard-driven UI tests" implies the same rendered-UI test
      capability gap noted above — the current tests are source-level guards, not live keyboard
      interaction tests.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) sections 13 and 15.
- [x] Move application-owned UI strings into localizable resources without changing rendered English text; verify a resource-coverage test that rejects newly hard-coded UI strings in target components.

## Library location, availability, and recovery

- [x] Persist a Windows volume identity alongside each remembered library path and verify that a path on a different volume is not treated as the same remembered location.
- [x] Revalidate root writability, exclusive locking, and required atomic filesystem operations before every create/open; verify failure leaves no newly created library files or changed remembered entry.
- [x] Detect an active library becoming unavailable or read-only, close it safely, preserve its remembered location, and show an unavailable state; verify with a controllable filesystem test double.
- [x] Implement Windows moved-library relinking only when the original remembered location is unavailable; verify same-ID validation, lock acquisition, remembered-path update, and rejection while the original is available.
- [x] Represent failed opens as remembered **Corrupt** entries with a sanitized stage/diagnostic identifier and Retry, Choose Another Library, Forget, and Windows Open Location actions; verify no automatic repair or mutation occurs.
- [x] Mark a full scan as recommended after watcher overflow, unsafe volume removal, or storage inconsistency, and offer Start/Defer without automatically scanning; verify each trigger.
- [x] Persist and resume a content-free integrity-scan checkpoint; verify resumed scanning does not repeat completed entries and cancellation leaves a valid checkpoint.
- [x] Add derived-preview clear/rebuild actions to Library settings; verify they never modify managed media, records, metadata, links, or recycle-bin entries.

## Android storage and permissions

- [ ] Track Android app-specific external storage by stable volume identity and close/reopen the
      active library as that volume disappears/reappears. `LibraryVolumeIdentity.ForPath` already
      has a real Android branch (`#if ANDROID`, using `Android.OS.Storage.StorageManager.GetStorageVolume`
      to return a stable `"android-volume:primary"`/`"android-volume:{uuid}"` identity), and the
      close/reopen mechanism (`LibraryAvailabilityProbe.IsAvailable`, `ManagedContentWatchService.CheckAvailability`
      on its 2-second timer, `AppLibraryState.CloseUnavailableLibraryAsync`/`RetryAsync`) is
      platform-agnostic — none of it is gated to Windows only. This stays unchecked because
      `tests/Mellow.SlopFactory.Tests.csproj` targets only `net10.0` (no `net10.0-android` test
      target exists), so the `#if ANDROID` branch is never compiled or exercised by any automated
      test, and because [MT-04](manual_tests.md#mt-04--android-app-specific-and-removable-storage)
      still requires an actual device/emulator pass.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) sections 14 and 15.
- [ ] Add Android uninstall/app-specific-storage warnings and exclude SlopFactory application data
      from Android backup. Already implemented: `Platforms/Android/AndroidManifest.xml` sets
      `android:allowBackup="false"` and `android:fullBackupContent="false"`, and
      `LibrarySettings.razor`'s Android-only panel shows the retention/permissions warning text with
      an **Open system settings** action. `UiAssetTests.AndroidManifestAndStorageGuidanceExcludeBackupAndBroadPermissions`
      already asserts both manifest flags and the warning strings at the source level. This stays
      unchecked only because [MT-05](manual_tests.md#mt-05--android-uninstall-backup-document-pickers-and-permissions)
      still requires the device pass.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) section 15.
- [ ] Use Android system document pickers for both import and export, request only
      operation-specific permissions, and declare no broad-storage/camera/microphone/contact/location/media-library
      permissions. Already implemented: the manifest declares only `android.permission.INTERNET`;
      import uses `Intent.ActionOpenDocumentTree` and export uses `Intent.ActionCreateDocument` via
      `MainActivity.PickDocumentTreeAsync`/`CreateDocumentAsync` — genuine Storage Access Framework
      pickers, never a broad-storage API.
      `UiAssetTests.AndroidManifestAndStorageGuidanceExcludeBackupAndBroadPermissions` asserts the
      manifest declares none of `MANAGE_EXTERNAL_STORAGE`/`READ_MEDIA_*`/`READ_EXTERNAL_STORAGE`/
      `WRITE_EXTERNAL_STORAGE`/`CAMERA`/`RECORD_AUDIO`/`READ_CONTACTS`/`ACCESS_FINE_LOCATION`. This
      stays unchecked only because "verify the built manifest" implies inspecting the actual
      merged/compiled manifest (not just the source file) and because
      [MT-05](manual_tests.md#mt-05--android-uninstall-backup-document-pickers-and-permissions)
      still requires the device pass.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) sections 13 and 15.
- [ ] Provide a contextual system-settings shortcut when a permanently denied permission blocks the
      requested action. `PlatformFileActionService`'s Android import path already catches
      `Java.Lang.SecurityException`, sets `HasPermissionBlock = true`, and `Home.razor` conditionally
      renders the **Open system settings** shortcut only `@if (PlatformFiles.HasPermissionBlock)` —
      contextual, not an always-visible button. The Android export path previously had no equivalent
      handling (a document-provider `SecurityException` during export would have propagated
      uncaught, since the callers in `FileDetails.razor` only catch
      `IOException`/`UnauthorizedAccessException`/`InvalidOperationException`/`SlopFactoryException`);
      it now catches `Java.Lang.SecurityException` the same way, sets `HasPermissionBlock`, and
      returns a `FileExportResult` with `Outcome: Failed` instead of throwing, matching import's
      contextual-shortcut behavior and the existing "return a result, don't throw" convention its
      callers already expect. There is still no automated test for this (the Android-conditional
      code isn't compiled by the `net10.0`-only test project, same limitation as the volume-identity
      item above), and [MT-05](manual_tests.md#mt-05--android-uninstall-backup-document-pickers-and-permissions)
      still requires the device pass.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) sections 14 and 15.

## Import preflight and source safety

- [x] Add recursive folder selection on Windows and Android document providers; verify an imported directory hierarchy becomes matching virtual library folders.
- [x] Build a cancellable, non-mutating recursive-import inventory that reports eligible count, known bytes, virtual hierarchy, duplicate groups, name conflicts, and skipped-reason counts; verify cancellation creates no records, folders, or managed files.
- [x] Freeze the confirmed recursive candidate set and revalidate each source immediately before copying; verify newly appearing entries are excluded and changed/missing entries fail independently.
- [x] Add an Include Hidden Files review option; verify hidden entries are excluded by default, included only when selected, and protected/system/reparse entries are always excluded.
- [x] Recreate selected source directories as virtual folders only after preflight confirmation; verify a failed or cancelled item does not leave unrelated empty folder artifacts.
- [x] Resolve active, recycled, and pending-deletion duplicate matches in preflight with explicit per-item choices; verify Restore Existing runs normal restoration preview, Import Anyway creates a new record, and Skip changes nothing.
- [x] Read and normalize Windows Mark-of-the-Web zone classification without retaining source/referrer URLs; verify alternate streams and unavailable zone data are handled safely.
- [x] Ensure imports copy only the primary regular byte stream with SlopFactory-controlled managed permissions and do not propagate source ACLs, attributes, executable flags, alternate streams, or extended attributes; verify on Windows fixtures.

## Export, changed-content recovery, and external opening

- [x] Implement user-selected single-file export with byte-for-byte verification, safe destination validation, cancellation cleanup, and per-file result reporting; verify exported bytes/hash match the managed record.
- [x] Implement **Export Changed Bytes** as a separate recovery action that exports currently present safe bytes without accepting them or attaching a normal sidecar; verify record/provenance remain unchanged.
- [x] Implement normal and bulk export preflight with safe-name mapping, collision choices, and per-file results; verify no partial replacement of existing destinations and no silent renaming.
- [x] Add safe Windows/Android external opening using a temporary/read-only copy rather than a managed path; verify the external target cannot modify managed bytes.
- [x] Block external opening for known active content and require a warning for potentially active documents; verify the block/warning decision uses detected bytes rather than display extension.
- [x] Keep export, external-open, and future provider-source actions unavailable for Missing or Content Changed records; verify every entry point rejects those states.

## Viewers, format handling, and technical metadata

- [x] Add an unsupported-format detail state that shows safe system information and offers export/external opening when those actions exist; verify unsupported content is never sent to an inappropriate built-in viewer.
- [x] Add bounded orientation extraction for supported raster images and apply it only to temporary viewing representation; verify original managed bytes and recorded hash do not change.
- [x] Add bounded audio/video probing for duration, codecs, channel count, sample rate, frame rate, and dimensions where applicable; verify malformed media reports unavailable properties without rejecting stored bytes.
- [x] Move technical properties into a read-only system-metadata model separate from user metadata; verify they cannot be edited, searched as user values, or copied into diagnostics.
- [x] Add background progress/cancellation for thumbnail and media-metadata extraction; verify cancellation leaves original content and records unchanged.
- [x] Add the remaining preview safety limits and explicit **Preview Too Complex or Large** states; verify oversized/complex fixtures remain exportable and are not marked corrupt.
- [x] Add the large-text partial/range viewer threshold and external-open route; verify it does not load the full file into the WebView.

## Metadata and sensitive-data behavior

- [x] Implement bulk metadata type-normalization preview and commit; verify convertible entries change independently, incompatible entries remain unchanged, and sensitivity flags are preserved.
- [x] Add first-use Sensitive disclosure explaining display/search/export safeguards and non-encryption; verify acknowledgement is device-local and does not reveal a value.
- [x] Replace sensitive-value edit/filter controls with masked secure-entry controls that disable autocomplete/autocorrect where supported; verify rendered accessibility attributes and platform behavior.
- [x] Make concealed metadata accessible as key, type, and concealed state without value or length, then expose the normal value only after session reveal; verify with accessibility-tree tests.
- [x] Clear session reveals when the library closes, switches, locks, becomes unavailable, or the app restarts; verify each lifecycle transition.
- [x] Add explicit sensitive-value copy with clipboard-retention warning and ensure no value enters diagnostics, history, notifications, or automatic clipboard-clearing logic; verify logging fixtures.
- [x] Add sensitive JSON validation errors that report only error class and position, never tokens/property names/value excerpts; verify malformed sensitive JSON fixtures.
- [x] Add duplicate-review disclosure of sensitive metadata counts and clear copied reveal state; verify no sensitive key/value is rendered in single or bulk duplication review.

## Provenance and organisation

- [x] Persist immediate read-only provenance for Duplicate and Edit as Copy; verify it points to the direct source and does not create transitive links.
- [x] Add a read-only provenance-chain view that stops safely at missing/non-restorable endpoints; verify rename/move does not break ID-based traversal.
- [x] Make provenance relationships recycle/restore with endpoints and replace permanently deleted sources with a non-restorable identity snapshot; verify neither deletion nor restore creates editable provenance links.
- [x] Add current-file/overall progress and cancellation for bulk duplication; verify completed copies remain, unstarted copies do not begin, and the active atomic copy either commits or rolls back. `Home.razor.Dispose()` now also cancels `_bulkDuplicateCancellation` (it previously cancelled its sibling `_thumbnailCancellation`/`_importCancellation` operations but not this one), closing a gap where navigating away from a page mid-bulk-duplicate let the operation keep running in the background to completion instead of honoring the same "leaving cancels it" behavior already applied to the other two long-running operations on this page.
- [x] Disable folders and ineligible records in bulk duplicate review with explanations; verify a duplicate never copies generation-history relationships.

## Final Milestone 1 verification

- [ ] Add automated coverage for every new Milestone 1 behavior above, including cancellation, failed I/O, reparse substitution, and cross-library isolation cases.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) section 14.
- [x] Run the full shared test suite, Windows MAUI build, and Android MAUI build with zero errors.
- [ ] Execute [MT-06](manual_tests.md#mt-06--cross-platform-acceptance-workflow) on supported Windows and Android devices.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) section 15.
- [ ] Update `plan.md` by removing only verified completed requirements and keep user/developer documentation and `README.md` aligned with the finished behavior.
      Owned by [IMPLEMENTATION_COMPLETION_CHECKLIST.md](IMPLEMENTATION_COMPLETION_CHECKLIST.md) section 1.
