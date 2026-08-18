# ComfyUI provider integration plan

Status: planning only — nothing in this document is implemented. This is a design plan for adding a
`ComfyUi` provider adapter (covering both a self-hosted local instance and the official
`cloud.comfy.org` API) to SlopFactory, written against the current codebase as of the commits ending
in "Checklist Section 9" (see `git log`). Cross-references throughout point at the real files/lines
this plan builds on, so implementation can start from here without re-deriving the architecture.
Update this document as decisions are confirmed or revised; move confirmed, implemented behavior into
`docs/developer/architecture.md` the same way DeepInfra's and 1min.AI's contracts moved from
speculative notes into `docs/developer/deepinfra-audio-video-contract.md` /
`docs/developer/1minai-contract.md` once verified.

See `providers.md`'s "Workflow-engine providers" section for the original research this plan expands
on, including why ComfyUI is structurally different from every provider integrated so far.

**2026-08-18 update: Comfy Cloud has now been live-verified** (real API key, one approved test
generation) — see `docs/developer/comfycloud-contract.md` for the confirmed contract, including a
corrections table against this plan's original (pre-verification) assumptions. Several endpoint paths
and the `/view` same-origin-vs-redirect assumption below were wrong and have been corrected in place;
paragraphs touched by that correction are marked **[corrected 2026-08-18]**.

## 1. Scope decision: one adapter, two deployment targets

