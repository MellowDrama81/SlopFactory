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
- [x] Implement the full revisioned secure-storage credential lifecycle: candidate namespace during
      key replacement, promotion only after a successful test, crash-safe reconciliation trusting
      only a committed revision pointer, and **Credential State Requires Repair** handling. Schema
      v23 adds a non-secret `connection_credential_revisions` ledger (`CredentialLedgerRevision`,
      `CredentialLedgerConnectionSnapshot`, `CredentialPromotionResult`) plus
      `connections.credential_revision_id`/`credential_requires_repair`, with
      `BeginCredentialCandidateAsync`/`PromoteCredentialRevisionAsync`/`DiscardCredentialCandidateAsync`/
      `MarkCredentialRequiresRepairAsync`/`GetCredentialLedgerSnapshotAsync`/`DeleteCredentialLedgerRowAsync`
      on `ILibraryWorkspace`. `ISecureCredentialStore` moved to a revision-aware
      active/candidate/legacy API (`MauiSecureCredentialStore` namespaces candidate keys separately
      from active keys, per plan.md's "separate indexed secure-storage namespace"). `ConnectionEdit.razor`
      stages a candidate, runs a fresh test against the exact staged value, and only promotes after
      writing-and-verifying the new secure-storage entry; a failed test surfaces a **Keep Existing
      Key**/**Save New Key as Unverified** decision panel instead of silently discarding or saving.
      A new `CredentialReconciliationService` singleton (mirroring `ManagedContentWatchService`'s
      shape) sweeps orphaned candidates, detects a committed pointer with no matching/readable active
      revision (**Credential State Requires Repair**, touching nothing else), cleans up superseded
      active revisions, and silently one-time-adopts each pre-existing connection's legacy
      (non-revisioned) credential into revision 1 without a forced retest or visible change — every
      existing library upgrades without any connection being incorrectly flagged. Deliberately
      excludes plan.md's unresolved-cleanup/remote-job gating and **Submission Outcome Unknown**
      reconciliation (below, still open) — both depend on remote-job/cleanup-tracking infrastructure
      this app doesn't have, since every provider call here is synchronous request/response. Verified
      by `CredentialRevisionLifecycleTests.cs`, `CredentialReconciliationServiceTests.cs`, and the
      `OpeningVersionTwentyTwoLibraryAddsCredentialRevisionLedger` migration test.
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
- [x] Extend the unified recycle bin (`RecycleBin.razor` and its shared category/search/sort
      workflow) to include connections, models and saved generation settings instead of the
      dedicated recycle sections added in these milestone slices. `RecycleBinItemKind` gains
      `Connection`/`Model`/`SavedSetting`; `GetRecycleBinEntriesAsync` gains three query blocks
      mirroring the existing folder/file "hidden while its parent is also recycled" filter exactly
      (a model never shows as its own top-level entry while its connection is recycled too, and
      likewise for a saved setting under a recycled model) — this was already safe because
      `RecycleConnectionAsync`/`RestoreConnectionAsync` and `RecycleModelAsync`/`RestoreModelAsync`
      already cascade fully to their dependents inside one DB transaction (already tested
      independently in `SavedGenerationSettingTests.cs`), so this slice is purely a listing/preview/
      dispatch layer over already-solid domain logic, not new cascade logic. `GetRestoreBlockersAsync`
      gains matching cases: Connection and Model both check recycled-state, an owning-parent-must-be-
      Active precondition (Model only — Connection has no parent), and an active-label conflict;
      **SavedSetting deliberately does not check its owning model's state**, since
      `RestoreSavedSettingAsync` itself never enforces that (a real, verified difference from Model,
      not an oversight — mirroring it would have silently blocked restores that the underlying
      domain method actually allows). New `connections_credential_revisions`-aware permanent-delete
      handling was **not** needed: unlike folder/file permanent-delete (non-transactional filesystem
      I/O, hence the `permanent_deletion_failures` retry table), connection/model/saved-setting
      permanent-delete is pure transactional SQL with no filesystem component, so it can never leave a
      retryable partial state. One real relocation was required: `Connections.razor`'s old dedicated
      permanent-delete handler also cleaned up the deleted connection's secure-storage credential
      ledger entries (`ISecureCredentialStore`), which `Infrastructure` cannot do (Gui-layer only,
      and `CredentialReconciliationService` can't serve as a safety net here either — once a
      connection's DB row is gone, its cascade-deleted ledger rows can never be found again). This
      cleanup now lives in `RecycleBin.razor` itself, wrapped around its delete/empty-bin actions,
      isolated per connection so one connection's secure-storage failure doesn't block cleanup for
      the rest of a batch. `Connections.razor`/`Models.razor`/`SavedSettings.razor` keep only their
      active list and the **Recycle** action; restore and permanent delete now live exclusively in
      `/recycle-bin`, with a plain link back to it from each page. While in this code, also fixed a
      pre-existing gap (not introduced by this slice, but touched by it): `ProcessRecycleBinItemsAsync`'s
      catch filter didn't include `SqliteException`, so a raw SQLite error from `FileLink`'s (already
      direct, unwrapped) restore/delete calls could have aborted an entire batch instead of being
      recorded as one item's failure like every other kind already does.

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
- [x] Add **Needs Review** propagation when a configured model's provider model ID or mode changes,
      including confirmation before applying the change (schema v20: `needs_review` on `models` and
      `saved_generation_settings`). `UpdateModelAsync` compares the incoming provider model ID/mode
      against the stored values and, when either differs, marks the model **and** every one of its
      *active* saved generation settings **Needs Review** in the same transaction.
      `ModelEdit.razor` detects the same change client-side, looks up the affected active saved
      settings, and requires an explicit **Confirm change** click (listing them by title) before
      calling `UpdateModelAsync` — mirroring the recycle-confirmation pattern used elsewhere rather
      than a bespoke dialog. A model marked **Needs Review** is excluded from `/generate`'s
      selectable model list (a distinct empty-state message appears when every active model needs
      review), which is how "cannot be used for generation until validated" is enforced; a saved
      setting referencing such a model falls through to the existing missing-model fallback warning
      it already had for a recycled/deleted model. Since there is no real settings-schema/capability
      re-validation to perform, "validates" is substituted with a manual **Mark as Reviewed** action
      (`MarkModelReviewedAsync`) that clears the flag on the model and its active saved settings — an
      explicit, documented simplification, not real automated validation. Input-capability and
      settings-schema triggers, and a connection base-URL change cascading **Needs Review** to its
      dependent models, remain open (input-capability declarations don't exist yet, the settings
      schema below is fixed/global rather than per-model-configured so it never itself changes, and
      base-URL-change-triggered dependent scanning doesn't exist yet).
- [x] Add typed provider settings schemas per model/modality and the generated settings-control UI
      described under Connections and Models, including **Use Provider Default** and **Reset to
      Provider Default** semantics. Scoped to the standard OpenAI chat-completion parameters
      (`temperature`, `top_p`, `max_tokens`, `frequency_penalty`, `presence_penalty`), the only
      settings requested for this milestone, and to Text-mode models — both of these params only
      apply to the `chat/completions` shape; the OpenAI-compatible `images/generations` endpoint
      doesn't accept them, so Image-mode models have no settings schema. A new `GenerationSettings`
      record (`Domain/LibraryModels.cs`) holds all 5 as independently nullable fields: null means
      **Use Provider Default** and the field is omitted entirely from the outbound request (never a
      guessed default); clearing a field back to blank in the UI is **Reset to Provider Default**.
      Since both existing `ProviderType`s (`OpenAi`, `GenericOpenAiCompatible`) are OpenAI-shaped and
      share `OpenAiCompatibleProtocol.BuildChatCompletionRequestBody`, the schema is fixed and
      identical for every Text-mode model rather than a per-model-configured concept — there is
      nothing to attach to `Model`/`ModelEdit.razor`, and no new **Needs Review** trigger is needed
      (the schema never changes independently of a code update). `LibraryRules.ValidateGenerationSettings`
      enforces OpenAI's documented ranges server-side (mirroring `NormalizeConnectionTimeoutSeconds`'s
      existing pattern) from every mutation path, not just client-side Razor validation. The 5 fields
      flow as a unit alongside Prompt/ResultCount/SystemInstructions through `GenerateForm`,
      `GenerationDraft`, `SavedGenerationSetting`, `GenerationRecord` and `GenerationJobSnapshot`
      (schema v27: 5 new nullable columns each on `generation_records`, `saved_generation_settings`
      and `generation_drafts`), so a generation's exact settings are preserved in history the same
      way its prompt is. `Generate.razor` exposes them in a collapsed **Generation settings**
      `<details>` section next to the source-image field, gated to Text mode the same way that field
      already is — not physically cleared on a mode switch, only gated at save/request time, matching
      `SourceFileId`'s existing convention.
- [x] Add the manual/advanced-JSON settings editor with reserved-key protection and bounded
      size/nesting for models without a maintained schema. **Scoped out**: every currently-implemented
      `ProviderType` (`OpenAi`, `GenericOpenAiCompatible`) is OpenAI-shaped and always has the
      maintained chat-completion settings schema above, so no model currently lacks one. This bullet
      only becomes relevant if a future, genuinely non-OpenAI-shaped provider type is added.
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

- [x] Add a per-library generation-tab draft model (`GenerationDraft`: automatic/custom title,
      model, prompt, system instructions, source image, result count, destination folder, and
      prompt-improvement model/guidance) persisted in the library database (schema v22,
      `generation_drafts` table, `ILibraryWorkspace.GetDraftsAsync`/`CreateDraftAsync`/
      `ReplaceDraftStateAsync`/`DuplicateDraftAsync`/`DeleteDraftAsync`). There are no source
      roles/order (a draft still has a single optional source image, matching the current
      single-source generation form) and no Session Recovery emergency-snapshot staging; those
      remain separate unchecked items below. `GenerationDraft` deliberately snapshots no
      `ModelLabel` (unlike `SavedGenerationSetting`) since a draft is ephemeral working state, not a
      permanent artifact — so when a draft's stored model becomes unavailable (recycled, permanently
      deleted, or newly marked **Needs Review**), `LoadDraftIntoForm` falls back to the first active
      model and shows a generic **DraftModelUnavailable** notice rather than a specific "model X is
      gone" message with a label it doesn't have. This closes what was a real gap when first
      shipped: the model select previously kept the stale ID with no matching `<option>` and no
      warning, and clicking **Generate** silently did nothing. The prompt-improvement model select
      had the identical gap and is fixed the same way, but silently (falling back to **None** rather
      than showing its own notice) — matching the already-established convention for the source-image
      reference, which has always silently cleared to "no source" rather than warning when its file
      is gone, since it is a secondary field rather than the one that blocks submission.
- [x] Add tab lifecycle on `/generate`: a plain-HTML tab strip with create (**+**), duplicate
      (`DuplicateDraftAsync`, without any run/history association), rename (an editable **Tab
      title** field) and **Reset to automatic title**, and close via an inline confirm/cancel
      panel. **Closing a tab is an instant, permanent discard with no recycle-bin entry and no
      undo** (a deliberate departure from this app's usual recycle/restore safety net, since a
      draft is working form state and **Save settings** already exists as a standing way to keep
      one before closing) — there is no three-way discard/save-as-named-settings/cancel dialog and
      no tab reordering yet; those remain separate unchecked items below.
- [x] Add debounced draft autosave (800 ms, cancel-and-restart on each edit) with
      **Saving**/**Saved**/**Not Saved** status and a **Retry Save** action
      (`GenerationDraftTests`, `LibraryWorkspaceTests.OpeningVersionTwentyOneLibraryAddsGenerationDrafts`).
      The library-switch and in-app navigate-away gates described further below now flush a pending
      autosave rather than losing it in those two cases; true OS-driven application-exit remains
      open for the platform reasons documented there. `Generate.razor`'s original fix here (before
      those gates existed) only *cancelled* any pending debounced autosave timer on disposal, closing
      a real bug where navigating away mid-debounce left the timer running in the background: it
      would still fire ~800 ms later and call `PersistCurrentDraftAsync`, which reads
      `AppLibraryState.Workspace` at that later moment — whichever library happens to be active by
      then, not necessarily the one the edit belonged to — and attempt to update a draft ID that may
      no longer exist there. That cancel-only fix stopped the orphaned write from silently targeting
      the wrong library's database, but still lost the edit itself; the gates below replace it with an
      actual flush.
- [x] Add tab reordering: `ILibraryWorkspace.ReorderDraftsAsync` takes the full ordered list of
      draft IDs (a whole-order-replace, matching `ReplaceDraftStateAsync`'s philosophy rather than a
      granular move-by-index method), validates it contains exactly the current set of drafts, and
      rewrites `tab_order` transactionally. `/generate`'s tab strip exposes this as **‹**/**›**
      move-left/move-right buttons per tab (disabled at the respective end) rather than
      drag-and-drop, which needs no pointer-drag JS interop and works identically with mouse, touch,
      and keyboard activation.
- [x] Add the three-way close dialog: the tab close panel now offers **Discard without saving**
      (the original instant, permanent delete), **Save settings first** (reveals a title field,
      then `CreateSavedSettingAsync` followed by the same permanent `DeleteDraftAsync` — a
      `NameConflictException`/other `SlopFactoryException` from a duplicate title is shown inline
      without closing the tab, so the user can retry with a different title), and **Keep tab open**
      (cancel). There is no settings-title-uniqueness pre-check before showing the confirm panel and
      no undo after **Discard without saving** — both match the already-established instant-discard
      behavior for that path.
- [x] Add the Android compact tab-switcher and a searchable tab-management list shared by both
      platforms. Android now shows a single compact control ("Title (N of M)") instead of the tab
      strip; Windows/tablet keep the existing strip (now actually horizontally scrollable —
      `flex-wrap: nowrap; overflow-x: auto` — rather than wrapping onto new rows) plus a **Manage
      tabs** button. Both open the same `role="dialog"` panel (reusing the existing dialog
      focus-restoration convention) with a live client-side title filter and, per tab, run-status
      (Queued/Generating, via `GenerationQueueService`), move/select/rename/duplicate/close — the
      first switcher-only rename/duplicate flow that operates on a non-active draft directly
      (`CommitSwitcherRenameAsync`/`DuplicateDraftAsync(draftId)`, generalized from the old
      active-tab-only methods) rather than requiring a switch first. **Scope note**: "inactive tabs
      unload their rendered interface" from plan.md was already true — there's exactly one `_form`
      field, rebuilt fresh on every switch, so no code was needed for it. The switcher list uses
      Blazor's `<Virtualize>` (a first-time introduction in this project); the Windows/tablet strip
      itself is deliberately **not** virtualized — `Virtualize` fits a single-axis scrolling list, not
      a wrapping/horizontal flex strip, so virtualizing only the switcher list (the one plan.md
      actually calls "searchable") was chosen over a poor-fit implementation on the strip too.
- [x] Add the library-switch unsaved-edit gate, and the in-app navigate-away gate that was the other
      real (non-OS-driven) way a pending debounced autosave could previously be lost silently.
      `AppLibraryState` gained a `Closing` event (`event Func<Task>?`, awaited across every
      subscriber in registration order via `GetInvocationList()`) raised immediately before
      `SwitchAsync`/`RelinkAsync`/`AdoptCopyAsync`/`CloseInvalidLibraryAsync` replace or null out
      `Workspace` — while the outgoing workspace is still valid and reachable, so a flush against it
      can actually succeed before it's disposed. `Generate.razor` subscribes in `OnInitializedAsync`
      and its handler calls the same `FlushPendingAutosaveAsync` already used for in-page tab
      switches. `Generate.razor` also changed from `IDisposable` to `IAsyncDisposable`, so navigating
      away from `/generate` entirely (a different page in the nav sidebar) now flushes the pending
      autosave the same way instead of merely cancelling the debounce timer and discarding the edit —
      closing what was actually the more common real bug (this path needs no library switch or
      programmatic close at all, just clicking anywhere else within roughly 800 ms of the last
      keystroke). `Closing` is deliberately **not** raised ahead of `CloseUnavailableLibraryAsync`,
      since that path only runs once the workspace's storage is already confirmed unreachable and a
      flush attempt there could only add a doomed I/O wait, never succeed. **True OS-driven
      application-exit remains open and is not attempted here**: MAUI's `Window.Destroying` is a
      plain synchronous event with no cross-platform way to defer/await async cleanup before the
      process actually exits, and Android in particular can kill the process without invoking any
      lifecycle callback at all under memory pressure — a fully reliable exit-time flush is not
      buildable within those platform constraints, only a best-effort one, which is a materially
      different (and not yet made) guarantee.
- [ ] Add emergency draft snapshot staging and reconciliation for an unavailable/read-only library,
      per Session Recovery.

## Generation inputs

- [x] Add a minimal single-page generation form (`/generate`: mode-labelled model select covering
      both Text and Image models, prompt textarea, result count, destination folder) with no tabs,
      drafts, source inputs or prompt improvement yet; those remain separate unchecked items below.
- [x] Add an optional **System Instructions** field, shown only for Text-mode models, sent through
      the documented `system` chat-completion role (OpenAI and generic adapters), persisted on
      `GenerationRecord` and `SavedGenerationSetting` (schema v12), and carried through **Save
      settings** and **Use Again**. There is no CRLF/CR normalization, atomic oversized-edit
      rejection, or adapter-declared-capability gating yet — any text-mode model is currently
      allowed to receive it regardless of documented support.
- [x] Add the 1 MiB well-formed-UTF-8 bound on the prompt, system instructions and
      prompt-improvement raw prompt/guidance (`LibraryRules.ValidateGenerationTextLength`, applied
      in `CreateGenerationRecordAsync`, `CreateSavedSettingAsync`/`UpdateSavedSettingAsync` and
      `CreatePromptImprovementRecordAsync`) — closing a real gap where none of these fields had any
      length validation at all despite the GUI's textareas already declaring `maxlength="1048576"`
      as a client-side backstop. There is no CRLF/CR normalization and no atomic oversized-edit
      rejection UI (an edit that would exceed the bound is rejected server-side with a validation
      error at save time rather than being prevented/reverted interactively as the user types); the
      raw-prompt/system-instructions/result-count generation form redesign itself remains open too.
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
      request/response), now scheduled through `GenerationQueueService` rather than called directly
      from the `/generate` page, with a lightweight `GenerationRecord` (schema v10:
      `generation_records`/`generation_results`) capturing the model/provider snapshot, prompt,
      result count, status and sanitized error. There is no per-child-request tracking, retry beyond
      the bounded model-listing case below, or async job polling yet — the relevant items below
      remain open.
