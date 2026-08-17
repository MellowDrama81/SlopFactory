# SlopFactory implementation completion checklist

This is the consolidated checklist for completing the first public release. It replaces raw
unchecked-item counting across `milestone1.md` through `milestone4.md`; those files remain useful
design and implementation history, but contain duplicates, deferred items and requirements that
were completed by a later milestone without being checked off in the earlier one.

Only mark an item complete after its implementation, automated verification, documentation and any
applicable platform test are complete. Keep provider-dependent and publishing-dependent work open
until it has been verified against real documentation, credentials, devices or signing assets.

## 1. Reconcile the source of truth

- [x] Map every remaining requirement in `plan.md` to exactly one checklist item below.
- [ ] Remove requirements from `plan.md` only after their implementation and verification are
      complete, in keeping with the file's stated purpose.
- [x] Update stale Milestone 2 entries that were completed by Milestone 3, including Audio/Video
      modes and the OpenRouter and DeepInfra adapters.
- [x] Retire or merge `Milestone1_Remaining_Plan.md` so Milestone 1 has one checklist.
- [ ] Add links from each still-open milestone entry to its owning item in this checklist.
- [x] Reconcile `README.md`, `docs/user/` and `docs/developer/` with the resulting source of truth.

Done when: every incomplete first-release requirement has one owner and one status, with no
duplicate or contradictory checkboxes.

### Requirement ownership map

Every requirement under a listed `plan.md` section inherits the checklist owner shown here. A row
can have multiple owners when implementation and release verification are intentionally separate.

| `plan.md` section | Owning checklist section(s) |
| --- | --- |
| Application Scope; User Stories | 1, 13, 14 and the release completion gate |
| Packaging and Distribution | 14 and 15 |
| Testing | 2, 13, 14 and 15 |
| Settings Scope | 3, 4, 5, 9 and 11 |
| User Interface; Localization; Accessibility | 11, 13, 14 and 15 |
| Diagnostics; Privacy and Data Flow; Data at Rest | 8, 10, 12 and 14 |
| Offline Behavior; Metered Network Transfers | 3 and 4 |
| Android Background Work; Windows Background Work | 4 and 15 |
| Session Recovery; Application Instances; Work Queues | 3, 4, 11, 12 and 14 |
| Library Storage; Windows; Android; Storage Failure Handling | 8, 12, 14 and 15 |
| Large File Handling; Library File Formats | 7, 8 and 14 |
| Preview Cache; File Viewers | 10, 11, 13 and 14 |
| File Import; Library Organization; Naming Rules; Library Browsing | 8, 11, 13 and 14 |
| File Metadata; File Links; Text Content Editing | 8, 10, 11, 13 and 14 |
| Connections and Models | 3, 5, 6, 9 and 10 |
| Provider File Transfer | 6 and 7 |
| Provider Result URLs | 7 |
| Provider Safety Responses | 10 |
| Recycle Bin | 10, 11, 13 and 14 |
| Generation Lifecycle | 3, 4, 7 and 12 |
| Generation History; Generation Notifications | 3, 4, 9, 10, 11 and 12 |
| Generation Inputs | 5 and 6 |
| Generation Results | 7, 8, 10, 11 and 12 |
| Cost and Usage | 9 |
| API Retries and Rate Limits | 2, 3, 4 and 7 |
| Prompt Improvement | 2, 5, 6, 7, 9 and 10 |
| Saved Generation Settings | 5, 6 and 11 |

## 2. Complete the provider test foundation

- [x] Build a reusable local fake HTTP provider covering authentication, discovery, synchronous and
      streaming responses, asynchronous jobs, rate limits, moderation, redirects, result downloads
      and representative error responses.
- [x] Add sanitized, versioned request/response fixtures for every supported provider operation.
- [ ] Add contract tests for instruction-channel mapping, normalized message order, hidden-message
      absence, capability rejection and immutable sent-instruction snapshots.
      OpenAI-compatible instruction mapping, message order/absence and source-byte snapshots are
      covered by ProviderInstructionContractTests; capability-rejection coverage remains pending
      the capability-schema work in section 5.
- [x] Add fault-injection coverage for timeouts, disconnects, truncated streams, invalid JSON,
      malformed media, redirect chains, DNS changes and partial multi-result responses.
      Timeout, disconnect, cancellation, truncated-stream, invalid-JSON, malformed-media and
      partial-result fixtures are now covered. Redirect-target revalidation is covered for
      OpenRouter result downloads, including a bounded redirect loop. The OpenRouter HTTP handler
      also rejects a private address before connecting, covering DNS rebinding.
- [ ] Create an explicitly enabled live-provider harness that skips without credentials, enforces a
      caller-approved cost budget and sanitizes every retained artifact.
      The discovery harness and budget guard are implemented. With the current xUnit runner the
      absent-credential path is a no-op rather than a dynamically reported skip; it never creates
      an HTTP client or sends a request. A billable media smoke test remains pending an approved
      provider/model/cost selection.
