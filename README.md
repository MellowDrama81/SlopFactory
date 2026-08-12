# SlopFactory

SlopFactory is a local-first application for organizing files and creating text, image, audio, and video content with configurable AI providers. It is designed as a single-user application for Windows and Android, with each library stored entirely in a user-selected location on the local device.

Imported files are copied into application-managed storage. Library records, folders, metadata, file relationships, and generation history are stored in SQLite, while API credentials remain in the operating system's secure storage. Libraries are independent, portable between supported Windows installations where the filesystem permits it, and are not synchronized or backed up by SlopFactory.

The current development build implements the local-library foundation, including:

- configurable local libraries and persistent library identity;
- copied-library detection plus explicit in-place adoption as a new independent library;
- remembered-library discovery with availability, duplicate-ID and nested-location safeguards, switching, relinking, and non-destructive forgetting;
- reviewed managed-file import with hashing/copy progress, cancellation, duplicate choices and recycled-match recovery, per-file outcomes, plus duplication, rename, and move operations;
- recursive system-picker import with a non-mutating inventory, frozen source snapshots, hidden/protected-entry controls, virtual-folder recreation, and normalized Windows security-zone handling;
- verified single and bulk export, a recovery-only changed-byte export, and external opening through temporary read-only copies with detected-byte active-content safeguards;
- Android share intents and Windows file activation/drag-and-drop routed through private staging and the same explicit import review;
- virtual folders and breadcrumbs, plus paged library-wide search, filters, stable sorting, and list/grid views;
- cross-page file selection with reviewed bulk move, recycle, and typed user-metadata operations;
- bulk marking or unmarking of sensitive metadata without exposing stored values;
- reviewed bulk duplication with per-file outcomes and safe name conflict handling;
- retained original filenames, privacy-safe typed-metadata match explanations, and strict type-aware metadata filters;
- a read-only UTF-8 viewer with full-file search, sanitized rendered Markdown, confirmed external links, and a non-destructive **Edit as Copy** workflow;
- a verified image viewer with fit, zoom, pan, viewing rotation, and sanitized SVG;
- verified, seekable audio and video playback with no autoplay, one active player, speed controls, and platform caption/full-screen support;
- on-demand image thumbnails and video posters in a separate size-limited, clearable device cache;
- typed user metadata and sensitive-value concealment;
- directed file links with recycle and restore behavior;
- searchable aggregate recycling with conflict-aware bulk recovery, persisted retryable-deletion failures, and empty-bin processing;
- coherent non-mutating integrity scans covering active and recycled managed content while library changes wait;
- durable missing/changed managed-content health with exact-byte recovery, explicit safe changed-byte inspection, and preserved metadata and links;
- reviewed permanent managed-content replacement with immutable original identity and optional transactional metadata clearing;
- detected-byte format classification with persistent display-extension mismatch warnings;
- debounced managed-media watching with silent validation of expected writes and global external-change review notices;
- fail-closed manifest/database watching with mutation-bound identity and integrity revalidation;
- Windows managed-file hard-link detection and containment safeguards;
- transactional SQLite schema upgrades;
- shared .NET MAUI application projects for Windows and Android; and
- API connections (OpenAI and generic OpenAI-compatible) with OS secure-storage credentials, connection testing, configured models with provider-backed or manual model discovery, and base-URL transport-security validation (HTTPS required for public hosts, with a visible warning where HTTP is permitted for loopback/private-network hosts); and
- a cached model catalogue per connection with a visible retrieval timestamp, **Possibly Stale**/**Stale** labelling, and a **Not Currently Listed** label (never a deletion) for a configured model absent from the latest refresh; and
- a per-connection request timeout override (5–600 seconds, default 100), with a provider timeout now reliably reported as a provider error rather than mistaken for user-initiated cancellation; and
- additional non-secret connection headers for gateway/routing use cases, with reserved transport headers and credential-carrying header names (`Authorization`, `Proxy-Authorization`, `Cookie`) refused outright rather than half-supported; and
- per-modality relative-path overrides and per-modality enable/disable for a generic OpenAI-compatible connection, with a disabled modality refusing generation before any request is sent; and
- a connection's provider type can be changed once it has no active dependent models (locked while any exist), resetting its generic per-modality settings; and
- a minimal synchronous text and image generation workflow: a **Generate** page that sends a prompt to a configured Text or Image model, commits each returned result as a library file (images use real file-signature detection rather than a trusted provider format), and records a lightweight generation-history entry; and
- saved generation settings (title, model, prompt, result count, destination folder) that can be reused to open a prefilled generation, with the same recycle/restore/permanent-delete lifecycle and cascading as connections and models; and
- **Use Again** from generation history, which repopulates a new generation from a past attempt's model, prompt, result count and destination without altering the historical record; and
- an optional **System Instructions** field for Text-mode generation, shown only for a model configured to support it, sent through the documented `system` chat role and carried through saved settings and Use Again; and
- provider-reported prompt/completion token usage captured from the OpenAI-shaped chat-completions response and shown alongside each text generation result; and
- an optional single vision source image for Text-mode generation, read through the existing verified image pipeline and sent as an OpenAI-shaped image content part, with the reference carried through saved settings and Use Again; and
- status/mode/model/provider and from/to date-range filters on the generation-history list; and
- a **Cancel** action for an in-flight generation request that records no history entry rather than guessing an outcome; and
- an **Improve Prompt** action that sends the current prompt to a chosen text model with a built-in instruction template and lets the user accept a returned suggestion into the prompt, without altering saved settings; every attempt (successful, failed, or retried) is recorded as its own lightweight prompt-improvement history entry, shown separately on the generation-history page, and an accepted suggestion links its originating attempt to the resulting generation record; and
- a distinct **Credentials Required** connection status that takes priority over a connection's test result, surfaced on the Connections list and as a submission-blocking notice on **Generate** (with a warning, not a block, for a credentialed but never-successfully-tested connection); and
- a revisioned credential lifecycle: replacing a connection's API key stages it, tests it, and promotes it only after a successful test (or an explicit **Save New Key as Unverified** override), so a crash mid-replacement can never leave a connection without a usable credential; a **Credential State Requires Repair** status surfaces if reconciliation ever finds a credential it cannot trust, without guessing or discarding anything; and
- **Needs Review** propagation: changing a configured model's provider model ID or mode requires confirmation (showing which saved settings are affected) and marks the model and those saved settings for review; a model needing review is excluded from generation until a **Mark as Reviewed** action clears it; and
- a per-model text result format setting (Markdown by default, or plain text), recorded on each generation-history entry alongside the actual committed file; and
- bounded automatic retry (honoring `Retry-After`, exponential backoff with jitter) for model-listing requests specifically, since that is the one operation documented as safe to retry without provider-confirmed idempotency support — a generation-submission request never auto-retries; and
- a 1 MiB well-formed-UTF-8 bound on the prompt, system instructions, and prompt-improvement raw prompt/guidance; and
- a **Partially Completed** generation status when a provider returns fewer results than requested without the request failing outright, shown distinctly from full completion on both **Generate** and generation history; and
- a dedicated generation-history detail page (`/generation-history/{Id}`), with the main list trimmed to a summary row plus **View Details** and **Use Again**; and
- a persisted, multi-tab generation workspace on **Generate**: create, duplicate, rename (or reset to an automatic title), reorder, and close draft tabs, each autosaved with a **Saving**/**Saved**/**Not Saved** status and **Retry Save**; closing a tab offers **Discard without saving** (an instant, permanent delete with no recycle-bin entry — a deliberate departure from the recycle/restore lifecycle used elsewhere in this application), **Save settings first**, or **Keep tab open**; and
- generation submissions now go through a per-connection FIFO queue with a device-wide submission cap (3 on Windows, 2 on Android) assigned by fair round-robin, so multiple tabs' **Generate** clicks schedule fairly instead of blocking each other; a queued submission continues and commits even if the user navigates away from `/generate` and back, and cancelling one before it starts contacts no provider and creates no history entry; and
- a dedicated **Queue** page showing every queued or running submission grouped by connection, with move-left/move-right reordering of waiting jobs on the same connection and a **Cancel** action for either state.

Adjustable per-connection/device concurrency settings, multiple concurrent runs from one tab, asynchronous provider job polling, multi-source inputs, the full prompt-improvement opt-in/disclosure model, full saved-settings revision handling, audio/video generation, provider result download/validation, the remaining connection-state distinctions (**Secure Storage Unavailable**, **Authentication Failed**) and per-revision confirmation, provider breadth beyond OpenAI/generic, and release-resilience features (including emergency draft-snapshot recovery) remain under development. The outstanding product requirements are tracked in [plan.md](plan.md) and [milestone2.md](milestone2.md).

## Documentation

- [User documentation](docs/user/README.md) explains installation assumptions, library locations, importing, browsing, metadata, links, and recycle-bin behavior.
- [Developer documentation](docs/developer/README.md) covers the solution architecture, library format, persistence protocols, builds, and tests.

## Development

The solution requires .NET 10 with the .NET MAUI Windows and Android workloads.

```powershell
dotnet restore SlopFactory.slnx --configfile NuGet.Config
dotnet test tests\Mellow.SlopFactory.Tests\Mellow.SlopFactory.Tests.csproj --no-restore
```

See the [developer build and test guide](docs/developer/testing.md) for platform-specific build commands and prerequisites.
