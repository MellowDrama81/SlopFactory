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

- [ ] Expand `FakeHttpMessageHandler` into a shared, reusable test fixture covering streaming
      responses, asynchronous job submission/polling, rate-limit (`429`/`Retry-After`) responses,
      moderation/content-filter responses, redirects (same-host and cross-host), binary downloads
      and a representative set of transport/provider error shapes, per the Testing section of
      `plan.md`.
- [ ] Add provider contract fixtures (versioned sanitized request/response JSON) for 1min.AI,
      OpenRouter and DeepInfra so adapter behavior is pinned against real shapes rather than
      hand-written approximations, and so future provider API changes are reviewed deliberately
      against a diff.
- [ ] Add a live-provider manual test harness (explicitly enabled, skipped when credentials are
      absent, cost-budget-bounded) for each of the three new adapters, mirroring the existing
      OpenAI/generic live-test conventions.

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
      recycled).
- [ ] Add a persisted async-job record (provider job ID, connection ID, library ID, submitted/last
      polled timestamps, adapter-declared max monitoring lifetime) distinct from the immutable
      request snapshot, and resume polling for incomplete jobs on library open and app foreground
      per the minimal device-wide pending-job registry described under Session Recovery.
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
- [ ] Add the unresolved-cleanup/reconciliation gate before a base URL, provider type, credential
      header or auth-structure change takes effect, with **Retry Cleanup**, **Stop Tracking and
      Apply Changes**, **Attempt Reconciliation** and **Abandon Recovery and Apply Changes**
      (deferred from Milestone 2 for exactly this infrastructure).
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
- [ ] Add `ProviderType.OpenRouter` and its adapter: OpenAI-compatible base URL and chat/text
      handling, but modality-specific endpoints and schemas for image generation, audio generation
      and asynchronous video generation (explicitly handled by the adapter, not the generic
      OpenAI-compatible path).
- [ ] Add `ProviderType.DeepInfra` and its adapter: OpenAI-compatible endpoints for chat, image and
      audio where supported, falling back to its native inference API for models/modalities not
      exposed through the compatible endpoints, including video.
- [ ] Add signed adapter versioning for normalized snapshot formats, so generation-history and
      saved-setting records created by an earlier adapter version remain readable after that
      adapter is updated.
- [ ] Extend connection testing, transport-security validation, TLS/redirect rules and the
      **Unverified**/**Authentication Failed**/**Credentials Required** connection states to each
      new adapter's actual authentication and discovery mechanism.
- [ ] Add provider- and model-capability detection/settings-schema definitions for each new
      adapter's models, generating the same structured setting controls (selectors, sliders,
      toggles, voice lists, dimensions) as the existing adapters, including per-adapter
      concurrency-limit declarations where a provider has no safe parallel-submission behavior.

## Audio and video generation

- [ ] Add `GenerationMode.Audio` and `GenerationMode.Video` (today's `GenerationMode` only has
      `Text`/`Image`), including model configuration, drafts, saved settings and generation-history
      support for both new modes.
- [ ] Replace the current 3 generic source-file slots (`SourceFileId`/`SecondarySourceFileId`/
      `TertiarySourceFileId`) with a named input-slot capability model — reference image, mask,
      first frame, last frame, source audio, source video — each with its own required media
      type(s), count and ordering, as `plan.md` defines under Generation Inputs.
- [ ] Add audio and video result commit pipelines mirroring the existing atomic
      stage-hash-move-then-link pattern (`RecordTextGenerationResultCoreAsync`/
      `RecordImageGenerationResultCoreAsync`), detecting media type/extension from provider bytes
      rather than a declared format, and covering the failed-attempt (no orphaned files/history)
      path with the same crash-injection technique used for text/image.
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
- [ ] Add indivisible multi-request queue groups: a multi-result generation requiring several
      separate provider submissions occupies one queue position, its children execute consecutively
      before the next queued generation begins, and reordering/cancellation act on the whole group
      (cancelling prevents remaining unsent children while retaining already-completed results and
      their per-child statuses).
- [ ] Extend the existing content-filter partial-shortfall handling (`GenerationRecord
      .SafetyBlockedCount`) to have real per-child identity once the item above lands, rather than
      only an aggregate blocked count.

## File-transfer variations and result validation

- [ ] Add pre-commit result validation: response status, content type, expected media category,
      non-zero size and provider-supplied checksum when available; bytes that cannot be validated
      fail the result rather than creating a successful media record automatically.
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