- [ ] Add the generation-history record model (immutable request snapshot, normalized status
      timeline, per-child-request tracking for multi-result generations without a native count
      parameter).
- [x] Add per-connection FIFO queues, a device-wide submission cap with fair round-robin slot
      allocation, scoped to what's real and buildable without fabricating provider behavior: neither
      the OpenAI nor the generic OpenAI-compatible adapter exposes an actual asynchronous
      submit-then-poll job API, so this slice adds queueing/concurrency control on top of today's
      existing synchronous `GenerateTextAsync`/`GenerateImageAsync` calls rather than any async-job
      state machine. `GenerationQueueService` (`src/Mellow.SlopFactory.Gui/Services/`) holds a
      per-connection FIFO (concurrency hardcoded to 1 per connection — true for both real adapters
      today, since neither declares a safe higher bound) and enforces a device-wide cap (a hardcoded
      constant: 3 on Windows, 2 on Android, matching the plan's stated defaults; not yet
      user-adjustable), assigning freed slots by fair round-robin across connections with pending
      work. `/generate`'s **Generate** button now enqueues an immutable snapshot of the draft's
      current form values instead of awaiting the provider call inline; the tab shows **Queued**
      (with position) then **Generating…**, and its **Cancel** action either removes a still-queued
      job before its delegate ever runs (no `GenerationRecord` created, matching the existing
      pre-submission-cancellation contract) or cancels a running one exactly as before. A small
      `MainLayout.razor` indicator shows aggregate queued/running counts with a link back to
      `/generate`. Because the service — not the page — owns execution and the durable commit, a
      submission now survives the user navigating away from `/generate` and back. Switching the
      active library drops every still-queued job and cancels every running one tied to the outgoing
      workspace, since `AppLibraryState` only ever holds one live, disposable workspace at a time — a
      real multi-library background-work model remains a separate, larger milestone. Adjustable
      per-connection concurrency, an adjustable device-cap settings UI, a dedicated **Queue** page
      with reordering, multiple concurrent run cards from the same tab, and OS
      thermal/battery-driven cap reduction all remain open, tracked below.
