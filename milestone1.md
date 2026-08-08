# Milestone 1 completion checklist

This checklist replaces broad remaining Milestone 1 themes with independently completable units. An item is complete only when its stated automated verification passes; platform-labelled items also require the stated manual device check.

## Platform shell and baseline UI

- [x] Enforce the documented minimum Windows and Android versions at startup, show a blocking unsupported-version message, and verify the decision logic with platform-version unit tests.
- [ ] Add device-local Follow System, Light, and Dark theme settings that apply immediately and survive restart; verify preference persistence and manual Windows high-contrast behavior.
- [ ] Make all primary library workflows usable at phone width, tablet width, and desktop width without clipped controls; verify with fixed viewport UI tests and manual touch checks.
- [ ] Add visible keyboard focus, keyboard activation, and focus restoration for primary library/recycle-bin controls; verify with keyboard-driven UI tests on Windows.
- [ ] Move application-owned UI strings into localizable resources without changing rendered English text; verify a resource-coverage test that rejects newly hard-coded UI strings in target components.

## Library location, availability, and recovery

- [ ] Persist a Windows volume identity alongside each remembered library path and verify that a path on a different volume is not treated as the same remembered location.
- [ ] Revalidate root writability, exclusive locking, and required atomic filesystem operations before every create/open; verify failure leaves no newly created library files or changed remembered entry.
- [ ] Detect an active library becoming unavailable or read-only, close it safely, preserve its remembered location, and show an unavailable state; verify with a controllable filesystem test double.
- [ ] Implement Windows moved-library relinking only when the original remembered location is unavailable; verify same-ID validation, lock acquisition, remembered-path update, and rejection while the original is available.
- [ ] Represent failed opens as remembered **Corrupt** entries with a sanitized stage/diagnostic identifier and Retry, Choose Another Library, Forget, and Windows Open Location actions; verify no automatic repair or mutation occurs.
- [ ] Mark a full scan as recommended after watcher overflow, unsafe volume removal, or storage inconsistency, and offer Start/Defer without automatically scanning; verify each trigger.
- [ ] Persist and resume a content-free integrity-scan checkpoint; verify resumed scanning does not repeat completed entries and cancellation leaves a valid checkpoint.
- [x] Add derived-preview clear/rebuild actions to Library settings; verify they never modify managed media, records, metadata, links, or recycle-bin entries.

## Android storage and permissions

- [ ] Track Android app-specific external storage by stable volume identity and close/reopen the active library as that volume disappears/reappears; verify on an emulator or device with removable/emulated storage.
- [ ] Add Android uninstall/app-specific-storage warnings and exclude SlopFactory application data from Android backup; verify manifest configuration and manual Settings behavior.
- [ ] Use Android system document pickers for both import and export, request only operation-specific permissions, and declare no broad-storage/camera/microphone/contact/location/media-library permissions; verify the built manifest and manual denial paths.
- [ ] Provide a contextual system-settings shortcut when a permanently denied permission blocks the requested action; verify from an Android device.

## Import preflight and source safety

- [ ] Add recursive folder selection on Windows and Android document providers; verify an imported directory hierarchy becomes matching virtual library folders.
- [ ] Build a cancellable, non-mutating recursive-import inventory that reports eligible count, known bytes, virtual hierarchy, duplicate groups, name conflicts, and skipped-reason counts; verify cancellation creates no records, folders, or managed files.
- [ ] Freeze the confirmed recursive candidate set and revalidate each source immediately before copying; verify newly appearing entries are excluded and changed/missing entries fail independently.
- [ ] Add an Include Hidden Files review option; verify hidden entries are excluded by default, included only when selected, and protected/system/reparse entries are always excluded.
- [ ] Recreate selected source directories as virtual folders only after preflight confirmation; verify a failed or cancelled item does not leave unrelated empty folder artifacts.
- [ ] Resolve active, recycled, and pending-deletion duplicate matches in preflight with explicit per-item choices; verify Restore Existing runs normal restoration preview, Import Anyway creates a new record, and Skip changes nothing.
- [ ] Read and normalize Windows Mark-of-the-Web zone classification without retaining source/referrer URLs; verify alternate streams and unavailable zone data are handled safely.
- [ ] Ensure imports copy only the primary regular byte stream with SlopFactory-controlled managed permissions and do not propagate source ACLs, attributes, executable flags, alternate streams, or extended attributes; verify on Windows fixtures.

## Export, changed-content recovery, and external opening

