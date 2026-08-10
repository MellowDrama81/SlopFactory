# Localization migration plan

This checklist breaks the remaining Milestone 1 localization work into independently completable passes. Each pass must add neutral-English entries to `src/Mellow.SlopFactory.Gui/Resources/UiStrings.resx`, replace the corresponding application-owned UI text with `IStringLocalizer<UiStrings>` lookups, preserve accessibility labels and formatted arguments, and finish with the stated verification.

Do not change user-supplied content, filesystem paths, file names, metadata values, exception messages, media text, or diagnostic payloads; only application-owned UI text belongs in this migration.

## Shared infrastructure and guardrails

- [x] Add `UiStrings.resx`, the marker type, DI registration, and initial shell/Library settings migration.
- [x] Add source-level regression checks for resource wiring plus responsive/focus UI assets.
- [x] Expand the localization regression test to enumerate the target components and assert each injects `IStringLocalizer<UiStrings>` before migrating the remaining pages. Verify `dotnet test tests\Mellow.SlopFactory.Tests\Mellow.SlopFactory.Tests.csproj --no-restore`.

## Recycle Bin

- [x] Finish Recycle Bin header/page title, sort options, primary controls, filters, selection actions, empty states, confirmations, dynamic count messages, deletion failures, state/kind labels, and operation results. Use formatted resource entries for counts. Verify the Windows and Android targets build.
- [x] Replace the representative Recycle Bin assertion with coverage that rejects remaining application-owned static text in the component. Verify the full test suite.

## Home: startup, import, and navigation

- [x] Finish Home startup/unavailable/loading states, primary library actions, new-folder form, import review, inventory summaries, duplicate choices, cancellation/progress labels, and import-result messages. Preserve user filenames and source errors as arguments, never resource keys. Verify the full test suite and Windows build.
- [x] Finish Home browsing headings, filters/sort options, pagination, selection labels, item actions, and empty/result states. Verify the full test suite and Windows build.
- [x] Finish Home bulk-selection, duplicate, metadata, and reviewed-operation dialogs/messages. Use formatted resources for counts and dynamically supplied names. Verify the full test suite and Windows build.
- [x] Replace the representative Home assertion with coverage that rejects remaining application-owned static text in the component. Verify the full test suite and Windows build.

## File Details: file actions and content health

- [x] Finish File Details loading/header text, primary actions, external-open confirmation, extension mismatch notice, and managed-content health panel. Verify the full test suite and Windows build.
- [x] Finish File Details replacement/changed-content review, unsupported-content, image, text, Markdown, audio, and video viewer controls and status messages. Preserve file content, filenames, MIME types, and detected metadata as formatted arguments. Verify the full test suite and Windows build.
- [x] Finish File Details technical metadata, ordinary/sensitive metadata editing, sensitive disclosure/reveal/copy actions, links, provenance, and all confirmation/error/status text. Verify the full test suite and Windows build.
- [x] Replace the representative File Details assertion with coverage that rejects remaining application-owned static text in the component. Verify the full test suite and Windows build.

## Completion and release validation

- [x] Review all four target components—`MainLayout.razor`, `LibrarySettings.razor`, `Home.razor`, `FileDetails.razor`, and `RecycleBin.razor`—for remaining application-owned hard-coded UI text. Move any remaining text to `UiStrings.resx` with stable descriptive keys.
  - [x] Audit `MainLayout.razor`: notices, action links, counts, and ARIA labels; migrate every application-owned literal and add a markup guard.
  - [x] Audit `LibrarySettings.razor` part 1: active library, location selection, adoption/cloud warnings, and Android storage; migrate remaining literals and formatted status messages.
  - [x] Audit `LibrarySettings.razor` part 2: recent libraries and preview cache; migrate actions, state summaries, confirmations, cache status, and operation results.
  - [x] Audit `LibrarySettings.razor` part 3: integrity scan controls, progress, report headings, issue kinds, export text, and scan results; migrate remaining literals and add a markup guard.
  - [x] Re-audit `Home.razor` and `FileDetails.razor` after the shared-key additions; ensure dynamic values remain arguments and strengthen guards for remaining generated messages.
  - [x] Re-audit `RecycleBin.razor`; ensure all dynamic count/state labels use resources and strengthen its markup guard if needed.
  - [x] Run a cross-component resource-key/duplicate-key audit, then full tests and both platform builds before marking the parent audit complete.

- [x] Final cross-component resource audit complete: all application-owned UI text in the five target components is localized, and the duplicate-key, test, Windows, and Android validation checks pass.

- [x] Add a culture-specific resource fixture (for example, `UiStrings.en-AU.resx`) that overrides a small representative set of strings, and verify resource resolution does not fall back to key names.
- [x] Run `dotnet test tests\Mellow.SlopFactory.Tests\Mellow.SlopFactory.Tests.csproj --no-restore`, Windows MAUI build, and Android MAUI build with zero errors. Update `docs/developer/testing.md` and mark the Milestone 1 localization item complete only after all checks pass.
