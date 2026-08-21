# Inpainting handoff

## Goal

Support OpenAI image inpainting without treating masks as ordinary library files. A mask is a
private PNG editing attachment owned by one library image. The intended end state is:

1. Open an image, create/select a private mask, and paint the region to edit.
2. Select that image and one of its masks on **Generate**.
3. Submit an OpenAI `images/edits` request containing `image` and `mask` multipart parts.
4. Preserve the exact submitted source image and mask so queued work, retries, and **Use Again**
can still run after the original library image or editable mask is deleted.

## What is already implemented

### Private mask model and storage

- `ImageMask` in `src/Mellow.SlopFactory.Core/Domain/LibraryModels.cs` represents an owner-bound
  mask. It is not a `FileRecord`.
- Schema migrations through `LibraryRules.SchemaVersion == 46` add:
  - `image_masks`: owner file ID, label, owner content hash, dimensions, content hash, and PNG BLOB.
  - `generation_source_slots.attachment_id`: private mask identity.
  - `generation_source_slots.attachment_snapshot_bytes`: the submitted mask PNG.
  - `generation_input_snapshots`: submitted reference-image bytes, media type, and hash per
    generation/role/ordinal.
  - `generation_source_slots.snapshot_source_generation_id`: marks a slot as cloned from another
    generation's own snapshot rather than a live file — see the "Status update" section below.
- `ILibraryWorkspace` and `LibraryWorkspace` provide create/list/read/delete APIs for private masks,
  plus APIs for reading generation mask/source snapshots.
- Mask creation validates PNG safety, exact dimensions, owner image type, and owner content hash.
- A mask used by a generation cannot be deleted through the mask API. The database throws a clear
  validation error; `FileDetails.razor` displays it.

### UI

- `FileDetails.razor` includes a minimal mask editor for raster images:
  - Paints opaque brush strokes on a transparent PNG canvas.
  - Uses the source image as a CSS backdrop, so only mask pixels are exported.
  - Brush size, erase mode, clear, name, empty-mask validation, and basic ARIA labels exist.
  - Saved masks are private to the displayed image and can be listed/deleted there.
- `Generate.razor` lists only masks belonging to the selected primary source image. It emits a
  `GenerationInputSlotRole.Mask` slot whose `FileId` is the owner image and whose `AttachmentId` is
  the mask ID.
- Generation history displays that an inpainting mask was used.

### Provider and queue plumbing

- OpenAI Image mode exposes one `Mask` capability in `LibraryRules.GetInputSlotCapabilities`.
- `IProviderAdapter` has an overload accepting an optional mask.
- `OpenAiProviderAdapter` sends `POST images/edits`; `OpenAiCompatibleProtocol`
  `BuildImageEditMultipartContent` emits a `mask` multipart part named `mask.png`.
- `GenerationQueueService` reads the mask snapshot and reference-image snapshots when a durable
  generation record exists. Queued Image jobs are not paused when an already-snapshotted source file
  is recycled/deleted.
- `CreateQueuedGenerationRecordCoreAsync` captures reference-image snapshots in
  `generation_input_snapshots` immediately after creating the durable record.

## Status update (2026-08-21)

All five items are now addressed, at `SchemaVersion == 46`: item 1 (snapshot-backed **Use Again**) and
most of item 2 (queue consistency) are implemented; item 3's test gaps are largely filled; item 4
(editor improvements) is fully implemented and interactively verified; item 5 (provider constraints)
has the one universally-confirmed limit (mask size) encoded, with the remaining per-model nuance
documented rather than guessed at. See each numbered section below for specifics and what's
deliberately still open.

### 1. Snapshot-backed **Use Again** — done

Implemented the "practical shape" this doc suggested: `GenerationSourceSlot` gained a nullable `FileId`
plus `SnapshotSourceGenerationId` (mutually exclusive — exactly one is always set; enforced in
`LibraryRules.ValidateSourceSlots`). `GenerationSourceSlotSnapshot` gained `AttachmentId` so a
historical mask can still be identified after both the mask row and its owning image are gone.

- `generation_source_slots.snapshot_source_generation_id` (schema 46) marks a slot as cloned from
  another generation's own already-captured snapshot rather than a live file. Deliberately **not** a
  real foreign key (see the migration comment in `SqliteLibraryDatabase.UpgradeAsync`) — a real FK made
  SQLite rewrite this column's target whenever `generation_records` is renamed mid-migration (several
  existing schema-migration tests do this), permanently corrupting it once the original name was
  restored. `generation_input_snapshots.generation_id` had the same latent issue and was fixed the same
  way. Both are cleaned up explicitly in `PermanentlyDeleteGenerationRecordAsync` instead of relying on
  `ON DELETE`.
