# SlopFactory

SlopFactory is a local-first application for organizing files and creating text, image, audio, and video content with configurable AI providers. It is designed as a single-user application for Windows and Android, with each library stored entirely in a user-selected location on the local device.

Imported files are copied into application-managed storage. Library records, folders, metadata, file relationships, and generation history are stored in SQLite, while API credentials remain in the operating system's secure storage. Libraries are independent, portable between supported Windows installations where the filesystem permits it, and are not synchronized or backed up by SlopFactory.

The current development build implements the local-library foundation, including:

- configurable local libraries and persistent library identity;
- copied-library detection plus explicit in-place adoption as a new independent library;
- remembered-library discovery with availability, duplicate-ID and nested-location safeguards, switching, relinking, and non-destructive forgetting;
- reviewed managed-file import with hashing/copy progress, cancellation, duplicate choices and recycled-match recovery, per-file outcomes, plus duplication, rename, and move operations;
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
- transactional SQLite schema upgrades; and
- shared .NET MAUI application projects for Windows and Android.

AI-provider connections, generation workflows, generation-aware and typed-comparison filters, export, and release-resilience features remain under development. The outstanding product requirements are tracked in [plan.md](plan.md).

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
