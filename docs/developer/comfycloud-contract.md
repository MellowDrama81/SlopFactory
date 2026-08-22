# Comfy Cloud API contract notes

Confirmed 2026-08-18 against `cloud.comfy.org` with a real, user-supplied Comfy Cloud API key and an
explicit user-approved test spend (one minimal image generation). This supersedes every endpoint
assumption made in `Comfy.md`'s original section 9 before this pass — several of those assumptions,
derived from `docs.comfy.org` alone, turned out to be wrong once checked live. Treat this document as
authoritative over `Comfy.md`'s pre-verification text; `Comfy.md` should be updated to match (see its
own note at the top of section 9).

Base URL: `https://cloud.comfy.org`. Authentication: `X-API-Key: <api-key>` header (matches
`Connection.CredentialHeaderName = "X-API-Key"`, `AuthPrefix = ""`, per this app's existing generic
credential mechanism — see `Comfy.md` section 2). **Every real endpoint lives under an `/api/` prefix**
— `https://cloud.comfy.org/system_stats` (no prefix) does not hit the API at all; it silently returns
the ComfyUI web app's SPA shell (HTML, HTTP 200) instead of an error, which is a real footgun for an
adapter that doesn't already know to expect `/api/...` paths.

## Authentication and account status — `GET /api/user`

```json
{"id":"EcyUgBJwS4cnZATlg2tLrDchYbn1","status":"active"}
```

Confirmed live. A good `TestConnectionAsync` candidate: cheap (no generation cost), confirms both that
the key is valid and that the account is active. **Confirmed error shape for an invalid key:**

```
HTTP 401
{"code":"UNAUTHORIZED","message":"Unauthorized"}
```

with a `WWW-Authenticate: Bearer realm="comfy-cloud", X-API-Key realm="comfy-cloud"` response header.
This is a clean, directly-sanitizable error message — no provider-internal detail leaks into it.

## Node/checkpoint catalog — `GET /api/object_info`