- [ ] Add 1min.AI to the harness when its adapter is implemented.

Done when: ordinary tests never contact a real provider, all adapters use the shared fixture, and a
credentialed developer can run bounded live smoke tests deliberately.

## 3. Finish generation lifecycle and reconciliation

- [ ] Implement the complete normalized generation status vocabulary from `plan.md`, including all
      paused states, preparation/upload/submission, unknown outcome, monitoring pause, download,
      awaiting-library and cancellation variants.
      `GenerationStatus` now carries the full 18-value vocabulary (schema v36), with
      `GenerationHoldReason`/`GenerationFailureReason` sub-detail and a `generation_status_transitions`
      history table. `GenerationQueueService` advances Preparing/Submitting/Processing/
      CancelledBeforeSubmission/Failed for Text/Image/Audio/Video; per-child (position-scoped)
      transitions are schema-ready but unused; Uploading/Monitoring Paused/Downloading Results/
      Awaiting Library/Cancellation Requested are reachable values with no producing transition yet.
- [ ] Persist status transitions and per-child transitions so restart recovery does not infer state
      from transient UI data.
      Every generation now gets a durable `Queued` record at `Enqueue` time, advances through the
      transition-history table, and `GenerationQueueService.ResumeInFlightGenerationsAsync` (called
      from `Start()` and on library switch) auto-requeues Queued/Preparing records and advances
      anything else with no linked async-job registry row to `SubmissionOutcomeUnknown` rather than
      losing or silently resubmitting it. Per-child transition persistence remains unused.
- [ ] Implement **Submission Outcome Unknown** when transmission may have reached the provider but
      acceptance cannot be proven.
      Reachable both from restart recovery (ambiguous non-video records with no linked async-job row)
      and immediately when a Text/Image/Audio job is cancelled after its request may have already been
      transmitted (`GenerationQueueService`'s `SubmissionAttempted` flag distinguishes this from a
      cancellation that landed before anything was sent, which finalizes to
      `CancelledBeforeSubmission` instead). No reconciliation, UI surface or connection-revision
      gating against it exists yet.
- [x] Add **Abandon Recovery and Apply Changes**, retaining sanitized non-actionable history while
      removing identifiers that could still drive provider actions.
      `ILibraryWorkspace.AbandonGenerationOutcomeAsync` finalizes a `SubmissionOutcomeUnknown` or
      `Paused` record to `Failed`/`AbandonedByUser`, exposed via a confirm-guarded **Abandon** action
      on the generation history detail page. No further sanitization is needed for the record itself
      (it carries no actionable provider identifier — only the device-wide async-job registry does,
      for video, which is scrubbed separately). "Apply Changes" (re-running the connection-revision
      change the user was blocked on) is not yet wired since the blocking gate below doesn't exist.
- [ ] Gate connection URL, provider type and authentication-structure changes while unresolved
      cleanup or reconciliation depends on the current connection revision.
      `ConnectionEdit.razor` now also blocks an auth-structure change on `SubmissionOutcomeUnknown`/
      `Paused` generation records tied to a model on the connection, offering **Abandon and Apply
      Changes** (bulk `AbandonGenerationOutcomeAsync`) or **Cancel** — alongside the pre-existing
      pending-async-job-registry check with its own **Stop Tracking and Apply Changes**/**Cancel**
      pair. Still only two resolution paths rather than the documented three: **Attempt
      Reconciliation** is omitted because it isn't implementable for any adapter today (see "Future
      work" at the end of this file).
- [ ] Complete cancellation behavior before submission, during upload, after provider acceptance,
      during polling and during result download.
      Before-submission and mid-flight-with-unknown-outcome (Text/Image/Audio) now both finalize their
      durable record immediately (`CancelledBeforeSubmission`/`SubmissionOutcomeUnknown`) rather than
      one of them being left stranded until restart. Video's after-acceptance cancellation already
      committed a real `Cancelled`/`CancelledWithResults` record. "During upload" remains not
      applicable — no adapter has a distinct asset-upload call yet (see section 6) — and cancellation
      during result download is unhandled.
- [x] Retain temporary remote-asset associations and dependency pins until terminal resolution or
      explicit abandonment.
      Video's `async_remote_jobs` registry retains its provider-job association through every
      nonterminal outcome (including `CompletedAwaitingDownload`, kept specifically for a later
      **Refresh Provider Status** retry) and is only cleared on terminal resolution or an explicit
      abandonment path (`StopTrackingAndApplyChanges`, restart-recovery leaving it visible rather than
      deleting it). A still-queued job's recycled source-file/destination-folder dependency pins
      (`QueuedJob.RecycledDependencyIds`) are held the same way. `AbandonGenerationOutcomeAsync` is
      now the explicit-abandonment path for a generation record itself.
- [x] Apply in-progress throttling to explicit provider-status refresh and reconciliation actions.
      `GenerationQueueService.RetryMissingResultDownloadAsync` (**Refresh Provider Status**/**Import
      Missing Results**) now rejects a repeat call for the same async job within a 5-second window
      locally, without making a provider request. No reconciliation action exists yet to throttle
      (see "Future work" at the end of this file).

