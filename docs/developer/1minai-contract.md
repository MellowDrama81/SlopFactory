# 1min.AI API contract notes

Confirmed 2026-08-17 against 1min.AI's published documentation (<https://docs.1min.ai/docs/api/intro>)
**and** live API calls made with an explicit user-approved budget (well under the 100,000-credit
ceiling given — see "Live verification results" below for what was actually spent). This supersedes
the "docs site could not be fetched at all" status previously recorded in `docs/developer/architecture.md`
— the documentation exists, is fetchable, and one representative model per modality
(chat, image, audio, video) has now been exercised end to end.

**Caveat that still applies:** only one model was live-tested per modality. The other 40 image models
and 9 other video models each have their own `promptObject` field set on their own docs page, unverified
by a live call. The stale-model-identifier problem found below (see Live verification results) means
none of those unverified per-model docs pages should be trusted at face value either — each one needs
its own live check before the adapter special-cases it, the same way Flux Schnell's documented
identifier turned out to be wrong.

Base URL: `https://api.1min.ai`. Authentication: `API-KEY: <api-key>` header (not `Authorization:
Bearer`) plus `Content-Type: application/json`.

## Chat — `POST /api/chat-with-ai`

Chat requests use its unified chat API.

- Streaming variant: `POST /api/chat-with-ai?isStreaming=true` (SSE: `content`, `result`, `done`,
  `error` events, `event: <type>\ndata: <JSON>` format).
- Request body:
  ```json
  {
    "type": "UNIFY_CHAT_WITH_AI",
    "model": "gpt-4o-mini",
    "promptObject": {
      "prompt": "Explain quantum computing in simple terms"
    }
  }
  ```
  `promptObject` also accepts `conversationId` (multi-turn), `settings` (`webSearchSettings`,
  `historySettings`, `withMemories`), and `attachments` (`images`, `files` — image/file references
  for multimodal input). **Now confirmed and wired to this app's own Text-mode `ReferenceImage`
  source-file slots** — see "Image-conditioned chat" below; this note previously said the opposite
  before that was live-verified.
- Non-streaming response:
  ```json
  {
    "aiRecord": {
      "uuid": "string",
      "userId": "string",
      "teamId": "string",
      "model": "string",
      "type": "UNIFY_CHAT_WITH_AI",
      "status": "SUCCESS",
      "createdAt": "ISO 8601 timestamp",
      "aiRecordDetail": {
        "promptObject": {"prompt": "string", "linkContentList": [], "searchContentList": []},
        "resultObject": ["string"]
      },
      "modelDetail": {"name": "string", "provider": "string"}
    }
  }
  ```
  The generated text is `aiRecordDetail.resultObject[0]` (an array of strings — unclear from docs
  whether more than one element is ever populated for a non-multi-candidate request).
- Error response: `{"success": false, "error": {"code": "ERROR_CODE", "message": "Description"}}`.

## Image-conditioned chat — `type: "CHAT_WITH_IMAGE"` and the Asset API

Confirmed live 2026-08-19. Two-step flow, both steps required:

1. **`POST /api/assets`** — upload the image first. `multipart/form-data` with a single field named
   `asset` (the file), same `API-KEY` auth header as every other request. Response:
   ```json
   {
     "asset": {"fieldname": "asset", "originalname": "...", "mimetype": "image/jpeg", "size": 45228,
       "bucket": "asset.1min.ai", "key": "images/...", "location": "https://s3.us-east-1.amazonaws.com/...", "metadata": {}},
     "fileContent": {"uuid": "...", "path": "images/2026_08_19_05_27_59_899_....jpg", "type": "jpg",
       "name": "...", "status": "ACTIVE", "createdAt": "..."}
   }
   ```
   `fileContent.path` is the value used in step 2 — a storage-relative path, not a public URL.
