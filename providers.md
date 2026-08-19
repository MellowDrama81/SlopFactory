# Candidate generative AI providers

Research notes on generative AI API providers that could be added to SlopFactory, beyond the seventeen
currently integrated (`OpenAi`, `GenericOpenAiCompatible`, `OneMinAi`, `OpenRouter`, `DeepInfra`,
`ComfyUi`, `Mistral`, `Groq`, `TogetherAi`, `FireworksAi`, `DeepSeek`, `Perplexity`, `XAi`, `Anthropic`,
`Gemini`, `Cohere`, `AI21` — see `docs/developer/architecture.md`). This is a research/backlog document,
not a record of implemented behavior for anything below.

**Update:** every provider in both the "Directly OpenAI-compatible" and "Custom shape required" sections
immediately below has since been implemented (see `docs/developer/architecture.md`'s "Seven
directly-OpenAI-compatible providers" and "Four bespoke-shape providers" sections) — the entries are
left in place as a record of what was/wasn't confirmed live before shipping (**none** of the eleven were
live-verified with a real API key; the four bespoke ones carry materially higher shape-mismatch risk
than the OpenAI-compatible seven, since they don't reuse the well-exercised OpenAI-compatible request
path), not as an open backlog item. Image generation was added on top of the initial text-only pass for
the two providers whose image API is a real, single-endpoint call confirmed reachable without extra
speculative work: Google Gemini (Imagen's `predict` endpoint) and xAI (Grok Imagine's
`images/generations`-shaped endpoint). Mistral's image generation was deliberately **not** added even
though its API genuinely exists — it's an agent-tool/Conversations-API flow (multi-step, not a single
endpoint call), too speculative to implement without live verification. DeepSeek's Janus-Pro image
family was also not added — it isn't confirmed to be exposed through `api.deepseek.com` at all, as
opposed to only via third-party inference hosts (DeepInfra, Together AI) that already carry it. Audio
generation was added for the two providers whose text-to-speech API reuses an already-proven, confirmed
shape: OpenAI and Groq both got it via the standard OpenAI Audio API (`POST .../audio/speech`, the exact
same shape already shipped for OpenRouter/DeepInfra — Groq's PlayAI-based TTS models are documented
against this endpoint too); OpenRouter's existing audio implementation was also fixed to actually honor
a caller-chosen voice rather than always sending its hardcoded default, a pre-existing gap noticed while
extending this same capability elsewhere. Google Gemini also got audio — its TTS reuses `generateContent`
itself (`responseModalities:["AUDIO"]` + `speechConfig`), the same endpoint Text/Image already use, with
its raw-PCM response wrapped in a WAV header before returning it. No video generation was added anywhere
in this pass: Gemini's Veo is a genuinely separate, asynchronous long-running-operation API whose exact
response envelope this pass didn't have enough confidence in to guess at for something this consequential
to get wrong, and xAI's Grok Imagine video rides on an endpoint shape that was never verified at all —
both are left unimplemented rather than shipping a guess. Every other provider's audio (Mistral's
Voxtral, Groq's own Whisper hosting — speech-to-text, no surface in this app — Together AI/Fireworks
AI's unconfirmed shapes, DeepSeek V4's unconfirmed-GA speech) and video offerings stay unimplemented for
the same reasons as before. None of the three that support chat vision input (Anthropic, Gemini, Cohere)
have that translated either; see each adapter's own remarks for why.
Everything else in this document (the aggregators, ComfyUI's original research — now implemented, see
`docs/developer/architecture.md`'s ComfyUI section — and the 3D/music/world-model/embeddings categories)
remains unimplemented backlog, and stays out of scope for the same reason ComfyUI originally did before
its own dedicated pass: each needs new domain concepts (a workflow entity, a new generation surface, a
different credential/auth model) rather than just another `IProviderAdapter` implementation.

## How a provider plugs in today

Every adapter implements `IProviderAdapter` (`src/Mellow.SlopFactory.Core/Application/IProviderAdapter.cs`): `TestConnectionAsync`, `ListModelsAsync`, `GenerateTextAsync`, `GenerateImageAsync`, `GenerateAudioAsync`, `SubmitVideoGenerationAsync`/`PollVideoGenerationAsync`. The existing adapters all speak the OpenAI `chat/completions` / `images/generations` request-response shape via the shared `OpenAiCompatibleProtocol` helper, add a `ProviderType` enum value, register a typed `HttpClient` in `DependencyInjection.cs`, and declare capabilities (system-instruction support, source-image slots, generation-settings flags) in `LibraryRules`. There is no streaming support anywhere in the interface today.

This matters for prioritization: providers whose API is OpenAI-compatible (or close to it) are cheap to add — largely config plus a capability declaration. Providers with a materially different request/response shape (Anthropic's Messages API, Google's Gemini API) need a bespoke adapter, similar in effort to the existing `OneMinAi`/`DeepInfra` adapters that already carry provider-specific contracts.

## Candidates

Output types are given against this app's four generation surfaces — **Text** (`GenerateTextAsync`, incl. chat/reasoning), **Image** (`GenerateImageAsync`), **Audio** (`GenerateAudioAsync`, TTS/STT), **Video** (`SubmitVideoGenerationAsync`/`PollVideoGenerationAsync`) — plus an **Other** column for capabilities (embeddings, rerank, web search) that don't map onto any of the app's four surfaces at all.

### Directly OpenAI-compatible (low integration cost)

These expose `chat/completions`-shaped endpoints and could likely go through `GenericOpenAiCompatibleProviderAdapter` today, or get a thin dedicated adapter for model listing/branding.

- **Mistral AI** — `api.mistral.ai`, OpenAI-compatible chat endpoint. Notably cheap (`Mistral Small` ~$0.10/$0.30 per M tokens in/out; `Mistral Large 3` ~$0.50/$1.50). EU data residency, open-weight models available.
  - Text: yes (chat, code, reasoning tiers). Image: yes, via an "image generation" agent tool on the Conversations API rather than a plain `images/generations` endpoint — would need adapter-level translation, not a drop-in. Audio: yes — Voxtral TTS (released Mar 2026), 9 languages, zero-shot voice cloning from ~3s reference audio; also has STT/transcription. Video: none found. Other: none notable.
- **Groq** — OpenAI-compatible endpoint, distinguishing feature is inference speed (custom LPU hardware) rather than model novelty; hosts Llama/Mixtral/Gemma/DeepSeek models.
  - Text: yes (its entire value proposition — high tokens/sec). Image: no (Groq doesn't host image-gen models). Audio: yes — hosts Whisper for STT and some TTS models. Video: none. Other: none.
- **Together AI** — OpenAI-compatible endpoint, large catalog of open-weight hosted models (Llama, Qwen, DeepSeek, etc.), fine-tuning support.
  - Text: yes. Image: yes (hosts FLUX and other open image models via `images/generations`-shaped endpoint). Audio: yes (TTS/STT models in the catalog). Video: yes (hosts open video-gen models). Other: embeddings, moderation endpoints, fine-tuning. Broadest single-provider modality coverage of the OpenAI-compatible group, closest fit to this app's four-surface adapter shape.
- **Fireworks AI** — OpenAI-compatible endpoint, hosted open-weight models plus function-calling/JSON-mode support.
  - Text: yes. Image: yes (hosts SDXL/FLUX-class image models). Audio: limited (some STT). Video: no. Other: embeddings.
- **DeepSeek** (direct, `api.deepseek.com`) — OpenAI-compatible chat endpoint. `DeepSeek V3`/`V4 Flash` are aggressively priced (~$0.14/$0.28 per M tokens, with a much cheaper cached-input rate) and support function calling and prompt caching. Already reachable indirectly via OpenRouter/DeepInfra, but a direct adapter would cut a hop and expose provider-specific caching pricing.
  - Text: yes, including the reasoning-tuned R1 line. Image: image generation exists but ships under the separate **Janus-Pro** model family, not the mainline chat API — treat as a distinct model/adapter path, not free with a text integration. Audio: V4 is described as natively multimodal with speech generation, but this is not confirmed as a stable public API surface yet. Video: same caveat — V4 reportedly generates short (~30s) video with synced audio from text/image, unconfirmed as GA API. Other: none. Verify current API surface before relying on anything past text.
- **Perplexity** (`pplx-api`) — OpenAI-compatible chat endpoint, differentiator is built-in web search / citations in responses rather than raw model choice.
  - Text: yes (Sonar model tiers: fast lookup, pro multi-source, reasoning, deep-research). Image: no. Audio: no. Video: no. Other: **web search with inline citations is the core feature** — every response includes grounded source URLs, which doesn't map onto any existing generation surface and would need new UI/domain concepts to expose.
- **xAI (Grok)** — OpenAI-compatible chat endpoint (`api.x.ai`). Current lineup centers on `Grok 4.3` and a coding-focused `Grok Build 0.1`; long-context requests past 128k tokens double in price, similar to Gemini's tiered pricing.
  - Text: yes. Image: yes — **Grok Imagine** (Aurora engine), up to 2K resolution with multilingual text rendering. Audio: yes, but only bundled *into* video generation (native synchronized audio), not a standalone TTS/STT endpoint. Video: yes — up to 15s at 480p/720p (1080p on the Video 1.5 endpoint), image-to-video and text-to-video, currently a leaderboard-leading model. Other: none. Notably the most video-capable of the OpenAI-compatible-shaped candidates, but image/video/audio ride on a different (Imagine-specific) endpoint than chat, so still needs bespoke request handling despite the chat API being OpenAI-compatible.

### Custom shape required (higher integration cost)

- **Anthropic (Claude)** — Messages API (`api.anthropic.com/v1/messages`) is not OpenAI-shaped: system prompt is a top-level field (maps cleanly to this app's `SupportsSystemInstructions` capability), content blocks distinguish text/image/tool-use explicitly, and streaming uses SSE events with a different envelope. Sonnet 5 / Opus 5 / Haiku 4.5 support a 1M-token context at standard pricing with no long-context surcharge, which is unusual among the surveyed providers. Would need a dedicated adapter analogous to `OneMinAiProviderAdapter`.
  - Text: yes (chat/reasoning/agentic/coding — its primary strength). Image: **no** — no native image-generation model exists; image *understanding* (vision input) is supported but that's an input capability, not an output one. Anthropic has publicly reaffirmed no plans to add native image generation. Audio: no native TTS/STT; third-party integration only (e.g. via MCP to ElevenLabs). Video: a real-time video *understanding*/interaction API launched mid-2026, but its generative video output is explicitly described as behind competitors and not a general-purpose video-gen endpoint like Veo/Grok Imagine. Other: none. Net: if added, this would only light up the Text surface for this app; Image/Audio/Video would stay unsupported (`ProviderAdapterException`), same pattern as `OneMinAi`'s partial capability set today.
- **Google (Gemini)** — `generativelanguage.googleapis.com` REST API uses `contents`/`parts` request shape, not `messages`; safety settings and generation config are separate top-level objects. Gemini 3.1 Pro offers a very large (2M token class) context window with a pricing step past 200k tokens. Also reachable via Vertex AI with different auth (GCP service account vs API key), which is a separate integration decision from the direct Gemini API.
  - Text: yes. Image: yes via Imagen models, though note the API has been mid-migration (older image endpoints deprecated Aug 2026 in favor of newer stable/preview ones — check current endpoint before integrating). Audio: yes — Gemini 3.1 Flash TTS (streaming-capable) plus separate Lyria models for music/audio generation. Video: yes — Veo 3.1 generates up to 8s video at 720p/1080p/4K with natively synchronized audio, reference-image-guided direction, and video extension; also a newer "Gemini Omni Flash" fast/conversational video-editing model. Other: none beyond the above. Broadest single-provider modality coverage of any candidate here (matches or exceeds Together AI), but every modality is a genuinely distinct API family (Gemini/Imagen/Veo/Lyria), so it's really 3-4 separate integrations bundled under one account, not one endpoint with four capability flags.
- **Cohere** — `api.cohere.com` chat endpoint has its own request/response shape (`message`/`chat_history` rather than `messages`), plus first-class RAG/citation and rerank endpoints that don't map onto this app's `GenerateText`/`GenerateImage`/`GenerateAudio` surface at all.
  - Text: yes (Command R+ family), with image *input* via Aya Vision (multimodal understanding, not generation). Image: no generation. Audio: yes, but **input-only** — Cohere Transcribe is STT (audio-to-text), there is no TTS/audio-output model. Video: no. Other: **Embed** (text/image → vector embeddings) and **Rerank** (relevance reordering) are Cohere's actual differentiators and are core to its RAG-focused product — neither has any equivalent in this app's adapter surface today.
- **AI21 (Jamba)** — Similar situation to Cohere: OpenAI-adjacent but not identical chat shape, smaller relative market share.
  - Text: yes — Jamba's hybrid Mamba-Transformer architecture is tuned for a long (256K) context window and fast long-document processing. Image: no. Audio: no. Video: no. Other: none found. Effectively a single-modality (long-context text) candidate.

### Aggregators (alternative to adding providers one at a time)

- **OpenRouter** is already integrated and is itself an aggregator; expanding the set of models exposed through it (rather than adding new direct adapters) may cover several of the above providers for near-zero additional integration cost, at the cost of an extra hop and OpenRouter's own markup/rate limits.
  - Modality note: OpenRouter's own catalog is predominantly text; it does not uniformly re-expose every upstream provider's image/audio/video endpoints, so relying on it does not automatically bring Veo-, Imagine-, or Voxtral-class outputs into this app — each modality would still need to be checked model-by-model.
- Cloud multi-model gateways (**AWS Bedrock**, **Azure AI Foundry**, **Google Vertex AI**) host several of the above providers behind cloud-specific auth (SigV4, Azure AD, GCP service accounts) rather than a simple bearer-token API key, which is a different trust/config model than every currently integrated provider and would likely need its own credential-lifecycle handling.
  - Modality note: these gateways generally do carry the full modality range of whatever underlying model is selected (e.g. Vertex AI exposes Gemini text + Imagen + Veo + Lyria under one account), so they're the most complete route to multi-modal coverage — at the cost of the heaviest auth/config lift of any option here.

### Workflow-engine providers (structurally different from every candidate above)

- **ComfyUI** — not a fixed model+prompt API like every other candidate in this document; it's a node-graph workflow engine. The unit of work is an entire workflow JSON (checkpoint loader, sampler, ControlNet/LoRA nodes, etc.), not a `(model, prompt, settings)` tuple. This has real consequences for how it would plug into this app:
  - **Self-hosted by default.** The reference server (`aiohttp`, port 8188 locally) has REST endpoints (`POST /prompt` to submit a workflow, `GET /history/{prompt_id}` to retrieve results) and a `/ws` WebSocket for live per-node progress. There's typically **no auth at all** on a local instance — a bearer-token `ApiKey` field (the credential shape every current adapter assumes) doesn't map cleanly; a self-hosted deployment would need a base-URL-only "trust the network" credential, closer to how `GenericOpenAiCompatible` already lets a user point at an arbitrary base URL.
  - **Hosted options exist** if self-hosting is unwanted, including an official one: **Comfy Cloud** (`cloud.comfy.org`, run by Comfy Org itself) does expose a real REST API — base URL `cloud.comfy.org`, auth via an `X-API-Key` header (keys generated at `platform.comfy.org/profile/api-keys`, prefixed `comfyui-`), and it's explicitly documented as **API-compatible with local ComfyUI's API**, so the same `/prompt`-submit workflow-JSON shape carries over. This is the closest thing here to a normal bearer-token credential fitting this app's existing `ApiKey` pattern directly, no "trust the network" special-case needed. Caveats: API access is gated to paid tiers (Standard/Creator/Pro — the Free tier has no API access at all) and each tier caps concurrency (1/3/5 concurrent workflow runs respectively, with up to 100 queued). Third-party hosts also exist — **RunComfy** (per-second GPU billing), **ComfyDeploy** (versions/locks a workflow's node dependencies at deploy time, avoiding node-drift on a shared instance), Wireflow, RunPod serverless — each adding its own API key on top of a broadly similar `/prompt`/`/history` protocol. Self-hosting remains the only auth-free option; every hosted route (official or third-party) uses a normal API key.
  - **No fixed model list.** `ListModelsAsync` has nothing to call — there's no `/v1/models`-style endpoint; the closest equivalent is `/object_info`, which enumerates installed node types and checkpoint files on that specific server/deployment, not a stable catalog. Model selection in this app's `Model` entity would really mean "which workflow JSON (with which checkpoint baked in)," which is a different configuration shape than every existing provider.
  - **Output modality is genuinely workflow-dependent, not provider-fixed.** The same server can produce images (SD/Flux/SDXL nodes), video (AnimateDiff/SVD/Wan nodes), audio, or even 3D meshes (community Hunyuan3D/TripoSR nodes exist), entirely depending on which workflow graph is submitted. So unlike every provider in the tables above, "what can ComfyUI generate" isn't a per-provider constant — it would need to be a per-Connection or per-workflow capability declaration in `LibraryRules`, not a static one keyed on `ProviderType` alone.
  - **API shape fit**: submit-then-poll (or submit-then-websocket-for-progress) matches this app's existing video async-job pattern (`SubmitVideoGenerationAsync`/`PollVideoGenerationAsync`) better than the synchronous `GenerateImageAsync`/`GenerateTextAsync` calls, even when the workflow only produces a still image — ComfyUI generation is not guaranteed fast enough for a synchronous request.
  - **Net assessment**: the official Comfy Cloud API removes the auth mismatch (bullet above) — that part is now a normal `ApiKey`-shaped integration. What's left is structural, not credential-related: it's still the most architecturally different candidate in this whole document because it breaks two other assumptions baked into `IProviderAdapter`/`LibraryRules` today: (1) a provider has a listable, stable model catalog (ComfyUI has none — `/object_info` lists installed node types/checkpoints on that deployment, not a queryable catalog), and (2) a provider's output modality is a fixed capability of the provider (ComfyUI's is workflow-dependent — image, video, audio, or 3D, chosen by whichever graph JSON is submitted). A viable integration would likely need a new "workflow" concept (a saved JSON graph + parameter-mapping into that graph) sitting alongside `Model` in the domain, rather than reusing `Model`/`ProviderType` as-is. With Comfy Cloud specifically, this is now closer to a scoped feature than a speculative one — worth prototyping if support for arbitrary community SD/video/audio pipelines (not just fixed hosted models) is an explicit goal.

### Modality summary

| Provider | Text | Image | Audio | Video | Other |
|---|---|---|---|---|---|
| OpenAI *(integrated)* | yes | yes | yes (TTS+STT) | no | embeddings, moderation |
| DeepInfra *(integrated)* | yes | yes | yes | yes | — |
| Mistral | yes | yes (agent tool, not plain endpoint) | yes (Voxtral TTS/STT) | no | — |
| Groq | yes | no | yes (STT, some TTS) | no | — |
| Together AI | yes | yes | yes | yes | embeddings, moderation |
| Fireworks AI | yes | yes | limited (STT) | no | embeddings |
| DeepSeek (direct) | yes | yes (separate Janus-Pro family) | unconfirmed GA | unconfirmed GA | — |
| Perplexity | yes | no | no | no | grounded web search + citations |
| xAI (Grok) | yes | yes (Imagine/Aurora) | bundled into video only | yes (up to 15s, native audio) | — |
| Anthropic (Claude) | yes | no | no (third-party only) | limited (understanding, not general-gen) | — |
| Google (Gemini) | yes | yes (Imagen) | yes (Flash TTS, Lyria) | yes (Veo 3.1, Omni Flash) | — |
| Cohere | yes | no | input-only (Transcribe/STT) | no | embeddings, rerank |
| AI21 (Jamba) | yes | no | no | no | — |

## Modalities beyond Text/Image/Audio/Video

None of these have any home in `IProviderAdapter` today — adding one would mean a new generation surface (new method, new `LibraryRules` capability enum, new UI), not just a new `ProviderType`. Flagged here as candidates for future scope, not for the current adapter shape.

- **3D model generation (text/image → mesh)** — a distinct, maturing category with dedicated providers rather than an extra flag on existing image/video providers. None of the providers already covered above (OpenAI, Anthropic, Gemini, etc.) offer 3D generation at all — this is a separate provider category entirely, closer in spirit to DeepInfra's audio/video contract (bespoke, job-submission-style, async-poll API returning a downloadable file) than to a `chat/completions`-shaped endpoint. See the dedicated breakdown below.
- **Music generation** — separate again from generic TTS/audio. **Suno** and **Udio** lead on output quality (full songs with vocals) but have **no public API** as of Aug 2026 (Suno is trialing a partner API). Providers with an actual API today: **ElevenLabs Music**, **Google Lyria** (already noted under Gemini/Vertex), **Stability's Stable Audio**. Licensing terms vary a lot by provider — worth checking before treating any output as safe to reuse commercially.
- **World models / interactive environment generation** — a genuinely new category, not an extension of video: Google DeepMind's **Genie 3** (and the consumer-facing **Project Genie**, Jan 2026) generates real-time, explorable 3D environments (720p/24fps) from a text prompt or image, distinct from a fixed video clip in that the output is interactive/steerable frame-by-frame rather than pre-rendered. No comparable offering from the providers already surveyed; likely out of scope unless the app's use case moves toward interactive/game content rather than static generated assets.
- **Embeddings and rerank** — already called out under Cohere/OpenAI/Together above; worth restating here as a modality class in its own right (vector output, not human-consumable text/image/audio/video) since it would need its own surface (`GenerateEmbeddingAsync`-style) rather than fitting any of the four existing ones.
- **Structured/code output as a distinct surface** — most text providers already support JSON-mode/structured-output and code execution as *options on* `GenerateTextAsync`, not a separate modality, so this likely doesn't need new surface area — flagged only because "code interpreter"/sandboxed execution (e.g. OpenAI's Code Interpreter, Anthropic's code execution tool) does return a genuinely different artifact type (files, execution results) that the current text-only `GenerateTextAsync` response shape doesn't model.

### 3D model generation providers

All of these are async, credit-metered, job-submission APIs (submit a text/image prompt → poll for a task → download a file) — structurally the best existing analogue in this app is the video surface (`SubmitVideoGenerationAsync`/`PollVideoGenerationAsync`), not the synchronous `GenerateImageAsync`. None speak an OpenAI-compatible shape.

- **Meshy** — REST API covering text-to-3D, image-to-3D (single or multi-image), texturing, remeshing, rigging, and 3D-printing utility endpoints; GA since Jan 2026 with the Meshy 6/7 line (watertight geometry, a low-poly mode aimed at game dev). Exports GLB, FBX, OBJ, USDZ, STL, 3MF, .blend — the widest format coverage surveyed. Credit-based pricing (e.g. image-to-3D runs ~20-35 credits depending on texture/resolution); Pro tier caps at 20 requests/sec and 10 queued tasks, Enterprise unlocks the full endpoint set. SOC2 + ISO 27001, which stands out among these smaller providers.
- **Tripo** — REST API (`openapi.tripo3d.ai/v3`) for generation, texturing, auto-rigging (`POST /animations/rig`, 7 creature-type skeletons), and animation — the furthest along toward a full game-ready asset pipeline rather than a static mesh. Pay-as-you-go credits, roughly $0.20-0.25 per asset for a full generate→rig pipeline at ~100 credits/$1. Exports FBX/GLB with optional embedded skeleton, animation, and PBR textures.
- **Rodin (Hyper3D)** — largest model of this group (10B params, 4K textures), enterprise-leaning. Unusual pricing model: generation itself is free/unlimited, you only pay on download (~$0.50-1.50/model depending on complexity, or a Business-tier monthly plan at $120/mo for API access) — worth a compliance/cost-control look before adopting, since "free to generate, pay to retrieve" is a different metering shape than every other provider surveyed in this document.
- **Stability AI (SPAR3D / Stable Point Aware 3D)** — sub-second single-image-to-3D reconstruction with real-time editing of the point cloud before meshing. Model weights are free for commercial and non-commercial use under Stability's Community License, with a revenue threshold (>$1M/yr) that requires a separate enterprise license — reachable via Stability's Developer Platform API or self-hosted. Lower fidelity than Meshy/Tripo/Rodin; strongest where speed matters more than detail.
- **TRELLIS 2** (Microsoft Research) — open-weight, self-hostable, outputs Gaussian Splats rather than a traditional mesh (a materially different asset type — would need its own rendering/export path, not just a different file format). No hosted API/pricing of its own; would only be reachable by self-hosting or via an aggregator like fal.ai/Replicate.
- **Luma AI** — architecturally different from the rest: NeRF-based reconstruction from a video capture (walk around a physical object with a phone) rather than generative text/image-to-3D. Excels at scan-quality reconstruction of real objects, weaker than dedicated generators for single-still or pure-text input. Likely a poor fit for this app's presumed text/image-prompt-driven workflow.

| 3D provider | Input | Output formats | Pricing model | API shape |
|---|---|---|---|---|
| Meshy | text, image(s) | GLB, FBX, OBJ, USDZ, STL, 3MF, .blend | per-credit, tiered plans | REST, async job |
| Tripo | text, image | FBX, GLB (+ rig/anim) | per-credit (~$0.20-0.25/asset) | REST, async job |
| Rodin (Hyper3D) | text, image | not fully confirmed | free to generate, pay per download | REST, async job |
| Stability SPAR3D | single image | not fully confirmed | free (weights) / revenue-gated enterprise license | REST or self-hosted |
| TRELLIS 2 | text, image | Gaussian Splats | open-weight, self-host only | none hosted — self-hosted or via aggregator |
| Luma AI | video capture | scan/mesh export | subscription | REST |

## Suggested next step

Every provider named in this document's "Directly OpenAI-compatible" and "Custom shape required"
sections, plus ComfyUI, is now implemented — see `docs/developer/architecture.md`. What's left is
qualitatively different, not just "the next provider on the list":
- **Live-verify before depending on any of the eleven newly-implemented adapters.** None were checked
  against a real account. Text generation for all eleven, image generation for Together AI/Fireworks
  AI, and the bespoke Anthropic/Gemini/Cohere/AI21 wire shapes all rest on published documentation
  only — confirm against a real account before depending on them the way DeepInfra/1min.AI/Comfy
  Cloud's contracts were confirmed. The four bespoke adapters carry more risk than the seven
  OpenAI-compatible ones, since none of them reuse the well-exercised OpenAI-compatible request path.
- **Cohere's Embed/Rerank, and grounded web search (Perplexity)** have no home in this app's
  `IProviderAdapter` surface — adding them would mean a new generation-surface concept, not a
  capability flag on an existing one.
- **The remaining aggregators (AWS Bedrock, Azure AI Foundry, Google Vertex AI)** need a different
  credential/auth model (SigV4, Azure AD, GCP service accounts) than every provider integrated so far,
  which is a genuinely separate decision from "add one more adapter."
- **3D generation, music generation, world models, and embeddings/rerank as their own modality class**
  all need a new generation surface (new `IProviderAdapter` method, new `LibraryRules` capability enum,
  new UI) — see "Modalities beyond Text/Image/Audio/Video" below for the fuller breakdown.

---
*Research notes only — not reflected in `IMPLEMENTATION_COMPLETION_CHECKLIST.md`. Implemented providers are reflected in `docs/developer/architecture.md`, which is authoritative over this file where they overlap. Pricing figures are approximate as of August 2026 and should be re-verified against each provider's current pricing page before any integration decision.*
