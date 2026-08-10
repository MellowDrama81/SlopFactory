# Milestone 2 completion checklist

This checklist breaks the Milestone 2 scope in `plan.md` ("Core generation workflow") into
independently completable units, following the same convention as `milestone1.md`. An item is
complete only when its stated automated verification passes; platform-labelled items also require
the applicable device check in [manual_tests.md](manual_tests.md).

Milestone 1 remains partially open (see `milestone1.md`); per `plan.md`, milestone boundaries are
implementation/validation phases rather than strict release gates, so Milestone 2 work can proceed
in parallel. Both milestones must be complete before the first public release.

## Connections and secure credentials

- [x] Add `Connection` and `ProviderType` domain records/enum, library-database schema v9 tables,
      and CRUD (create/list active+recycled/get/update) through `ILibraryWorkspace`; verify with
      infrastructure tests covering validation, label uniqueness and not-found handling.
- [x] Add secure per-library, per-connection API-key storage (`ISecureCredentialStore` over MAUI
      `SecureStorage`, keyed by library ID and connection ID) with a `HasCredential` flag on the
      connection record rather than the key value; verify the key is never persisted in
      preferences or the library database.
- [x] Add connection recycle/restore/permanent-delete with cascade to dependent models, removing
      the stored credential only on permanent deletion; verify cascade and credential removal.
- [x] Add a Connections list page (active + recycled) and add/edit form (label, provider type,
      base URL, credential header name/prefix, API key) with masked credential display and a
      **Test Connection** action; verify the localization guard and manual add/edit/delete flow.
- [ ] Implement the full revisioned secure-storage credential lifecycle: candidate namespace during
      key replacement, promotion only after a successful test, crash-safe reconciliation trusting
      only a committed revision pointer, and **Credential State Requires Repair** handling.
- [x] Distinguish a **Credentials Required** connection status (no stored API key, checked ahead of
      any test result) from **Unverified**/**Verified**/**Test Failed** on the Connections list
      (`StatusLabel`), and surface both on `/generate`: a missing credential blocks the **Generate**
      action with a link to the connection editor, while a present-but-never-successfully-tested
      credential shows a non-blocking warning instead. Editing a connection already resets its test
      status to **Untested** (`UpdateConnectionAsync`), satisfying retest-required-on-edit. There is
      no **Secure Storage Unavailable** state (nothing yet models the credential store itself being
      temporarily inaccessible, as opposed to the credential being absent), no **Authentication
      Failed** state distinct from a generic failed test (no adapter code inspects the HTTP status
      to tell a connection-wide `401` apart from a request-specific error), and no first-submission
      confirmation that collapses after one acknowledgement per connection revision — the warning
      simply reappears on every use until a test succeeds. Those remain open below.
- [x] Add base-URL transport-security validation and warning: `LibraryRules.NormalizeConnectionBaseUrl`
      already requires an absolute http/https address, rejects an embedded username or password,
      and permits `http://` only for a loopback or private-network host (schema v9). This slice
      adds the missing user-facing **warned HTTP** behavior: `ConnectionEdit.razor` shows a live
      warning as soon as the typed base URL's scheme is `http`, and the Connections list shows a
      short **Unencrypted (HTTP)** badge next to any stored connection using one. There is still no
      path-duplication normalization (beyond trailing-slash trimming), no redirect-to-different-host
      confirmation, and no TLS/proxy/OS-trust-store configuration surface — `HttpClient` uses
      platform-default TLS/trust-store behavior with no adapter-level override; those remain open.