2. **`POST /api/chat-with-ai`** with `"type": "CHAT_WITH_IMAGE"` (not `UNIFY_CHAT_WITH_AI`) and the
   uploaded path(s) listed under `promptObject.attachments.images`:
   ```json
   {
     "type": "CHAT_WITH_IMAGE",
     "model": "gpt-4o-mini",
     "promptObject": {
       "prompt": "Describe exactly what is in this image in detail...",
       "attachments": {"images": ["images/2026_08_19_05_27_59_899_....jpg"], "files": []}
     }
   }
   ```
   Response envelope is identical to plain chat's. Verified genuinely correct with **two** different
   images in the same request (an illustrated cartoon vs. a real photo) — the model accurately
   described and distinguished both, and `metadata.inputCredit` scaled with image count (roughly 25x a
   text-only call per image, consistent with real image-token billing, not a flat per-request fee). No
   documented maximum image count; two is the highest actually tested.

**Two other candidate fields were tried and confirmed not to work — do not use them:**

- A bare `imageList: ["<path>"]` field (found mentioned in some third-party/community documentation)
  sent to **`POST /api/features`** with `type: "CHAT_WITH_IMAGE"` is rejected outright:
  `HTTP 400 {"errorCode":"UNKNOWN_ERROR","message":"Unsupported feature type: CHAT_WITH_IMAGE"}` —
  that type simply isn't valid on the Features endpoint.
- The same `imageList` field sent to **`POST /api/chat-with-ai`** (the correct endpoint) is accepted
  with `HTTP 200` and echoed back in the response's `promptObject.imageList`, but is **silently
  ignored** — the model's own reply states "I'm unable to see images directly," and the billed
  `inputCredit` matches a plain text-only request. This is a genuine silent-failure trap: no error,
  no indication anything is wrong, just a response that quietly never saw the image. Confirmed by
  contrast against the same request using `attachments.images` instead, which billed ~25x more input
  credit and produced an accurate image description.

There is also a separate, unrelated `IMAGE_VARIATOR` feature type (`POST /api/features`, model
`"dzine"`) documented with a single `imageUrl` field (also an Asset API path) for style-transfer image
editing — confirmed only from docs, not live-tested, single-image only per its own docs, and
structurally unrelated to `CHAT_WITH_IMAGE`. Do not confuse the two.

**Image generation reference-image support was also tested and found non-functional — across every
field name tried, including the one confirmed to work for chat.** Sending `imageUrls` (array),
`imageUrl` (singular), and `attachments.images` (the exact field confirmed working for
`CHAT_WITH_IMAGE` above) inside `promptObject` for `type: "IMAGE_GENERATOR"` with
`black-forest-labs/flux-2-klein-4b` are all accepted with `HTTP 200` and no error, but every one has
**zero effect on the generated image** — confirmed across five live tests (two image orderings with
two images, single-image tests with the plural and singular field names, and a fifth test reusing the
working `attachments.images` shape verbatim), every one producing output derived purely from the text
prompt with no trace of the uploaded image's actual content. This means `CHAT_WITH_IMAGE` (vision/chat)
and `IMAGE_GENERATOR` (image generation) are separate pipelines on 1min.ai's backend — whatever wiring
makes `attachments.images` reach the vision model does not extend to the image-generation model, so
this isn't a field-naming problem to keep guessing at. `imageUrls` (plural) is real elsewhere on this
API — it is the confirmed field for Pika's `IMAGE_TO_VIDEO` `pikascenes` mode (multiple images composed
into a *video*), which is likely why it appears associated with 1min.ai in general searches — but it
does not carry over to image generation either. No 1min.ai image-generation model is currently declared
as accepting a `ReferenceImage`
source slot in `LibraryRules.GetInputSlotCapabilities` because of this.