- [ ] Add adjustable per-connection submission concurrency once an adapter declares a safe range
      above one.
- [x] Add an adjustable device-wide submission cap settings UI (1–8 on Windows, 1–4 on Android).
      `GenerationQueueService.DeviceCap` is now an instance property backed by `IAppPreferenceStore`
      (falling back to the prior hardcoded default — 3 on Windows, 2 on Android — when unset or
      invalid) instead of a hardcoded constant; `SetDeviceCap` clamps to the platform's
      `MinDeviceCap`/`MaxDeviceCap` range, persists it, and immediately re-runs the pump loop so
      raising the cap starts additional already-queued jobs without waiting for a running job to
      finish first. `LibrarySettings.razor` exposes it as a plain number input (mirroring the
      existing preview-cache-limit form) under a new **Generation queue** panel. Lowering the cap
      below the current running count does not stop already-running jobs — it only limits how many
      new ones the pump loop starts, matching the non-preemptive scheduling model used everywhere
      else in the queue.
- [x] Add a dedicated **Queue** page (`/queue`) with visible cross-tab job ordering and reordering
      of waiting jobs: `GenerationQueueService.GetSnapshot()` exposes every non-terminal job across
      every connection (queued with position, or running), and `ReorderQueuedJobs(connectionId,
      orderedJobIds)` — a whole-order-replace validated against the connection's current queued set,
      matching `ReorderDraftsAsync`'s philosophy — rewrites a connection's FIFO order. The page groups
      entries by connection with **‹**/**›** move buttons for queued jobs (disabled at each end,
      mirroring the tab-strip reorder controls) and a **Cancel** action for either phase. The
      `MainLayout.razor` queued/running activity notice now links to `/queue` instead of `/generate`.
      Only waiting jobs on the *same* connection can be reordered relative to each other (matching
      the per-connection FIFO model); there is no cross-connection priority and no reordering of a
      job that has already started.
- [ ] Add multiple concurrent run cards from the same generation tab.
- [x] Add OS battery-driven temporary cap reduction, scoped to energy-saver mode (device thermal
      status has no simple cross-platform MAUI API, so that half remains open below).
      `GenerationQueueService` gains `EffectiveDeviceCap` (the configured `DeviceCap` clamped to 1
      while `IDeviceEnergyStateProvider.IsEnergySaverOn` is true) and `EnergySaverCapActive`; the pump
      loop's cap check now reads `EffectiveDeviceCap` instead of `DeviceCap` directly, so this only
      ever stops new jobs from starting — nothing already running is cancelled, matching plan.md's
      "stops only new starts and never cancels active requests." `IDeviceEnergyStateProvider`
      (Gui/Services, plain testable interface) is backed by `MauiDeviceEnergyStateProvider` wrapping
      `Microsoft.Maui.Devices.Battery.Default.EnergySaverStatus`/`EnergySaverStatusChanged` — no new
      package dependency, since `Microsoft.Maui.Devices` is already a default MAUI global using and
      the Android manifest already registers Essentials' `EnergySaverBroadcastReceiver` automatically.
      `Start()` also subscribes to the provider's `Changed` event and re-runs the pump loop on every
      transition, so a queued job starts immediately the moment energy-saver mode clears rather than
      waiting for the next unrelated queue event — matching "resumes automatically when the constraint
      clears." `/queue` and the `MainLayout.razor` aggregate activity notice both show
      **Energy Saver is limiting submissions to N at a time** while active. **True OS thermal-pressure
      detection remains open** — MAUI has no built-in cross-platform thermal-state API comparable to
      `Battery`, and inventing a platform-specific one (Windows power-throttling APIs, Android
      `PowerManager.getCurrentThermalStatus`) was judged out of scope for this slice.
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

  **This whole async-job group was explicitly reviewed and confirmed deferred this session** (a
  decision, not an oversight): neither the OpenAI nor the generic OpenAI-compatible adapter exposes
  submit-then-poll job semantics — both are synchronous request/response — so there is no remote job
  to poll, persist, pause monitoring on, or reconcile, and no idempotent-retry contract beyond the
  one already implemented (model listing, below). Building any of this now would be speculative
  infrastructure with nothing real to exercise it. Revisit once a genuinely asynchronous provider is
  actually integrated (Milestone 3+).
- [x] Add bounded automatic retry with `Retry-After`/rate-limit honoring and exponential backoff
      with jitter, scoped to the one operation the plan explicitly documents as idempotent and
      safe to retry without provider-confirmed idempotency support: model listing.
      `OpenAiCompatibleProtocol.SendAsync` takes an `allowRetry` flag (both adapters' `ListModelsAsync`
      pass `true`; `GenerateTextAsync`/`GenerateImageAsync` do not, matching the documented rule
      that a generation-submission request must not auto-retry without an idempotency key — which
      does not exist in this application). On a `429` response with retrying enabled, it retries up
      to 3 times, honoring a `Retry-After` header when present (capped at 30 seconds — an
      application safety bound, not a documented provider guarantee) or otherwise bounded
      exponential backoff with jitter, entirely inside the connection's existing single timeout
      budget rather than resetting the clock per attempt. Idempotency-key generation itself remains
      open — this slice is the piece that is safe to add without it, per the plan's own carve-out
      for model listing.

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
- [x] Fix a genuine, always-reproducible bug found while scoping the generation-history recycle-bin
      item below: `generation_results.file_id` had no `ON DELETE` clause on its foreign key to
      `files(id)`, so permanently deleting *any* generation-result file (a completely ordinary
      action, always available from the recycle bin) threw `LibraryValidationException("The library
      database could not finalize permanent deletion.")` every time, unconditionally. Schema v25
      rebuilds `generation_results` with `ON DELETE CASCADE` on `file_id` (SQLite requires a full
      table rebuild to change a foreign key, matching the standard rename/recreate/copy/drop
      procedure); permanently deleting a result file now simply drops that one `generation_results`
      row — the owning `GenerationRecord`/history entry stays intact with one fewer entry in
      `ResultFileIds`, exactly mirroring how `source_file_id`'s existing `ON DELETE SET NULL` already
      clears a reference rather than blocking or cascading further. Verified by a direct regression
      test and a `fromVersion < 25` migration test reproducing the old (pre-fix) schema shape.
- [ ] Add result download, validation (status/content-type/media-category/checksum), atomic
      managed-file commit, and the unverified-binary/unrecognized-content-type retention paths.
- [x] Add text-result formatting: `.md` remains the default, and a per-model **Text result
      format** setting (schema v21: `models.text_format`, `TextResultFormat.Markdown`/`PlainText`)
      lets a Text-mode model commit its results as `.txt`/`text/plain` instead. `GenerationRecord`
      also snapshots the format actually used (`generation_records.text_format`), so history
      reflects "the requested and actual text format" even though there is only ever one format per
      generation in this slice (no separate "requested vs. actual" divergence path exists, since
      there is no structured-output validation to fail). There is no `.json` structured-output
      format — that requires the adapters to support requesting/validating structured JSON output,
      which does not exist — and no streaming incremental display (text is still committed only
      once the full response is received). Selecting a format does not rewrite already-generated
      content, matching the documented rule, since format only ever applies at commit time for a
      new result.
- [x] Add whole-generation **Partially Completed** status: `GenerationStatus.PartiallyCompleted`
      (a third value alongside `Completed`/`Failed`, no schema change needed since the column was
      already a plain `INTEGER`) is computed by `LibraryWorkspace.DetermineGenerationStatus` by
      comparing the number of results actually committed against the originally requested count —
      a provider returning fewer candidates than requested (rather than failing outright) no longer
      reads as a full success. `/generate` and `/generation-history` both show a distinct label and
      an "N of M requested results were committed" detail line. There is no **per-child** status
      (an individual result within one multi-result request has no identity, retry or status of its
      own — the whole request is still one atomic commit), and no transport-archive extraction
      rules (no provider adapter documents or produces an archive-packaged multi-result response);
      both remain open.
- [ ] Add provider safety-response handling: blocked-bytes discard, **Provider Safety Warning**
      concealment/reveal, **Provider Blocked After Delivery**, and the shared classification-event
      model described under Provider Safety Responses.

## Generation history

- [x] Add a minimal generation-history list page (`/generation-history`: model label, status,
      created time, full prompt, result-file links, sanitized error) with no filters, detail view,
      **Use Again**, or recycle-bin integration yet.
- [x] Add client-side status/mode/model filters and a **Clear filters** action to
      `/generation-history`, later extended with provider and from/to date-range filters, covering
      every filter dimension named in the plan (status, date, provider, model, output type). All
      filtering is client-side over the already-loaded list (no server-side query), and filters are
      not persisted across navigation.
- [x] Add a generation-history browsing page separate from the file library, with a dedicated
      detail view: `/generation-history` now shows a concise summary row per record (model, status,
      created time, result count, prompt preview) with **View Details** and **Use Again** actions,
      and a new `/generation-history/{Id}` page shows the full prompt, system-instructions reveal,
      partial-completion detail, error, token usage, prompt-improvement-used note, source-image
      link and result-file links that previously lived inline in the list. The prompt-improvement
      history section on the list page is unchanged (it stays inline, since it is a much shorter
      record shape with no separate detail worth its own page).
- [x] Add a minimal **Use Again** (`/generate/history/{HistoryId}`) that repopulates the `/generate`
      form's prompt, result count, destination folder and model from a historical
      `GenerationRecord` without modifying the record, showing a model-unavailable warning when the
      snapshotted model no longer exists. There is no source/model-incompatibility confirmation
      (no source inputs exist yet) or system-instruction-channel-mismatch handling (no system
      instructions exist yet) — those remain open below.
- [x] **Use Again** now repopulates a *new* generation draft tab from a historical snapshot
      (`/generate/history/{HistoryId}` creates a fresh `GenerationDraft` alongside whatever tabs are
      already open, added as part of the generation-drafts/tabs slice above) rather than replacing
      the page's only form state. There is still no source/model-incompatibility confirmation (only
      one optional source-image slot exists, so there is nothing yet for it to conflict with) or
      system-instruction-channel-mismatch confirmation (it falls through to the existing
      model-unavailable-style warning rather than a dedicated dialog); those remain open below.
- [ ] Add the source/model-incompatibility and system-instruction-channel-mismatch confirmations for
      **Use Again**, once named source-input slots and capability-based validation exist.
- [x] Add generation-history recycle/restore/permanent-delete integrated with the unified recycle
      bin, plus file/source tombstoning. Schema v26 adds `generation_records.state`/`recycled_at`
      (the same shape every other entity already has) and, per plan.md's explicit rules, recycling or
      permanently deleting a generation record touches neither its source nor result files in either
      direction — `generation_results.generation_id`'s existing `ON DELETE CASCADE` only ever removes
      the link rows, never the underlying `files` rows. `RecycleBinItemKind.GenerationRecord` follows
      the exact same plumbing every other kind already established (`GetRecycleBinEntriesAsync`
      query block, `GetRestoreBlockersAsync` case — recycled-state only, since a generation record has
      no uniqueness constraint to conflict on — `ProcessRecycleBinItemsAsync` dispatch,
      `GetRecycleBinRestorePreviewAsync` effects arm); `GenerationHistory.razor`/
      `GenerationHistoryDetail.razor` each gained a **Recycle** action matching every other page's
      "list/detail recycles; the bin restores/permanently-deletes" convention, and
      `GetGenerationHistoryAsync` now filters to active records so a recycled one drops off
      `/generation-history` naturally.

      Tombstoning mirrors the already-existing `file_derivation_provenance` pattern exactly: when a
      source or result file is permanently deleted (`DeleteFileRecordAsync`), its display
      name/media type/content hash are snapshotted onto the referencing `generation_records`/
      `generation_results` row immediately before the file row is removed, in the same transaction.
      This required changing `generation_results.file_id` from the `ON DELETE CASCADE` added just
      earlier this session (schema v25, to stop permanent deletion from throwing) to `ON DELETE SET
      NULL` — the row now survives as a tombstoned placeholder instead of disappearing, which is what
      makes a preserved tombstone possible at all. `GenerationRecord`'s new `SourceFileTombstone`/
      `TombstonedResults` fields are purely additive; `ResultFileIds` keeps its exact original meaning
      (currently-live result files only), so no existing consumer needed any changes — verified via an
      adversarial plan review before implementation, alongside the FK/cascade ordering, which confirmed
      no other issues.

      **Explicitly out of scope**, matching this app's actual architecture (no async-job infrastructure,
      confirmed repeatedly this session): plan.md's **Submission Outcome Unknown** recycling rules,
      **Refresh Provider Status**/**Output Recycled** labeling, and **Reacquire Permanently Deleted
      Output** (which needs a provider-hosted result URL to re-download from — neither adapter has one;
      both return inline base64 content already committed as a local file). **Known, inherited
      limitation, not newly introduced**: `DeleteFolderRecordAsync`'s batch descendant-file delete
      already skipped the `file_derivation_provenance` tombstone update before this slice, and likewise
      skips the two new generation-tombstone updates — only the single-file permanent-delete path is
      tombstoned, matching the scope the provenance feature itself already settled for.