- [x] Add a connection timeout override (schema v16: `connections.timeout_seconds`, nullable —
      blank means the application default of 100 seconds). `LibraryRules.NormalizeConnectionTimeoutSeconds`
      enforces a shared 5–600 second range (a SlopFactory-wide policy, not a documented
      adapter-declared bound — no per-adapter bound infrastructure exists). `ConnectionEdit.razor`
      exposes it under **Advanced connection settings** and includes the candidate value in
      **Test Connection**'s probe before saving; editing a connection already resets its test status
      (existing retest-on-edit behavior), so a timeout change requires retesting like any other
      field. `OpenAiCompatibleProtocol.SendAsync` centralizes every adapter HTTP call behind a
      linked `CancellationTokenSource` so a timeout throws `ProviderAdapterException` with a clear
      message distinct from real user cancellation — fixing a latent bug where a slow provider
      response past `HttpClient`'s prior 100-second default would have surfaced on `/generate` as
      "Generation cancelled" rather than a provider error, because `HttpClient` never distinguished
      its own timeout from a caller-cancelled token.
- [x] Add additional non-secret connection headers for gateway/routing use cases (schema v17:
      `connection_headers`, a `(connection_id, name)`-keyed child table). `LibraryRules.NormalizeConnectionHeaders`
      caps the count (10) and per-value length (500 characters), rejects duplicates, and rejects
      reserved transport headers (`Host`, `Content-Length`, `Connection`, etc.) and
      `Authorization`/`Proxy-Authorization`/`Cookie`/the connection's own credential header name —
      those require secure-storage-backed secret handling that does not exist yet, so they are
      refused outright rather than half-supported. `ConnectionEdit.razor` exposes them as a
      `Name: Value`-per-line textarea, validated identically at **Test Connection** and **Save**;
      `OpenAiCompatibleProtocol.ApplyAdditionalHeaders` applies them on every adapter request
      alongside the credential header. There is no secret/marked-header support, no secure-storage
      backing for header values, and no redaction pass over connection tests/logs/history/exports —
      only plain, explicitly non-secret values are supported by design in this slice.
- [ ] Add unresolved-cleanup/reconciliation gating before base URL, provider type, credential
      header or auth-structure changes take effect (`Retry Cleanup`, `Stop Tracking and Apply
      Changes`, `Attempt Reconciliation`, `Abandon Recovery and Apply Changes`).
- [x] Add connection-label-change independence from dependent models (already true by construction —
      `Model` stores only a `ConnectionId` foreign key, never a cached connection label, so renaming
      a connection needs no propagation; covered by the existing
      `UpdatingAConnectionResetsItsTestStatusWithoutAffectingDependentModels` test) and a
      provider-type lock while dependent models exist. `ILibraryWorkspace.ChangeConnectionProviderTypeAsync`
      is a dedicated method (rather than folding a provider-type parameter into the existing
      `UpdateConnectionAsync`) that validates zero *active* dependent models server-side, resets
      generic-modality settings to their default, and requires retesting; `ConnectionEdit.razor`
      only disables the provider-type selector when the connection already has active dependent
      models. Recycled (non-active) dependent models do not block the change.
- [ ] Extend the unified recycle bin (`RecycleBin.razor` and its shared category/search/sort
      workflow) to include connections, models and saved generation settings instead of the
      dedicated recycle sections added in these milestone slices.

## Models and discovery

- [x] Add `Model` and `GenerationMode` (Text, Image) domain records, schema v9 `models` table, and
      CRUD (create/list active+recycled/get/update) through `ILibraryWorkspace`, including label
      uniqueness and dependency on an active connection.
- [x] Add model recycle/restore/permanent-delete cascading from and independent of connection
      lifecycle; verify a generated file's provider/model snapshot survives model deletion.
- [x] Add a Models list page and add/edit form (label, connection, mode, provider model ID) with a
      **Load Models** action calling the connection's adapter and a manual-entry fallback; verify
      the localization guard and manual add/edit/delete flow.
