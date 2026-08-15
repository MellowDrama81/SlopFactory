# Connections, models and AI generation

## Connections

Open **Connections** to add an API connection: a label, a provider type, a base URL, and an API key. Supported provider types are OpenAI, a generic OpenAI-compatible API, OpenRouter, and DeepInfra. The API key is stored only in the operating system's secure storage, never in the library database or in any export.

**Test Connection** validates the base URL and credential without sending a generation request — it lists available models where the provider supports that, or otherwise confirms only that authentication succeeds. A connection can still be saved after a failed or unreachable test; it shows **Unverified** until a test succeeds, and the first generation attempt through it asks for confirmation.

Replacing a connection's API key stages the new value, tests it, and only replaces the active credential once that test succeeds — a save that's interrupted partway through can never leave the connection without a working key. If SlopFactory ever finds a credential it can't trust (for example after an interrupted replacement), the connection shows **Credential State Requires Repair** rather than silently guessing which key is correct.

A connection's provider type can only be changed while it has no active models; recycle or reassign its models first.

## Models

Open **Models** to configure a model against a connection: a label, the model's provider-side ID (typed manually or chosen from **Load Models**, when the provider supports listing), and its generation mode — Text, Image, Audio, or Video. Audio and video models are only available on connections whose provider adapter actually supports that modality; DeepInfra does not yet generate audio or video.

**Load Models** also refreshes a per-connection cached model list, shown with its retrieval time and a **Possibly Stale**/**Stale** label if the cache hasn't been refreshed recently or a refresh failed. A configured model no longer present in that list is marked **Not Currently Listed** — it still works, this is only a hint that the provider may have removed or renamed it.

Changing a model's provider-model ID or generation mode marks it, and any saved settings that use it, **Needs Review**; a model needing review can't be used for generation until you explicitly clear that flag from the Models page.

## Generating

Open **Generate** to write a prompt against a configured model and choose how many results to create. Generate keeps a tab per in-progress or recently used prompt — switch, duplicate, rename, or close tabs independently; each tab autosaves as you type.

Text-mode generation can include an optional system-instructions field (only shown for a model configured to support it), up to three optional source images, and per-request settings (temperature, top P, max tokens, frequency/presence penalty) — leave any of these blank to use the provider's own default rather than sending a specific value. **Improve Prompt** sends your current prompt to a chosen text model for a rewritten suggestion you can accept or ignore.

Image, audio and video generation each use their target model's own request shape; audio and video currently accept no source input. Video generation is asynchronous — after it's submitted, the app polls the provider until every requested result finishes or fails, shown as **Submitted — awaiting the provider…** on the Queue page while it's in progress. Multiple video results in one request are tracked and completed as one group.

Clicking **Generate** enqueues the request rather than sending it inline — the page stays responsive, and the same tab can submit another run while an earlier one is still active. Every submission shows its own run card with a **Cancel** action; cancelling before anything reached the provider records no history entry, while cancelling after a video job was already accepted keeps whatever results had already finished rather than discarding them.

If a multi-result request completes with some results missing or failed, its history detail page offers **Retry Failed/Missing Results Only** — a new, independent run covering just the shortfall, without altering the original record. If a video result specifically failed only because downloading it didn't succeed (the provider itself finished the job), that position instead offers **Refresh Provider Status** — retrying just the download, without generating anything again.

## Queue and rate limits

Open **Queue** to see every submission across every tab and connection, grouped by connection, with reorder controls for anything still waiting its turn. A device-wide submission cap and a per-connection concurrency limit (both adjustable in Library Settings/Connection settings) control how many requests run at once; energy-saver mode temporarily reduces the device-wide cap without cancelling anything already running.

For a connection whose provider reports standard rate-limit headers (currently OpenAI), the Connections page shows the last-observed remaining quota and reset time. If a connection's quota is known to be exhausted, the Queue page shows a notice while its next submission waits for the reset window to pass, rather than sending a request that would just be rejected.

## Results and unverified binaries

Every generated result becomes a library file linked to its generation-history record. If a result's bytes don't match what the model's mode expects — and aren't recognizable as a provider error page or authentication response — its history detail page offers **Retain as Unverified Binary** or **Discard** instead of silently dropping it. A retained file is export-only: it can't be previewed, opened in another app, or selected as a source for a later generation.

## History, saved settings and cost

**Generation history** lists every completed, partial, failed or cancelled run, filterable by status, mode, model, provider and date range, with **Use Again** to start a new tab prefilled from a past run. **Saved generation settings** let you save a prompt/model/settings combination under a title and reopen it later without repeating the setup.

Where a provider reports the actual cost of a run (currently OpenRouter video generation), it's shown on that run's history detail page. **Cost Summary** aggregates reported cost across your generation history, filterable by date range, provider and model, with separate totals per currency.
