# DeepInfra audio and video contract notes

Confirmed 2026-08-17 against DeepInfra's published documentation
(<https://docs.deepinfra.com/>) and live API calls made with an explicit user-approved test budget
(~$0.04 spent of a $5 ceiling: a short TTS request and one 2-second video generation). This
supersedes the "contract not confirmed" status recorded in `milestone3.md` and
`IMPLEMENTATION_COMPLETION_CHECKLIST.md` for DeepInfra audio and video — those adapters can now be
implemented against a verified contract rather than deferred.

## Audio: `POST /v1/audio/speech`

OpenAI-compatible endpoint. Base URL `https://api.deepinfra.com`.

**Request body**

| Parameter | Type | Required | Default | Notes |
| --- | --- | --- | --- | --- |
| `model` | string | yes | — | e.g. `hexgrad/Kokoro-82M`, `deepinfra/tts` |
| `input` | string | yes | — | text to convert to speech |
| `voice` | string | no | null | preset voice selection |
| `response_format` | enum | no | `wav` | `mp3` \| `opus` \| `flac` \| `wav` \| `pcm` |
| `speed` | number | no | 1 | range 0.25–4.0 |
| `service_tier` | enum | no | null | `default` \| `priority` \| `flex` |
| `fail_fast` | boolean | no | false | reject immediately with HTTP 429 instead of queueing when capacity is unavailable |
| `extra_body` | object | no | null | additional per-model parameters |

Authentication: `Authorization: Bearer <token>` (also accepts `x-api-key`/`xi-api-key`/
`x-deepinfra-source` headers, not needed for normal use).

**Response — confirmed by live call, not documented explicitly in DeepInfra's own OpenAPI spec**
(their schema shows an empty `{}` body for the 200 response):

- `Content-Type: audio/mpeg` when `response_format: mp3` was requested.
- Body is the **raw binary audio bytes** directly — verified by checking the first bytes of the
  response (`ff f3`, a valid MPEG-1 Layer III frame sync) and successfully treating it as a playable
  MP3 file. Not JSON, not base64.
- `Content-Disposition: attachment; filename=orpheus_speech.mp3` was present but the filename looks
  like a generic/hardcoded template unrelated to the requested model (`Kokoro-82M` was requested;
  the filename says `orpheus_speech.mp3`) — do not rely on this header for anything meaningful.
- This exactly matches OpenAI's own `/v1/audio/speech` behavior, consistent with DeepInfra's
  "OpenAI-compatible" branding for this endpoint. Expect other `response_format` values to set
  `Content-Type` accordingly (`audio/opus`, `audio/flac`, `audio/wav`, `application/octet-stream` or
  similar for `pcm`) — only `mp3` was actually exercised.

**Pricing observed:** Kokoro-82M billed at $0.62 / 1M input characters (per DeepInfra's models page);
a 29-character test request cost approximately $0.000018.

## Video: `POST /v1/videos` (submit), `GET /v1/videos/{id}` (poll), `GET /v1/videos/{id}/content` (fetch)

Base URL `https://api.deepinfra.com`. This is a REST submit-then-poll job API, structurally the same
shape the app's `IProviderAdapter.SubmitVideoGenerationAsync`/`PollVideoGenerationAsync` already
expects (used today for OpenRouter).

### Submit — `POST /v1/videos`

| Parameter | Type | Required | Notes |
| --- | --- | --- | --- |
| `model` | string | yes | see "Not every model supports this API" below |
| `prompt` | string | yes | video description |
| `negative_prompt` | string | no | what to exclude |
| `aspect_ratio` | string | no | |
| `size` | string | no | resolution |
| `seconds` | integer | no | duration |
| `seed` | integer | no | reproducibility |
| `style` | string | no | visual style modifier |
| `image_url` | string | no | first-frame image for image-to-video: an `http(s)` URL or a `data:` URI; omit for text-to-video |

Response (immediate, HTTP 200) — confirmed by live call:

```json
{"id":"videos_7d0ao7zAHUfj8R8u","object":"video.generation.job","created_at":1786947130,"status":"queued","model":"PrunaAI/p-video","data":null,"error":null}
```

### Poll — `GET /v1/videos/{id}`

Same `VideoGenerationOut` shape as the submit response, with `status` and `data` updated in place.
Confirmed observed values:

- `status: "queued"` — accepted, not yet started.
- `status: "succeeded"` — terminal success. `data` becomes a non-null array; observed shape:
  `data: [{"url": "https://api.pruna.ai/v1/predictions/delivery/xezq/.../output.mp4"}]`.
  **The URL is on a third-party CDN host, not `deepinfra.com`** — a per-model/per-provider result
  host, not a fixed DeepInfra domain. Any code that fetches this URL directly needs the same
  redirect-target-revalidation and DNS-rebinding protections already implemented for OpenRouter
  result downloads (`ResultUrlValidator`), since the host isn't fixed or necessarily first-party.
- A terminal failure state was not directly observed in testing (not worth spending budget to force
  one). The documented schema has `status: string` (values not enumerated by DeepInfra) and
  `error: string | null`, so a failure is expected to set `status` to some failure value with `data`
  null and `error` populated — implement defensively: treat only `"succeeded"` as success, and
  anything else that isn't a known in-progress value (`"queued"`, and presumably `"processing"`/
  similar, unconfirmed) as a terminal failure with the `error` message surfaced, rather than assuming
  a specific failure string.

### Fetch content — `GET /v1/videos/{id}/content?variant=video`

Confirmed by live call:

- Returns the **raw video bytes directly**, proxied through `api.deepinfra.com` itself — not a
  redirect to the third-party `data[].url`. Verified via `ftypisom` at the start of the response
  body (a valid MP4 file signature).
- `Content-Type: video/mp4`.
- This is the safer of the two ways to obtain the finished video: it stays same-host with DeepInfra
  rather than requiring the adapter to trust and fetch an arbitrary third-party CDN URL from
  `data[].url`. Prefer this endpoint over following `data[].url` directly unless there's a specific
  reason not to.
- The `variant` query parameter defaults to `"video"` per the OpenAPI spec; no other variant values
  were discovered or tested.

### Not every model supports the async job API

Confirmed by live call: submitting to `/v1/videos` with `FastVideo/FastWan-QAD-FP8-1.3B` (the
cheapest listed text-to-video model, $0.0025/second) was rejected outright:

```json
{"error":{"message":"FastVideo/FastWan-QAD-FP8-1.3B does not support asynchronous video jobs. Use POST /v1/inference/FastVideo/FastWan-QAD-FP8-1.3B, which returns the video in the response.","type":"invalid_request_error","param":"model","code":null}}
```

`PrunaAI/p-video` ($0.02/second) did work through `/v1/videos` and completed successfully. This
means DeepInfra has (at least) two distinct, non-interchangeable video generation contracts:

1. **`/v1/videos` submit-then-poll job API** — some models only (confirmed: `PrunaAI/p-video`).
2. **`/v1/inference/{model}` native synchronous API** — other models only (confirmed rejection
   message names `FastVideo/FastWan-QAD-FP8-1.3B` as one). This path was not itself tested live; per
   DeepInfra's own docs its example response is `result["video"]` containing a URL, but the docs
   also warn "video generation requires asynchronous handling via webhooks rather than synchronous
   polling patterns" for at least some models — this native path's real behavior (synchronous body
   vs. webhook callback) was not independently confirmed and should not be assumed.

**Implementation implication:** the app cannot treat "DeepInfra video" as one uniform contract driven
purely by model ID substitution. Either restrict SlopFactory's DeepInfra video support to an
explicit, tested allow-list of models confirmed to work through `/v1/videos`, or detect this specific
rejection (`invalid_request_error` naming the model as not supporting async jobs) and handle it as a
clear, distinct local failure rather than a generic provider error — never silently retry against the
native endpoint without separately confirming that endpoint's real response contract first.

## Pricing reference (informational, confirmed via DeepInfra's public models pages)

| Model | Price |
| --- | --- |
| FastVideo/FastWan-QAD-FP8-1.3B | $0.0025/second (480p) — **not** async-job-compatible |
| PrunaAI/p-video | $0.02/second — confirmed async-job-compatible |
| Pixverse/Pixverse-6-T2V | $0.045/second |
| Wan-AI/Wan2.2-T2V-A14B | $0.075/second |
| Wan-AI/Wan2.6-T2V | $0.10/second |
| google/veo-3.1-fast | $0.15/second |
| google/veo-3.1 | $0.40/second |
| hexgrad/Kokoro-82M (TTS) | $0.62 / 1M input characters |
