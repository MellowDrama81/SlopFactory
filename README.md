# SlopFactory

SlopFactory is a local-first, single-user application for generating text, image, audio, and video
content with configurable AI providers. Generation is the point of the application: a **Generate**
page submits prompts (and, where a model supports it, source images) to a connected provider,
tracks the request through a real submission queue, and commits each result.

The bundled media library exists to serve that workflow, not to compete with it. It is where the
source files you feed into a generation and the outputs you get back are organized, previewed,
linked, and kept safe — folders, search, metadata, a recycle bin, and integrity checks are all in
service of managing generation inputs and outputs, not a general-purpose file manager. Everything
lives entirely on the local device: library records, folders, metadata, file relationships, and
generation history are stored in SQLite, while API credentials remain in the operating system's
secure storage. Libraries are portable between supported Windows installations where the filesystem
permits it, and are not synchronized or backed up by SlopFactory itself.

SlopFactory runs on Windows and Android, sharing one .NET MAUI Blazor Hybrid codebase.

## Generation

- **Providers**: OpenAI, a generic OpenAI-compatible endpoint (for gateways and self-hosted
  OpenAI-shaped APIs), OpenRouter, DeepInfra, 1min.AI, ComfyUI (self-hosted or Comfy Cloud), Mistral AI,
  Groq, Together AI, Fireworks AI, DeepSeek, Perplexity, xAI (Grok), Anthropic (Claude), Google Gemini,
  Cohere, and AI21 (Jamba) — each with its own connection, secure credential storage, and per-provider
  capability rules rather than one-size-fits-all behavior. ComfyUI is image-generation only: a model's
  provider-model ID is paired with a workflow-JSON template (exported from ComfyUI's "Save (API
  format)" button, with a few placeholder tokens for the prompt/seed/reference image(s)) instead of a
  plain model name — a built-in library of 11 ready-to-use workflows (SD/Flux/Qwen text-to-image and
  image-edit variants) is available to start from when adding a ComfyUI model, though none of them have
  been independently re-verified against a live Comfy Cloud account. Every other newly-added provider is
  text-only in this app except Together AI,
  Fireworks AI, and xAI, which also support plain (non-reference) image generation, and Google Gemini,
  which supports both image and text-to-speech audio generation; OpenAI and Groq also gained
  text-to-speech audio generation via the standard OpenAI Audio API shape. None of these eleven
  providers have been live-verified against a real account — see `providers.md`.
- **Modes**: text, image, audio, and video, with per-model capability tracking (which modes a
  connection actually supports, whether a model accepts system instructions, and which source-input
  roles — reference image, DeepInfra video's first frame, and so on — it declares).
- **DeepInfra multi-reference image editing is provider-dependent, not app-limited**: SlopFactory
  offers up to three reference images to every DeepInfra image model, same as OpenAI/OpenRouter, but
  DeepInfra's own backend behavior varies by model and isn't always intuitive — some models silently
  keep only the last supplied image rather than combining them, others genuinely use more than one
  but with real run-to-run quality variance, and at least one observed model favors whichever image
  is supplied *second* over how a prompt's own "Image 1"/"Image 2" wording is phrased. This isn't
  something the app can normalize away, so treat DeepInfra multi-image results as provider-quality-
  dependent and worth a quick retry (or reordering your source images) if a result looks off.
- **A real submission queue**: generation requests go through a per-connection FIFO queue with a
  device-wide submission cap and an adjustable per-connection concurrency limit, so multiple
  concurrent generation tabs schedule fairly instead of blocking each other. Offline and metered
  connections pause new submissions automatically without cancelling one already running; per-model
  rate-limit headers are tracked so a connection's next submission holds back once its quota is
  known to be exhausted.
- **Prompt improvement**: an optional, explicitly separate step that sends the current prompt to a
  chosen text model for a suggested rewrite, recorded as its own history entry and never silently
  applied.
- **Cost awareness**: actual provider-reported cost is captured per run where an adapter exposes it,
  aggregated on a dedicated Cost Summary page; OpenRouter's own published per-model pricing also
  drives a live pre-generation cost estimate for Text-mode models, shown with its source and fetch
  time rather than a fabricated number.
- **Generation history**: every submission — successful, failed, cancelled, or partially completed —
  is recorded with the same recycle/restore/permanent-delete lifecycle as the rest of the library,
  plus a **Use Again** action that repopulates a new generation from a past attempt without altering
  the historical record.
- **Resilience**: a hard process kill mid-generation never leaves a submission stranded or silently
  duplicated — durable status transitions, restart recovery, and (for asynchronous video jobs) a
  persisted job registry all exist specifically so an interrupted generation resolves correctly the
  next time the app runs.

## Library

The library is the supporting structure around generation, not the headline feature:

- managed import (reviewed, hashed, deduplicated) for files you want to use as generation sources,
  and the same review path for files handed to SlopFactory by the OS (share intents, drag-and-drop,
  file activation);
- folders, search, filters, and typed metadata for organizing both sources and generated outputs;
- built-in viewers for text, images, audio, and video, plus on-demand thumbnails and posters;
- verified single and bulk export — including versioned, privacy-minimal `.slopfactory.json`
  sidecars that can optionally disclose a file's prompt, generation settings, cost, or other
  provenance alongside the exported media, with every opt-in defaulting off;
- a unified recycle bin and non-mutating integrity scans covering the whole library;
- device-local diagnostics (a rolling local log, exportable, with sensitive content kept out of it
  by construction rather than by after-the-fact redaction).

## Current status

SlopFactory is under active development. Provider adapters, generation modes, the submission queue,
and the library foundation described above are implemented and tested; some machinery — a full
asynchronous-status vocabulary for long-running jobs, named per-modality source-input slots beyond
what's listed above, the full cost-threshold/acknowledgement system, and provider safety-response
handling — is partially built or still open.

## Documentation

- [User documentation](docs/user/README.md) explains installation assumptions, library locations,
  importing, browsing, metadata, links, and recycle-bin behavior.
- [Developer documentation](docs/developer/README.md) covers the solution architecture, library
  format, persistence protocols, builds, and tests.

## Development

The solution requires .NET 10 with the .NET MAUI Windows and Android workloads.

```powershell
dotnet restore SlopFactory.slnx --configfile NuGet.Config
dotnet test tests\Mellow.SlopFactory.Tests\Mellow.SlopFactory.Tests.csproj --no-restore
```

See the [developer build and test guide](docs/developer/testing.md) for platform-specific build
commands and prerequisites.