Support both **self-hosted ComfyUI** (a user's own local or LAN server, reference `aiohttp` server on
port 8188 by default) and **Comfy Cloud** (`cloud.comfy.org`, Comfy Org's official hosted API) through
a single `ComfyUiProviderAdapter` and a single new `ProviderType.ComfyUi = 5` value, not two separate
adapters. This is possible because Comfy Cloud's API is documented as **compatible with local
ComfyUI's API** — same `/prompt` submit shape, same workflow-JSON format — so the two targets differ
only in base URL and whether a credential is present, both of which are already per-`Connection`
concerns in this app, not per-adapter ones. A user picks which target they're talking to purely by
what `BaseUrl` they enter when creating the connection (`https://cloud.comfy.org` vs. their own
`http://192.168.x.x:8188` or `http://localhost:8188`).

This mirrors how `GenericOpenAiCompatibleProviderAdapter` already treats "the base URL determines
which real service you're hitting" as a `Connection`-level concern rather than a `ProviderType`-level
one (`docs/developer/architecture.md`'s "Connections, models, and provider adapters" section) — the
same shape applies here, just with a fixed pair of expected shapes (self-hosted vs. Comfy Cloud)
instead of an open-ended one.

## 2. What already fits with zero core changes

This turned out to be more than expected once checked against the actual code — worth stating plainly
so implementation doesn't rebuild machinery that already exists:

- **Credential shape already fits Comfy Cloud's `X-API-Key` header exactly.** `Connection` already
  carries `CredentialHeaderName`/`AuthPrefix` as free-form, user-editable fields
  (`ConnectionEdit.razor:88-91`, defaulting to `"Authorization"`/`"Bearer"` for new connections but
  fully overridable), and `OpenAiCompatibleProtocol.ApplyAuthorization`
  (`OpenAiCompatibleProtocol.cs:225-230`) already builds `{CredentialHeaderName}: {AuthPrefix}
  {apiKey}` generically — an empty `AuthPrefix` just omits the space-joined prefix. A Comfy Cloud
  connection sets `CredentialHeaderName = "X-API-Key"`, `AuthPrefix = ""` (empty string), and the
  existing generic mechanism produces exactly the required `X-API-Key: <key>` header. **No new
  credential-shape code is needed** — this just needs the same pattern reused inside the new adapter
  rather than assuming `OpenAiCompatibleProtocol`'s Bearer-token defaults.
- **No-auth self-hosted instances already fit the existing null-apiKey path.** `ApplyAuthorization`
  already no-ops when `apiKey` is null/empty (`OpenAiCompatibleProtocol.cs:227`:
  `if (string.IsNullOrEmpty(apiKey)) return;`), and `IProviderAdapter`'s methods already take `string?
  apiKey` throughout. A self-hosted connection with `HasCredential = false` naturally produces a
  request with no auth header at all — the standard shape for a local ComfyUI instance. No "trust the
  network" special case needs inventing; it's already how an uncredentialed connection behaves today.
- **Base-URL validation already accepts both targets unchanged.** `LibraryRules.NormalizeConnectionBaseUrl`
  (referenced in `docs/developer/architecture.md`'s Connections section) requires HTTPS for public
  hosts and allows HTTP only for loopback/private-network hosts. `https://cloud.comfy.org` and
  `http://localhost:8188` / `http://192.168.x.x:8188` both already satisfy this rule as written — no
  change needed here either.
- **`Model.Mode` already lets a model declare its own output modality.** Because `Model` already
  carries a per-model `GenerationMode Mode` field (`LibraryModels.cs:782`) set explicitly by the user
  when creating the model (not inferred from the provider), the "ComfyUI's output modality is
  workflow-dependent, not provider-fixed" problem flagged in `providers.md` is **already solved at the
  `Model` level** — a user creates one `Model` record per saved ComfyUI workflow and picks its
  `GenerationMode` explicitly, exactly like every other provider's models. What's still missing is
  *where the workflow itself lives* — see section 4.
- **`ConnectionTestResult.SupportsModelDiscovery`** (`LibraryModels.cs:804`) already exists as a
  per-connection false path for providers with no listable model catalog. ComfyUI has no queryable
  fixed model catalog in the sense this app means (see section 3), so `ListModelsAsync` can return
  `SupportsModelDiscovery: false` using an already-supported code path rather than inventing a new
  one — 1min.AI likely already exercises a similar shape given its per-feature model docs pages, worth
  confirming by reading `OneMinAiProviderAdapter.cs` before assuming this is genuinely novel.

## 3. What's genuinely new

Two real architectural gaps remain, matching what `providers.md` already flagged, now scoped concretely:

### 3.1 No fixed model catalog, and "the model" is really "a saved workflow"

There is no `/v1/models`-equivalent endpoint. The closest thing, `GET /object_info`, enumerates
installed node *types* and their parameter enums (e.g. `CheckpointLoaderSimple.ckpt_name`'s allowed
values) for whatever's actually installed on that specific server/deployment — it is not a stable,
comparable-across-installs catalog the way OpenAI's `/v1/models` is. `ListModelsAsync` should not try
to synthesize a fake "model" list from this; it should return an empty result with
`SupportsModelDiscovery: false`, and every ComfyUI `Model` in this app is created manually by pasting
in a workflow (see 3.2), the same manual-entry path already available for connections without
discovery today.

### 3.2 Where the workflow JSON lives, and how a prompt gets into it

A ComfyUI "generation" is really "submit this entire node graph," not "call this model with this
prompt." The unit of configuration this app is missing is the workflow itself. Proposed design,
chosen for being the smallest change that's still honest about ComfyUI's real shape (see section 8 for
the alternative considered and rejected):

- **`Model` gains a new nullable field, `ComfyWorkflowTemplate` (string?)** — populated only when
  `Connection.ProviderType == ProviderType.ComfyUi`, null for every other provider type. This holds
  the **raw API-format workflow JSON** a user exports from the ComfyUI web UI via its "Save (API
  format)" button, with a small set of literal placeholder tokens substituted in place of the values
  that should vary per generation:
  - `{{PROMPT}}` — required in every ComfyUI model's template, placed wherever the workflow's positive
    prompt text node (typically a `CLIPTextEncode.text` input) currently holds a literal string.
  - `{{NEGATIVE_PROMPT}}` — optional; if present in the template, filled from... **open question**,
    see section 9 — this app's `GenerateImageAsync` signature has no separate negative-prompt
    parameter today, so a first pass either leaves this token unsupported (workflows requiring a
    non-empty negative prompt keep the value they were exported with, uneditable per-generation) or
    the interface needs a new optional parameter, which is a wider ripple (every existing adapter's
    signature, `OpenAiCompatibleProtocol`, etc.) — worth a deliberate decision, not a silent addition.
  - `{{SEED}}` — optional; if present, filled with a fresh random seed per generation call (ComfyUI
    workflows almost always fix a seed for reproducibility, and reusing the exported literal seed on
    every call would make every generation from the same model produce the same image).
  - `{{IMAGE_B64}}` — optional; only meaningful when the model's `GetInputSlotCapabilities` declares a
    `ReferenceImage`/`FirstFrame` slot (section 3.3). ComfyUI does not accept inline base64 image data
    inside the workflow JSON itself — an image must first be uploaded via `POST /upload/image`
    (documented ComfyUI endpoint, **not yet independently verified in this repo — see section 9**),
    which returns a server-side filename, and *that filename* is what an `LoadImage` node's `image`
    input expects. So this token's substitution is two-step: upload first, then substitute the
    returned filename string (not raw bytes) into `{{IMAGE_B64}}`'s position — the token name is
    slightly misleading before implementation fixes it to something like `{{UPLOADED_IMAGE_FILENAME}}`.
- **`LibraryRules.ValidateComfyWorkflowTemplate(string json, GenerationMode mode)`** (new): parses the
  string as JSON, requires it to be an object keyed by numeric-string node IDs each holding
  `class_type`/`inputs` (the documented ComfyUI API-format shape), requires `{{PROMPT}}` to appear at
  least once, and applies the same size bound `LibraryRules.ValidateGenerationTextLength` already uses
  for prompts/candidates (1 MiB) — a workflow JSON can legitimately be large (many nodes), but an
  unbounded one is still a mistake to accept silently. Called from every `SqliteLibraryDatabase`
  mutation path that creates/updates a `Model`, mirroring how `ValidateGenerationSettings` is already
  wired into every settings-accepting mutation path rather than only client-side.
- **`ModelEdit.razor`** gains a workflow-JSON `<textarea>` plus a short placeholder-token legend, shown
  only when the owning connection's `ProviderType == ComfyUi` — same conditional-section pattern
  already used for the Text-mode-only system-instructions/source-image/generation-settings fields on
  `/generate`.
- **Schema**: one new nullable `TEXT` column, `models.comfy_workflow_template`, added via plain `ALTER
  TABLE ADD COLUMN` — following the explicit precedent and warning already recorded in
  `docs/developer/architecture.md`'s "Multi-source input slots" section: **do not** rebuild the
  `models` table to add this column; a table rename during rebuild was found this session (per that
  section) to silently corrupt a dependent table's foreign-key reference via SQLite's
  auto-rewrite-on-rename behavior. Plain `ADD COLUMN` avoids that entirely.

This design deliberately does **not** build a node-graph editor, a visual workflow builder, or a
structured node/input-path mapping UI. The user is expected to already be a ComfyUI user who exports a
working workflow from the ComfyUI web UI and hand-edits 1-4 literal string values to placeholder
tokens before pasting it in — the same "you already have a working config, we don't build a config
generator for you" posture recorded in `docs/developer/architecture.md` for `Connection.AdditionalHeaders`
(free-form `Name: Value` lines, no schema-aware header builder) and consistent with what
`providers.md`'s ComfyUI writeup already anticipated: "not a quick win like Mistral or DeepSeek."

### 3.3 Input-slot capability is per-workflow, not per-(provider, mode)

`LibraryRules.GetInputSlotCapabilities(ProviderType, GenerationMode)` (`LibraryRules.cs:212-217`) is
purely a static switch on those two enum values today — it has no way to know that *this specific*
ComfyUI Image-mode model's workflow accepts a reference image (has a `LoadImage` node wired to
`{{IMAGE_B64}}`) while *that* one doesn't (pure text-to-image, no image input node at all).

