# Milestone 3 completion checklist

This checklist breaks the Milestone 3 scope in `plan.md` ("Provider and media breadth": 1min.AI,
OpenRouter and DeepInfra adapters; audio and video generation; provider-specific capabilities;
asynchronous remote jobs; file-transfer variations; usage and cost handling; rate-limit behavior;
and multi-result workflows) into independently completable units, following the same convention as
`milestone1.md` and `milestone2.md`. An item is complete only when its stated automated
verification passes; platform-labelled items also require the applicable device check in
[manual_tests.md](manual_tests.md).

Milestones 1 and 2 remain partially open (see `milestone1.md`/`milestone2.md`); per `plan.md`,
milestone boundaries are implementation/validation phases rather than strict release gates, so
Milestone 3 work can proceed in parallel. All milestones must be complete before the first public
release.

Several groups below were explicitly reviewed and deferred into this milestone while closing out
Milestone 2, rather than discovered fresh here: the async-job group (`milestone2.md` lines
592-621), the three new adapters (`milestone2.md` line 263), signed adapter versioning (line 265),
the fake HTTP provider expansion (lines 245-246, 947-949), unresolved-cleanup/reconciliation
gating on connection changes (lines 97-110), the local cost-summary view (lines 909-919), and the
remaining Provider Safety Responses machinery — concealment/reveal, per-file override
preferences, cross-duplicate shared classification, and **Provider Blocked After Delivery** late
reclassification (lines 686-706). Each is folded into its natural section below instead of being
repeated as a separate list.

## Testing foundation

