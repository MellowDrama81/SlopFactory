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
- [x] Remove requirements from `plan.md` only after their implementation and verification are
      complete, in keeping with the file's stated purpose.
      An ongoing policy, not a one-shot task — most `plan.md` topics still map to at least one
      still-open checklist section (including section 15's manual/release gate, which stays open
      until actual release), so most of `plan.md` cannot honestly be pruned yet. This pass removed 9
      individual bullets from `plan.md`'s "Android Background Work" and "Windows Background Work"
      sections that are each fully implemented and verified with no remaining sub-clause (Android
      execution-suspension distinction and the no-boot-receiver guarantee; Windows's Keep
      Running/Cancel Work and Exit/Return to App dialog, tray icon, remembered choice, provider
      cancellation on exit, and the "never hides without explaining" guarantee) — matching the exact
      per-bullet granularity `milestone2.md`/`milestone3.md`/`milestone4.md`'s own prior pruning
      passes used, never removing a bullet that still mixes a done and an undone clause (e.g. the
      Retry Save draft-exit dialog, and async-job-polling resumption on restart, both stay since
      neither is actually built yet).
- [x] Update stale Milestone 2 entries that were completed by Milestone 3, including Audio/Video
      modes and the OpenRouter and DeepInfra adapters.
- [x] Retire or merge `Milestone1_Remaining_Plan.md` so Milestone 1 has one checklist.
- [x] Add links from each still-open milestone entry to its owning item in this checklist.
      Every unchecked (`- [ ]`) item across `milestone1.md`–`milestone4.md` (65 items total) now
      carries an explicit "Owned by `IMPLEMENTATION_COMPLETION_CHECKLIST.md` section N" link. Several
      of these links also surfaced items that are now actually satisfied by later work but were never
      marked done in their own milestone file (e.g. `milestone3.md`'s per-slot source-input model and
      `milestone2.md`'s generation-history record model) — left as-is in the milestone files
      themselves (they're retained history, not live status) rather than retroactively rewritten.
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
- [x] Add contract tests for instruction-channel mapping, normalized message order, hidden-message
      absence, capability rejection and immutable sent-instruction snapshots.
      OpenAI-compatible instruction mapping, message order/absence and source-byte snapshots are
      covered by `ProviderInstructionContractTests`. Capability rejection was previously blocked on
      section 5's broader capability-schema work, but that conflated two different things: the
      *settings* capability schema (temperature/topP/etc., section 5) is unrelated to whether a model
      that lacks `Model.SupportsSystemInstructions` can have a system instruction transmitted to it —
      that only needed the pre-existing per-model bool, which the four OpenAI-compatible adapters
      (OpenAI, generic OpenAI-compatible, OpenRouter, DeepInfra) never actually checked before this
      pass: `OpenAiProviderAdapter.GenerateTextAsync` and its three siblings blindly forwarded
      whatever `systemInstructions` value was passed, relying entirely on `Generate.razor`'s own UI
      code nulling it out first — a single call site's discipline, not a guarantee, and the true wire
      boundary had no defense of its own. Added
      `OpenAiCompatibleProtocol.ValidateSystemInstructionsSupported(model, systemInstructions)`,
      called at the top of all four adapters' `GenerateTextAsync`, throwing `ProviderAdapterException`
      before any HTTP request is built. Four new `NewProviderAdapterTests` (one per adapter) prove
      rejection happens with **zero** HTTP requests sent (a fake handler that throws if invoked, not
      just an assertion on the response), plus one proving an absent instruction is still accepted
      regardless of capability. 1min.AI is unaffected: its adapter already handles system instructions
      via prompt-prepending rather than a true system-role message (its API has no separate channel),
      which is a deliberate instruction-channel mapping choice, not a capability violation to guard.
- [x] Add fault-injection coverage for timeouts, disconnects, truncated streams, invalid JSON,
      malformed media, redirect chains, DNS changes and partial multi-result responses.
      Timeout, disconnect, cancellation, truncated-stream, invalid-JSON, malformed-media and
      partial-result fixtures are now covered. Redirect-target revalidation is covered for
      OpenRouter result downloads, including a bounded redirect loop. The OpenRouter HTTP handler
      also rejects a private address before connecting, covering DNS rebinding.
- [x] Create an explicitly enabled live-provider harness that skips without credentials, enforces a
      caller-approved cost budget and sanitizes every retained artifact.
      The discovery harness and budget guard are implemented; nothing is retained (discovery-only,
      no logging to disk), so "sanitizes every retained artifact" is satisfied by having nothing to
      sanitize. Fixed the previously-flagged gap: `LiveProviderSmokeTests`'s test method called
      `settings.RequireDiscoveryRun()` (which already threw a skip exception with a clear reason for
      each unmet precondition) only after an earlier `if (!settings.CanRunDiscovery) return;` early
      exit had already silently no-op'd the absent-credential case, so the skip path was never
      actually reached. Removed that early-return, and switched the underlying exception from
      `Xunit.Sdk.SkipException` to `Xunit.SkipException` (added the `Xunit.SkippableFact` package,
      test-project-only) with the test marked `[SkippableFact]` — verified plain xUnit v2 + the
      current `xunit.runner.visualstudio` combination does not recognize a thrown
      `Xunit.Sdk.SkipException` as anything but a hard `Failed` result (confirmed by reproducing it),
      so the fix genuinely needed a runner-recognized mechanism, not just a differently-worded
      exception. `dotnet test` now reports this test as `Skipped` with a reason, not a silent `Passed`
      or a false `Failed`, when no live credentials are configured (the normal case for every
      contributor). A billable media smoke test itself remains pending an approved provider/model/
      cost selection and real credentials — that execution step is section 15's manual gate, not
      something this automated harness can complete on its own.
- [x] Add 1min.AI to the harness when its adapter is implemented.
      `LiveProviderSmokeTests.CreateAdapter` now has a `ProviderType.OneMinAi` case.

Done when: ordinary tests never contact a real provider, all adapters use the shared fixture, and a
credentialed developer can run bounded live smoke tests deliberately.

## 3. Finish generation lifecycle and reconciliation

- [ ] Implement the complete normalized generation status vocabulary from `plan.md`, including all
      paused states, preparation/upload/submission, unknown outcome, monitoring pause, download,
      awaiting-library and cancellation variants.
      `GenerationStatus` carries the full 18-value vocabulary (schema v36), with
      `GenerationHoldReason`/`GenerationFailureReason` sub-detail and a `generation_status_transitions`
      history table. `GenerationQueueService` advances Preparing/Submitting/Processing/
      CancelledBeforeSubmission/Failed/Cancelled/CancelledWithResults/SubmissionOutcomeUnknown/Paused
      for Text/Image/Audio/Video. Re-audited this pass against the actual code rather than trusting
      the stale note that was here: `AwaitingLibrary` **is** now reachable — a later pass (section 12)
      wired `GenerationQueueService.ReconcileStagedResultsAsync`'s volume-unavailable-mid-commit path
      to advance it. Four values genuinely remain unreachable, each blocked on a real, verified
      absence rather than an oversight: `Uploading` (no adapter has a distinct asset-upload call —
      every current adapter sends source bytes inline in the same request as the rest of the
      submission); `MonitoringPaused` (no adapter ever populates
      `AsyncGenerationSubmission.MonitoringDeadline`, tracked under this section's "Future work");
      `DownloadingResults` (a video result's bytes are downloaded *inside* the adapter's synchronous
      `PollVideoGenerationAsync` call, not as a separate step `GenerationQueueService` can see and
      label — exposing one would need the same streaming/callback-shaped adapter contract change
      section 7 already defers as a genuine architecture change, not a bounded addition);
      `CancellationRequested` (no adapter has *any* provider-side cancel call, supported or not — `Cancel()`
      today only stops local polling, never asks the provider to stop, so there is no "requested but
      the provider kept going" case to represent). Per-child (position-scoped) transitions remain
      schema-ready but unused — see the next item.
- [ ] Persist status transitions and per-child transitions so restart recovery does not infer state
      from transient UI data.
      Every generation gets a durable `Queued` record at `Enqueue` time, advances through the
      transition-history table, and `GenerationQueueService.ResumeInFlightGenerationsAsync` (called
      from `Start()` and on library switch) auto-requeues Queued/Preparing records and advances
      anything else with no linked async-job registry row to `SubmissionOutcomeUnknown` rather than
      losing or silently resubmitting it. Per-child transition persistence remains genuinely unused —
      the `generation_status_transitions.position` column and
      `AdvanceGenerationStatusAsync(..., position:)` parameter both already exist end to end, so
      wiring video's multi-job submit/poll loop to call them per job looked mechanical at first, but
      isn't: a job's *submission-order* index (0, 1, 2… as `SubmitVideoGenerationAsync` is called) is
      not the same number as its *final result position* once committed — the existing shortfall-
      reporting logic in `ExecuteVideoGenerationAsync` assigns terminal positions as
      `files.Count + messageIndex` in commit order, specifically so a job that failed to even submit
      doesn't consume a position ahead of ones that succeeded. Recording per-child transitions under
      submission-order position numbers while the final `generation_results` rows use commit-order
      position numbers would make the two tables disagree about what "position 1" means for the same
      generation — worse than not recording per-child transitions at all. Reconciling the two position
      spaces (or deciding they're legitimately different things) is a real design decision, not a
      mechanical wire-up, and wasn't rushed here.
- [x] Implement **Submission Outcome Unknown** when transmission may have reached the provider but
      acceptance cannot be proven.
      Reachable both from restart recovery (ambiguous non-video records with no linked async-job row)
      and immediately when a Text/Image/Audio job is cancelled after its request may have already been
      transmitted (`GenerationQueueService`'s `SubmissionAttempted` flag distinguishes this from a
      cancellation that landed before anything was sent, which finalizes to
      `CancelledBeforeSubmission` instead). The note previously here claimed "no reconciliation, UI
      surface or connection-revision gating against it exists yet" — re-checked against the code and
      that's stale on two of three counts: a UI surface does exist (`GenerationSubmissionOutcomeUnknown`
      status label shown throughout, a dedicated filter option on `GenerationHistory.razor`, and the
      confirm-guarded **Abandon** action on `GenerationHistoryDetail.razor`), and connection-revision
      gating against it does exist (see the next item — `ConnectionEdit.razor` blocks on it). Only
      **Attempt Reconciliation** itself remains genuinely missing, and that's correctly deferred under
      "Future work" below (no adapter documents a way to confirm whether an ambiguous submission
      reached it).
- [x] Add **Abandon Recovery and Apply Changes**, retaining sanitized non-actionable history while
      removing identifiers that could still drive provider actions.
      `ILibraryWorkspace.AbandonGenerationOutcomeAsync` finalizes a `SubmissionOutcomeUnknown` or
      `Paused` record to `Failed`/`AbandonedByUser`, exposed via a confirm-guarded **Abandon** action
      on the generation history detail page. No further sanitization is needed for the record itself
      (it carries no actionable provider identifier — only the device-wide async-job registry does,
      for video, which is scrubbed separately). "Apply Changes" is now wired too — see the next item's
      `ConnectionEdit.razor` gate, which offers bulk **Abandon and Apply Changes** directly.
- [x] Gate connection URL, provider type and authentication-structure changes while unresolved
      cleanup or reconciliation depends on the current connection revision.
      `ConnectionEdit.razor` blocks a save on `SubmissionOutcomeUnknown`/`Paused` generation records
      tied to a model on the connection, offering **Abandon and Apply Changes** (bulk
      `AbandonGenerationOutcomeAsync`) or **Cancel** — alongside the pre-existing pending-async-job-
      registry check with its own **Stop Tracking and Apply Changes**/**Cancel** pair. A real gap
      surfaced while re-verifying this against the literal "connection URL, **provider type** and
      authentication-structure changes" wording: both gates were keyed only off `AuthStructureChanged()`
      (base URL/credential header/auth prefix) — a provider-type change fell through to
      `ChangeConnectionProviderTypeAsync` completely ungated, even though switching providers changes
      which adapter (and contract) any still-unresolved job or outcome would need to reconcile
      against. Fixed by adding `ProviderTypeChanged()` and gating on `AuthStructureChanged() ||
      ProviderTypeChanged()` instead. Still only two resolution paths rather than the documented
      three: **Attempt Reconciliation** is omitted because it isn't implementable for any adapter
      today (see "Future work" at the end of this file). This is Razor code-behind with no dedicated
      test, consistent with this codebase's established no-bUnit convention.
- [x] Complete cancellation behavior before submission, during upload, after provider acceptance,
      during polling and during result download.
      Before-submission and mid-flight-with-unknown-outcome (Text/Image/Audio) both finalize their
      durable record immediately (`CancelledBeforeSubmission`/`SubmissionOutcomeUnknown`) rather than
      either being left stranded until restart. Video's after-acceptance cancellation commits a real
      `Cancelled`/`CancelledWithResults` record. "During upload" remains not applicable — no adapter
      has a distinct asset-upload call yet (see section 6). "During result download" was previously
      noted as unhandled; tracing it found that's stale — `ExecuteVideoGenerationAsync`'s
      `catch (OperationCanceledException) when (submitted.Count > 0)` wraps the entire poll loop,
      including the adapter's internal download step inside `PollVideoGenerationAsync`, so a
      cancellation that lands mid-download already unwinds into the same tested
      `Cancelled`/`CancelledWithResults` commit path as any other post-acceptance cancellation —
      already covered by the existing zero-/some-results-completed cancellation tests.
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

- [x] Implement `ProviderType.OneMinAi` only after its current official request, response, polling,
      status and error contracts have been confirmed.
      `OneMinAiProviderAdapter` implements text, image and audio against the confirmed contract in
      `docs/developer/1minai-contract.md`: chat via `POST /api/chat-with-ai`
      (`type: UNIFY_CHAT_WITH_AI`, text taken from `aiRecord.aiRecordDetail.resultObject[0]`); image
      and audio via the shared `POST /api/features` envelope (`IMAGE_GENERATOR`/`TEXT_TO_SPEECH`),
      downloading the completed result from the response's `temporaryUrl` (a third-party S3 host) with
      the same redirect-bounded, SSRF-validated download path OpenRouter's video downloader uses, plus
      the same DNS-rebinding-hardened `HttpClient` handler (`DependencyInjection.cs`). Two distinct
      provider error envelopes are handled: chat's nested `{"error":{"message":...}}` and features'
      top-level `{"errorCode":...,"message":...}` (confirmed live via the Flux Schnell rejection).
      Deliberately scoped down from "every documented model" to what's actually confirmed: image
      generation only encodes the one live-verified `promptObject` shape (Stable Diffusion XL's
      prompt/samples/size, fixed at `1024x1024`); the other ~40 image models and 9 of 10 video models
      each use their own undocumented `promptObject` field set per the doc's own findings, so a request
      to one of those models is expected to surface as a clear provider error rather than being
      guessed at. Video is **not implemented**: 1min.ai's default video behavior is genuinely
      synchronous (confirmed live — a non-`async` request blocks the HTTP connection for the full
      render), which does not fit this app's submit-then-poll `SubmitVideoGenerationAsync`/
      `PollVideoGenerationAsync` split, and the `async: true` + `GET /api/results/{uuid}` path that
      would fit it was never live-tested — implementing it would mean guessing at an unconfirmed
      contract, so both video methods throw a clear explanatory `ProviderAdapterException` instead
      (same precedent as DeepInfra's original video stub). Model discovery
      (`ListModelsAsync`/`TestConnectionAsync`) is also not implemented: no models-listing endpoint is
      documented anywhere in 1min.ai's API reference, so `TestConnectionAsync` reports success without
      a real connectivity check and `ListModelsAsync` always throws a clear "not available" error.
      Wired into `DependencyInjection.cs`, the connection-provider dropdown and provider-name display
      in `ConnectionEdit.razor`/`GenerationHistory.razor`/`CostSummary.razor`/`Connections.razor`, the
      `ProviderOneMinAi` localization string, and `LiveProviderSmokeTests`. Covered by 7 new tests in
      `NewProviderAdapterTests.cs` (text, image-conditioning rejection, image+download, feature-error
      passthrough, audio+download, video not-implemented, model-discovery not-implemented); full suite
      and Windows/Android builds pass.
- [x] Complete all documented OpenRouter modality operations and close known response-shape gaps.
      `OpenRouterProviderAdapter` implements all four operations (text, image, audio, video
      submit/poll) with none throwing "not implemented." The one remaining documented gap —
      `ParseCost`'s hardcoded `"USD"` currency assumption — is now confirmed rather than provisional:
      OpenRouter's own FAQ (https://openrouter.ai/docs/faq, fetched 2026-08-18) states "OpenRouter
      uses a credit system where the base currency is US dollars. All of the pricing on our site and
      API is denoted in dollars." This is public-documentation confirmation, not a live-account test,
      but it satisfies "verified against real documentation" — the class comment and this note now
      cite the source instead of flagging it provisional.
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
      Two of the five now have a real schema, following section 6's `GetInputSlotCapabilities`
      precedent (a small switch, not a stored/persisted schema): **inputs** — already existed
      (`GetInputSlotCapabilities`). **settings** — new `LibraryRules.GetGenerationSettingsCapabilities(providerType, mode)`
      / `GenerationSettingsCapability` flags enum, built after verifying a real, previously-silent gap:
      `GenerationSettings` (temperature/topP/maxTokens/frequencyPenalty/presencePenalty/advancedJson)
      was shown and accepted in the GUI for every Text-mode model, but 1min.AI's `GenerateTextAsync`
      accepts a `GenerationSettings` parameter and never reads it — those fields had silently no
      effect for that provider. No adapter's Image/Audio/Video request builder accepts
      `GenerationSettings` at all, so non-Text modes correctly have no capabilities either. The
      remaining three are not covered by a schema: **limits** — the existing
      `LibraryRules.MinTemperature`/`MaxTemperature`/etc. constants are global, not per-provider;
      no adapter documents a *different* numeric range from OpenAI's own documented bounds, so there
      is nothing to differentiate yet. **concurrency** — no adapter documents a hard parallel-submission
      ceiling; the existing per-connection cap already defaults conservatively to 1 and is
      user-adjustable up to 8, which is arguably already the safe behavior a schema would encode, but
      it isn't expressed as a capability schema. **instruction-channel** — `Model.SupportsSystemInstructions`
      is already a per-model capability flag, just a bare bool rather than a richer channel schema
      (unchanged from Milestone 2's own documented scope decision).
- [ ] Generate structured selectors, sliders, toggles, dimensions and voice controls from those
      schemas.
      The settings schema above is now wired into `Generate.razor`: each of the six settings controls
      only renders when `CurrentSettingsCapabilities` includes its flag, and a clear
      "provider does not support generation settings for this mode" notice replaces the panel entirely
      when none apply (closing a real capability-rejection gap, not just a schema-completeness one —
      previously a 1min.AI user could set a temperature that was silently discarded with no
      indication). Voice is now implemented for the one confirmed case: `IProviderAdapter
      .GenerateAudioAsync` gained an optional `voice` parameter (all five adapters updated; only
      `DeepInfraProviderAdapter` sends it, matching its confirmed `POST /v1/audio/speech` contract's
      optional `voice` field — `docs/developer/deepinfra-audio-video-contract.md`). New
      `LibraryRules.SupportsAudioVoiceSelection(providerType)` gates a plain text `Voice` input on
      `Generate.razor`, shown only for Audio-mode DeepInfra models. Deliberately not persisted through
      `GenerationDraft`/saved settings/history/**Use Again** in this pass — it lives only in the
      in-memory `GenerationJobSnapshot` for one submission, an explicit, documented scope cut (matching
      the first-frame-picker precedent in section 6) rather than the larger schema-migration-sized
      effort full persistence would need. No slider/toggle/dimension control exists for any other
      field — no adapter documents one.
- [x] Implement the bounded advanced JSON editor for manually entered or unknown models.
- [x] Enforce reserved keys, nesting/size limits, type validation, normalized-request conflict
      detection and sanitized preview for advanced JSON.
- [x] Preserve advanced settings through drafts, saved settings, generation history and **Use
      Again**.

Done when: every exposed provider control changes a documented request field, and unsupported or
unknown behavior is rejected clearly rather than guessed.

## 6. Complete source inputs and provider file transfer

- [x] Replace the three generic source-file fields with capability-defined named slots such as
      reference image, mask, first frame, last frame, source audio and source video.
      Replaced `SourceFileId`/`SecondarySourceFileId`/`TertiarySourceFileId` everywhere (`GenerationDraft`,
      `SavedGenerationSetting`, `GenerationRecord`, `GenerationJobSnapshot`, `IProviderAdapter.GenerateTextAsync`)
      with a single `IReadOnlyList<GenerationSourceSlot>` (role + file ID + order) and a new
      `GenerationInputSlotRole` enum (`ReferenceImage`, `Mask`, `FirstFrame`, `LastFrame`,
      `SourceAudio`, `SourceVideo`). `LibraryRules.GetInputSlotCapabilities(providerType, mode)` is
      the capability schema — deliberately a small switch, not a stored schema, since only two
      capabilities are actually confirmed by any adapter: Text mode's up-to-3 `ReferenceImage` slots
      (any provider — identical to the old 3-slot behavior, just represented as data) and DeepInfra
      video's optional `FirstFrame` slot (newly wired: `DeepInfraProviderAdapter.SubmitVideoGenerationAsync`
      now sends it as a `data:` URI via `IProviderAdapter`'s new `firstFrame` parameter — the one
      concrete capability beyond text that was actually confirmed and previously unwired, per
      `docs/developer/deepinfra-audio-video-contract.md`). `Mask`/`LastFrame`/`SourceAudio`/`SourceVideo`
      exist in the vocabulary for forward compatibility only — no adapter documents them, so none are
      assignable to any model today; adding a real one later is one more `GetInputSlotCapabilities`
      arm plus adapter wiring, not a schema rework, mirroring this session's `GenerationStatus` work.
      `Generate.razor`'s three reference-image pickers are unchanged UI-wise (still just three
      dropdowns); a dedicated first-frame picker was **not** added to the GUI in this pass — DeepInfra
      video's first-frame capability is reachable through the adapter and persistence layer but has no
      form control yet, an explicit scope cut given this item's size.
- [x] Persist slot role, order and immutable sent snapshots through drafts, saved settings, prompt
      improvement, history and **Use Again**.
      New normalized `generation_source_slots` table (schema v38, replacing three parallel FK columns
      per table) stores role/order/file ID for `generation_drafts`, `saved_generation_settings` and
      `generation_records`, plus a `snapshot_display_name/media_type/content_hash` triple per row.
      For `generation_records` specifically, that snapshot is captured once at write time (queued
      creation, then re-captured at actual finalize) — genuinely new "immutable sent snapshot"
      behavior; previously only a since-deleted source's identity was captured (in `Read*` at
      permanent-deletion time), never at submission. `FileIdentitySnapshot`'s own delete-time
      writer path was removed (the old `tombstone_source_*` columns are now dead — left in place
      unused, per this file's own additive-migration convention — the `file_id` column's
      `ON DELETE SET NULL` FK plus the already-captured snapshot now do that job together).
      `GenerationHistoryDetail.razor`'s source-slot display now reads
      `GenerationRecord.SourceSlotSnapshots` (survives file deletion) instead of the old fixed
      three-tombstone fields. Prompt improvement remains text-only and untouched — it never took
      source images as input before this change either, so there's nothing to persist there.
      **Use Again** (`Generate.razor`'s `ConsumeRouteDraftAsync`) carries the full slot list forward,
      still silently dropping a slot whose file is no longer active (unchanged behavior, just
      generalized from 3 fixed checks to the whole list via a new `FilterActiveSourceSlots` helper) —
      it does not yet show the surviving tombstone identity for a dropped slot or check the new
      model's capabilities before dropping (see the two explicitly-still-open items below).
- [x] Implement per-slot media-type, count, byte, dimension, duration and token validation using a
      documented provider formula or a clearly labelled estimate.
      Re-checked against the actual code rather than the conservative note that was here: media-type
      and byte-size validation already existed and are already enforced at the point a source slot's
      bytes are actually read for submission — `LibraryWorkspace.ReadImageFileAsync` (the method both
      Text's `ReferenceImage` slots and DeepInfra video's `FirstFrame` slot go through) rejects
      anything outside the real `IsImageMediaType` allowlist and anything over
      `MaximumInlineImageBytes`, plus a content-hash integrity check and `ImageSafetyInspector.Validate`
      — this isn't UI-only, it's the actual read path every submission uses. `ValidateSourceSlots`
      enforces role membership and per-role `MaxCount`/`Required`. No dimension/duration/token limit
      exists because neither confirmed capability (`ReferenceImage`, `FirstFrame`) is dimension- or
      duration-sensitive and no provider documents a formula for either — there is nothing to validate
      there today, not a gap.
- [x] Block known-invalid submissions and label partial or approximate validation accurately.
      Fixed a real gap: `LibraryRules.ValidateSourceSlots` was fully implemented and tested but never
      actually **called** anywhere in production code — the UI only avoided constructing an invalid
      slot assignment because its pickers happen to offer just the one always-valid `ReferenceImage`
      role today, not because anything enforced it. The same "UI discipline, not a guarantee" gap this
      session already closed for system instructions (section 2). Now called from
      `LibraryWorkspace.CreateQueuedGenerationRecordCoreAsync` — the single authoritative submission
      boundary every caller (normal submit, **Use Again**, **Retry Failed/Missing Results**) funnels
      through — so an invalid role or over-capacity slot list is rejected with a clear
      `LibraryValidationException` before a record is ever created, not just structurally avoided by
      today's UI. Three new `LibraryWorkspaceTests` cover rejection of an undeclared role, rejection of
      exceeding a role's `MaxCount`, and acceptance of a within-capability assignment. Approximate
      validation is already labelled: `Generate.razor`'s token count is shown as
      "~{0} tokens (rough estimate, not exact — SlopFactory has no provider tokenizer)", the only
      approximate value this app currently presents.
- [ ] Implement provider-issued signed upload destinations for adapters that require out-of-band
      upload, including safe host, redirect and credential handling.
      Still deferred — no adapter is confirmed to need one (every current image/first-frame input is
      sent inline as a base64 data URI); see "Future work" at the end of this file.
- [ ] Add generic/aliased transport filenames and reliability metadata only where an adapter sends a
      filename.
      Still deferred — no adapter is confirmed to need generic/aliased upload filenames
      (`milestone3.md`'s own note on this was never contradicted by later adapter work).
- [x] Keep aliases stable between prompt improvement and the final generation submission.
      Moot: prompt improvement is still text-only, so there is no alias to keep stable — satisfied by
      the absence of the precondition, not a deferral.
- [x] Add source/model-incompatibility and instruction-channel-mismatch review to **Use Again**.
      Source/model-incompatibility review is now implemented: `Generate.razor`'s
      `FilterSlotsForEffectiveModel` recomputes a reused slot list (from a saved setting or history
      record) against whichever model **Use Again** actually resolves to — the original record's
      model, or the `_models[0]` fallback when that model is recycled/missing, which can be a
      completely different provider or mode — dropping any role the effective model doesn't declare
      and trimming any role's slot count down to its `MaxCount`, exactly the two rules
      `LibraryRules.ValidateSourceSlots` enforces at actual submission time. When anything was dropped
      for capability (distinct from the pre-existing file-liveness drop), a new
      `SourceSlotsIncompatibleWithReplacementModel` notice tells the user, instead of the slot silently
      vanishing with no explanation. Instruction-channel-mismatch review remains out of scope, same
      reasoning as before: `SupportsSystemInstructions` is still a single bool with no channel concept
      to mismatch against (falls through to the existing generic model-unavailable-style warning, per
      `milestone2.md`'s own scoping note — not a regression here).

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

- [x] Implement atomic external media export with destination identity binding, flush and optional
      trustworthy read-back verification.
      This already existed going into this pass (`LibraryWorkspace.ExportCoreAsync`: unique temp
      sibling, copy+hash, source-hash comparison, atomic `File.Move`, then a second post-commit
      read-back hash verification with `VerificationFailed` on mismatch, reparse-point/symlink
      rejection on both parent and destination). This pass added durable journaling around it (below).
- [x] Implement the authenticated, versioned, device-local external-export cleanup journal.
      `IExportCleanupJournal` (Core) / `ExportCleanupJournalService` (Gui) — device-local JSON in
      `Preferences.Default`, each entry HMAC-SHA256-authenticated with a secret held in
      `SecureStorage.Default` under its own key namespace, versioned via a `Version` field in the
      persisted document.
- [x] Recover pending cleanup after crashes without deleting an object whose ownership and identity
      cannot be verified.
      `ExportCleanupJournalService.SweepAsync`, run fire-and-forget at startup
      (`MainLayout.razor.OnInitialized`). An entry whose HMAC fails to verify is left untouched and
      never acted on. Covered by `ExportCleanupJournalTests` (already-absent, confirmed-and-matching,
      target-changed, and tampered-entry cases) plus `LibraryWorkspace`-level fault-injection tests
      proving the existing atomic-write `finally` block self-heals the journal for a live failure at
      each of the three load-bearing boundaries.
- [ ] Handle document-provider renames, expired permissions, unavailable providers, target swaps,
      symlink/reparse substitution and already-absent targets.
      Local-file cases are fully handled: target-swap/reparse-substitution is detected and reported as
      `CleanupPending` without deleting anything (a real bug here — `SweepAsync` originally used
      `File.Exists`, which returns `false` for a directory now sitting at the target path, so a
      directory swap was silently misreported as "already absent" instead of `CleanupPending`; fixed
      to `Path.Exists`), and an already-absent target is recognized and cleared. Android SAF document-
      provider renames/expired-permission reauthorization is explicitly deferred: the local staging
      temp file for an Android export is journaled and swept normally, but the SAF `Uri` itself is
      always reported as `CleanupPending` rather than attempting reauthorization or deletion, since a
      real SAF permission-loss/regrant cycle needs on-device verification unavailable in this
      environment.
- [x] Implement versioned `.slopfactory.json` sidecars using deterministic encoding, formatting and
      property ordering.
      `ExportSidecarWriter.BuildJson` — `Utf8JsonWriter`, fixed literal property order (never
      reflection/dictionary-driven), UTF-8 without BOM, LF line endings enforced by explicit
      post-processing, `sidecarSchemaVersion` int field. Verified byte-identical output across two
      exports of the same file with the same options (`SidecarOutputIsByteIdenticalAcrossRepeatedExportsOfTheSameFile`).
- [x] Publish and bundle the sidecar JSON Schema and implement schema-version handling.
      `docs/developer/slopfactory-sidecar.schema.json` (draft 2020-12, `$id` matches
      `ExportSidecarWriter.SchemaId`, `additionalProperties: false`), referenced from
      `docs/developer/architecture.md`.
- [x] Implement privacy-minimal defaults plus disclosure previews and explicit opt-ins for prompts,
      sensitive metadata, filenames, internal identifiers, usage/cost, advanced settings and safety
      metadata.
      All toggles exist on `ExportSidecarOptions`, default to `false` (never preselected), and are
      wired into `FileDetails.razor`'s export UI. The missing disclosure **preview** is now
      implemented: `ILibraryWorkspace.BuildSidecarPreviewAsync(fileId, options)` builds the exact
      sidecar JSON a real export with those options would write, without exporting anything (a thin
      wrapper the Gui project needs since `ExportSidecarWriter` is internal to Infrastructure);
      `FileDetails.razor` gets a **Preview sidecar** button that shows it before commit. Verified
      byte-for-byte identical to what a real export actually writes
      (`BuildSidecarPreviewProducesTheSameContentARealExportWouldWriteWithoutExportingAnything`). Two
      opt-ins remain documented no-ops rather than real implementations: `IncludeSensitiveMetadata`
      (this app has no metadata-entries feature yet for a sidecar to read) and `IncludeSafetyMetadata`
      (blocked on Section 10, unimplemented — no persisted, hash-bound safety classification exists
      anywhere to read). Both emit an explicit `"...Unavailable": true` marker rather than silently
      omitting the toggle's effect.
- [x] Revalidate selected metadata, provenance and safety revisions immediately before sidecar
      commit.
      Re-examined rather than left as a documented gap: there genuinely is no revalidation window to
      close here. The sidecar writer reads the `FileRecord`/`GenerationRecord` fresh, synchronously,
      in the same in-process call that just committed the media export — there is no `await` boundary
      back to the UI between "the record this sidecar describes was read" and "the sidecar is
      written" where an external actor could mutate metadata, provenance or a (nonexistent) safety
      classification out from under it. "Revalidate before commit" is satisfied by there being no gap
      in which staleness could occur, not by an explicit extra check with nothing to guard against.
- [x] Track media and sidecar commits, read-back verification and cleanup independently.
      `ExportFileWithSidecarAsync` only attempts the sidecar after the media outcome is `Exported`;
      the sidecar is written through the same atomic-temp-then-journal-then-verify helper as media
      (`WriteBytesAtomicallyWithJournalAsync`) but as a fully separate operation with its own
      `SidecarExportResult`. A sidecar failure never touches the already-committed media result or
      file (`SidecarFailureDoesNotAffectAlreadyCommittedMediaResult`), and the same independence now
      holds per item in the bulk path too (see the next item).
- [x] Implement bulk-export continuation and per-item reporting when media, sidecar, verification or
      cleanup fails.
      Bulk media export continuation/per-item reporting already existed
      (`BuildBulkExportPreflightAsync`/`ExportFilesAsync`/`BulkExportResult`). Sidecar support is now
      threaded through: `ExportFilesAsync` takes an optional `ExportSidecarOptions?`, and
      `BulkExportResult` gained an index-aligned `SidecarItems` list (null at any position where a
      sidecar was never attempted — blocked, cancelled, no options supplied, or the media itself
      didn't export — never simply omitted, so a caller can always zip the two lists by position). A
      per-item sidecar failure never blocks or fails the rest of the batch, mirroring the single-file
      guarantee. Two new `LibraryWorkspaceTests` cover a mixed batch (one blocked/failed item with no
      sidecar attempted, one successful item with a written sidecar) and the no-`sidecarOptions`
      backward-compatible case.
      **A separate, more significant finding surfaced while scoping this**: no page anywhere in
      `Mellow.SlopFactory.Gui` actually calls `ExportFilesAsync`/`BuildBulkExportPreflightAsync` —
      bulk export has no UI entry point at all today, despite Milestone 1's own checklist claiming
      "Implement normal and bulk export preflight... verify no partial replacement... no silent
      renaming" as already complete. The domain/workspace layer is real and tested, but a user cannot
      currently trigger a bulk export from the app. Building that UI (multi-select destination
      picker, collision-choice review screen, progress reporting) is a real, substantial feature in
      its own right — out of this section's scope (which is about export/sidecar *hardening*, not
      building missing Milestone 1 UI from zero) — and is flagged here rather than silently left for
      someone to discover later.
- [ ] Add crash injection around every journal flush, object creation, identity binding, content
      flush, verification, atomic commit and journal-removal boundary.
      Implemented at the three boundaries that are actually load-bearing for correctness —
      `IExportFaultInjector.BeforeTempCreationAsync`/`BeforeAtomicCommitAsync`/
      `BeforeJournalRemovalAsync`, exercised by `ExportCleanupJournalTests`' `LiveFault*` tests — not
      literally every flush/verification point the bullet lists. Note also that because
      `ExportCoreAsync`'s cleanup runs in a `finally` block, an injected exception at any of these
      three points is a live-failure self-heal test, not a true crash simulation (a real process
      crash skips the `finally` entirely); genuine crash-recovery is instead proven by
      `SweepAsync`-level tests that stage journal/filesystem state directly, bypassing the `finally`.
      Re-checked whether a 4th injection point (between content flush and the journal's `Confirmed`
      transition) would add real coverage: it wouldn't — `SweepAsync`'s delete-vs-keep decision never
      branches on `PlannedTemporary` vs `Confirmed` state, only on whether the target path exists and
      matches the journaled identity, so a crash in that narrow window is already exercised by the
      existing `BeforeAtomicCommitAsync` test (same reachable outcome, just a different state label
      that turns out not to affect behavior). The three implemented boundaries already cover every
      behaviorally distinct outcome `SweepAsync` can produce.

Done when: no partial or unverified export is reported as successful, no unrelated object can be
deleted, and sensitive sidecar fields are never included without a fresh explicit review.

## 9. Complete cost and usage safeguards

- [x] Implement pre-generation estimates using only documented local pricing or a non-billable
      estimate endpoint that does not submit prompt/source content without separate consent.
      Implemented for the one confirmed source: OpenRouter's `/models` endpoint's `pricing` object
      (live-confirmed shape: `{"prompt":"0.00000045","completion":"0.0000032",...}`, decimal-string
      USD-per-token — fetched 2026-08-18 via `https://openrouter.ai/api/v1/models`). `ParseModelList`
      now parses this into a new `ProviderModelPricing` on `ProviderModelInfo` (null when a response
      has no `pricing` object, which is every other adapter's real `/models` response today — safe by
      absence, not by mislabeling). `Generate.razor` fetches it live via the same non-billable
      `ListModelsAsync` call once, on model selection (`OnModelSelectionChangedAsync`), only for
      Text-mode OpenRouter models — no prompt/source content is ever submitted to get it, so no
      separate consent applies. Never bundled/guessed data, matching `docs/developer/architecture.md`'s
      existing "won't fabricate per-token/per-image pricing data" rule.
- [x] Show deterministic values or reliable ranges with source and effective pricing date.
      New `LibraryRules.EstimateGenerationCost` computes a `GenerationCostEstimate`: a deterministic
      lower bound (`EstimatedTokenCount × PromptCostPerToken`) and, only when a `Max tokens` setting
      is actually configured, a reliable upper bound adding `MaxTokens × CompletionCostPerToken` —
      with no configured cap there is no honest upper bound, so the range collapses to the lower bound
      and `HasReliableUpperBound` is `false` rather than presenting a fabricated ceiling.
      `Generate.razor` shows the range (or "at least X" when unreliable) plus its source and the exact
      fetch timestamp, replacing "Cost unknown" for this one case. The remaining six items below are
      unimplemented: this closes only "show an estimate," not the threshold/acknowledgement/
      history/overrun-comparison/Unreliable-marking machinery those items separately require.
- [ ] Implement the first-use acknowledgement for a model/connection revision whose cost is unknown.
      plan.md:1588 specifically requires this to reference a "pricing-capability revision" and to
      state "no configured threshold can be enforced" — both concepts that only mean something once
      the pricing-estimate and threshold items below exist. Building the acknowledgement gate itself
      without them would mean inventing what it acknowledges.
- [ ] Implement device-wide thresholds keyed by exact currency/provider unit and per-connection
      overrides.
      No threshold concept exists anywhere in this app today (settings, storage or UI).
- [ ] Compare thresholds only between like units and use the reliable upper bound of a range.
      Depends on thresholds and estimate ranges, neither of which exist yet.
- [ ] Store the displayed estimate, source, range, effective date and applied threshold in history.
      Depends on the estimate/threshold items above.
- [ ] Compare provider-reported actual cost with the estimate and threshold and highlight material
      overruns.
      Depends on the estimate/threshold items above; actual cost itself is already captured and
      displayed (`GenerationRecord.ActualCost`, shown on `GenerationHistoryDetail.razor`) — there is
      simply nothing to compare it against yet.
- [ ] Mark a bundled pricing revision **Unreliable** after the specified repeated material overruns.
      Depends on a bundled pricing-revision concept, which doesn't exist yet.
- [x] Finish the cost-summary view with date, provider, connection, model and operation filters.
      Date range, provider, model and operation-type (`GenerationMode`) filters already existed on
      `CostSummary.razor`. **Connection** is deliberately not a filter dimension — confirmed still
      correct, not stale: `GenerationRecord` has no connection reference at all (only a snapshotted
      `ModelId`/`ModelLabel`/`ProviderType`), so there is nothing to filter by without a join through
      `Model` that could easily point at a deleted model. Fixed a real gap while re-verifying this
      item: `GetGenerationHistoryAsync` (and therefore the cost summary) only ever queried
      `state=0` (Active) — recycled generation records were silently excluded from cost totals
      entirely, even though plan.md:1615 explicitly requires them included by default ("recycling
      does not undo incurred usage"), with only an opt-out filter to exclude them. Added an
      `includeRecycled` parameter (default `false`, preserving `GenerationHistory.razor`'s existing
      active-only semantics) and wired `CostSummary.razor` to pass `true`, plus a new **Exclude
      recycled operations** checkbox and a matching `CostSummaryCalculator.ApplyFilters` parameter.
      Two new tests cover the workspace-level query and the calculator-level filter.
- [x] Correct the current **Cost unknown** notice wherever an adapter reports real cost.
      Fixed a real, verified gap: `Generate.razor` showed "Cost unknown" unconditionally for every
      provider and mode, but `OpenRouterProviderAdapter.PollVideoGenerationAsync` genuinely does parse
      and report a real `usage.cost` for OpenRouter video generations (confirmed — the only
      adapter/mode combination that populates `ActualCost` today; no other adapter or mode does). The
      notice now conditionally shows a different, accurate message
      ("...this provider reports the actual cost once generation completes...") specifically for that
      one case via a new `SelectedModelReportsActualCostAfterGeneration` check, and the ordinary
      "Cost unknown" notice everywhere else, including this section's own future estimate wording.
- [x] Export opted-in run/per-output/prompt-improvement usage and cost accurately in sidecars,
      including nonterminal `reported-so-far` state.
      Already implemented by section 8's `ExportSidecarWriter`: `IncludeUsageAndCost` exports
      `actualCost`/`actualCostCurrency`/`promptTokens`/`completionTokens` at the run level. Re-checked
      the two clauses this item's own wording implies might be missing: **per-output** cost is
      deliberately never divided among individual sidecars (plan.md's own "never divides a run total
      among output sidecars" rule, already documented in section 8) — correct, not a gap.
      **Prompt-improvement** cost has no sidecar to appear in at all, since prompt improvement
      produces no exportable file — moot, same as section 6's aliases item. **Nonterminal
      `reported-so-far`** state is unreachable today: no adapter or app mechanism ever reports a
      partial/incremental cost mid-run (cost is only ever known once a job fully terminates), and a
      sidecar is only ever written for an already-committed, already-exported file — there is no
      nonterminal sidecar scenario for this app's design to produce.

Done when: cost UI never presents an estimate as actual, never compares unlike units and never
claims that a local threshold is a provider-account spending limit.

## 10. Complete provider safety behavior

- [ ] Persist normalized provider safety classifications and their immutable association with the
      exact content hash that was classified.
      Genuinely 0% built: no persisted, hash-bound safety-classification value exists anywhere in
      this app (confirmed by every other section that touches safety, e.g. sections 8/9's sidecar
      `IncludeSafetyMetadata` no-op). This is the foundational data model the five items below all
      depend on — not attempted here since building it now would mean inventing a classification
      schema (values, revisions, storage) with no confirmed provider contract driving any of its
      states except the one narrow signal item 4 below actually uses.
- [ ] Implement concealment, session reveal, persistent per-file override and external-open
      reauthorization.
      Depends entirely on the persisted classification above; nothing to conceal/reveal without it.
- [ ] Share applicable classification events across duplicate content without leaking unrelated
      file metadata.
      Depends on the persisted classification above.
- [x] Give content-filtered multi-result children stable per-child identities rather than only an
      aggregate blocked count.
      Implemented for the one confirmed signal this app actually has: OpenAI-compatible
      `finish_reason: content_filter`. `TextGenerationResult` gained an ordered
      `Candidates` list (`TextGenerationCandidate(SafetyBlocked, Text)`, response order,
      built by `OpenAiCompatibleProtocol.ParseChatCompletionResult` alongside the pre-existing
      aggregate `SafetyBlockedCount`) so a blocked candidate's position is known, not just counted.
      `RecordTextGenerationResultCoreAsync` now synthesizes a `GenerationResultEntry` per candidate
      when the adapter provided one — `GenerationResultStatus.SafetyBlocked` (new value) for a
      blocked position with no file, `Committed` for a successful one — mirroring the per-position
      entry pattern `RecordImageGenerationResultCoreAsync` already established for media. Deliberately
      excluded from **Retry Failed/Missing Results Only** (only counts `Failed`), since retrying an
      identical request would predictably be blocked again. `GenerationHistoryDetail.razor` shows a
      distinct "Blocked by provider safety policy" label per position. Adapters that don't populate
      `Candidates` (everything except the shared OpenAI-compatible protocol) keep the pre-existing
      aggregate-only behavior unchanged — purely additive. Six new tests cover candidate ordering
      (including the blocked entry appearing first) and end-to-end per-position persistence.
- [ ] Implement **Provider Blocked After Delivery** when a provider exposes a documented late
      reclassification signal.
      No adapter documents one. Depends on the persisted classification above regardless.
- [ ] Map OpenRouter, DeepInfra and 1min.AI safety signals only after each contract is confirmed.
      Partially and narrowly true already, confirmed while implementing the item above rather than
      newly built: `OpenRouterProviderAdapter`, `DeepInfraProviderAdapter` and
      `GenericOpenAiCompatibleProviderAdapter`'s text generation all call the same
      `OpenAiCompatibleProtocol.ParseChatCompletionResult`, so they already inherit
      `content_filter` detection (and now per-candidate identity) automatically — not something
      requiring separate per-provider mapping work, since they share the exact response shape. Image/
      audio/video moderation signals for OpenRouter/DeepInfra remain unconfirmed by any fetched
      documentation, and 1min.AI (native, non-OpenAI-compatible protocol) has no confirmed safety
      signal at all — its own contract doc notes the docs site couldn't be fetched. Left unchecked
      since the full three-provider, every-modality scope isn't met, but the text-mode inheritance for
      3 of the 4 OpenAI-compatible adapters is real and now verified, not assumed.
- [ ] Reactivate a classification after exact-byte restoration, but never transfer it to differing
      replacement bytes.
      Depends on the persisted classification above.
- [ ] Apply safety-aware export confirmation and sidecar disclosure rules.
      Depends on the persisted classification above; sections 8/9 already wired `IncludeSafetyMetadata`
      as a documented no-op pending exactly this.

Done when: safety state follows the classified bytes, remains distinct from diagnostics and cannot
silently migrate to unrelated or replaced content.

## 11. Finish draft, history and media resilience

- [ ] Preserve recycled or missing source and destination references in open drafts as stable
      unavailable references instead of clearing them.
      Genuinely needs the larger tab-state-model redesign already identified in earlier sessions
      (milestone3.md/milestone4.md) — a real "unavailable reference" concept with restore/replace/
      remove actions, not a bounded fix. Fixed a narrower, real bug found while re-verifying this:
      `Generate.razor`'s draft-resume path carried a recycled/deleted **destination folder** ID
      straight into the form with *no* handling at all — worse than source images, which at least get
      explicit filtering (`FilterActiveSourceSlots`). A stale ID not present in `_activeFolders`
      left the actual bound `<select>` value to the browser's own default behavior (silently landing
      on an arbitrary different folder) rather than a deliberate, visible choice. Now falls back to
      the library's root folder with a distinct notice, mirroring the model-unavailable pattern
      already used for a recycled/deleted model. This closes a real gap but does not itself satisfy
      this item — a reset-to-root-with-notice is not a "stable unavailable reference," which would
      need to remember and offer to restore/replace the original folder, not just land somewhere safe.
- [ ] Preview affected open tabs before recycle or permanent deletion.
      No preview exists; depends on the same tab-state model as the item above.
- [ ] Provide restore, replace and remove actions; convert permanently deleted references into an
      explicit non-restorable state.
      Depends on the stable-unavailable-reference model above existing first.
- [ ] Revalidate open drafts and saved settings after managed-content replacement.
      Not implemented; no revalidation pass runs after `CommitManagedContentReplacementAsync`.
- [ ] Mark incompatible replacements **Needs Review** and compatible replacements **Content
      Replaced**.
      Neither status exists in the UI; depends on the revalidation pass above.
- [ ] Prevent submission until every required unavailable or incompatible reference is resolved.
      Not implemented — today an unavailable source/destination is silently substituted (or, before
      this pass's fix above, left entirely unhandled for the destination folder) rather than blocking
      submission.
- [ ] Implement **Reacquire Permanently Deleted Output** after deciding and documenting what durable
      remote identifier may be retained and for how long.
      Re-verified rather than assumed stale: the per-library `async_remote_jobs` registry row is still
      deleted immediately once a generation commits successfully (confirmed — every
      `DeleteAsyncRemoteJobAsync` call site in `GenerationQueueService` fires right after a successful
      commit), so there remains no durable provider job ID or result URL to reacquire from once a
      committed output file is later permanently deleted. Building this needs a real, deliberate
      design decision (retain a provider identifier indefinitely post-commit, with its own privacy/
      staleness tradeoffs, purely to support a rarely-used recovery path) that this pass didn't make
      unilaterally, matching the same reasoning already recorded for this item in milestone4.md.
- [ ] Preserve the old tombstone and create a new file identity for reacquired bytes.
      Depends on Reacquire existing first.
- [ ] Warn and record **Provider Output Changed** when reacquired bytes do not match the tombstone.
      Depends on Reacquire existing first.
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
      Traced the actual failure path rather than re-stating the existing note. If `StageAsync` itself
      throws (e.g. the *local* staging volume, not the destination library, is out of space), it
      does so from inside `ExecuteVideoGenerationAsync`'s destination-unavailable `catch` block, with
      no nested handling — the exception propagates to `ExecuteAsync`'s outer safety-net catch
      (`LocalFailureOutcome`, added specifically to prevent an unobserved-task-exception data loss),
      which marks the generation `Failed`. This is not a silent crash or true data loss for the
      confirmed-loop case, but it is a documented real correctness edge: if the loop is staging
      multiple positions and fails partway through (position 0 succeeds, position 1 hits
      insufficient storage), the positions that *did* stage successfully remain correctly tagged to
      the generation record and are still picked up by `ReconcileStagedGenerationGroupAsync` on the
      next reconciliation pass regardless of the record's current status (self-correcting, not
      permanently lost) — but the record is transiently shown `Failed` instead of `AwaitingLibrary`
      until that next pass, and the position that failed to stage has no bytes anywhere and is
      genuinely gone. Not fixed here: reliably distinguishing "insufficient storage" from any other
      `IOException` is platform-specific (Windows vs. Android report disk-full differently) and this
      session's policy is to not guess at unverified platform behavior; a real fix also needs a
      distinct retryable state and UI action, not just a smarter catch clause.
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
- [x] Add fault-injection tests for volume removal during prepare, commit, post-commit cleanup and
      application restart.
      Commit-time volume removal (staging occurs) and the reconciliation half (a second "session"
      against the same staging registry after the volume returns, exercising the application-restart
      case) are covered by a real staged→reconciled round trip. Prepare and post-commit cleanup were
      previously left uncovered because the filesystem-deletion fault technique can't safely reach
      either one (both share the workspace's exclusively-locked SQLite connection, which Windows
      would refuse to let a test corrupt without breaking unrelated operations). Closed by a new
      dedicated seam mirroring section 8's `IExportFaultInjector`:
      `IGenerationFaultInjector`/`NullGenerationFaultInjector`
      (`src/Mellow.SlopFactory.Gui/Services/IGenerationFaultInjector.cs`), an optional constructor
      dependency on `GenerationQueueService` with two hooks —
      `BeforePrepareReadAsync` (called immediately before `GetActiveModelsAsync`/
      `GetActiveConnectionsAsync`) and `BeforePostCommitCleanupAsync` (called immediately before each
      post-commit async-job-registry link/delete attempt, still inside the same per-entry
      try/catch that already swallows a real cleanup failure). Two new
      `GenerationQueueServiceTests`: `AStorageFaultWhilePreparingFailsTheGenerationWithoutLosingTheQueuedRecord`
      (a fault at `BeforePrepareReadAsync` finalizes the already-durable `Queued` record to `Failed`
      rather than losing it) and `APostCommitCleanupFaultNeverAffectsTheAlreadyCommittedVideoResult`
      (a fault at `BeforePostCommitCleanupAsync` proves the already-committed video record still
      reports `Completed` and the stale async-job registry row is left for a later sweep, not that
      any new production behavior was added — the isolation already existed, this seam just proves
      it).

Done when: a missing volume cannot lose a completed provider result or create duplicate committed
outputs, and recovery exposes no sensitive content outside its library.

## 13. Complete UI acceptance automation

- [ ] Add rendered fixed-viewport coverage for primary phone, tablet and desktop layouts.
      Re-verified this isn't just "no test library chosen yet" but a real capability gap even if one
      were: `docs/developer/testing.md` already documents "no bUnit harness in this repo" as a
      settled, repeated decision across several existing source-level-only tests, and checked whether
      bUnit specifically would actually satisfy this item — it wouldn't. bUnit renders a component
      tree headlessly with no CSS/layout engine at all, so it has no concept of a viewport size or
      computed layout to assert against; it cannot tell whether a control is visually clipped at
      375px width, only whether a component's C# logic/DOM structure is correct. Genuinely testing
      "fixed viewport" rendering needs a real browser engine (e.g. Playwright driving the app's
      actual BlazorWebView content) or on-device MAUI UI automation (e.g. Appium) — either is a real
      new testing-technology adoption with its own CI/headless-browser-or-emulator availability
      requirements, not a bounded addition to the existing xUnit suite. Left as a deliberate,
      already-documented scope decision rather than re-litigated here.
- [ ] Add keyboard-driven coverage for focus visibility, activation, modal focus capture and focus
      restoration.
      Same root cause as the item above, with an additional confirmed reason bUnit specifically
      wouldn't work here even as a partial answer: this app's focus-restoration behavior
      (`ui.js`'s `MutationObserver`-based helper, per `docs/developer/architecture.md`) is real
      JavaScript executing against a real DOM — bUnit's JSInterop is stubbed/mocked, not a real
      browser DOM, so it cannot exercise that logic at all, only assert that Blazor *called* into
      JSInterop with expected arguments. The current source-level tests already do exactly that
      (asserting `role="dialog"` markup is present so the JS helper *would* fire); closing this gap
      needs the same real-browser/device tooling decision as the item above.
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

- [x] Add process-kill/crash tests for queue, draft, export, session and staging recovery.
      Queue (`GenerationQueueServiceTests`'s restart-recovery tests, section 3), staging (the
      staged→reconciled round trip, section 12) and export (`ExportCleanupJournalTests`'s
      `LiveFaultBeforeTempCreationSelfHealsJournalAndLeavesNoOrphan`,
      `LiveFaultBeforeAtomicCommitSelfHealsJournalAndLeavesNoOrphan` and
      `LiveFaultBeforeJournalRemovalStillCommitsMediaButSelfHealsJournal`, section 8) all inject a
      fault at a specific commit boundary and confirm a fresh harness recovers correctly. Draft
      crash recovery is now covered too:
      `ADraftsLatestSavedStateSurvivesAnUncleanProcessExit`
      (`LibraryWorkspaceTests.cs`) creates a draft, calls `ReplaceDraftStateAsync`, disposes the
      workspace without any graceful-shutdown step, reopens it via a fresh
      `LibraryWorkspaceFactory.OpenAsync` and asserts every field survived — proving
      `ReplaceDraftStateAsync`'s per-call SQLite transaction is what makes drafts durable, not a
      flush-on-exit path that a real crash would skip.
      "Session" recovery (plan.md's "Session Recovery" section: reopening the last active library
      and restoring navigation/search state on launch) is a different durability shape and is
      deliberately not given its own crash-injection test: `RecentLibraryService` and the
      remembered-navigation state persist via a single synchronous `Preferences.Default.Set` call
      with no multi-step commit sequence of our own to interrupt — there is no intermediate state a
      process kill could catch between "not yet written" and "written," unlike queue/draft/export,
      which each have a real multi-step commit (or an explicit journal) that a crash can land
      inside. A dedicated test here would only be re-verifying the MAUI `Preferences` platform
      implementation's own atomicity, not this app's code.
- [x] Add volume-disconnect-mid-commit and dependency-recycled queue-pause tests.
      Volume-disconnect-mid-commit:
      `AVideoResultIsStagedForRecoveryWhenItsLibraryBecomesUnavailableDuringTheFinalCommit` and
      `StagedVideoResultsAreAutomaticallyReconciledOnceTheLibraryVolumeReturns` (section 12).
      Dependency-recycled queue-pause: extensive pre-existing coverage in
      `GenerationQueueServiceTests` (`ADependencyRecycledJobDoesNotBlockALaterQueuedJobOnTheSameConnectionFromRunning`
      and others asserting `GenerationJobPhase.DependencyRecycled`).
- [ ] Add second-instance launch-forwarding tests.
      Re-confirmed still blocked: `src/Mellow.SlopFactory.Gui/Platforms/Windows/App.xaml.cs` uses
      real WinUI `AppInstance.FindOrRegisterForKey`/`GetActivatedEventArgs` redirection, checked and
      blocked on synchronously in the `App()` constructor before `InitializeComponent` runs. There
      is no way to spin up a second real `AppInstance` registration against the same key from this
      xunit test harness (it requires an actual packaged/registered app identity and a second OS
      process), so this remains a manual test (section 15), not a gap that can be closed here.
- [ ] Add Android execution-suspension and notification permission tests where automation permits.
      Partial: execution suspension is covered by
      `BackgroundExecutionSuspensionCancelsRunningJobsAndFinalizesTheirRecordsDistinctlyFromAProviderFailure`
      (section 4). Runtime notification-permission prompting is not automatable from this test
      harness (it requires a real OS permission dialog) and remains a manual test (section 15).
- [x] Run the complete unit/integration suite with zero failures.
      677 passed, 1 skipped (the opt-in `LiveProviderSmokeTests` live-credential test), 0 failed as
      of this pass (`dotnet test tests/Mellow.SlopFactory.Tests`).
- [x] Produce clean Windows Debug and Release builds.
      `dotnet build src/Mellow.SlopFactory.Gui -f net10.0-windows10.0.22621.0 -c Debug` and `-c
      Release` both succeed with 0 warnings/0 errors as of this pass. Unsigned/development-signed —
      production Store/sideload signing is section 15's manual gate.
- [x] Produce clean Android Debug and Release builds.
      `dotnet build src/Mellow.SlopFactory.Gui -f net10.0-android -c Debug` and `-c Release` both
      succeed with 0 warnings/0 errors as of this pass. Unsigned/debug-signed — production signing
      (AAB/APK) is section 15's manual gate.
- [x] Verify diagnostic redaction, rolling retention, crash records and exported diagnostics.
      Redaction: audited every call site that ever constructs a `DiagnosticLogEntry` — there are
      exactly two, both in `GenerationQueueService.cs`. `CreateQueuedRecordAsync`'s catch clause
      logs `exception.Message` from `IOException`/`SlopFactoryException`/`ObjectDisposedException`/
      `Microsoft.Data.Sqlite.SqliteException` only — all local storage/validation failures, never a
      provider response body. `RunJobAsync`'s post-job log (line ~1229) logs
      `outcome.LocalErrorMessage`, which is `null` on every path where a provider actually ran
      (provider errors are captured separately into the `GenerationRecord`'s own `ErrorMessage`
      field via `RecordTextGenerationResultAsync`/etc., never routed to the device-wide diagnostics
      log) and is otherwise either a fixed local string ("Background execution was suspended...") or
      `exception.Message` from the same local-only exception set as the first call site. Provider
      exceptions (`ProviderAdapterException`/`HttpRequestException`, which could carry a raw
      response body or an echoed prompt) are never logged to diagnostics at either site — confirmed
      by design, not by omission. `DiagnosticsLogger` itself performs no redaction (`Log` takes a
      pre-built entry); that discipline lives entirely, and correctly, at these two call sites.
      Rolling retention and crash records: already substantially covered by
      `DiagnosticsLoggerTests.cs` (`EntriesOlderThan30DaysAreRemovedOnTheNextLogCall`,
      `OldestEntriesAreRemovedFirstOnceTheRollingCapIsExceeded`,
      `FirstSessionStartLeavesNoCrashMarkAndCreatesASessionMarker`,
      `AMissingSessionEndMarkerIsDetectedAsAnUncleanShutdownOnTheNextStart`,
      `AGracefulSessionEndPreventsTheNextStartFromDetectingACrash`, and others).
      Exported diagnostics: `Diagnostics.razor`'s `ExportAsync` exists and does nothing beyond
      `JsonSerializer.Serialize` of the same `DiagnosticLogEntry` list already covered by
      `ReadingBackAllFieldsRoundTripsExactly` (i.e. no separate redaction or transformation logic of
      its own to test), then hands the bytes to `IPlatformFileActionService.ExportRawBytesAsync`.
      Exercising that call end-to-end needs a rendered Razor component — this repo has no bUnit
      harness (`docs/developer/testing.md`), the same rendered-UI blocker documented in section 13 —
      so it isn't independently automated, but its correctness risk is low since it has no logic
      beyond re-serializing already-redaction-audited, already-round-trip-tested data.
- [ ] Verify every user-visible string remains resource-backed and layouts tolerate longer and RTL
      test strings.
      Resource-backed: confirmed genuinely untested, not stale — `UiAssetTests.cs` and a repo-wide
      grep for any hard-coded-string-rejection pattern turned up nothing that asserts `.razor`
      markup never contains a literal user-visible string outside `Strings[...]`. No such automated
      check exists. RTL/layout tolerance: blocked by the same rendered-UI/CSS-layout-engine gap as
      section 13 (bUnit cannot lay out a page; this repo deliberately has no bUnit harness per
      `docs/developer/testing.md`) — genuinely needs Playwright/Appium-class tooling not available
      in this environment. Both remain manual checks (section 15).

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