Two options, and this plan recommends the first for v1:

- **(a) Conservative fixed declaration, no signature change.** Declare
  `GetInputSlotCapabilities(ComfyUi, GenerationMode.Image)` as always
  `[new GenerationInputSlotCapability(ReferenceImage, 0, 1, Required: false)]` — optional, at most one
  reference image, for every ComfyUI Image-mode model regardless of its actual workflow. A workflow
  whose template has no `{{IMAGE_B64}}` token simply never receives the substitution even if the user
  picks a source image in `/generate` (the app resolves the image bytes and calls `UploadAndGetFilename`,
  but if the token isn't present in the template string, the substitution step is a no-op — harmless,
  not an error). This keeps `GetInputSlotCapabilities`'s signature and every existing call site
  unchanged.
- **(b) Per-model capability, signature change.** Extend `GetInputSlotCapabilities` to accept the
  `Model` (or a workflow-derived capability flag stored alongside `ComfyWorkflowTemplate`) so a
  workflow that genuinely has no image node can correctly advertise zero slots rather than an unused
  optional one. More honest, but a substantially bigger change — every call site of
  `GetInputSlotCapabilities` across validation, the `/generate` UI, and saved-settings would need to
  thread a `Model` through where today they only need a `(ProviderType, GenerationMode)` pair.