**A real `IMAGE_EDITOR` feature does exist, confirmed via a third-party reverse-engineered client —
but it lives on a different, inaccessible surface, not the public developer API.**
[cyber-wojtek/1MinAI-API](https://github.com/cyber-wojtek/1MinAI-API), a Python wrapper for 1min.ai's
actual web app (not the documented public API), implements genuine image editing/compositing —
`IMAGE_EDITOR` (Flux Kontext, Qwen Image Edit, Klein), `FACE_SWAPPER` (two-image, `sourceImageList`/
`targetImageList`), `IMAGE_VARIATOR`, `IMAGE_OBJECT_REMOVER`, `BACKGROUND_REPLACER`,
`SKETCH_TO_IMAGE`, `IMAGE_3D_GENERATOR` — all keyed on a `promptObject.imageList` array, matching the
chat contract's own field naming more closely than anything documented for `/api/features`. Confirmed
live (2026-08-19) that this genuinely is a different, inaccessible surface, not just a different field
name to plug into what we already have:

- These calls target a **team-scoped endpoint**, `POST https://api.1min.ai/teams/{team_id}/features`
  (not the plain `POST /api/features` this adapter uses), authenticated with
  `X-Auth-Token: Bearer <token>` — **not** the `API-KEY` header every confirmed-working endpoint in
  this document uses.
- Sending our real `API-KEY` value in that `X-Auth-Token` header returns
  `HTTP 401 {"errorCode":"INVALID_AUTH_TOKEN",...}` — the endpoint expects a genuine logged-in web
  session JWT (obtained via email/password or Google OAuth login through the client's `oauth_login()`
  method), not a developer API key. An API key and a session token are different credential types on
  1min.ai's backend, and only the latter can reach this endpoint.
- For completeness, sending `type: "IMAGE_EDITOR"` to the **public**, `API-KEY`-authed
  `POST /api/features` endpoint (in case the feature type is dispatched the same way regardless of
  route) returned `HTTP 522` (Cloudflare origin timeout) on two separate attempts, ~19 seconds each —
  not a working response, and not a clean rejection either. Left unresolved rather than retried
  further, since each attempt risks real cost/time for an inconclusive result.

**Deliberately not pursued further.** Reaching `IMAGE_EDITOR` would require this adapter to obtain and
maintain a logged-in web-session token (email/password or OAuth login, plus token refresh) rather than
using the single static API key this app's `Connection` credential model is built around for every
provider — a materially different, more sensitive trust relationship (impersonating a user's actual
account session, not calling a published developer API with a scoped key), and likely outside the
terms 1min.ai intends for third-party API-key access. If this is ever revisited, it needs an explicit
product decision about that credential-model change, not just a request-shape fix.

## AI Feature API — `POST /api/features` (image, audio, video, writing, code)

Image, audio and video generation use its AI Feature API with feature-specific
request parameters. One unified endpoint and response envelope for every feature type; the
`type`/`model`/`promptObject` fields vary per feature.

- Streaming variant: `POST /api/features?isStreaming=true` (for feature types that support it).
- Request shape: `{"type": "<FEATURE_TYPE>", "model": "<model id>", "promptObject": {...}, "async":
  false}`.
- **Sync vs async is caller-chosen, not fixed per model** (long-running feature
  requests **can** use its asynchronous result polling — optional, not mandatory):
  - Default (`async` omitted or `false`): the HTTP call itself blocks until the result is ready and
    returns `status: "SUCCESS"` directly in the response body — **confirmed live**, including for
    video: a non-`async` `TEXT_TO_VIDEO` request to `lucataco/animate-diff:...` genuinely held the
    HTTP connection open for 75 seconds before returning `status: "SUCCESS"` with the finished video,
    no polling involved. This settles what was previously only inferred from the docs' example
    shapes. Heavier video models (Kling, Veo3, Sora) were not tested live, so whether they behave the
    same way (long-held connection) or something else (timeout, forced async) for genuinely long
    renders remains unconfirmed — worth an explicit HTTP client timeout budget in the adapter either
    way.
  - `"async": true`: returns immediately with `status: "PROCESSING"` and a `uuid`; poll
    `GET /api/results/{uuid}` (the "Get Result API") until `status` becomes `"SUCCESS"` or
    `"FAILURE"`. The docs state explicitly: *"The `uuid` returned in the response **is** the result id
    used by the Get Result endpoint."*
- Response envelope (same shape whether sync or the terminal state of an async poll):
  ```json
  {
    "aiRecord": {
      "uuid": "string",
      "status": "SUCCESS | PROCESSING | FAILURE",
      "model": "string",
      "type": "string",
      "createdAt": "ISO 8601 timestamp",
      "aiRecordDetail": {"promptObject": {}, "resultObject": ["array of results"]},
      "temporaryUrl": "https://storage.1min.ai/...?X-Amz-Signature=..."
    }
  }
  ```
  - `aiRecordDetail.resultObject` holds **relative storage keys**, not directly downloadable URLs
    (e.g. `"images/2024_09_30_03_47_31_072_210865.webp"`) — do not treat these as fetchable URLs.
  - `temporaryUrl` is the actual signed, time-limited S3/storage download link for the generated
    asset — this is what the adapter should fetch, the same role `data[].url` plays for DeepInfra
    video, and it carries the same implication: it's a third-party (AWS S3-hosted) URL, not
    `api.1min.ai` itself, so the same redirect-target-revalidation/DNS-rebinding protections built
    for OpenRouter apply if the adapter follows it directly. **Confirmed live** for image, audio and
    video: all three landed on the same bucket, `https://s3.us-east-1.amazonaws.com/asset.1min.ai/
    {images,audios,videos}/<generated-name>?X-Amz-...`, and all three downloaded successfully as
    valid files (PNG signature `89 50 4e 47`, MP3 frame sync `ff f3`, MP4 `ftypisom` respectively).
  - **Get Result "not found" response:** `{"aiRecord": null}` — a request for an unknown/expired
    `resultId` returns 200 with a null record rather than 404, so the adapter must check for null
    explicitly rather than relying on HTTP status alone.

### Image generation — `type: "IMAGE_GENERATOR"`

**The documented Flux Schnell model identifier is stale/wrong — confirmed live.** The docs page for
Flux Schnell gives `"model": "black-forest-labs/flux-schnell"` verbatim, but submitting that live
returned `HTTP 400 {"errorCode":"UNSUPPORTED_MODEL","message":"Model black-forest-labs/flux-schnell
is not supported for feature IMAGE_GENERATOR"}`. This is real evidence that 1min.AI's per-model docs
pages can drift from what the API actually accepts — every model identifier needs a live check before
the adapter trusts it, not just a docs read.

**Confirmed working live** (`stable-diffusion-xl-1024-v1-0`, from the Stable Diffusion XL 1.0 docs
page):

```json
{
  "type": "IMAGE_GENERATOR",
  "model": "stable-diffusion-xl-1024-v1-0",
  "promptObject": {
    "prompt": "a small red apple on a white background",
    "samples": 1,
    "size": "1024x1024",
    "cfg_scale": 7,
    "steps": 20,
    "seed": 42
  }
}
```

- `size` **must** be one of a fixed set of dimensions — confirmed by a live rejection: requesting
  `"512x512"` returned `HTTP 400 {"errorCode":"EXTERNAL_API_RESPONSE_WITH_ERROR", ...,
  "details":"...\"message\":\"for stable-diffusion-xl-1024-v0-9 and stable-diffusion-xl-1024-v1-0 the
  allowed dimensions are 1024x1024, 1152x896, 1216x832, 1344x768, 1536x640, 640x1536, 768x1344,
  832x1216, 896x1152, but we received 512x512\"..."}`. Note this error is the *downstream* provider's
  (StabilityAI's) own message, forwarded through 1min.AI's `EXTERNAL_API_RESPONSE_WITH_ERROR`
  envelope — the adapter should expect model-specific validation errors to arrive this way rather than
  being caught by 1min.AI's own request-shape validation first.
- Successful response returned `status: "SUCCESS"` synchronously (no `PROCESSING` step for this
  model), `resultObject: ["images/2026_08_17_06_31_44_448_201553.png"]`, and a working `temporaryUrl`
  that downloaded a valid PNG.
- `metadata` was an empty `{}` on this response — unlike chat's populated
  `metadata.credit`/`inputCredit`/`outputCredit` breakdown, image generation doesn't report a
  per-call credit cost inline (see "Live verification results" for how cost was inferred instead).

Every other image model (41 total, per the site's own model index — Magic Art, GPT Image, Leonardo
variants, other Stable Diffusion/Flux variants, Gemini image, Grok-2, Ideogram, Qwen, Recraft, Dzine)
has its own dedicated docs page under
`/docs/api/ai-for-image/image-generator/{model-slug}-image-generation` with its own `promptObject`
field set — **do not assume SDXL's or Flux's fields apply to other models, and do not trust a
model's documented identifier without a live check**, given Flux Schnell's identifier was already
found to be wrong.

### Text-to-speech — `type: "TEXT_TO_SPEECH"`

```json
{
  "type": "TEXT_TO_SPEECH",
  "model": "tts-1",
  "conversationId": "TEXT_TO_SPEECH",
  "promptObject": {
    "text": "input text, max 4096 characters",
    "voice": "alloy | echo | fable | onyx | nova | shimmer",
    "response_format": "mp3 | opus | aac | flac | wav | pcm (default mp3)",
    "speed": 1.0
  }
}
```

`model` is `"tts-1"` or `"tts-1-hd"` — these are OpenAI's own TTS model names, consistent with the
page living under `/docs/api/ai-for-audio/text-to-speech/openai`. **Confirmed working live** exactly
as documented, with `model: "tts-1"`, `voice: "alloy"`: response follows the general AI Feature API
envelope — audio bytes are **not** inline in the JSON; `resultObject: ["audios/<generated-name>.mp3"]`
plus a `temporaryUrl` that downloaded a valid MP3 (`ff f3` frame sync). This is a different shape from
DeepInfra's `/v1/audio/speech`, which returns raw audio bytes directly in the HTTP response body —
1min.AI always wraps results in the JSON envelope plus a follow-up fetch, for every feature type,
audio included.

### Video generation — `type: "TEXT_TO_VIDEO"`

Ten distinct models, each with its own docs page and `promptObject` shape under
`/docs/api/ai-for-video/text-to-video/{model-slug}-text-to-video`: AnimateDiff, Hailuo, Hunyuan,
Kling, Luma, Pika AI, Sora, TongYi, Veo3, Wan 2.7, Wanx.

**Confirmed working live** (AnimateDiff, the only video model actually exercised):

```json
{
  "type": "TEXT_TO_VIDEO",
  "model": "lucataco/animate-diff:beecf59c4aee8d81bf04f0381033dfa10dc16e845b4ae00d281e2fa377e48a9f",
  "conversationId": "TEXT_TO_VIDEO",
  "promptObject": {
    "prompt": "mystical forest with glowing fireflies",
    "path": "toonyou_beta3.safetensors",
    "n_prompt": "blurry, low quality, distorted",
    "guidance_scale": 7.5,
    "motion_module": "mm_sd_v15_v2",
    "seed": 0,
    "steps": 25
  }
}
```

Unlike Flux Schnell's image identifier, this exact (long, hash-suffixed) model identifier from the
docs page **did** work as documented. The call took 75 seconds end to end and returned `status:
"SUCCESS"` synchronously (no `async` flag set, no polling) with `resultObject:
["videos/<generated-name>.mp4"]` and a `temporaryUrl` that downloaded a valid MP4. This is the call
that settled the "does a sync video request really block for the full render time" question left open
by the documentation-only pass — yes, at least for this lightweight model.

Kling and Veo3's request shapes below remain **documentation-only, not live-tested** — treat their
model identifiers with the same suspicion Flux Schnell's turned out to deserve.

Documented example (Kling, unverified):

```json
{
  "type": "TEXT_TO_VIDEO",
  "model": "kling",
  "conversationId": "TEXT_TO_VIDEO",
  "promptObject": {
    "prompt": "a futuristic city with flying cars at sunset",
    "duration": 5,
    "negative_prompt": "blurry, low quality",
    "aspect_ratio": "16:9",
    "mode": "std",
    "version": "1.0"
  }
}
```

Kling-specific notes: `duration` is 5 or 10 seconds for `version` below 3.0, 3–15 seconds for 3.0+;
`mode` is `"std"` or `"pro"` (3.0 Omni uses `resolution` instead); `cfg_scale` (0–1) and
`camera_control_type` only apply below version 3.0.

Documented example (Veo3, unverified) — note the model-specific field names differ entirely from
Kling's:

```json
{
  "type": "TEXT_TO_VIDEO",
  "model": "veo3",
  "conversationId": "TEXT_TO_VIDEO",
  "promptObject": {
    "prompt": "a majestic eagle soaring through mountain peaks at golden hour",
    "negativePrompt": "buildings, cars, people",
    "task_type": "veo3-video",
    "generate_audio": true,
    "aspect_ratio": "16:9",
    "veo3_duration": "8s",
    "resolution": "1080p"
  }
}
```

**Implementation implication:** unlike DeepInfra (where the split is "which endpoint," sync vs. async
job), 1min.AI's video split is "which `promptObject` fields," per model — every one of the ten video
models needs its own confirmed field mapping before the adapter can submit to it, matching how this
codebase already treats each image/audio/video model individually rather than assuming a shared
shape.

## Live verification results

Verified 2026-08-17 with a real API key and an explicit user-approved 100,000-credit ceiling. All
four calls together used well under 20,000 credits (roughly 19,300, per the account's own running
`usedCredit` counter — see the credit-lag caveat below), leaving most of the budget unused.

| Modality | Model tested | Result | Notes |
| --- | --- | --- | --- |
| Chat | `gpt-4o-mini` | Success | Matched documented shape exactly; only modality with an inline `metadata.credit` cost breakdown (56 credits: 45 input + 11 output). |
| Image | `stable-diffusion-xl-1024-v1-0` | Success (after fixing dimensions) | Flux Schnell's documented identifier failed first — see above. |
| Audio (TTS) | `tts-1` / `alloy` | Success | Matched documented shape exactly. |
| Video | `lucataco/animate-diff:...` | Success | 75-second synchronous call, matched documented shape exactly. |
| Image-conditioned chat | `gpt-4o-mini`, `CHAT_WITH_IMAGE` | Success (2026-08-19, separate pass) | Asset API upload + `attachments.images` confirmed working with 2 images; `imageList` confirmed silently non-functional; see "Image-conditioned chat" above. |
| Image-generation reference input | `black-forest-labs/flux-2-klein-4b`, `IMAGE_GENERATOR` | Confirmed non-functional (2026-08-19) | `imageUrl`/`imageUrls`/`attachments.images` (all 3 field shapes, the last being chat's confirmed-working field) accepted with no error but had zero effect across 5 trials; see "Image-conditioned chat" above. |

**Credit-cost figures are approximate, not authoritative**, because the `teamUser.usedCredit` field in
each response appears to lag by one request (it reflects credits used as of *before* the current call
was billed, not after) — e.g. the response immediately after the image call still showed the
pre-image total. Attributing exact per-call costs from these snapshots would mean guessing at the lag
semantics rather than reading a real value, so this doc reports only the overall trend: the SDXL image
call was the single largest cost of the four (roughly an order of magnitude more than chat), video and
TTS were mid-range, and chat was cheapest. A future pass wanting exact per-call costs should either
find a more authoritative source (a dedicated usage/billing endpoint) or issue calls one at a time with
a delay and a fresh balance check before each one, rather than reading the trailing snapshot.

**Not tested live** (left as documentation-only, flagged in their sections above as needing the same
scrutiny Flux Schnell's identifier required before being trusted):

- Kling and Veo3 (or any other of the 9 non-AnimateDiff video models).
- Any of the other 40 image models beyond Stable Diffusion XL 1.0.
- Whether a heavier/slower video model (Kling, Veo3, Sora) behaves the same way AnimateDiff did for a
  synchronous request (blocks for the full render) or hits some other behavior (timeout, forced
  async) — AnimateDiff's 75-second render is not necessarily representative of a multi-minute Sora or
  Veo3 job.
- Streaming responses (`isStreaming=true`) for chat or features.
- The `async: true` opt-in path and Get Result polling — every live call in this pass used the
  synchronous default.
- Pricing/rate-limit details from `/docs/api/specifications/rate-limits` and
  `/docs/api/specifications/credits-limits` (page content wasn't substantive enough to extract a
  pricing table when fetched).