- [ ] Implement user-selected single-file export with byte-for-byte verification, safe destination validation, cancellation cleanup, and per-file result reporting; verify exported bytes/hash match the managed record.
- [ ] Implement **Export Changed Bytes** as a separate recovery action that exports currently present safe bytes without accepting them or attaching a normal sidecar; verify record/provenance remain unchanged.
- [ ] Implement normal and bulk export preflight with safe-name mapping, collision choices, and per-file results; verify no partial replacement of existing destinations and no silent renaming.
- [ ] Add safe Windows/Android external opening using a temporary/read-only copy rather than a managed path; verify the external target cannot modify managed bytes.
- [ ] Block external opening for known active content and require a warning for potentially active documents; verify the block/warning decision uses detected bytes rather than display extension.
- [ ] Keep export, external-open, and future provider-source actions unavailable for Missing or Content Changed records; verify every entry point rejects those states.

## Viewers, format handling, and technical metadata

- [x] Add an unsupported-format detail state that shows safe system information and offers export/external opening when those actions exist; verify unsupported content is never sent to an inappropriate built-in viewer.
- [x] Add bounded orientation extraction for supported raster images and apply it only to temporary viewing representation; verify original managed bytes and recorded hash do not change.
- [ ] Add bounded audio/video probing for duration, codecs, channel count, sample rate, frame rate, and dimensions where applicable; verify malformed media reports unavailable properties without rejecting stored bytes.
- [ ] Move technical properties into a read-only system-metadata model separate from user metadata; verify they cannot be edited, searched as user values, or copied into diagnostics.
- [ ] Add background progress/cancellation for thumbnail and media-metadata extraction; verify cancellation leaves original content and records unchanged.
- [ ] Add the remaining preview safety limits and explicit **Preview Too Complex or Large** states; verify oversized/complex fixtures remain exportable and are not marked corrupt.
- [ ] Add the large-text partial/range viewer threshold and external-open route; verify it does not load the full file into the WebView.

## Metadata and sensitive-data behavior

- [ ] Implement bulk metadata type-normalization preview and commit; verify convertible entries change independently, incompatible entries remain unchanged, and sensitivity flags are preserved.
- [x] Add first-use Sensitive disclosure explaining display/search/export safeguards and non-encryption; verify acknowledgement is device-local and does not reveal a value.
- [ ] Replace sensitive-value edit/filter controls with masked secure-entry controls that disable autocomplete/autocorrect where supported; verify rendered accessibility attributes and platform behavior.
- [x] Make concealed metadata accessible as key, type, and concealed state without value or length, then expose the normal value only after session reveal; verify with accessibility-tree tests.
- [ ] Clear session reveals when the library closes, switches, locks, becomes unavailable, or the app restarts; verify each lifecycle transition.
- [x] Add explicit sensitive-value copy with clipboard-retention warning and ensure no value enters diagnostics, history, notifications, or automatic clipboard-clearing logic; verify logging fixtures.
- [x] Add sensitive JSON validation errors that report only error class and position, never tokens/property names/value excerpts; verify malformed sensitive JSON fixtures.
- [x] Add duplicate-review disclosure of sensitive metadata counts and clear copied reveal state; verify no sensitive key/value is rendered in single or bulk duplication review.

## Provenance and organisation

- [x] Persist immediate read-only provenance for Duplicate and Edit as Copy; verify it points to the direct source and does not create transitive links.
- [x] Add a read-only provenance-chain view that stops safely at missing/non-restorable endpoints; verify rename/move does not break ID-based traversal.
- [x] Make provenance relationships recycle/restore with endpoints and replace permanently deleted sources with a non-restorable identity snapshot; verify neither deletion nor restore creates editable provenance links.
- [x] Add current-file/overall progress and cancellation for bulk duplication; verify completed copies remain, unstarted copies do not begin, and the active atomic copy either commits or rolls back.
- [x] Disable folders and ineligible records in bulk duplicate review with explanations; verify a duplicate never copies generation-history relationships.

## Final Milestone 1 verification

- [ ] Add automated coverage for every new Milestone 1 behavior above, including cancellation, failed I/O, reparse substitution, and cross-library isolation cases.
- [ ] Run the full shared test suite, Windows MAUI build, and Android MAUI build with zero errors.
- [ ] Execute manual acceptance on supported Windows and Android devices covering creation/switching, import, viewers, metadata, links, recycle bin, integrity scan/export, unavailable storage, and permissions.
- [ ] Update `plan.md` by removing only verified completed requirements and keep user/developer documentation and `README.md` aligned with the finished behavior.