Recommend **(a)** for the initial implementation — it's a one-line addition to an existing switch
expression, ships a working feature, and its only cost is an inert, ignored slot when a workflow
doesn't use it. Revisit **(b)** only if per-model input-capability precision becomes a real, requested
need beyond ComfyUI (at which point it likely wants a general solution, not a ComfyUI-only one).

## 4. Adapter design: `ComfyUiProviderAdapter`

New class, `src/Mellow.SlopFactory.Infrastructure/Providers/ComfyUiProviderAdapter.cs`, implementing
`IProviderAdapter` directly (not built on `OpenAiCompatibleProtocol` — the wire shape has nothing in
common with `chat/completions`). Mirrors the existing per-provider bespoke-adapter precedent already
set by `OneMinAiProviderAdapter`/`DeepInfraProviderAdapter` rather than trying to force-fit the shared
OpenAI-compatible helper.

| `IProviderAdapter` member | ComfyUI behavior |
| --- | --- |
| `TestConnectionAsync` | **[corrected 2026-08-18]** `GET /api/user` (not `/system_stats` — that path silently returns the web app's HTML shell rather than erroring, since only `/api/...` paths are real endpoints on Comfy Cloud; confirmed live, see `docs/developer/comfycloud-contract.md`), with `ApplyAuthorization`-style header if `apiKey` is present. Confirmed response shape `{"id": "...", "status": "active"}`; confirmed `401 {"code":"UNAUTHORIZED","message":"Unauthorized"}` for a bad key. Returns `SupportsModelDiscovery: false`. Self-hosted ComfyUI has no known equivalent `/api/user` — its `TestConnectionAsync` path still needs its own live check (self-hosted was not covered by this verification pass). |
| `ListModelsAsync` | Returns an empty list, `SupportsModelDiscovery: false` (section 3.1). Models are always created manually. `GET /api/object_info` (confirmed live, ~9.6 MB, 3,642 node types) exists and could back a future "browse installed checkpoints" feature, but is too large/slow to call from `TestConnectionAsync` or routine model listing. |
| `GenerateTextAsync` | Throws `ProviderAdapterException` — no verified general-purpose chat/LLM API exists on ComfyUI's own server surface (the interface's existing doc-comment convention for "provider adapter with no verified [X] generation API," already used for `GenerateAudioAsync`, applies equally here). Community LLM nodes exist but are not a "verified" capability in this app's sense, and are out of scope for this plan. |
| `GenerateImageAsync` | See section 5 — the main new work. |
| `GenerateAudioAsync` | Throws `ProviderAdapterException` for the initial implementation (section 6 — deferred, not architecturally blocked). |
| `SubmitVideoGenerationAsync` / `PollVideoGenerationAsync` | See section 6 — deferred to a follow-up pass once the Image path is proven, but noted as the *cleanest* architectural fit of all four surfaces. |

## 5. `GenerateImageAsync`: internal poll loop, no interface change

**[corrected 2026-08-18 — endpoint paths below were live-verified against Comfy Cloud and differ from
the original docs-only draft; see `docs/developer/comfycloud-contract.md` for full detail.]**

ComfyUI's submit call, `POST /api/prompt`, returns immediately with a `prompt_id` — the actual
generation happens server-side and must be discovered by polling (or a `/ws` WebSocket, not used
here — see section 8) until the job completes. This does **not** match `GenerateImageAsync`'s
synchronous, single-call signature
(`Task<IReadOnlyList<byte[]>> GenerateImageAsync(...)`, `IProviderAdapter.cs:18`) the way OpenAI's
`images/generations` does (one request, one response, done).