- `LibraryWorkspace.CreateQueuedGenerationRecordCoreAsync` clones bytes forward at record-creation time
  for a snapshot-backed slot (`ReferenceImage`/`FirstFrame` via `generation_input_snapshots`,
  `Mask` via the source record's own `attachment_snapshot_bytes` row, cloned inside
  `SqliteLibraryDatabase.ReplaceSourceSlotsAsync`) — the new record never depends on the source record
  surviving afterward.
- `Generate.razor`'s `BuildReplaySourceSlots` reconstructs a snapshot-backed slot for every
  `GenerationRecord.SourceSlotSnapshots` entry missing from the live `SourceSlots` list, instead of
  `FilterActiveSourceSlots` silently dropping it. The form tracks these separately as
  `_retainedSourceSlots` (no live-file picker applies) and shows a notice + remove button; `BuildSourceSlots`
  merges them back in unless the corresponding picker gained a live value.
- `GenerationHistoryDetail.razor` labels a permanently-deleted source/mask slot as still available via a
  retained snapshot (`SourceFilePermanentlyDeletedSlot`, `InpaintingMaskUsedSourceDeleted`).

### 2. Queue consistency and recovery — mostly done

`GenerationQueueService.ExecuteAsync` now resolves every reference-image/first-frame/mask slot through
two new helpers, `ReadReferenceSourceContentAsync`/`ReadMaskContentAsync`, which prefer this job's own
durable record's already-captured snapshot (populated for both live-file and snapshot-backed slots
alike) over a live-file read — Text mode and Video's FirstFrame previously always read the live file
directly, unlike Image mode, which was already snapshot-aware. `CreateQueuedGenerationRecordCoreAsync`'s
capture loop now also covers `FirstFrame`, not just `ReferenceImage`, closing a pre-existing gap where a
DeepInfra video generation's first-frame image had no durable snapshot at all.

