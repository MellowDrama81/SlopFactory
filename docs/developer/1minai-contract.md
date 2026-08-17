# 1min.AI API contract notes

Confirmed 2026-08-17 against 1min.AI's published documentation (<https://docs.1min.ai/docs/api/intro>).
**Documentation-only** — unlike the DeepInfra contract notes in this same directory, none of this has
been verified with a live API call yet (no 1min.AI API key/budget was provided). Treat the shapes
below as a strong starting point for implementation, not a guarantee; a short live-verification pass
(one cheap chat call, one cheap image call, one TTS call) is recommended before shipping the adapter,
the same way the DeepInfra contract was confirmed.

This supersedes the "docs site could not be fetched at all" status recorded in
`docs/developer/architecture.md` and `milestone3.md` — the documentation exists and is fetchable now.

Base URL: `https://api.1min.ai`. Authentication: `API-KEY: <api-key>` header (not `Authorization:
Bearer`) plus `Content-Type: application/json`.

## Chat — `POST /api/chat-with-ai`

Matches plan.md's "Chat requests use its unified chat API."

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
  for multimodal input, not this app's own source-file slots).
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

## AI Feature API — `POST /api/features` (image, audio, video, writing, code)

Matches plan.md's "image, audio and video generation use its AI Feature API with feature-specific
request parameters." One unified endpoint and response envelope for every feature type; the
`type`/`model`/`promptObject` fields vary per feature.

- Streaming variant: `POST /api/features?isStreaming=true` (for feature types that support it).
- Request shape: `{"type": "<FEATURE_TYPE>", "model": "<model id>", "promptObject": {...}, "async":
  false}`.
- **Sync vs async is caller-chosen, not fixed per model** (matches plan.md's "long-running feature
  requests **can** use its asynchronous result polling" — optional, not mandatory):
  - Default (`async` omitted or `false`): the HTTP call itself blocks until the result is ready and
    returns `status: "SUCCESS"` directly in the response body — including for video models, per the
    Kling and Veo3 example requests below, both of which show a synchronous response shape with no
    `PROCESSING` step. Whether this genuinely means the backend holds the HTTP connection open for a
    slow video job, or the documented examples are simplified/aspirational, is exactly the kind of
    thing a live call would settle.
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
    for OpenRouter apply if the adapter follows it directly.
  - **Get Result "not found" response:** `{"aiRecord": null}` — a request for an unknown/expired
    `resultId` returns 200 with a null record rather than 404, so the adapter must check for null
    explicitly rather than relying on HTTP status alone.

### Image generation — `type: "IMAGE_GENERATOR"`

Per-model `promptObject` shape (confirmed example, `black-forest-labs/flux-schnell`):

```json
{
  "type": "IMAGE_GENERATOR",
  "model": "black-forest-labs/flux-schnell",
  "promptObject": {
    "prompt": "Modern minimalist logo design, clean lines, professional",
    "aspect_ratio": "1:1",
    "num_inference_steps": 4,
    "go_fast": true,
    "megapixels": "1",
    "output_quality": 80,
    "disable_safety_checker": false,
    "seed": null
  }
}
```

Every other image model (41 total, per the site's own model index — Magic Art, GPT Image, Leonardo
variants, Stable Diffusion, Flux variants, Gemini image, Grok-2, Ideogram, Qwen, Recraft, Dzine) has
its own dedicated docs page under `/docs/api/ai-for-image/image-generator/{model-slug}-image-generation`
with its own `promptObject` field set — **do not assume Flux's fields apply to other models**; each
one needs its own page checked before the adapter special-cases it.

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
page living under `/docs/api/ai-for-audio/text-to-speech/openai`. Response follows the general
AI Feature API envelope above — audio bytes are **not** inline in the JSON; fetch them from
`temporaryUrl`. This is a different shape from DeepInfra's `/v1/audio/speech`, which returns raw
audio bytes directly in the HTTP response body — 1min.AI always wraps results in the JSON envelope
plus a follow-up fetch, for every feature type, audio included.

### Video generation — `type: "TEXT_TO_VIDEO"`

Ten distinct models, each with its own docs page and `promptObject` shape under
`/docs/api/ai-for-video/text-to-video/{model-slug}-text-to-video`: AnimateDiff, Hailuo, Hunyuan,
Kling, Luma, Pika AI, Sora, TongYi, Veo3, Wan 2.7, Wanx.

Confirmed example (Kling):

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

Confirmed example (Veo3) — note the model-specific field names differ entirely from Kling's:

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

## Still open after this documentation pass

- No live call has confirmed any of the above — recommended before implementing: one cheap chat call,
  one cheap image call (Flux Schnell looks like the cheapest/fastest option from its request shape),
  and one TTS call, mirroring the DeepInfra verification pass in
  `docs/developer/deepinfra-audio-video-contract.md`.
- Whether a synchronous (non-`async`) video request genuinely blocks the HTTP connection for the
  full generation time, or whether some video models silently ignore the sync/async choice, is
  unconfirmed.
- Pricing/rate-limit/credit-cost details (`/docs/api/specifications/rate-limits`,
  `/docs/api/specifications/credits-limits`) were not fetched in this pass.
- Per-model `promptObject` field sets for the other 8 video models and 40 other image models beyond
  Flux Schnell/Kling/Veo3 were not individually fetched.