- [x] Add prompt-improvement history records as a distinct lightweight AI-operation entry type
      (schema v19: `prompt_improvement_records`, plus a nullable `generation_records.prompt_improvement_record_id`
      with `ON DELETE SET NULL`). Every submitted **Improve Prompt** attempt on `/generate` —
      success or failure — persists its own record (model snapshot, raw prompt, guidance, template
      version, candidates as JSON, token usage, status/error, timestamps); failed and retried
      attempts each get their own record rather than overwriting one another. Accepting a
      candidate via **Use This Suggestion** remembers which attempt it came from, and the
      resulting `GenerationRecord` is linked to it when the user then generates.
      `/generation-history` shows a separate **Prompt improvement history** section (not merged
      into the main filtered generation list — a real scope reduction from the documented unified
      "Prompt Improvement" operation-type display) and marks a generation record that used an
      accepted suggestion. There is no cost estimation on these records (matching the
      already-deferred cost-estimation gap elsewhere), no source-role capture (no source inputs
      exist for prompt improvement yet), and the accepted-attempt link is not cleared if the user
      edits the prompt further after accepting — it stays linked to whichever attempt was most
      recently accepted.

## Prompt improvement

- [x] Add a minimal optional prompt-improvement flow on `/generate`: pick any active Text-mode
      model as the improvement model (separate from the output model), optional free-text
      guidance, and an **Improve Prompt** action that sends the current prompt plus a built-in
      versioned instruction template (tailored to the output model's mode, delivered through the
      existing `system`-instructions channel) to the improvement model, showing the returned
      candidate(s) for the user to accept into the prompt textarea or discard untouched. Every
      attempt is now persisted as its own `PromptImprovementRecord` (see below) and an accepted
      candidate links the eventual `GenerationRecord` to it — but there is still no raw-vs-improved
      distinction on `SavedGenerationSetting` (saved settings only ever stored the final prompt),
      no raw-prompt-only-by-default disclosure UI, and no **Include Target Model
      Identity**/**Include System Instructions in Improvement**/**Include Compatible Sources**
      opt-ins or **View Instruction** display; those remain open below.