Not audited/extended in this pass: `LastFrame`/`SourceAudio`/`SourceVideo` roles have no capture
mechanism (no adapter uses them yet, per `GenerationInputSlotRole`'s own remarks) — add one only once a
real provider capability needs it.

### 3. Tests — mostly filled in

Added: `LibraryRulesTests` coverage for the new mutually-exclusive `FileId`/`SnapshotSourceGenerationId`
invariant and snapshot-backed mask pairing; `LibraryWorkspaceTests` coverage for private mask lifecycle
(dimension mismatch, empty/invalid PNG, stale owner-revision rejection, unused vs. used delete), a
combined schema-45→46 migration test (`OpeningVersionFortyOneLibraryAddsPrivateMasksAndSnapshotSupport`),
end-to-end **Use Again** after permanent source deletion
(`UseAgainAfterPermanentSourceDeletionClonesAndUsesRetainedSnapshots`), and generation-record permanent-
deletion cleanup of snapshot references held by other drafts
(`PermanentlyDeletingAGenerationRecordClearsSnapshotReferencesHeldByDraftsAndDeletesItsOwnInputSnapshots`).

Still not covered by a dedicated test: an Image job remaining runnable after its source is
recycled/deleted mid-queue (the mechanism is the same `ReadReferenceSourceContentAsync` path the new
Use Again test exercises, but not from the queue-execution angle specifically).

### 4. Editor improvements — done

All five listed items are implemented in `wwwroot/ui.js`'s `slopFactoryMask` module and
`FileDetails.razor`'s mask editor markup:

- **Undo/redo**: `pushUndoSnapshot`/`undo`/`redo` keep a capped stack (`MaxHistoryEntries = 20`) of
  compressed PNG data URLs (`canvas.toDataURL`), not raw `getImageData` buffers — a sparse mask
  compresses to a few KB regardless of canvas resolution, so the cap bounds memory even for a large
  source image. Restoring a snapshot uses `fetch` + `createImageBitmap` (awaitable, so Blazor can
  reliably refresh Undo/Redo button state afterward).
- **Zoom/pan**: a zoom slider (25%-400% of natural pixel size) sets the canvas's CSS width/height
  directly; the canvas sits inside a new scrollable `.mask-canvas-viewport` wrapper for panning. The
  existing pointer-mapping math already derives from `getBoundingClientRect()`, so it needed no
  changes to stay accurate at any zoom level.
- **Brush cursor preview**: a `.mask-cursor-overlay` div tracks the pointer (or the keyboard cursor —
  see below), sized to the actual brush diameter in CSS pixels. Its position is computed from
  `canvas.offsetLeft/offsetTop`, not a `getBoundingClientRect()` diff against the container — the
  latter double-counts the container's own scroll offset once the canvas is zoomed in and panned,
  which was caught and fixed via a standalone interactive test harness (see below) before shipping.
- **Legend/opacity preview**: `.mask-legend` swatches show painted-vs-transparent meaning;
  `setPreviewOpacity` toggles `canvas.style.opacity` for display only — verified (via the same test
  harness) that it never affects the bytes `toPng()`/`hasPixels()` read.
- **Pointer/touch/keyboard**: `touch-action: none` was already set; `setPointerCapture` failures
  (observed in headless replay, and plausible on some real touch/pen sequences too) are now caught
  rather than aborting the stroke. A real keyboard-operable alternative was added — arrow keys move a
  dashed cursor ring (step scaled to brush size, Shift for a faster step), Enter/Space paints or
  erases a dab at that position, and the canvas's `aria-label` updates with the cursor's position as a
  percentage. This is handled with a native `canvas.onkeydown` listener (not a Blazor `@onkeydown`
  round-trip) specifically so only the keys we handle get `preventDefault()`-ed, leaving Tab free to
  move focus out of the canvas.

This was validated with a standalone HTML/JS harness (mirroring the real markup/CSS/module) driven via
synthetic pointer/keyboard events and direct pixel/DOM assertions — not just code review — which is how
the scroll-position overlay bug above was actually caught.

### 5. Provider constraints — mask size limit encoded; per-model limits still open

Researched OpenAI's current `images/edits` documentation (August 2026). Confirmed and encoded:

- The mask must be a PNG under 4 MiB, **regardless of which GPT image model is selected** — this is
  now `LibraryRules.MaximumMaskPngBytes`, enforced in `LibraryWorkspace.CreateImageMaskCoreAsync`
  before the existing PNG-safety/dimension checks.

Confirmed but **not** encoded, and still open:

- Per-model image-count/size/squareness limits vary a lot (legacy `dall-e-2`: single square PNG under
  4 MiB; current GPT image models: up to 16 images, PNG/WebP/JPG, under 50 MiB each — this app's own
  32 MiB inline-display cap already keeps any usable source file under the GPT-model limit, so only
  `dall-e-2`'s narrower contract is actually at risk). This app's capability model
  (`LibraryRules.GetInputSlotCapabilities`) is keyed by `(ProviderType, GenerationMode)`, not by the
  specific `ProviderModelId`, so a model-specific cap can't be enforced client-side without that keying
  changing first — a bigger change than this pass's scope. Until then, selecting `dall-e-2` with more
  than one reference image degrades gracefully (a provider-side error), not silently.

Mask support is intentionally still advertised only for `ProviderType.OpenAi`. Do not expose masks for
OpenRouter, DeepInfra, ComfyUI, or generic OpenAI-compatible providers until their masked-edit request
contracts are confirmed and tested.

## Important files

| Area | Files |
| --- | --- |
| Domain/capabilities | `src/Mellow.SlopFactory.Core/Domain/LibraryModels.cs`, `LibraryRules.cs` |
| Workspace API | `src/Mellow.SlopFactory.Core/Application/ILibraryWorkspace.cs`, `IProviderAdapter.cs` |
| Persistence/migrations | `src/Mellow.SlopFactory.Infrastructure/Persistence/SqliteLibraryDatabase.cs` |
| Workspace snapshot capture | `src/Mellow.SlopFactory.Infrastructure/LibraryWorkspace.cs` |
| Provider request | `src/Mellow.SlopFactory.Infrastructure/Providers/OpenAiProviderAdapter.cs`, `OpenAiCompatibleProtocol.cs` |
| Queue | `src/Mellow.SlopFactory.Gui/Services/GenerationQueueService.cs` |
| UI | `src/Mellow.SlopFactory.Gui/Components/Pages/FileDetails.razor`, `Generate.razor`, `GenerationHistoryDetail.razor` |
| Canvas | `src/Mellow.SlopFactory.Gui/wwwroot/ui.js`, `wwwroot/css/app.css` |
| Tests | `tests/Mellow.SlopFactory.Tests/ProviderAdapterTests.cs`, `LibraryRulesTests.cs`, `GenerationQueueServiceTests.cs`, `LibraryWorkspaceTests.cs` |

## Verification

Run:

```powershell
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-windows10.0.22621.0
dotnet test tests\Mellow.SlopFactory.Tests\Mellow.SlopFactory.Tests.csproj --no-restore
```

At the time of this update, all three main projects build clean and the full test suite passes
(817 passed, 1 skipped — the live-provider smoke test, intentionally skipped unless explicitly
credentialed/enabled). The previously-noted intermittent failure in
`GenerationQueueServiceTests.IsConnectionAwaitingRateLimitResetReflectsTheThrottleStateUntilItElapses`
did not reproduce in this run; if it recurs, treat it as a pre-existing timing flake, not a regression
from this change. `AdoptingACopiedLibraryAssignsANewIdentityAndPreservesLocalRecords` has also been
observed to fail under parallel test execution on Windows (`UnauthorizedAccessException` moving its
manifest file) but passes reliably in isolation — treat that one as filesystem-contention flakiness
too, not a functional regression.