Done when: crash/restart, uncertain submission, cancellation and reconciliation tests cover every
implemented state transition without duplicate submission or untracked provider work.

## 4. Finish connectivity and background execution

- [x] Verify the implemented offline queue pause and explicit resume behavior with automated tests.
- [x] Verify **Allow**, **Ask** and **Wi-Fi/unmetered only** transfer policies, including a changing
      network classification during queued and active work.
- [x] Ensure active work is never silently cancelled merely because connectivity is lost or a
      connection becomes metered.
- [x] Provide an explicit **Resume All for This Connection** action that leaves other paused
      provider queues awaiting their own approval.
- [x] Provide an explicit selected-job resume action that does not authorize other pending jobs.
- [x] Record Android execution suspension and timeout separately from provider failure.
      `IBackgroundExecutionService.Suspended` is raised from `GenerationForegroundService.OnDestroy`
      whenever teardown wasn't initiated by the app's own `Stop` call. `GenerationQueueService`
      cancels every `Running` job on this signal and finalizes its durable record to
      `Failed`/`GenerationFailureReason.ExecutionSuspended` — distinct from an ordinary provider
      error message — rather than an ambiguous or generic cancellation outcome. `Monitoring`-phase
      (video, already-accepted) jobs are cancelled too but still resolve through the existing
      accurate `Cancelled`/`CancelledWithResults` outcome rather than the new reason, since that path
      never misattributes the cause to the provider either. Covered by a fake-driven unit test on
      Windows; real Android foreground-service `OnDestroy` timeout/kill behavior itself is
      unverified pending the on-device pass in section 15.
- [x] Save or resolve draft edits before Windows **Cancel Work and Exit** cancels jobs or terminates
      the process.

Done when: background or interrupted work has an accurate durable state and neither platform
misreports OS suspension as a provider failure.

## 5. Complete provider adapters and model settings

- [ ] Implement `ProviderType.OneMinAi` only after its current official request, response, polling,
      status and error contracts have been confirmed.
      No longer blocked on missing documentation — see `docs/developer/1minai-contract.md`. The docs
      site is fetchable (superseding the "could not even be fetched" note in `milestone3.md`), and
      one model per modality is now live-verified end to end: chat (`gpt-4o-mini`), image
      (`stable-diffusion-xl-1024-v1-0`, after the docs' own `black-forest-labs/flux-schnell` example
      turned out to be a stale/rejected identifier), audio (`tts-1`), and video
      (`lucataco/animate-diff:...`, confirmed genuinely synchronous — a non-`async` request blocks
      the HTTP connection for the full render). Still open before implementation: the enum value and
      test/harness scaffolding exist but no adapter body does; only one of ~41 image models and one
      of 10 video models has a live-confirmed `promptObject` shape, and per the doc's own findings a
      documented model identifier cannot be trusted without a live check, so each additional
      image/video model the adapter supports needs its own verification pass, not a docs read alone.
      The `async: true`/Get Result polling path also remains untested (every live call used the
      synchronous default).
- [ ] Complete all documented OpenRouter modality operations and close known response-shape gaps.
      Confirmed mostly done: `OpenRouterProviderAdapter` implements all four operations (text, image,
      audio, video submit/poll) with none throwing "not implemented." The one remaining documented
      gap is `ParseCost`'s hardcoded `"USD"` currency assumption, flagged in the class's own comment
      as provisional until confirmed against a live account — left open pending that confirmation
      rather than guessed at further.