- [x] Add improvement-candidate size bounds: every returned candidate is validated against the same
      1 MiB UTF-8 bound as the raw prompt and guidance (`CreatePromptImprovementRecordAsync`) before
      the attempt is persisted — a provider returning an oversized candidate fails that recorded
      attempt rather than silently storing unbounded text. Multiple candidates are already handled
      (each shown separately, never concatenated, from the initial prompt-improvement slice). There
      is no **Refused**/**Unsupported Response**/**Interrupted** outcome classification — none of
      those are detectable without provider-specific response inspection this application does not
      implement, similar to the deferred Provider Safety Response work — and no **Needs Review**
      invalidation for prompt-improvement candidates specifically (the model/saved-setting **Needs
      Review** propagation added earlier does not extend to prompt-improvement records, which have
      no dependents to invalidate).

## Saved generation settings

- [x] Add a minimal `SavedGenerationSetting` (schema v11: `saved_generation_settings`; title, model
      snapshot, prompt, result count, destination folder) with CRUD through `ILibraryWorkspace`,
      title uniqueness, and recycle/restore/permanent-delete cascading correctly from and to its
      owning model and connection (mirroring the connection→model cascade); `/saved-settings` lists,
      uses, recycles, restores and permanently deletes them.
- [x] Add explicit **Save**/**Save As** actions and revision-conflict detection, replacing the
      original same-title-means-update heuristic. Schema v24 adds
      `saved_generation_settings.revision` (an opaque counter starting at 1, incremented on every
      successful update); `UpdateSavedSettingAsync` takes the tab's loaded `expectedRevision` and
      throws a new `SavedSettingRevisionConflictException` (carrying the current, authoritative
      record) without writing anything when it no longer matches the stored value, rather than
      silently last-write-wins overwriting a change made from another tab opened against the same
      saved setting (this is genuinely reachable in this single-window app, since **Use** already
      supports opening the same saved settings into more than one tab at once). `Generate.razor` now
      shows **Save** (always updates the loaded record in place; disabled when no saved setting is
      loaded) beside **Save As** (always creates a new, separate record) instead of one button that
      guessed the intent from whether the title matched; a conflict shows **Overwrite** (retries the
      update using the just-fetched current revision, so it always succeeds cleanly), **Save As**, or
      **Cancel**, instead of the previous silent overwrite. **Deliberately excludes** plan.md's
      **Review Changes** field-level diff view (Overwrite/Save As/Cancel is offered without first
      showing what changed) and the recycled/permanently-deleted-source special handling (**Save**
      on a since-recycled or since-deleted saved setting still surfaces only the existing generic
      validation-error message, not a dedicated restore-or-Save-As flow) — both remain open below,
      alongside settings-schema/sources/improvement-state capture. Verified by
      `SavedGenerationSettingTests` and the `OpeningVersionTwentyThreeLibraryAddsSavedSettingRevision`
      migration test.
- [x] Add the **Review Changes** field-level diff view offered alongside Overwrite/Save As/Cancel on
      a save conflict. `Generate.razor`'s conflict panel gained a `<details>` disclosure (matching the
      `<details>`/`<summary>` pattern `GenerationHistoryDetail.razor` already uses) that diffs the
      current form/tab values against `SavedSettingRevisionConflictException.Current` — model label,
      prompt, system instructions, source image, destination folder path and result count — showing
      only the fields that actually differ, as a read-only `<dl>` list (no new CSS/table needed; this
      app has no other `<table>` markup, so the existing `<dl>` idiom from `LibrarySettings.razor`'s
      Active Library panel was reused instead) without modifying either version. **Excludes**
      settings-schema/sources/improvement-state capture and the recycled/missing-model-or-source
      dependency-restoration handling, both of which remain open below since neither exists yet for
      this diff to draw on.
- [x] Add dependency-restoration handling for a tab's **source saved settings** (the record it was
      loaded from) becoming recycled or permanently deleted out from under it. `SaveInPlaceAsync` now
      looks the source up (`GetSavedSettingAsync`, catching `RecordNotFoundException`) before
      attempting the update: if it's gone, the tab clears its `_loadedSettingsId`/`_loadedTitle`/
      `_loadedRevision` link (only **Save As** is offered afterward, exactly per plan.md, since the
      normal Save button is already hidden whenever no settings are loaded) and shows a message
      explaining why; if it's merely recycled, a new conflict-style panel (mirroring the existing
      revision-conflict panel's shape) offers **Restore and save** (`RestoreSavedSettingAsync` then
      `UpdateSavedSettingAsync` with the tab's current values, in one action), **Save As**, or
      **Cancel**. This check runs lazily at Save time, the same way revision-conflict detection
      already does — no background polling was added. **Excludes** settings-schema/sources/
      improvement-state capture (still blocked on the typed-provider-settings-schema decision) and
      the separate, still-open dependency-restoration gap for a tab's own **model/source-file**
      references — see the next item, which now covers the **model** half of that gap directly).
- [x] Add dependency-restoration handling for a tab's own **model** becoming recycled or permanently
      deleted (the other half of the gap noted above; source-image is deliberately left as-is —
      milestone2.md already documents that field as an intentionally silent, non-blocking secondary
      field, unlike the model, which is mandatory). `LoadDraftIntoForm` became
      `LoadDraftIntoFormAsync` (a trivial, fully-contained change — every one of its 9 call sites was
      already inside an `async Task` method) so it can call `workspace.GetModelAsync(draft.ModelId)`
      when the model isn't in the already-loaded active list, distinguishing three cases a plain
      `_models.FirstOrDefault` miss couldn't: recycled (`GetModelAsync` returns it with
      `State == Recycled` and its label intact) shows a specific **"model X was moved to the recycle
      bin"** notice with an inline **Restore model** button (`RestoreModelAsync` then a shared
      `RefreshActiveModelsAsync` helper, reused from `OnInitializedAsync` too); permanently deleted
      (`RecordNotFoundException`) leaves `_form.ModelId` empty — a new placeholder `<option>` renders
      so the select visibly shows nothing chosen — and disables **Generate** via
      `string.IsNullOrEmpty(_form.ModelId)` until the user explicitly picks a replacement, exactly per
      plan.md; a model merely excluded for being **Needs Review** keeps the pre-existing generic
      `DraftModelUnavailable` message and auto-fallback, unchanged. `ConnectionModelTests` covers the
      underlying domain sequence directly (recycle → `GetModelAsync` returns it with its label →
      restore → active again; restoring into a label collision throws `NameConflictException`, the
      same as every other restore-conflict class in this app).

## Cost, usage and notifications

- [x] Capture provider-reported prompt/completion token usage from the OpenAI chat-completions
      response (`usage.prompt_tokens`/`usage.completion_tokens`) and persist it on `GenerationRecord`
      (schema v13), shown on the `/generate` result panel and in `/generation-history`. There is no
      cost estimation, no image/other-modality usage capture, and no cost-summary view yet.
- [x] Add a persistent, non-blocking **Cost unknown** notice on `/generate`'s submit action and the
      **Improve Prompt** panel, decided explicitly rather than left as an oversight: no adapter
      exposes a provider cost-estimate API, and this project will not fabricate per-token/per-image
      pricing data to seed a bundled-pricing file. **Excludes** the full estimate/acknowledgement
      machinery plan.md describes (per-model/connection/pricing-revision acknowledgement tracking,
      **Always Confirm Unknown-Cost Requests**, confirmation thresholds, overrun highlighting,
      pricing-revision **Unreliable** marking) — none of it has real data to estimate from, so
      building it now would only be scaffolding around a permanently-`null` value. Revisit if either
      an adapter gains a real estimate API or real, currently-accurate pricing data is supplied.
- [ ] Add a local cost-summary view aggregating provider-reported actual cost, filterable by date,
      provider, connection, model and operation type.
- [x] Add OS generation-completion/failure notifications, disabled by default, toggled from
      `/library-settings`. `Plugin.LocalNotification` (the community-standard MAUI local-notification
      package) was evaluated and rejected: its own README states "Only support **iOS** and **Android**
      for the moment" under Limitations even though its NuGet listing also carries a
      `net10.0-windows10.0.19041` target — a contradiction that couldn't be resolved from its docs, and
      Windows is one of this app's two target platforms. Hand-rolled instead, with **zero new NuGet
      packages**: Windows uses `Microsoft.Windows.AppNotifications`/`.Builder` (Windows App SDK,
      already a transitive dependency of the MAUI Windows target — this project already ships a
      packaged `Package.appxmanifest`), Android uses
      `AndroidX.Core.App.NotificationCompat`/`NotificationManagerCompat` plus a runtime
      `POST_NOTIFICATIONS` permission request (API 33+ only) gated to only fire when the user enables
      the setting. `GenerationQueueService` gained a `JobCompleted` event (fired for every finished job,
      success or failure); `GenerationNotificationCoordinator` gates on the setting, a new
      `IAppLifecycleState` (wired from `Window.Activated`/`Deactivated`/`Resumed`/`Stopped` in
      `App.xaml.cs`) being non-foreground, the outcome having a real `GenerationRecord` (local
      pre-submission failures and cancellations never notify — they never had a record to summarize),
      and the generation-history detail page for that record not already being open; the notification
      body is limited to model label + status, never prompts/filenames/provider error details. Tapping
      a notification navigates to `/generation-history/{id}`. **Excludes** the
      **Submission Outcome Unknown**/**Submission outcome needs attention** alert variant — this app has
      no async-job/reconciliation infrastructure, so that state can't occur.