- [ ] Add Audio and Video to `GenerationMode` when their generation workflows are implemented in a
      later milestone slice (kept out of this slice's UI since no adapter yet produces them).
- [x] Add model catalogue caching with retrieval timestamp, **Stale**/**Possibly Stale** labelling,
      and **Not Currently Listed** handling for a configured model absent from a refreshed
      catalogue (schema v15: `connections.catalogue_retrieved_at`/`catalogue_possibly_stale` and a
      new `connection_model_catalogue` table). `ModelEdit`'s **Load Models** action persists a
      successful discovery through `ILibraryWorkspace.RefreshModelCatalogueAsync` and flags a failed
      one **Possibly Stale** through `MarkModelCatalogueRefreshFailedAsync` without clearing the
      retained catalogue; the editor shows the retrieval timestamp/age and a **Stale** label once it
      is older than `LibraryRules.ModelCatalogueStalenessPeriod` (7 days, uniform across adapters —
      no per-adapter override yet). `Models.razor` marks a configured model **Not Currently Listed**
      when its `ProviderModelId` is absent from its connection's cached catalogue, without deleting
      or disabling it. The catalogue is not yet refreshed automatically during initial connection
      setup or connection retesting (only via the explicit **Load Models** action), and manual model
      entry remains unaffected by catalogue state either way.
- [ ] Add **Needs Review** propagation when a configured model's provider model ID, mode, input
      capabilities or settings schema changes, including confirmation before applying the change.
- [ ] Add typed provider settings schemas per model/modality and the generated settings-control UI
      described under Connections and Models, including **Use Provider Default** and **Reset to
      Provider Default** semantics.
- [ ] Add the manual/advanced-JSON settings editor with reserved-key protection and bounded
      size/nesting for models without a maintained schema.
- [x] Enforce the per-model **Supports System Instructions** flag on `/generate`: the field is
      hidden (with an explanatory note) rather than shown for a Text-mode model whose
      `SupportsSystemInstructions` is false, and both `GenerateAsync` and `SaveSettingsAsync` check
      the flag directly rather than trusting field visibility, so a stale value left over from a
      previously selected model can never be sent or saved. There is no confirmation-on-provider-
      rejection flow — the generic adapter does not inspect a provider's response for signs it
      rejected an unsupported `system` role and does not offer to flip the flag automatically; that
      remains open.

## Provider adapters

- [x] Add `IProviderAdapter`/`IProviderAdapterResolver` contracts and implement the OpenAI and
      generic OpenAI-compatible adapters' connection test and model listing; verify against a fake
      `HttpMessageHandler` covering success, authentication failure and unreachable-host cases.
- [ ] Add the local fake HTTP provider covering authentication, discovery, streaming, asynchronous
      jobs, rate limits, moderation, redirects, downloads and errors described under Testing.
- [x] Add per-modality relative-path overrides and per-modality enable/disable for the generic
      OpenAI-compatible connection (schema v18: 6 new `connections` columns, all-enabled/no-override
      by default). `LibraryRules.NormalizeGenericModalitySettings`/`NormalizeRelativePathOverride`
      reject an absolute-URL override or one containing `..` segments (a relative-path override
      also cannot change scheme/host structurally, since `OpenAiCompatibleProtocol.CombineUrl`
      always concatenates it onto the connection's own base URL rather than parsing it as a
      standalone URI). `GenericOpenAiCompatibleProviderAdapter` reads the configured path (or the
      OpenAI-standard default) for each of `ListModelsAsync`/`GenerateTextAsync`/`GenerateImageAsync`,
      and throws `ProviderAdapterException` immediately — without ever sending a request — when the
      corresponding modality is disabled. `ConnectionEdit.razor` exposes this only for the generic
      provider type (the dedicated OpenAI adapter always uses its fixed paths and ignores these
      settings entirely) and shows each modality's resolved endpoint as a computed preview. There is
      no live reachability validation of an enabled modality's endpoint during **Test Connection** —
      only the model-listing endpoint is actually called, matching the documented "without issuing
      paid generation requests" rule, but chat/image endpoints are never probed at all (not even
      non-mutating checks); that remains open.
- [ ] Add the 1min.AI, OpenRouter and DeepInfra adapters (scoped for Milestone 3 alongside audio
      and video generation).
- [ ] Add signed adapter versioning for normalized snapshot formats so older history remains
      readable across adapter updates.

## Generation workspace (tabs and drafts)

- [ ] Add a per-library generation-tab draft model (automatic/custom title, models, prompts,
      settings, source roles/order, destination folder, result count) persisted in the library
      database per the Session Recovery requirements.
- [ ] Add tab lifecycle: create, duplicate (without run/history association), rename, reset to
      automatic title, reorder, and close with the discard/save-as-named-settings/cancel dialog.
- [ ] Add debounced atomic draft autosave with **Saving**/**Saved**/**Not Saved** status and
      **Retry Save**, plus the library-switch and application-exit unsaved-edit gates.
- [ ] Add the Android compact tab-switcher and Windows/tablet tab-strip UI, including
      virtualization for a large number of drafts.
- [ ] Add emergency draft snapshot staging and reconciliation for an unavailable/read-only library,
      per Session Recovery.

## Generation inputs

- [x] Add a minimal single-page generation form (`/generate`: mode-labelled model select covering
      both Text and Image models, prompt textarea, result count, destination folder) with no tabs,
      drafts, source inputs or prompt improvement yet; those remain separate unchecked items below.
- [x] Add an optional **System Instructions** field, shown only for Text-mode models, sent through
      the documented `system` chat-completion role (OpenAI and generic adapters), persisted on
      `GenerationRecord` and `SavedGenerationSetting` (schema v12), and carried through **Save
      settings** and **Use Again**. There is no 1 MiB well-formed-UTF-8 bound, CRLF/CR
      normalization, atomic oversized-edit rejection, or adapter-declared-capability gating yet —
      any text-mode model is currently allowed to receive it regardless of documented support.
- [ ] Add the raw-prompt, system-instructions and result-count generation form with the 1 MiB
      well-formed-UTF-8 bounds, CRLF/CR normalization and atomic oversized-edit rejection.
- [x] Add a minimal single-image vision source input for Text-mode generation: `/generate` offers
      one optional source image (from active image-media library files), read through the existing
      verified `ReadImageFileAsync` pipeline and sent as an OpenAI-shaped `image_url` data-URI
      content part alongside the prompt (OpenAI and generic adapters). The reference is persisted on
      `GenerationRecord`/`SavedGenerationSetting` (schema v14, `ON DELETE SET NULL`) and carried
      through **Save settings**/**Use Again**. There are no named input slots, multiple sources,
      role/order persistence, or capability-based validation yet, and image-mode generation accepts
      no source input at all — those remain open below.
- [ ] Add the source-file picker with named input slots, capability-based compatibility/validation,
      and role/order persistence.
- [ ] Add debounced background prompt/context validation with **Estimating**/**Stale**/**Partial
      Validation** states and the exact-vs-approximate submission-gating rule.
- [ ] Add provider-facing transport filenames/aliases (generic and custom modes) per Provider File
      Transfer, including filename-reference reliability metadata and cross-adapter alias
      validation for shared prompt-improvement/generation sources.

## Generation submission, queues and lifecycle

- [x] Add a minimal synchronous generation submission path: `IProviderAdapter.GenerateTextAsync` and
      `GenerateImageAsync` (OpenAI and generic adapters, chat-completions and images/generations
      request/response) called directly from the `/generate` page, with a lightweight
      `GenerationRecord` (schema v10: `generation_records`/`generation_results`) capturing the
      model/provider snapshot, prompt, result count, status and sanitized error. There is no queue,
      cancellation, retry, multi-request tracking or async job polling yet — every item below this
      one remains open.
- [ ] Add the generation-history record model (immutable request snapshot, normalized status
      timeline, per-child-request tracking for multi-result generations without a native count
      parameter).
- [ ] Add per-connection FIFO queues, the device-wide submission cap with fair round-robin slot
      allocation, and configurable per-connection concurrency within adapter-declared bounds.
- [x] Add a minimal **Cancel** action on `/generate` backed by a `CancellationTokenSource` passed
      through to the adapter call, the result-file commit and the history-record insert. On
      cancellation, the page shows a message warning that the provider may still process or charge
      for an already-sent request, and deliberately records no `GenerationRecord` at all rather than
      guessing a status — there is no **Submission Outcome Unknown** state, no distinction between
      pre-submission and mid-upload cancellation, and no reconciliation; those remain open below.
- [ ] Add cancellation handling for every defined stage (before submission, mid-upload, provider
      already accepted) and the **Submission Outcome Unknown** indeterminate state with
      reconciliation.
- [ ] Add asynchronous remote job polling, persistence across restart, and **Monitoring Paused**
      after the adapter-defined maximum monitoring lifetime.
- [ ] Add offline/metered-network handling: **Paused — Connection Lost**, **Resume Queue**,
      metered-network transfer warnings and the device-wide transfer-option setting.
- [ ] Add idempotency-key generation and scoped reuse for adapters with documented idempotency
      support.
- [ ] Add bounded automatic retry with `Retry-After`/rate-limit honoring and exponential backoff
      with jitter for idempotent operations.

## Generation results and result ingestion

- [x] Commit each returned text candidate as a distinct `.md` managed file (`FileOrigin.Generated`)
      using the same atomic staging-then-move commit primitive as `CreateEditedTextCopyAsync`,
      linked to its `GenerationRecord` via `generation_results`; verify commit, hash, linkage and the
      failed-attempt (no files, sanitized error retained) path.
- [x] Commit each returned image candidate (decoded from a provider's base64 response) as a managed
      file whose media type/extension come from `MediaTypeDetector.DetectAsync` on the staged bytes
      (the same Milestone 1 file-signature detection import already uses), rather than trusting a
      provider-declared format; verify PNG detection, hash and the failed-attempt path. Direct
      provider-hosted image URLs, provider result-status/content-type/checksum validation, the
      unverified-binary retention path, and audio/video results all remain open.
- [ ] Add result download, validation (status/content-type/media-category/checksum), atomic
      managed-file commit, and the unverified-binary/unrecognized-content-type retention paths.
- [ ] Add text-result formatting (`.md` default, `.json` for validated structured output, `.txt`
      fallback/override) and streaming incremental display.
- [ ] Add multi-result generation handling: per-child status, **Partially Completed**, and the
      documented transport-archive extraction rules for providers that document it.
- [ ] Add provider safety-response handling: blocked-bytes discard, **Provider Safety Warning**
      concealment/reveal, **Provider Blocked After Delivery**, and the shared classification-event
      model described under Provider Safety Responses.

## Generation history

- [x] Add a minimal generation-history list page (`/generation-history`: model label, status,
      created time, full prompt, result-file links, sanitized error) with no filters, detail view,
      **Use Again**, or recycle-bin integration yet.
- [x] Add client-side status/mode/model filters and a **Clear filters** action to
      `/generation-history`. There is no date/provider filter, no separate detail view (the list
      already shows full prompt/settings/errors/usage inline), and filters are not persisted across
      navigation.
- [ ] Add a generation-history browsing page separate from the file library, with the documented
      filters (status, date, provider, model, output type) and detail view (prompts, settings,
      sources, outputs, attempts, errors, usage).
- [x] Add a minimal **Use Again** (`/generate/history/{HistoryId}`) that repopulates the `/generate`
      form's prompt, result count, destination folder and model from a historical
      `GenerationRecord` without modifying the record, showing a model-unavailable warning when the
      snapshotted model no longer exists. There is no source/model-incompatibility confirmation
      (no source inputs exist yet) or system-instruction-channel-mismatch handling (no system
      instructions exist yet) — those remain open below.
- [ ] Add **Use Again** to repopulate a new generation tab from a historical snapshot, including
      the source/model incompatibility and system-instruction-channel-mismatch confirmations.
- [ ] Add generation-history recycle/restore/permanent-delete integrated with the unified recycle
      bin, including file/source tombstoning rules.
- [ ] Add prompt-improvement history records as a distinct lightweight AI-operation entry type.

## Prompt improvement

- [x] Add a minimal optional prompt-improvement flow on `/generate`: pick any active Text-mode
      model as the improvement model (separate from the output model), optional free-text
      guidance, and an **Improve Prompt** action that sends the current prompt plus a built-in
      versioned instruction template (tailored to the output model's mode, delivered through the
      existing `system`-instructions channel) to the improvement model, showing the returned
      candidate(s) for the user to accept into the prompt textarea or discard untouched. There is
      no raw-prompt-only-by-default disclosure UI, **Include Target Model Identity**/**Include
      System Instructions in Improvement**/**Include Compatible Sources** opt-ins, or **View
      Instruction** display — improvement is a purely in-session prompt-textarea helper with no
      persisted raw-vs-improved distinction on `GenerationRecord`/`SavedGenerationSetting`; those
      remain open below.
- [ ] Add improvement-candidate handling: multiple candidates, size bounds, **Refused**/
      **Unsupported Response**/**Interrupted** outcomes, and **Needs Review** invalidation rules.

## Saved generation settings

- [x] Add a minimal `SavedGenerationSetting` (schema v11: `saved_generation_settings`; title, model
      snapshot, prompt, result count, destination folder) with CRUD through `ILibraryWorkspace`,
      title uniqueness, and recycle/restore/permanent-delete cascading correctly from and to its
      owning model and connection (mirroring the connection→model cascade). The `/generate` page
      offers **Save settings**, which updates in place only when reopened from the same saved
      setting with an unchanged title, otherwise creates a new one; `/saved-settings` lists, uses,
      recycles, restores and permanently deletes them. There is no true **Save**/**Save As**
      revision-conflict review, no settings-schema/sources/improvement-state capture, and no
      dependency "Needs Review" handling yet — those remain open below.
- [ ] Add saved generation settings (title, model, prompts, settings, sources, improvement state)
      with **Save**/**Save As**, revision-conflict review, and dependency-restoration handling
      matching the recycled/missing-model-or-source rules.

## Cost, usage and notifications

- [x] Capture provider-reported prompt/completion token usage from the OpenAI chat-completions
      response (`usage.prompt_tokens`/`usage.completion_tokens`) and persist it on `GenerationRecord`
      (schema v13), shown on the `/generate` result panel and in `/generation-history`. There is no
      cost estimation, no image/other-modality usage capture, and no cost-summary view yet.
- [ ] Add pre-generation cost estimation (provider estimate API or versioned bundled pricing),
      **Cost unknown** acknowledgement, and confirmation thresholds keyed by currency/credit unit.
- [ ] Add a local cost-summary view aggregating provider-reported actual cost, filterable by date,
      provider, connection, model and operation type.
- [ ] Add OS generation-completion/failure notifications (enabled by default off, sanitized
      content, **Submission outcome needs attention** alert) per Generation Notifications.

## Testing infrastructure

- [x] Add adapter unit tests using a fake `HttpMessageHandler` for the OpenAI and generic
      OpenAI-compatible connection-test, model-listing, chat-completion text-generation and
      images/generations paths.
- [ ] Expand the fake HTTP provider into a shared reusable test fixture covering the full Testing
      section requirements (streaming, async jobs, rate limits, moderation, redirects, downloads,
      errors) before Milestone 3 adapters are added.
- [ ] Add release-blocking export-style crash-injection tests for generation-history and saved
      settings once their persistence paths exist, mirroring the Milestone 1 export crash-injection
      coverage.

## Final Milestone 2 verification

- [ ] Add automated coverage for every remaining Milestone 2 behavior above, including
      cancellation, partial failure and cross-library isolation cases.
- [ ] Run the full shared test suite, Windows MAUI build, and Android MAUI build with zero errors.
- [ ] Execute a Milestone-2 manual acceptance pass on supported Windows and Android devices and
      record it in `manual_tests.md`.
- [ ] Update `plan.md` by removing only verified completed requirements and keep user/developer
      documentation and `README.md` aligned with the finished behavior.