Build this before wiring up the new adapters — they are the first real consumers of streaming,
async-job and rate-limit behavior, and testing them against a stub that only knows
success/auth-failure/unreachable-host (`FakeHttpMessageHandler`'s current 19-line scope) would
mean writing adapter-specific fakes three times over instead of once.

- [x] Expand `FakeHttpMessageHandler` into a shared, reusable test fixture: `Sequenced(...)` for
      submit-then-poll/redirect-chain scenarios (throws if a test's adapter code calls more times
      than responses were configured, catching a wrong-call-count bug rather than hanging), plus
      `JsonResponse`/`RateLimited`/`Redirect`/`BinaryResponse`/`StreamingResponse` canned-response
      builders. Covered by `FakeHttpMessageHandlerTests.cs`. Moderation/content-filter responses
      needed no new fixture support — that's provider JSON shape, already exercised via the existing
      `finish_reason: content_filter` tests — and a representative *provider-error-shape* sweep
      (beyond the 401/404/429/5xx cases already covered per-adapter) remains open below.
- [ ] Add provider contract fixtures (versioned sanitized request/response JSON) for OpenRouter and
      DeepInfra so adapter behavior is pinned against real shapes rather than hand-written
      approximations, and so future provider API changes are reviewed deliberately against a diff.
      (1min.AI is excluded — see the Provider adapters section below.) The adapters shipped this
      pass are tested with inline JSON literals in `NewProviderAdapterTests.cs`, not a separate
      versioned fixture file; promoting those to real fixture files is still open.
- [ ] Add a live-provider manual test harness (explicitly enabled, skipped when credentials are
      absent, cost-budget-bounded) for the OpenRouter and DeepInfra adapters, mirroring the existing
      OpenAI/generic live-test conventions. Doubly important here since OpenRouter's audio
      transcript field name and DeepInfra's dual OpenAI-compatible/native base-URL split were not
      fully confirmed by research — a live call is the only way to close that gap.

## Asynchronous remote jobs and reconciliation

This is the load-bearing infrastructure the rest of the milestone depends on: 1min.AI's AI Feature
API polling, OpenRouter's asynchronous video generation, and any provider-hosted result URL all
need a real submit-then-poll model that does not exist today (`GenerationJobPhase` in
`GenerationQueueService.cs` currently has only `Queued`/`Running`, and `GenerationStatus` in
`LibraryModels.cs` has only `Completed`/`Failed`/`PartiallyCompleted`).

- [ ] Extend the generation status model to the full normalized set `plan.md` defines: `Queued`,
      `Paused`, `Preparing`, `Uploading`, `Submitting`, `Submission Outcome Unknown`, `Processing`,
      `Monitoring Paused`, `Downloading Results`, `Awaiting Library`, `Cancellation Requested`,
      `Completed`, `Partially Completed`, `Completed Before Cancellation`, `Cancelled Before
      Submission`, `Cancelled`, `Cancelled with Results` and `Failed`, plus the `Paused` hold-reason
      variants (Connection Lost, restart-confirmation-required, metered-network, dependency
      recycled). Deliberately not attempted yet: `GenerationJobPhase`/`GenerationStatus` still only
      cover Queued/Running and Completed/Failed/PartiallyCompleted — video generation today reaches
      a real terminal outcome (see below) without needing the fuller state vocabulary, and adding
      unreachable states before something produces them would be exactly the speculative
      infrastructure this project avoids.
- [x] Add a persisted async-job record (schema v30 `async_remote_jobs` table,
      `AsyncRemoteJobRecord`/`AsyncRemoteJobPhase`: provider job ID, connection ID, draft ID,
      submitted/last-polled timestamps, adapter-declared monitoring deadline) distinct from the
      immutable request snapshot, with full `ILibraryWorkspace` CRUD
      (`CreateAsyncRemoteJobAsync`/`GetPendingAsyncRemoteJobsAsync`/
      `GetAsyncRemoteJobsForConnectionAsync`/`UpdateAsyncRemoteJobPhaseAsync`/
      `DeleteAsyncRemoteJobAsync`). `GenerationQueueService.ExecuteVideoGenerationAsync` creates a
      row on submit, updates its phase on every poll, and removes it after the generation commits.
      **Resuming polling on library open and app foreground is not implemented** — the registry is
      populated and queryable (proven by
      `VideoGenerationSubmitsPersistsAndPollsUntilCompletedThenCleansUpTheAsyncJobRegistryEntry`
      asserting the row exists mid-poll), but nothing reads it back on startup yet; an in-flight
      video job would be abandoned in memory if the app closes mid-poll, leaving an orphaned
      registry row. That startup-resume wiring remains open.
- [ ] Add **Monitoring Paused**: when an async job exceeds its adapter-defined maximum monitoring
      lifetime while the provider still reports it running, stop automatic polling and expose
      **Check Now**/**Resume Monitoring** rather than treating it as failed or cancelled.
- [ ] Add idempotency-key generation and persistence, scoped only to adapters with documented
      idempotency support, generated and durably stored before any bytes are sent; separate runs,
      multi-result children, prompt-improvement attempts and **Use Again** each receive distinct
      keys, and the exact key is dropped after terminal resolution (retaining only a non-reusable
      fingerprint for diagnostics).
- [ ] Add **Submission Outcome Unknown**: when transmission began but SlopFactory cannot confirm
      provider acceptance, record this indeterminate (not locally active) state instead of ordinary
      `Failed`, releasing queue slots/dependency pins and retaining only the minimum provider
      request ID/idempotency context needed for a documented reconciliation operation.
- [ ] Add **Attempt Reconciliation**, exposed on the affected history/activity record, and the
      **Abandon Recovery and Apply Changes** path that removes actionable request IDs/idempotency
      context while retaining sanitized non-actionable history.
- [x] Add the unresolved-async-job gate before a base URL, credential header name or auth prefix
      change takes effect (`ConnectionEdit.razor`'s `AuthStructureChanged()`), scoped to a reduced,
      honest 2-way choice: **Stop Tracking and Apply Changes** (deletes the connection's unresolved
      registry rows with an explicit warning that provider processing/charges are unaffected) or
      **Cancel**. **Retry Cleanup** and **Attempt Reconciliation** are deliberately excluded — both
      would need a working reconciliation/status-recheck operation for an already-submitted job,
      which does not exist yet (see the still-open items above); offering a button with no real
      operation behind it would be worse than not offering it. Provider type is not gated: changing
      it is already blocked by the pre-existing "no active dependent models" rule whenever an async
      job could exist, since a model must stay active for its connection to have submitted
      anything. This logic has no dedicated automated test (this codebase does not unit-test Razor
      pages — no bUnit or similar is referenced — consistent with how the rest of
      `ConnectionEdit.razor`'s save/credential-decision flow is already only manually verified); the
      data it depends on (`GetAsyncRemoteJobsForConnectionAsync`, `DeleteAsyncRemoteJobAsync`,
      `AsyncRemoteJobPhase`) is fully covered by `AsyncRemoteJobTests.cs`.
- [ ] Add cancellation handling for every defined stage (before submission, mid-upload, provider
      already accepted) distinct from today's single `CancellationTokenSource`-only path.
- [ ] Add offline/metered-network queue handling: **Paused — Connection Lost**, manual **Resume
      Queue**/**Resume All for This Connection** with per-job revalidation, and the device-wide
      metered-network transfer setting (**Allow**/**Ask**/**Wi-Fi/Unmetered Only**).
- [ ] Add a **Refresh Provider Status** / **Import Missing Results** action for late-recovered
      results (job succeeded but result download failed, or a result becomes available after
      `Monitoring Paused`), retaining remote job details and retrying while the provider result
      remains available.
- [ ] Add the required temporary asset association lifecycle for an in-flight async job (kept until
      the job reaches a terminal or explicitly discarded state) and its dependency-pin release.

## Provider adapters

- [ ] Add `ProviderType.OneMinAi` and its adapter: native unified chat API for text, and the AI
      Feature API with feature-specific request parameters for image, audio and video, including
      long-running feature requests through the async-job infrastructure above.

  **Deliberately deferred, not attempted this pass**: dedicated research (WebSearch/WebFetch against
  1min.AI's own docs and third-party sources) could confirm only the base URL (`https://api.1min.ai`)
  and that authentication uses a bare `API-KEY` header rather than `Authorization: Bearer` — a real
  divergence `LibraryRules`'s existing per-connection `CredentialHeaderName`/`AuthPrefix` fields
  already accommodate without adapter code changes, so nothing is blocked there. Everything else is
  unverified: `docs.1min.ai` could not be fetched by research tooling at all, no confirmed
  model-listing (or any other non-billable) endpoint exists to build **Test Connection** against
  (`/api/chat-with-ai` is itself a paid generation call, and `plan.md` explicitly forbids testing
  with "a paid generation request"), and the "AI Feature API"'s request/response shape for image,
  audio and video, plus its async-polling job-ID field and status values, are completely
  unconfirmed. Shipping this now would mean fabricating a wire format rather than following one.
  Revisit once the user (or a future session) can access `docs.1min.ai` directly or test against a
  live account/API key.
- [x] Add `ProviderType.OpenRouter` and its adapter (`OpenRouterProviderAdapter.cs`): reuses
      `OpenAiCompatibleProtocol` for connection test/model listing/text generation against its
      OpenAI-compatible base URL, and implements OpenRouter's own modality-specific endpoints for
      image (`POST {base}/images`, same `data[].b64_json` response shape as OpenAI — reuses
      `ParseImageGenerationBytes` directly), audio (`POST {base}/audio/speech`, one request per
      requested result since TTS has no `n` parameter, raw binary response body), and asynchronous
      video (`POST {base}/videos` submit → `GET {base}/videos/{id}` poll → authenticated download of
      each `unsigned_urls[]` entry once `status: "completed"`; `failed`/`cancelled`/`expired` all
      surface as `AsyncGenerationPollOutcome.Failed` rather than polling forever). A new
      `OpenAiCompatibleProtocol.SendForBytesAsync` reads raw bytes instead of a decoded string,
      needed because audio/video responses are binary and `ReadAsStringAsync` would corrupt them.
      13 tests in `NewProviderAdapterTests.cs` cover image/audio/video submit/poll/download,
      video failure/cancelled/expired handling, missing-job-ID validation, and rate-limit retry
      during polling. The audio transcript field name and the exact video submit parameter set
      beyond `model`/`prompt` (duration, resolution, aspect ratio, etc.) were not fully confirmed by
      research; a live test call remains the way to close that gap (tracked in Testing foundation
      above).
- [x] Add `ProviderType.DeepInfra` and its adapter (`DeepInfraProviderAdapter.cs`): its
      OpenAI-compatible surface (confirmed base `https://api.deepinfra.com/v1/openai`) uses the
      exact same relative paths and request/response shapes as OpenAI for chat, model listing and
      image generation, so this adapter reuses `OpenAiCompatibleProtocol` identically to
      `OpenAiProviderAdapter` for those three operations — no new request/response code needed.
      Audio and video generation deliberately throw a clear `ProviderAdapterException` explaining
      why rather than guessing: DeepInfra's audio endpoint exists but its exact schema wasn't
      fetched, and video generation was contradictory across sources (one candidate endpoint, one
      unrelated fragment, no confirmed docs page) — confirmed by `DeepInfraAdapterThrowsAClear
      NotYetImplementedErrorForAudioAndVideo`. 2 additional tests cover the reused text/image paths
      against DeepInfra's actual confirmed endpoints.
- [ ] Add signed adapter versioning for normalized snapshot formats, so generation-history and
      saved-setting records created by an earlier adapter version remain readable after that
      adapter is updated.
- [x] Extend connection testing, transport-security validation, TLS/redirect rules and the
      **Unverified**/**Authentication Failed**/**Credentials Required** connection states to
      OpenRouter and DeepInfra — both reuse the exact same `Connection`/base-URL/header validation
      path as the existing adapters (nothing provider-specific was needed since both use standard
      Bearer authentication over HTTPS). 1min.AI's divergent `API-KEY` header works through the
      existing configurable `CredentialHeaderName`/`AuthPrefix` fields without code changes, but the
      adapter itself remains deferred per above.
- [ ] Add provider- and model-capability detection/settings-schema definitions for each new
      adapter's models, generating the same structured setting controls (selectors, sliders,
      toggles, voice lists, dimensions) as the existing adapters, including per-adapter
      concurrency-limit declarations where a provider has no safe parallel-submission behavior.
      (OpenRouter/DeepInfra models can be added and used manually today via the existing
      manually-entered-model path; the generated-controls schema layer itself is still open.)

## Audio and video generation

- [x] Add `GenerationMode.Audio` and `GenerationMode.Video`, model configuration
      (`ModelEdit.razor` mode dropdown), and mode-aware labels/filters across `Generate.razor`,
      `GenerationHistory.razor` and `SavedSettings.razor` (all previously Text/Image-only switches
      already had a `mode.ToString()` fallback, so nothing crashed — this adds proper localized
      `ModeAudio`/`ModeVideo` strings and dropdown options in place of that fallback). Drafts and
      saved settings needed no changes: they already store a plain `ModelId` and don't branch on
      mode themselves.
- [ ] Replace the current 3 generic source-file slots (`SourceFileId`/`SecondarySourceFileId`/
      `TertiarySourceFileId`) with a named input-slot capability model — reference image, mask,
      first frame, last frame, source audio, source video — each with its own required media
      type(s), count and ordering, as `plan.md` defines under Generation Inputs. Audio and video
      generation ship this pass with no source inputs at all (matching image generation's existing
      behavior), since neither `OpenRouterProviderAdapter.GenerateAudioAsync`/
      `SubmitVideoGenerationAsync` accept one yet.
- [x] Add audio and video result commit: rather than two more near-duplicate methods alongside
      `RecordImageGenerationResultCoreAsync`, `LibraryWorkspace.RecordMediaGenerationResultAsync`
      reuses that exact same core method — the atomic stage-hash-detect-move-commit pipeline never
      had any image-specific behavior baked in, since the target `Model`'s own `Mode` already
      determines whether the resulting `GenerationRecord` is Audio/Video/Image. Wired into
      `GenerationQueueService.ExecuteAsync`: `GenerationMode.Audio` calls `GenerateAudioAsync`
      synchronously exactly like image generation; `GenerationMode.Video` submits, persists the
      async-job record, polls on a configurable interval (`_videoPollInterval`, defaulting to 5s)
      and commits once terminal. 4 new queue-level tests cover audio success/failure and video
      success (asserting the pending-registry row exists mid-poll and is removed after commit) and
      failure. **Known limitation, called out in code and tests**: a video job holds its queue
      submission slot for the entire poll duration rather than releasing it after durable provider
      acceptance as `plan.md` describes (`An asynchronous job releases its submission slot after the
      provider durably accepts it`) — fixing this needs the scheduler to separate "holding a
      submission slot" from "still being monitored," which is a real queue-architecture change, not
      attempted this pass to avoid rushing a concurrency change unverified.
- [ ] Add audio/video preview support (waveform data, video posters) in the regenerable preview
      cache, and the corresponding file-viewer behavior.
- [ ] Add per-slot source-input token/byte/dimension/duration accounting using each adapter's
      documented formula where one exists, and documented count/byte/dimension/duration limits
      otherwise.

## Multi-result workflows

- [ ] Add per-child result status within one multi-result generation (today a multi-result request
      is one atomic commit with no individual result identity, retry or status).
- [ ] Add **Partially Completed** with **Retry Failed/Missing Results Only** as the default recovery
      action when an adapter can safely represent the unsuccessful result count independently, plus
      **Run Entire Request Again**.
- [x] Add indivisible multi-request queue groups for video: `ExecuteVideoGenerationAsync` now
      submits `resultCount` independent provider jobs up front (one call per result — no adapter
      implemented this pass accepts an `n` parameter for video), persists each in the async-job
      registry, and polls all of them as one group before committing a single `GenerationRecord`
      with whichever results actually completed (`PartiallyCompleted` when some fail, matching
      every other mode's existing shortfall semantics). A submission failure partway through stops
      further submissions without abandoning jobs already accepted by the provider. Verified by
      `VideoGenerationWithMultipleResultsSubmitsOneIndependentJobPerResultAndCommitsAllOfThem` and
      `...IsPartiallyCompletedWhenOnlySomeJobsSucceed`. **Reordering/cancellation acting on the
      whole group** is not implemented — today's `GenerationQueueService.Cancel` only knows about
      one job per queue entry, not a multi-job group; a mid-group cancellation currently only stops
      the specific poll loop iteration via the shared `CancellationToken`, it doesn't yet have
      distinct "which children already completed" reporting back to the queue/GUI layer.
- [ ] Extend the existing content-filter partial-shortfall handling (`GenerationRecord
      .SafetyBlockedCount`) to have real per-child identity once the item above lands, rather than
      only an aggregate blocked count.

## File-transfer variations and result validation

- [x] Add pre-commit expected-media-category validation: `LibraryWorkspace`'s shared media commit
      path (`RecordImageGenerationResultCoreAsync`, now also used by `RecordMediaGenerationResultAsync`
      for Audio/Video) compares `MediaTypeDetector.DetectAsync`'s result against the target model's
      mode (`image/`/`audio/`/`video/` prefix) and skips committing — rather than silently creating a
      mis-typed library file — any single result whose bytes don't match, while still committing the
      rest of a multi-result batch normally (proven by
      `AMixedBatchOfValidAndMismatchedAudioResultsCommitsOnlyTheValidOnesAsPartiallyCompleted`). Response
      status and non-zero size were already enforced (each adapter already throws on a non-success
      status or empty bytes). **Not done**: provider-supplied checksum validation — no adapter
      implemented this pass reports one, so there's nothing to check yet.
- [ ] Add the **Retain as Unverified Binary** path for a result whose bytes cannot be classified
      into the expected media type, storing it distinctly rather than silently discarding or
      mis-typing it.
- [ ] Add support for provider-hosted result URLs (1min.AI/OpenRouter/DeepInfra features that
      return a URL rather than inline base64): HTTPS-by-default validation, resolved-address-class
      checks against loopback/link-local/private/multicast targets, redirect-target revalidation to
      detect DNS rebinding, authentication stripped across cross-host redirects, and streaming the
      download into temporary storage before committing.
- [ ] Add provider-issued signed upload destinations for adapters that require out-of-band asset
      upload before generation (rather than inline request-body bytes), following the same host/
      redirect/credential rules as result downloads.
- [ ] Add transport filename handling for adapters that require or benefit from generic/aliased
      upload names, including alias binding that stays consistent across a prompt-improvement
      attempt and its final generation submission.
- [ ] Add incremental text display for adapters that support streaming, writing to a temporary file
      until the response completes.

## Usage and cost handling

- [ ] Add provider-reported actual-cost fields to `GenerationRecord` (which today only has
      `PromptTokens`/`CompletionTokens`), scoped separately as `generation-run`, per-output (only
      when a provider explicitly reports that scope) and `prompt-improvement` — a run total is
      never divided among per-output records, and prompt-improvement cost is never added to the
      generation-run total.
- [ ] Add actual-cost display against any known estimate, including overrun highlighting and a
      pricing-revision **Unreliable** marking after repeated overruns, superseding today's
      permanently-`null`-cost **Cost unknown** notice for adapters that now report real figures.
- [ ] Add the local cost-summary view aggregating provider-reported actual cost, filterable by
      date, provider, connection, model and operation type (deferred from Milestone 2 pending real
      adapter cost data — 1min.AI/OpenRouter/DeepInfra are the first candidates to actually supply
      it).
- [ ] Add the usage/cost portions of the sidecar export spec that depend on this milestone's new
      data: the `Include Usage and Cost` opt-in category, run/per-output/prompt-improvement scope
      separation, and `reported-so-far`/incomplete-flag handling for a nonterminal multi-result run.
      The full sidecar/export-JSON system (`plan.md`'s Export section) is not claimed by any
      milestone's stated scope and has no implementation today; only the slice that would otherwise
      be dead weight without this milestone's cost/provenance data is included here; a dedicated
      pass to build the rest of the sidecar spec remains unscoped.
- [ ] Add the provider/model snapshot and generation-provenance fields to that same sidecar slice
      for the three new adapters and the Audio/Video modes, so a sidecar written for their results
      is not silently missing provenance that Text/Image results already have once sidecars exist.

## Rate-limit behavior

- [ ] Add per-connection rate-limit state (last observed limit, remaining, reset time) rather than
      today's stateless retry-only handling, and adaptive throttling that backs off proactively
      once a connection's remaining quota is known to be low.
- [ ] Extend bounded automatic retry with `Retry-After` honoring (currently scoped only to model
      listing, via `allowRetry` in `OpenAiCompatibleProtocol.SendAsync`) to result downloads and
      async-job status polling, without violating the rule that a generation-submission request
      itself only auto-retries under a confirmed idempotency key.
- [ ] Add rate-limited/delayed status display on `/generate` and `/queue` with cancellation
      available while waiting, and ensure Retry-After waits interact correctly with indivisible
      multi-request queue-group ordering.
- [ ] Add rate-limiting to explicit provider-status refresh/reconciliation actions (distinct
      per-request throttling that disables repeated activation while a lookup is in progress).

## Provider-specific capabilities and safety responses

- [ ] Add the remaining Provider Safety Responses machinery deferred from Milestone 2:
      concealment/reveal session state, per-file persistent override preferences, external-open
      re-authorization for concealed content, and cross-duplicate shared classification events keyed
      by content hash.
- [ ] Add **Provider Blocked After Delivery** late reclassification, using the async-job monitoring
      infrastructure above to detect a status change after initial delivery.
- [ ] Extend provider safety/moderation handling to the new adapters' actual signals (each may
      differ from OpenAI's `finish_reason: content_filter`-only model), including image, audio and
      video modality moderation where a provider documents it.
- [ ] Add manually entered/unknown-model advanced-JSON support for each new adapter's reserved-key
      list, mirroring the existing generic-adapter advanced editor.

## Final Milestone 3 verification

- [ ] Add automated coverage for every behavior above, including cancellation, partial failure,
      cross-adapter and cross-library isolation cases.
- [ ] Run the full shared test suite, Windows MAUI build, and Android MAUI build with zero errors.
- [ ] Execute a Milestone-3 manual acceptance pass on supported Windows and Android devices,
      including at least one real (budget-bounded) live-provider run per new adapter, and record it
      in `manual_tests.md`.
- [ ] Update `plan.md` by removing only verified completed requirements, and keep user/developer
      documentation and `README.md` aligned with the finished behavior.