Proposed resolution: the adapter itself polls internally, in a loop, inside its own
`GenerateImageAsync` implementation, and does not return to the caller until the job reaches a
terminal state or the connection's own timeout elapses. This is not a new mechanism — it's the same
"a single `IProviderAdapter` call may legitimately take a long time" behavior already accounted for by
`Connection.TimeoutSeconds`/`OpenAiCompatibleProtocol.SendAsync`'s timeout handling
(`docs/developer/architecture.md`'s "Connection timeout override" section) and already proven to work
for a comparably long single-call block by 1min.AI's confirmed 75-second synchronous video call
(`docs/developer/1minai-contract.md`'s Live verification results). The difference is mechanical (a
poll loop instead of one long-held HTTP connection) but the caller-visible contract — "this call can
legitimately take a while, respect `CancellationToken` and the configured timeout" — is identical to
what this app already handles correctly today.

Concretely:

1. Resolve the model's `ComfyWorkflowTemplate`, substitute `{{PROMPT}}` (and `{{SEED}}` if present,
   and the uploaded-image filename into `{{IMAGE_B64}}`'s token if a source image was supplied and the
   token is present — no-op otherwise, per section 3.3(a)).
2. `POST /api/prompt` with `{"prompt": <substituted workflow JSON>, "client_id": <a generated GUID>}`
   — confirmed live, matches the originally assumed request envelope exactly. Read the returned
   `prompt_id` from `{"node_errors":{},"prompt_id":"..."}`.
3. Loop: `GET /api/job/{prompt_id}/status` on a short interval (candidate: 2 seconds — the one live
   test observed a full ~9-second run for a minimal 8-step SD1.5 job, `"preparing"` → `"success"`, so
   2 seconds is a reasonable starting point but not derived from any documented rate limit; **still
   needs a considered decision at real scale**, see section 9), honoring `cancellationToken` and the
   connection's configured timeout exactly as `OpenAiCompatibleProtocol.SendAsync` already does for
   every other adapter. This status endpoint is cheap but returns no output data — only `status`.
4. On `status: "success"`: **a second call, `GET /api/jobs/{prompt_id}` (plural — confirmed live; the
   singular `/api/job/{id}` and a guessed `/api/history/{id}` both 404 with a message naming the
   correct plural path)**, whose `outputs` field (keyed by node ID) carries the real per-result
   `filename`/`subfolder`/`type`. **Use the `filename` field, not `display_name`** — `filename` is the
   real, opaque server-storage name `/api/view` expects; `display_name` is only the
   human-readable `filename_prefix`-based name and will 404 if used instead (confirmed live: the two
   differed, e.g. `filename: "14e85f9...f99.png"` vs. `display_name: "slopfactory_test_00001_.png"`).
   Then fetch bytes via `GET /api/view?filename=...&subfolder=...&type=...`. **This is a 302 redirect
   to a signed, time-limited `storage.googleapis.com` URL, not same-origin bytes** — confirmed live,
   correcting this plan's original assumption. It needs the *same* redirect-target-revalidation /
   DNS-rebinding hardening OpenRouter's and 1min.AI's result downloads already use
   (`ResultUrlValidator`, the hardened `SocketsHttpHandler` in `DependencyInjection.cs:38-78`), not the
   "no hardening needed" treatment originally proposed — see section 7's corrected registration
   snippet. Don't trust `mime_type`/`size_bytes` from the `/api/jobs/{id}` response either (both were
   empty/zero on a real, correctly-sized file in the live test) — detect the real media type from
   downloaded bytes, per this app's existing `MediaTypeDetector.DetectAsync` convention.
5. On a status other than `"preparing"`/`"success"` (the documented failure vocabulary — `error`,
   `non_retryable_error`, `lost`, `cancelled`, `queued_waiting`, `executing` — was **not** exercised
   live in this pass, only the two states above were actually observed; see
   `docs/developer/comfycloud-contract.md`): throw `ProviderAdapterException` with the sanitized error
   detail, following the same "surface a sanitized provider error rather than a raw exception"
   convention every other adapter already follows.