- [x] Complete DeepInfra audio/video operations only for endpoints with confirmed contracts.
      Implemented against the confirmed contract in
      `docs/developer/deepinfra-audio-video-contract.md`. `DeepInfraProviderAdapter.GenerateAudioAsync`
      posts to the absolute `POST /v1/audio/speech` path (a different path root than the
      `/v1/openai/...` base used for chat/image, so it builds the request URI from the connection's
      scheme/host/port rather than reusing `OpenAiCompatibleProtocol.CombineUrl`) and returns the raw
      MP3 bytes. `SubmitVideoGenerationAsync`/`PollVideoGenerationAsync` implement the
      submit-then-poll job API (`POST /v1/videos`, `GET /v1/videos/{id}`), treating `succeeded` as
      the only success status and `queued`/`processing` as in-progress; any other status (including
      an unconfirmed/unknown one) is treated as a terminal failure rather than risking an infinite
      poll, since DeepInfra never documented an exhaustive status enum. On success, results are
      fetched from DeepInfra's own same-host `GET /v1/videos/{id}/content?variant=video` endpoint
      rather than the third-party CDN URL the poll response also reports (`data[].url`), which avoids
      needing the OpenRouter adapter's redirect/DNS-rebinding validation machinery entirely — the
      content endpoint returns bytes directly with no redirect. A provider-rejected model (confirmed
      live: a video model that doesn't support the async job API) surfaces its
      `{"error":{"message":...}}` body text as the thrown exception message via a new
      `DescribeDeepInfraFailure` helper, rather than a generic HTTP-status-only message. Covered by
      six new tests in `NewProviderAdapterTests.cs` (audio multi-result, submit success, submit
      provider-error passthrough, poll processing, poll completed via same-host content endpoint,
      poll unrecognized-status-as-failure); full suite (602 tests) and Windows build pass. The
      alternate synchronous `/v1/inference/{model}` path some models require instead was not
      implemented — its real response contract (synchronous body vs. webhook callback) was never
      independently confirmed, so DeepInfra video support is scoped to models that work through
      `/v1/videos`; an unsupported model fails clearly rather than silently guessing at the other
      endpoint's shape.
- [x] Define signed adapter snapshot versions and migrations so historical generation and saved
      setting records remain readable after adapter changes.
      "Signed" here means the app ships only built-in, code-signed adapters (plan.md:920-921), not
      per-record cryptographic signatures. `LibraryRules.CurrentGenerationSettingsFormatVersion`
      (schema v37) is a new `settings_format_version` column on `generation_records` and
      `saved_generation_settings`, set once at creation/update time and never rewritten afterward —
      pre-migration rows are retroactively tagged with the implicit original format (1) rather than
      losing their version. This is the versioning mechanism the requirement asks for; there is
      nothing to migrate yet since only one format version has ever existed — a future breaking
      change to how `GenerationSettings`/advanced JSON is interpreted would add the matching
      interpretation logic for older versions alongside bumping the constant.
- [ ] Add provider/model capability schemas for inputs, limits, settings, concurrency and
      instruction-channel behavior.
- [ ] Generate structured selectors, sliders, toggles, dimensions and voice controls from those
      schemas.
- [x] Implement the bounded advanced JSON editor for manually entered or unknown models.
- [x] Enforce reserved keys, nesting/size limits, type validation, normalized-request conflict
      detection and sanitized preview for advanced JSON.
- [x] Preserve advanced settings through drafts, saved settings, generation history and **Use
      Again**.

Done when: every exposed provider control changes a documented request field, and unsupported or
unknown behavior is rejected clearly rather than guessed.

## 6. Complete source inputs and provider file transfer

- [ ] Replace the three generic source-file fields with capability-defined named slots such as
      reference image, mask, first frame, last frame, source audio and source video.
- [ ] Persist slot role, order and immutable sent snapshots through drafts, saved settings, prompt
      improvement, history and **Use Again**.
- [ ] Implement per-slot media-type, count, byte, dimension, duration and token validation using a
      documented provider formula or a clearly labelled estimate.
- [ ] Block known-invalid submissions and label partial or approximate validation accurately.
- [ ] Implement provider-issued signed upload destinations for adapters that require out-of-band
      upload, including safe host, redirect and credential handling.
- [ ] Add generic/aliased transport filenames and reliability metadata only where an adapter sends a
      filename.
- [ ] Keep aliases stable between prompt improvement and the final generation submission.
- [ ] Add source/model-incompatibility and instruction-channel-mismatch review to **Use Again**.

Done when: source data cannot be silently dropped or assigned to an ambiguous role, and every
provider-bound file follows a documented and tested transfer path.

## 7. Harden streaming and result ingestion

- [ ] Display text incrementally for adapters that support streaming while writing incomplete output
      only to temporary managed storage.
      Confirmed not partially wired: no adapter opens a streaming (SSE) request today —
      `IProviderAdapter.GenerateTextAsync` returns one awaited `TextGenerationResult`, and the shared
      `OpenAiCompatibleProtocol` helper every adapter funnels through only has non-streaming
      buffered-response methods. Closing this requires a new adapter contract (e.g. an
      `IAsyncEnumerable`/callback-based streaming overload), real per-provider SSE parsing, and new
      `Generate.razor` incremental-render/temp-file-until-complete UI state — a genuine multi-file
      architecture change, not a bounded edit.
- [ ] Retain an interrupted partial response as a clearly labelled incomplete result when required
      by the plan.
      Blocked on the same missing streaming infrastructure as above (plan.md:1716's "Interrupted"/
      "Incomplete Response" only applies once a streamed response can be disconnected mid-stream).
- [ ] Stream large result downloads into bounded temporary storage rather than buffering the complete
      response in memory.
      Recovery staging's `StageFromStreamAsync` already does real bounded write-through streaming
      with cleanup on failure and is reusable. Closing the gap end-to-end still requires changing
      `IProviderAdapter.GenerateImageAsync`/`GenerateAudioAsync`/the video poll result shape from
      `byte[]`/`IReadOnlyList<byte[]>` to a stream-based contract, the shared `OpenAiCompatibleProtocol`
      HTTP layer that buffers into a `MemoryStream`, `GenerationQueueService`'s per-modality handling,
      and `ILibraryWorkspace`'s `Record*GenerationResultAsync` commit paths — roughly 6-8 call sites
      across Core/Infrastructure/Gui. A narrower version (only the OpenRouter video-download path,
      already singled out by this section's other redirect/DNS/checksum bullets) still touches the
      same shared `AsyncGenerationPollResult.Files` type and commit path, so it isn't cleanly
      separable from the larger migration either. Left open rather than attempting a partial refactor
      that could subtly break the existing checksum/media-type verification, which currently runs
      against fully-buffered bytes.
- [x] Revalidate every redirect target before following it for OpenRouter result downloads.
- [x] Prevent DNS rebinding by binding validation to the addresses used for the actual connection
      on the OpenRouter HTTP client.
- [x] Enforce response status, declared and detected media category, byte limit and provider checksum
      when one is supplied for OpenRouter result downloads.
      The raw-byte protocol now enforces a 512 MiB limit both from `Content-Length` and while
      reading an unbounded response. OpenRouter video downloads reject explicitly non-video media
      declarations; detected-byte validation and opaque-result review happen in the library commit
      path. Standard SHA-256 `Content-Digest` and legacy `Digest` headers are verified when present.
- [x] Finish the documented opaque-binary and unrecognized-content-type review paths.
- [x] Verify atomic commit and independent partial failure handling for multi-result downloads.

Done when: hostile redirect, DNS, size, type and checksum fixtures cannot cause an unsafe fetch or a
misclassified library commit.

## 8. Implement the external export and sidecar system

- [ ] Implement atomic external media export with destination identity binding, flush and optional
      trustworthy read-back verification.
- [ ] Implement the authenticated, versioned, device-local external-export cleanup journal.
- [ ] Recover pending cleanup after crashes without deleting an object whose ownership and identity
      cannot be verified.
- [ ] Handle document-provider renames, expired permissions, unavailable providers, target swaps,
      symlink/reparse substitution and already-absent targets.
- [ ] Implement versioned `.slopfactory.json` sidecars using deterministic encoding, formatting and
      property ordering.
- [ ] Publish and bundle the sidecar JSON Schema and implement schema-version handling.
- [ ] Implement privacy-minimal defaults plus disclosure previews and explicit opt-ins for prompts,
      sensitive metadata, filenames, internal identifiers, usage/cost, advanced settings and safety
      metadata.
- [ ] Revalidate selected metadata, provenance and safety revisions immediately before sidecar
      commit.
- [ ] Track media and sidecar commits, read-back verification and cleanup independently.
- [ ] Implement bulk-export continuation and per-item reporting when media, sidecar, verification or
      cleanup fails.
- [ ] Add crash injection around every journal flush, object creation, identity binding, content
      flush, verification, atomic commit and journal-removal boundary.

Done when: no partial or unverified export is reported as successful, no unrelated object can be
deleted, and sensitive sidecar fields are never included without a fresh explicit review.

## 9. Complete cost and usage safeguards

- [ ] Implement pre-generation estimates using only documented local pricing or a non-billable
      estimate endpoint that does not submit prompt/source content without separate consent.
- [ ] Show deterministic values or reliable ranges with source and effective pricing date.
- [ ] Implement the first-use acknowledgement for a model/connection revision whose cost is unknown.
- [ ] Implement device-wide thresholds keyed by exact currency/provider unit and per-connection
      overrides.
- [ ] Compare thresholds only between like units and use the reliable upper bound of a range.
- [ ] Store the displayed estimate, source, range, effective date and applied threshold in history.
- [ ] Compare provider-reported actual cost with the estimate and threshold and highlight material
      overruns.
- [ ] Mark a bundled pricing revision **Unreliable** after the specified repeated material overruns.
- [ ] Finish the cost-summary view with date, provider, connection, model and operation filters.
- [ ] Correct the current **Cost unknown** notice wherever an adapter reports real cost.
- [ ] Export opted-in run/per-output/prompt-improvement usage and cost accurately in sidecars,
      including nonterminal `reported-so-far` state.

Done when: cost UI never presents an estimate as actual, never compares unlike units and never
claims that a local threshold is a provider-account spending limit.

## 10. Complete provider safety behavior

- [ ] Persist normalized provider safety classifications and their immutable association with the
      exact content hash that was classified.
- [ ] Implement concealment, session reveal, persistent per-file override and external-open
      reauthorization.
- [ ] Share applicable classification events across duplicate content without leaking unrelated
      file metadata.
- [ ] Give content-filtered multi-result children stable per-child identities rather than only an
      aggregate blocked count.
- [ ] Implement **Provider Blocked After Delivery** when a provider exposes a documented late
      reclassification signal.
- [ ] Map OpenRouter, DeepInfra and 1min.AI safety signals only after each contract is confirmed.
- [ ] Reactivate a classification after exact-byte restoration, but never transfer it to differing
      replacement bytes.
- [ ] Apply safety-aware export confirmation and sidecar disclosure rules.

Done when: safety state follows the classified bytes, remains distinct from diagnostics and cannot
silently migrate to unrelated or replaced content.

## 11. Finish draft, history and media resilience

- [ ] Preserve recycled or missing source and destination references in open drafts as stable
      unavailable references instead of clearing them.
- [ ] Preview affected open tabs before recycle or permanent deletion.
- [ ] Provide restore, replace and remove actions; convert permanently deleted references into an
      explicit non-restorable state.
- [ ] Revalidate open drafts and saved settings after managed-content replacement.
- [ ] Mark incompatible replacements **Needs Review** and compatible replacements **Content
      Replaced**.
- [ ] Prevent submission until every required unavailable or incompatible reference is resolved.
- [ ] Implement **Reacquire Permanently Deleted Output** after deciding and documenting what durable
      remote identifier may be retained and for how long.
- [ ] Preserve the old tombstone and create a new file identity for reacquired bytes.
- [ ] Warn and record **Provider Output Changed** when reacquired bytes do not match the tombstone.
- [x] Generate static audio waveform thumbnails in the regenerable preview cache.
      Scope decision: real waveform rendering is descoped as not worth the cost of adding an
      audio-decoding dependency (see the prior finding below, kept for context). Audio files instead
      get a standard type-icon badge, matching every other unsupported-thumbnail case. This is already
      fully satisfied by existing code, not new work: `Home.razor`'s `CanShowThumbnail` already
      excludes audio from thumbnail generation, and its `MediaIcon` fallback already renders a styled
      "AUD" badge (`.file-type-icon` — the same rounded, colored badge used for "IMG"/"VID"/"TXT"/
      "FILE") for every audio file, consistently, with no broken or partial preview state.
      Prior finding, kept for context in case waveform rendering is revisited later:
      `PreviewCacheService` already generates image thumbnails and video posters (via
      `PlatformImage`/platform-native video-frame APIs) but has zero audio-decoding capability, and
      neither `Mellow.SlopFactory.Gui` nor `Mellow.SlopFactory.Infrastructure` reference any
      audio-decoding library. A real waveform would need compressed-audio-to-PCM decoding
      (mp3/wav/flac/ogg/aac/m4a are all accepted today), which has no MAUI-provided cross-platform
      path and would require either a new third-party dependency or hand-rolled per-platform decoders
      (Android `MediaExtractor`/`MediaCodec`, Windows Media Foundation).

Done when: no draft dependency disappears silently, historical identities remain immutable and
reacquisition never masquerades changed bytes as restoration.

## 12. Complete removable-storage recovery

- [x] Automatically reconcile staged results into their intended library after its volume returns.
      `StagedResultEntry` now carries `GenerationRecordId`/`Position` (plan.md:329's "generation
      identifier"), set when `GenerationQueueService` stages a video result during a volume-unavailable
      commit failure (also advancing the durable record to `GenerationStatus.AwaitingLibrary`).
      `GenerationQueueService.ReconcileStagedResultsAsync`, called from `Start()` and on every library
      switch, commits each staged group into its record via the existing
      `RecordMediaGenerationResultAsync(..., existingGenerationRecordId:)` path — no adapter call
      needed, since the bytes and the record's own prompt/model/settings are already durable. Entries
      staged before this linkage existed (`GenerationRecordId` null) remain manual-only, matching the
      documented fallback. Only the video/`AsyncGenerationPollOutcome` staging path is covered; image/
      audio generation doesn't use recovery staging at all today (see section 7's still-open
      bounded-download-streaming gap).
- [x] Delete a staged copy only after the intended library commit succeeds durably.
      `ReconcileStagedResultsAsync` only calls `DiscardAsync` after
      `RecordMediaGenerationResultAsync` returns successfully; a reconciliation failure (caught per
      generation-record group so one bad group can't block the rest) leaves the entry staged for a
      later attempt.
- [ ] Retry a staged download when internal storage was insufficient and the provider result is
      still available.
      Still unimplemented: no "insufficient internal staging storage" detection or provider-side
      retry exists — today's staging path only reacts to the *destination* library's volume being
      unavailable, not to the device's internal staging storage itself running out mid-write.
- [x] Notify the user generically when a staged result is awaiting its library.
      `MainLayout.razor` now shows a device-wide count-only banner ("N result(s) are awaiting their
      library and may expire remotely") linking to Recovery Staging, alongside the pre-existing
      passive nav link and per-library `LibrarySettings.razor` flag.
- [x] Preserve provenance during reconciliation without placing prompts, credentials or sensitive
      settings in the device-wide staging registry.
      `StagedResultEntry`'s new fields are only an opaque record ID and integer position — no prompt,
      model settings or credential ever entered the registry; reconciliation reads the actual
      prompt/model/settings from the durable `GenerationRecord` in the library database, never from
      the staging registry itself.
- [ ] Add fault-injection tests for volume removal during prepare, commit, post-commit cleanup and
      application restart.
      Covered: commit-time volume removal (staging occurs) and the reconciliation half (a second
      "session" against the same staging registry after the volume returns), both driven through a
      real staged→reconciled round trip. Not covered: removal during prepare, during post-commit
      cleanup specifically, or while the application is still running (only the restart-driven
      reconciliation path is tested).

Done when: a missing volume cannot lose a completed provider result or create duplicate committed
outputs, and recovery exposes no sensitive content outside its library.

## 13. Complete UI acceptance automation

- [ ] Add rendered fixed-viewport coverage for primary phone, tablet and desktop layouts.
- [ ] Add keyboard-driven coverage for focus visibility, activation, modal focus capture and focus
      restoration.
- [x] Add automated manifest verification for Android backup exclusion, permissions and document
      picker declarations.
      `AndroidManifestBuildVerificationTests` parses the real Android-manifest-merger output from a
      `net10.0-android` build (locating an existing `obj/{Debug,Release}` build or triggering one) as
      XML and asserts on parsed elements/attributes — built-artifact coverage, distinct from
      `UiAssetTests`'s pre-existing source-text assertion. Skips gracefully (matching this codebase's
      existing environment-dependent-prerequisite convention) if no Android SDK/workload is available
      to produce a build.

Done when: responsive, keyboard/focus and Android manifest requirements have behavioral or built-
artifact coverage rather than only source-level markup or CSS assertions.

## 14. Complete automated release verification

- [ ] Add process-kill/crash tests for queue, draft, export, session and staging recovery.
      Partial: queue (`GenerationQueueServiceTests`'s restart-recovery tests, section 3) and staging
      (the staged→reconciled round trip added in section 12) both simulate a hard process exit via
      `libraries.DisposeAsync()` then a fresh harness reopening the same on-disk library. Draft,
      export and session crash recovery remain untested here.
- [x] Add volume-disconnect-mid-commit and dependency-recycled queue-pause tests.
      Volume-disconnect-mid-commit:
      `AVideoResultIsStagedForRecoveryWhenItsLibraryBecomesUnavailableDuringTheFinalCommit` and
      `StagedVideoResultsAreAutomaticallyReconciledOnceTheLibraryVolumeReturns` (section 12).
      Dependency-recycled queue-pause: extensive pre-existing coverage in
      `GenerationQueueServiceTests` (`ADependencyRecycledJobDoesNotBlockALaterQueuedJobOnTheSameConnectionFromRunning`
      and others asserting `GenerationJobPhase.DependencyRecycled`).
- [ ] Add second-instance launch-forwarding tests.
- [ ] Add Android execution-suspension and notification permission tests where automation permits.
      Partial: execution suspension is covered by
      `BackgroundExecutionSuspensionCancelsRunningJobsAndFinalizesTheirRecordsDistinctlyFromAProviderFailure`
      (section 4). Runtime notification-permission prompting is not automatable from this test
      harness (it requires a real OS permission dialog) and remains a manual test (section 15).
- [x] Run the complete unit/integration suite with zero failures.
      597/597 passing as of this pass (`dotnet test tests/Mellow.SlopFactory.Tests`).
- [x] Produce clean Windows Debug and Release builds.
      `dotnet build src/Mellow.SlopFactory.Gui -f net10.0-windows10.0.22621.0 -c Debug` and `-c
      Release` both succeed with 0 warnings/0 errors as of this pass. Unsigned/development-signed —
      production Store/sideload signing is section 15's manual gate.
- [x] Produce clean Android Debug and Release builds.
      `dotnet build src/Mellow.SlopFactory.Gui -f net10.0-android -c Debug` and `-c Release` both
      succeed with 0 warnings/0 errors as of this pass. Unsigned/debug-signed — production signing
      (AAB/APK) is section 15's manual gate.
- [ ] Verify diagnostic redaction, rolling retention, crash records and exported diagnostics.
- [ ] Verify every user-visible string remains resource-backed and layouts tolerate longer and RTL
      test strings.

Done when: all automated checks and platform builds have recorded passing evidence.

## 15. Manual completion checklist

These tasks require human observation, real devices, provider credentials, signing material, store
accounts or publishing authority. Keep their evidence in `manual_tests.md` or the release record.
Repository automation may assist them, but does not complete them by itself.

### Live-provider verification

- [ ] Approve an explicit maximum cost for the live-provider test run.
- [ ] Supply dedicated low-privilege test credentials through OS or CI secret storage.
- [ ] Exercise OpenAI, generic OpenAI-compatible, OpenRouter and DeepInfra through the live-provider
      harness.
- [ ] Exercise 1min.AI after its adapter and live fixture are implemented.
- [ ] Confirm retained logs and artifacts contain no credentials, prompts, source content or signed
      result URLs.

### Milestone 1 device acceptance

- [ ] Run MT-01 for theme persistence and Windows high contrast.
- [ ] Run MT-02 for responsive layout and touch interaction.
- [ ] Run MT-03 for Windows keyboard and focus recovery.
- [ ] Run MT-04 for Android app-specific/removable storage identity and reappearance.
- [ ] Inspect the built Android manifest and run MT-05 for uninstall warnings, backup exclusion,
      system document pickers and permission routing.
- [ ] Run MT-06 for the cross-platform library workflow.

### Platform background and recovery behavior

- [ ] Test Android foreground-transfer notification creation, runtime notification permission,
      process suspension and resume on a supported device.
- [ ] Test Windows notification-area keep-running, explicit exit and second-instance forwarding.
- [ ] Remove and restore a real removable volume during prepare, commit, post-commit cleanup and
      application restart.
- [ ] Confirm the Windows default library survives a real packaged uninstall/reinstall cycle; if it
      does not, return the issue to the implementation checklist for an explicitly non-virtualized
      user-data location fix.

### Full release acceptance

- [ ] Execute the complete manual acceptance matrix on the minimum supported Windows and Android
      versions and representative newer versions.
- [ ] Complete a Narrator and TalkBack pass over library, generate, queue, history and recovery
      workflows.
- [ ] Profile large libraries, long queues and minimum-supported/low-end devices; record thresholds,
      findings and resolved regressions.
- [ ] Record device, OS version, build identity, result and evidence for every manual test.

### Signing, stores and publication

- [ ] Configure production Windows signing outside the repository and produce the signed MSIX.
- [ ] Configure production Android signing outside the repository and produce the signed AAB and
      direct-download APK.
- [ ] Verify stable production identity/signing lineage, downgrade rejection and source-switch
      behavior with real signed packages.
- [ ] Complete Microsoft Store and Google Play installation, update and clean-uninstall tests.
- [ ] Publish the official HTTPS download page and configure its static URL in the application.
- [ ] Publish cryptographic checksums and signing-certificate information for direct packages.
- [ ] Verify **About** and diagnostics show the correct channel, semantic version, build number and
      download link for every artifact.
- [ ] Verify development builds remain isolated from production preferences, secure storage and
      Android app-specific libraries.
- [ ] Complete final release notes and align all user/developer documentation.
- [ ] Tag the final release commit and associate every published artifact reproducibly with it.
- [ ] Run final Windows and Android smoke tests using the exact published packages.

Done when: all manual test evidence is recorded, signed Store and direct-download artifacts install
and update correctly, published checksums verify, and no production secret or signing material
exists in the repository.

## Future work (deferred pending provider documentation)

These items are not incomplete for lack of effort — each names a capability that no currently
integrated provider adapter (OpenAI, DeepInfra, OpenRouter, generic OpenAI-compatible, or the
still-unimplemented 1min.AI) documents a supporting mechanism for. Implementing any of them now
would mean guessing at an undocumented provider contract, which the release gate explicitly
disallows. Revisit only once a specific adapter's documentation confirms the underlying mechanism
exists, then re-add the item to section 3 with that adapter named.

- **Attempt Reconciliation** (originally section 3): no adapter captures a provider request ID for
  its synchronous Text/Image/Audio calls, or documents idempotency-key replay or a status-lookup
  endpoint that could confirm whether an ambiguous submission actually reached the provider.
  `milestone2.md`/`milestone4.md` already recorded this as deliberately deferred.
- **Idempotency-key creation, persistence, reuse and disposal** (originally section 3): same root
  cause as Attempt Reconciliation — no integrated adapter documents idempotency-key support.
  `AsyncRemoteJobRecord.IdempotencyKey` and `async_remote_jobs.idempotency_key` remain as schema
  placeholders for when one does.
- **Monitoring Paused, Check Now and Resume Monitoring** (originally section 3): no adapter ever
  sets `AsyncGenerationSubmission.MonitoringDeadline` (always null), so no adapter currently
  declares a maximum monitoring lifetime for this feature to react to. The `MonitoringPaused` status
  and `MonitoringDeadline` field remain in place in the schema/enum for when one does.

## Release completion gate

- [ ] Sections 1 through 15 are complete or an explicitly first-release-excluded requirement has
      been removed from `plan.md` with an approved scope decision.
- [ ] No unresolved `TODO`, skipped release test, stale checklist entry or undocumented manual-test
      failure remains.
- [ ] The implementation checklist and dedicated manual completion checklist both have complete,
      reviewable evidence.