## Testing infrastructure

- [x] Add adapter unit tests using a fake `HttpMessageHandler` for the OpenAI and generic
      OpenAI-compatible connection-test, model-listing, chat-completion text-generation and
      images/generations paths.
- [ ] Expand the fake HTTP provider into a shared reusable test fixture covering the full Testing
      section requirements (streaming, async jobs, rate limits, moderation, redirects, downloads,
      errors) before Milestone 3 adapters are added.
- [x] Add crash-injection coverage for generation-history's multi-result commit pipeline, mirroring
      the Milestone 1 export crash-injection technique (`CancelledExportLeavesNoDestinationOrPartialFile`)
      of forcing an interruption at a specific boundary and asserting the recovery state is safe
      rather than merely trusting it. `RecordTextGenerationResultCoreAsync`/`RecordImageGenerationResultCoreAsync`
      commit each result file individually (stage → hash → atomic move → DB insert) inside one loop,
      only creating the `GenerationRecord` itself after every result has been attempted — so an
      interruption partway through the loop can leave 0..N-1 already-committed result files with no
      `GenerationRecord` ever created to reference them.
      `PartiallyCommittedTextGenerationLeavesTheEarlierResultFileIntactWithNoOrphanedHistoryRecord`
      forces exactly this deterministically (a second result string containing an unpaired surrogate
      throws `EncoderFallbackException` only after the first result has already committed) and
      verifies the first result file is a perfectly healthy, active, Generated-origin file with no
      dangling `.generating` staging file and no history entry was ever created for the attempt — the
      file itself is not corrupted, it is simply (correctly) not part of any generation-history
      record, which is the safe, already-designed-for outcome rather than a newly discovered bug.
      `CancelledImageGenerationCommitLeavesNoOrphanedStagingFileOrHistoryRecord` covers the same
      pipeline's other real boundary (a pre-cancelled token, mirroring the export test's exact
      technique) for the image path. **Saved generation settings are deliberately excluded**: a
      saved-setting write is a single plain SQL statement/transaction with no staged file, no
      external object, and no multi-step commit sequence of its own to interrupt — the "crash safety"
      question for it is fully answered by SQLite's own transaction durability (already relied on
      everywhere else in this application via `PRAGMA synchronous=FULL`), so there is no
      generation-history-style boundary here worth a dedicated crash-injection test; adding one would
      only re-verify SQLite's own guarantee, not this application's code.

## Final Milestone 2 verification

- [ ] Add automated coverage for every remaining Milestone 2 behavior above, including
      cancellation, partial failure and cross-library isolation cases.
- [ ] Run the full shared test suite, Windows MAUI build, and Android MAUI build with zero errors.
- [ ] Execute a Milestone-2 manual acceptance pass on supported Windows and Android devices and
      record it in `manual_tests.md`.
- [ ] Update `plan.md` by removing only verified completed requirements and keep user/developer
      documentation and `README.md` aligned with the finished behavior.