6. `resultCount > 1` (this app's existing "how many candidates to generate" parameter): submit
   `resultCount` independent `/api/prompt` jobs (mirroring the video interface's own doc comment: "never
   more than one per call — a caller wanting several results submits several independent jobs",
   `IProviderAdapter.cs:24-25`), most likely sequentially rather than concurrently for a first pass to
   keep load on a (possibly modest, self-hosted) GPU predictable — a concurrency decision worth
   revisiting once real hardware/latency numbers exist.

## 6. Modality rollout order

- **Phase 1 (this plan's actual scope): Image only.** Highest-value ComfyUI use case in practice
  (Stable Diffusion/Flux/SDXL-class workflows are what most people mean by "a ComfyUI workflow"), and
  the internal-poll-loop design in section 5 requires no interface or async-job-registry changes at
  all — it's contained entirely inside the new adapter class plus the `Model`/`LibraryRules` additions
  in section 3.
- **Phase 2 (natural follow-up, not this plan's scope): Video.** Architecturally the *cleanest* fit of
  all four surfaces — `SubmitVideoGenerationAsync`/`PollVideoGenerationAsync` already expect exactly
  the submit-then-poll shape ComfyUI's `/api/prompt`+`/api/job/{id}/status`+`/api/jobs/{id}` sequence
  naturally provides, with zero need for
  the internal-poll-loop workaround Image needs, and it plugs directly into this app's existing
  async-job registry/UI (`Refresh Provider Status`, `Import Missing Results`) with no new UI work.
  Practically deferred to a second pass purely because it depends on the same
  `ComfyWorkflowTemplate`/placeholder-token groundwork Phase 1 builds first, and because ComfyUI video
  workflows (AnimateDiff/SVD/Wan-class nodes) are a narrower, more specialized use case than image
  generation.
- **Phase 3 (later, not this plan's scope): Audio.** Same internal-poll-loop shape as Image once that
  pattern is proven; genuinely deferred only for sequencing, not blocked by anything architectural.
- **Out of scope for any phase covered by this plan: Text and 3D.** Text per section 4's table (no
  verified general chat API). 3D per `providers.md`'s "Modalities beyond Text/Image/Audio/Video"
  section — ComfyUI does have community 3D nodes (Hunyuan3D, TripoSR), but a 3D *output* doesn't fit
  any of this app's four existing `IProviderAdapter` surfaces at all (none of them return a mesh/asset
  file type), so supporting it would mean the same new-surface work `providers.md` already flagged as
  needed for dedicated 3D providers like Meshy/Tripo — a separate, larger plan, not an incremental
  addition to this one.

## 7. Registration

`DependencyInjection.AddSlopFactoryInfrastructure` (`DependencyInjection.cs:11-36`) gains one more
block matching the existing pattern exactly:

```csharp
services.AddHttpClient<ComfyUiProviderAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(DependencyInjection.CreateOpenRouterHttpHandler);
services.AddTransient<IProviderAdapter>(sp => sp.GetRequiredService<ComfyUiProviderAdapter>());
```

**[corrected 2026-08-18]** The original draft claimed no `SocketsHttpHandler`/DNS-rebinding hardening
would be needed here, on the assumption that ComfyUI/Comfy Cloud serve result bytes same-origin. **That
assumption was wrong, confirmed live** (`docs/developer/comfycloud-contract.md`): `GET /api/view`
redirects to a signed `storage.googleapis.com` URL, the same third-party-result-host shape OpenRouter
and 1min.AI already have hardening for. `ComfyUiProviderAdapter`'s `HttpClient` registration needs the
same `CreateOpenRouterHttpHandler`-based `SocketsHttpHandler` those two already use
(`DependencyInjection.cs:20-28,42-78`), not a bare `AddHttpClient` call. Whether self-hosted ComfyUI's
`/view` needs the same treatment (same-origin vs. its own redirect behavior) is unconfirmed — the
handler can safely apply to both deployment targets regardless, since it only restricts which network
addresses a redirect target may resolve to and has no effect when no redirect occurs.

`ProviderType.ComfyUi = 5` appended to the enum (`LibraryModels.cs:694-701`) — a pure addition, no
renumbering, consistent with the existing values' numeric stability (the same "numeric codes are
frozen because they're persisted" discipline already documented for `GenerationStatus`,
`docs/developer/architecture.md`'s "Partially Completed generation status" section, applies equally to
any enum backing a persisted integer column, and `ProviderType` is exactly that kind of column).

## 8. Alternative considered and rejected: a structured node/input-path mapping UI

Instead of the placeholder-token string-substitution design in section 3.2, a more "principled" design
would parse the submitted workflow JSON, let the user pick — from a dropdown built from the actual
node graph — which node ID and input key holds the prompt/seed/image, store that as structured
`(NodeId, InputKey)` pairs rather than string tokens, and substitute by JSON-path rather than
string-replace.

Rejected for this plan's scope: it requires building a genuine (if small) node-graph inspection UI,
duplicates ComfyUI's own web UI in miniature, and doesn't remove any of the real risk the simpler
design already accepts (a malformed/incompatible workflow still fails at generation time either way —
structured editing only makes *authoring* a valid workflow marginally more guided, at a much higher
implementation cost). The placeholder-token design is strictly simpler, ships an equally functional v1,
and is easy to *later* layer a structured picker on top of if that guidance genuinely turns out to be
worth the cost — the reverse migration (structured back to tokens) would be much more painful, so this
is not a decision that forecloses the richer option later.

## 9. Open questions requiring live verification before implementation

**2026-08-18 update: items 1-2 below are now resolved for Comfy Cloud** (real API key, one approved
test generation, full submit→poll→download path exercised end to end) — see
`docs/developer/comfycloud-contract.md` for the confirmed contract and its corrections table against
this plan's original assumptions. They're left in place below, struck through, so the "what we assumed
vs. what was actually true" gap stays visible rather than silently disappearing. Items 3-6 remain
genuinely open. Following this repo's own established convention
(`docs/developer/1minai-contract.md`, `docs/developer/deepinfra-audio-video-contract.md`) of never
trusting third-party docs alone before an adapter ships:

1. ~~**Comfy Cloud's exact endpoint set.**~~ **Resolved for Comfy Cloud, live-verified 2026-08-18.** The
   original guesses (`/prompt`, `/history/{id}`, `/view`, `/system_stats`, all without an `/api/`
   prefix) were wrong — real paths are `/api/prompt`, `/api/job/{id}/status` (status only),
   `/api/jobs/{id}` (plural — output filenames), `/api/view` (redirects to a third-party GCS URL), and
   `/api/user` (not `/system_stats`). See `docs/developer/comfycloud-contract.md`. **Still open:**
   whether self-hosted (non-Cloud) ComfyUI uses the same `/api/`-prefixed paths or the unprefixed ones
   its own docs describe — not tested in this pass, and plausibly genuinely different between the two
   deployment targets given how much else this pass got wrong on that assumption.
2. ~~**The exact shape of a `/history/{id}` entry.**~~ **Partially resolved for Comfy Cloud.** The
   success shape is confirmed (`docs/developer/comfycloud-contract.md`'s `/api/jobs/{id}` section).
   **Still open:** the failure shape — only a success path was exercised live; the documented failure
   status vocabulary (`error`, `non_retryable_error`, `lost`, `cancelled`, `queued_waiting`,
   `executing`) remains unconfirmed, so `ProviderAdapterException`'s error-message extraction (section
   5 step 5) is still working from documentation only for the failure case specifically.
3. **`POST /api/upload/image`'s exact request/response shape** — multipart form fields, returned
   filename format, and whether `subfolder`/`type` need to be echoed back exactly on the later
   `LoadImage` node reference. **Still fully open** — the live pass used a pure text-to-image workflow
   with no image input, so this endpoint was never exercised.
4. **Whether Comfy Cloud enforces the per-tier concurrency caps (`providers.md`: 1/3/5 concurrent runs
   for Standard/Creator/Pro) via a queueing 429/error response** that this app's error handling should
   specifically recognize and surface distinctly (similar to how DeepInfra's "model doesn't support
   async jobs" rejection got its own documented, distinctly-handled error shape in
   `docs/developer/deepinfra-audio-video-contract.md`'s "Not every model supports the async job API"
   section) rather than a generic provider-error message. **Still open** — only one job was ever in
   flight during the live pass, so concurrency limits were never actually hit.
5. **A reasonable poll interval** for step 3 in section 5 — chosen provisionally at 2 seconds. The one
   live data point (a minimal 8-step SD1.5 512×512 job completed in ~9 seconds end to end) doesn't move
   this much beyond "not too aggressive" — a single fast, cheap workflow isn't representative of the
   distribution of real workflow costs (larger models, more steps, video). **Still needs a considered
   decision at real scale**, not just this one sample.
6. **The negative-prompt parameter question from section 3.2** — whether to extend
   `GenerateImageAsync`'s signature (a ripple through every adapter) or accept the limitation that a
   ComfyUI model's negative prompt is fixed at workflow-authoring time for v1. This is a product
   decision as much as a technical one and should be made deliberately, not resolved implicitly by
   whichever is easiest to code first. **Unaffected by this verification pass** — still open.

## 10. Suggested implementation checklist

Mirroring `IMPLEMENTATION_COMPLETION_CHECKLIST.md`'s convention of only marking an item complete after
implementation, verification and documentation are all done:

- [x] Live-verify Comfy Cloud's core endpoint set (submit/poll/output-fetch path) — done 2026-08-18,
      see `docs/developer/comfycloud-contract.md`.
- [ ] Live-verify the remaining open items in section 9 (self-hosted instance entirely; Comfy Cloud's
      failure-status vocabulary, `/api/upload/image`, concurrency-limit enforcement, and a real-scale
      poll-interval decision) — extend `docs/developer/comfycloud-contract.md` and add a
      `docs/developer/comfyui-selfhosted-contract.md` (or fold self-hosted findings into the same file
      if the two targets turn out to share enough) once verified.
- [ ] Add `ProviderType.ComfyUi` (section 7) and confirm no existing exhaustive switch over
      `ProviderType` breaks without a default arm (grep for every `switch`/pattern-match on
      `ProviderType` before assuming this is purely additive).
- [ ] Add `Model.ComfyWorkflowTemplate` (schema: plain `ADD COLUMN`, section 3.2) and
      `LibraryRules.ValidateComfyWorkflowTemplate`, wired into every `Model`-mutation path.
- [ ] Extend `LibraryRules.GetInputSlotCapabilities` per section 3.3 option (a).
- [ ] Implement `ComfyUiProviderAdapter` (section 4-5): `TestConnectionAsync`, `ListModelsAsync`
      (discovery-disabled stub), `GenerateImageAsync` (poll loop), `GenerateTextAsync`/
      `GenerateAudioAsync`/video methods throwing `ProviderAdapterException` for this phase.
- [ ] Register in `DependencyInjection.AddSlopFactoryInfrastructure` (section 7).
- [ ] `ModelEdit.razor`: workflow-JSON textarea + placeholder-token legend, gated on
      `ProviderType.ComfyUi`.
- [ ] `ConnectionEdit.razor`: confirm the existing `CredentialHeaderName`/`AuthPrefix`/`BaseUrl` fields
      need no ComfyUI-specific changes (per section 2, they shouldn't) — verify by actually creating a
      Comfy Cloud connection end to end, not just by reading the code.
- [ ] Tests: adapter unit tests against a fake HTTP handler (matching the existing per-adapter test
      pattern used for OneMinAi/DeepInfra), `ValidateComfyWorkflowTemplate` domain tests, and a
      real-account integration pass before shipping (matching the explicit-budget live-verification
      precedent both existing contract docs already established).
- [ ] Update `docs/developer/architecture.md`'s "Connections, models, and provider adapters" section
      once implemented and verified, and remove/shrink this plan file's speculative sections
      accordingly (or archive it, matching how `milestone1.md`-`milestone4.md` are kept as history
      rather than deleted once superseded by `IMPLEMENTATION_COMPLETION_CHECKLIST.md`).
- [ ] Phase 2/3 (Video, Audio — section 6): separate future passes, not part of this checklist's
      completion criteria.