Confirmed live: HTTP 200, **9,656,239 bytes** (~9.6 MB) of JSON, 3,642 node type entries. This is the
full ComfyUI node-definition catalog (inputs, types, tooltips, enum values), not a "models" list in
this app's sense — matches `Comfy.md` section 3.1's assessment that there's no fixed, comparable model
catalog to back `ListModelsAsync`. Confirmed the standard SD1.5 node set is present and usable:
`CheckpointLoaderSimple`, `KSampler`, `CLIPTextEncode`, `EmptyLatentImage`, `VAEDecode`, `SaveImage`
all exist with the expected field shapes. `CheckpointLoaderSimple.ckpt_name`'s enum lists 82 installed
checkpoints on this deployment, including `v1-5-pruned-emaonly-fp16.safetensors`,
`flux1-dev-fp8.safetensors`, `flux1-schnell-fp8.safetensors`, `sd3.5_large_fp8_scaled.safetensors`, and
`stable-audio-open-1.0.safetensors` — this list is almost certainly account/deployment-specific
(installed models can differ per user), so don't assume any specific checkpoint name is universally
available. Fetching this endpoint has a real bandwidth/latency cost (~9.6 MB) worth avoiding on every
`TestConnectionAsync` call — use it only when actually needed (e.g. a future "browse available
checkpoints" feature), not as part of routine connection testing.

## Queue status — `GET /api/queue`

```json
{"queue_pending":[],"queue_running":[]}
```

Confirmed live, empty at time of testing. No cost. Matches the documented shape exactly.

## Submit — `POST /api/prompt`

Confirmed live. Request body: `{"prompt": <API-format workflow JSON>, "client_id": "<any string>"}`.
The workflow JSON is exactly the node-ID-keyed `{class_type, inputs}` shape `Comfy.md` section 3.2
already assumed — no surprises here. `client_id` was accepted as an arbitrary string (not required to
be a real UUID; a plain fixed string like `"slopfactory-verify-01"` worked).

Response (immediate, HTTP 200):

```json
{"node_errors":{},"prompt_id":"533a0a01-12bd-4101-b0f2-b849b0d29bfa"}
```

`node_errors` is presumably populated (and non-200 returned, unconfirmed) for a malformed workflow —
not exercised in this pass, since the test workflow was valid on the first attempt.

## Poll status — `GET /api/job/{prompt_id}/status` — **wrong path in original plan, corrected below**

**Correction:** this endpoint does exist and does work exactly as `Comfy.md`'s original table assumed
(`GET /api/job/{prompt_id}/status`, not `/api/history/{id}`), but it returns a **much smaller status
summary than expected** — no output filenames at all:

```json
{"assigned_inference":"10.4.54.213:8080","created_at":"2026-08-18T05:18:38.781177Z","id":"533a0a01-12bd-4101-b0f2-b849b0d29bfa","last_state_update":"2026-08-18T05:18:48.025124Z","status":"success","updated_at":"2026-08-18T05:18:48.025124Z"}
```

Confirmed observed status values: `"preparing"` (in progress) → `"success"` (terminal). The full run
(submit → `"success"`) took **~9 seconds** for this minimal SD1.5 workflow (8 steps, 512×512). Only
these two states were actually observed; the documented failure-state vocabulary
(`error`, `non_retryable_error`, `lost`, `cancelled`, `queued_waiting`, `executing`) was **not**
exercised live in this pass — treat those as documentation-only until a failing job is deliberately
forced and checked.

**This endpoint alone is not enough to retrieve results** — it has no `outputs`/filename field at all.
For that, see the next endpoint.

## Full job details (including output filenames) — `GET /api/jobs/{prompt_id}` — **not in the original docs pass, discovered via a live 404**

Neither `GET /api/job/{id}` (singular, no `/status` suffix) nor `GET /api/history/{id}` — both
plausible guesses based on local-ComfyUI naming conventions — are real endpoints. Both return a
same-shaped `404` whose message **names the correct endpoint directly**:

```json
{"error":{"message":"This endpoint is not available on Comfy Cloud. Use /api/jobs/{job_id} instead.","type":"not_found"}}
```

`GET /api/jobs/{prompt_id}` (**plural** `jobs`) is the real endpoint and is what an adapter should
actually call to retrieve output filenames after `/api/job/{id}/status` reports `"success"`. Confirmed
response (abbreviated):

```json
{
  "id": "533a0a01-12bd-4101-b0f2-b849b0d29bfa",
  "status": "completed",
  "execution_status": {"completed": true, "status_str": "success", "messages": [...]},
  "outputs": {
    "9": {
      "images": [
        {
          "asset_id": "f2aaf2ec-aa09-4be8-a026-3666ae8cebd1",
          "display_name": "slopfactory_test_00001_.png",
          "filename": "14e85f90978f8e496b235e7cc3abbba07aee2ff0aa71e812163e4b51cb6f4f99.png",
          "mime_type": "",
          "size_bytes": 0,
          "subfolder": "",
          "type": "output"
        }
      ]
    },
  "outputs_count": 1,
  "workflow": { "prompt": { ...the submitted workflow, echoed back... } }
}
```

Key/gotchas confirmed live:

- `outputs` is keyed by **node ID** (here `"9"`, the `SaveImage` node's ID in the submitted graph) —
  an adapter needs to iterate `outputs.*.images[]` (or `.videos[]`/`.audio[]`, unconfirmed, presumably
  analogous) rather than assuming a single fixed key.
- **`filename` is a server-assigned opaque hash-named file** (`14e85f9...f99.png`), *not* the
  human-readable `filename_prefix`-based name (`display_name: "slopfactory_test_00001_.png"`) the
  workflow requested. **Use `filename`, not `display_name`, when calling `/api/view`** — the adapter
  must read this field from the response, not construct a filename itself from the submitted
  `filename_prefix`.
- `mime_type` and `size_bytes` were both empty/zero in this response despite a real, correctly-sized
  file existing — **do not trust these fields**; detect the real media type and size from the
  downloaded bytes themselves, the same "never trust a provider's declared format" convention already
  used for image-generation results elsewhere in this app (`MediaTypeDetector.DetectAsync`,
  `docs/developer/architecture.md`'s "Minimal text generation" section).

## Fetch output bytes — `GET /api/view` — **redirects to a third-party host; original plan's assumption here was wrong**

`Comfy.md`'s original section 5 assumed `/view` served bytes same-origin, by analogy with DeepInfra's
`/v1/videos/{id}/content`. **This is wrong, confirmed live — correct the plan.** `GET
/api/view?filename=<filename>&subfolder=<subfolder>&type=<type>` (all three from the `/api/jobs/{id}`
response) returns:

```
HTTP 302 Found
location: https://storage.googleapis.com/comfy-cloud-assets/<filename>?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Credential=...&X-Goog-Date=...&X-Goog-Expires=21599&X-Goog-Signature=...&X-Goog-SignedHeaders=host&response-content-disposition=attachment%3B%20filename%3D%22slopfactory_test_00001_.png%22
```

A signed, time-limited (`X-Goog-Expires=21599`, ~6 hours) Google Cloud Storage URL on
`storage.googleapis.com` — a genuinely different host from `cloud.comfy.org`. **This is the same
third-party-result-URL shape as OpenRouter's video results and 1min.AI's `temporaryUrl`**, not the
safer same-origin-proxy shape DeepInfra's video-content endpoint offers. An adapter following this
redirect needs the same redirect-target-revalidation / DNS-rebinding protections already built for
those cases (`ResultUrlValidator`, the hardened `SocketsHttpHandler` in
`DependencyInjection.cs:38-78`) — **`Comfy.md` section 7's claim that "no custom `SocketsHttpHandler`...
is needed here" is wrong and should be corrected**; Comfy Cloud needs the same handler OpenRouter and
1min.AI already use, registered the same way.

Following the redirect (`curl -L`) confirmed a valid, complete PNG: `Content-Type: image/png`,
`Content-Length: 344405`, byte signature `89 50 4E 47 0D 0A 1A 0A` (the standard PNG magic number) at
the start of the body. The `Content-Disposition` header on the GCS response correctly names the
human-readable `slopfactory_test_00001_.png`, even though the storage key itself is the opaque hash
name — useful for a suggested local filename, but still not something to trust as the actual media
type (see above).

**Live update 2026-08-22:** Cloud's formerly documented singular `GET /api/job/{job_id}/status` route
now responds with a 404 directing callers to `GET /api/jobs/{job_id}`. The plural job-details endpoint
reports lifecycle states including `in_progress` and terminal `completed`; clients should poll that
endpoint and read its outputs from the same response.

**Whether a self-hosted (non-Cloud) ComfyUI instance's `/view` endpoint behaves the same way (redirect
vs. direct bytes) was not tested in this pass** — self-hosted ComfyUI's own documentation describes
`/view` as serving bytes directly, which would make this a genuine behavioral difference between the
two deployment targets `Comfy.md` otherwise treats as protocol-identical. Worth confirming against an
actual self-hosted instance before assuming the adapter can use one code path for both.

## Cost of the verified test call

Not directly observable — `GET /api/user`'s response carries no credit/balance field, and none of
`/api/credits`, `/api/billing`, `/api/usage`, `/api/account`, `/api/subscription` exist (`404` on all
five, confirmed live by direct probe). No credit-balance endpoint was found via guessing in this pass.
The Comfy Cloud web dashboard is the only confirmed way to check actual remaining balance/spend — this
mirrors the same "cost is knowable only from a trailing/lagging snapshot, not a clean per-call figure"
limitation already documented for 1min.AI (`docs/developer/1minai-contract.md`'s Live verification
results), just with even less visibility here (no snapshot field found at all, not even a lagging one).
An adapter cannot report a precise per-generation cost for Comfy Cloud from the API alone — matches
this app's existing "Cost unknown notice" precedent (`docs/developer/architecture.md`'s Token usage
section) rather than needing new machinery.

## Summary: corrections to `Comfy.md`'s pre-verification assumptions

| `Comfy.md` original assumption | Verified reality |
| --- | --- |
| `GET /system_stats` for `TestConnectionAsync` | Wrong path entirely (no `/api` prefix); silently returns the web app's HTML shell rather than erroring. Use `GET /api/user` instead. |
| `GET /history/{prompt_id}` for polling | Wrong path. Two-step: `GET /api/job/{prompt_id}/status` for a cheap status check, `GET /api/jobs/{prompt_id}` (plural) for the actual output filenames. |
| `GET /view` serves bytes same-origin, no `ResultUrlValidator`-class hardening needed | Wrong. `GET /api/view` redirects (302) to a signed `storage.googleapis.com` URL — needs the same third-party-result hardening as OpenRouter/1min.AI. |
| `POST /upload/image` shape | Not exercised in this pass — still open, no test workflow in this verification used an image input. |
| Self-hosted and Cloud share one code path unconditionally | Likely still true for submit/poll, but `/view`'s redirect-vs-direct-bytes behavior specifically was only confirmed for Cloud — flag as unconfirmed for self-hosted rather than assumed identical. |

## Not tested in this pass

- A failing/erroring generation (only a success path was exercised — the documented failure status
  vocabulary for `/api/job/{id}/status` remains unconfirmed).
- `POST /upload/image` / `POST /upload/mask` (image-input workflows).
- Video or audio output workflows (only a single SD1.5 image workflow was run).
- `POST /api/queue` (cancel) and `POST /api/interrupt`.
- The `wss://cloud.comfy.org/ws` WebSocket path.
- Self-hosted (local) ComfyUI's `/view` behavior, for the same-origin-vs-redirect question above.
- Exact concurrency-limit enforcement (`providers.md`'s documented 1/3/5 per-tier figures) — only one
  job was ever in flight during this pass.
