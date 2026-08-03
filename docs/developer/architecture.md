# Architecture

## Projects

| Project | Responsibility |
|---|---|
| `Mellow.SlopFactory.Core` | Platform-neutral domain records, validation rules, exceptions, and workspace contracts |
| `Mellow.SlopFactory.Infrastructure` | SQLite persistence, manifests, exclusive locks, hashing, media-type detection, managed file operations, and the workspace implementation |
| `Mellow.SlopFactory.Gui` | .NET MAUI Blazor Hybrid shell and user interface for Windows and Android |
| `Mellow.SlopFactory.Tests` | Cross-platform domain and infrastructure tests running on `net10.0` |

The GUI depends on the application contracts rather than SQLite directly. `LibraryWorkspaceFactory` is registered through dependency injection as `ILibraryWorkspaceFactory`.

## Platform targets

- Windows: `net10.0-windows10.0.19041.0`
- Android: `net10.0-android`, with API level 26 as the minimum supported platform version

The development package identifier is `com.mellow.slopfactory.dev`, keeping development storage separate from a future production identity.

`LibraryLocationService` supplies the platform storage policy. Windows accepts absolute paths on non-network drives. Android enumerates only the process's internal and external app-specific directories. `AppLibraryState` persists the last successfully opened path in device preferences and swaps workspaces only after the replacement library has opened successfully.

## Library workspace lifecycle

`ILibraryWorkspaceFactory.CreateAsync` initializes an empty directory. `OpenAsync` accepts only a manifest/database pair with matching identity and schema information. Both return an `ILibraryWorkspace`, which owns an exclusive lock for its lifetime and must be asynchronously disposed.

The workspace is the atomic application boundary for folder browsing, imports, file and folder rename/move operations, bounded text and verified image reads, edited-copy commits, metadata, link creation/relabel/reversal, recycle/restore, and permanent file deletion. Folder moves validate the complete descendant chain before updating the parent identifier; display-only organization never changes a file's internal managed name or byte location.

The built-in text reader accepts strict UTF-8 with or without its byte-order mark. Invalid UTF-8 and UTF-16 byte-order marks are rejected with a user-facing validation error. UI display is capped at 1,048,576 characters as a memory-safety boundary, not as a library storage limit.

The inline raster reader accepts PNG, JPEG, WebP, and GIF records up to 32 MiB. It reads only an active managed file and verifies its byte count and SHA-256 digest against the database before returning bytes to the WebView. The size boundary limits transient base64 and WebView memory; it is not a library storage quota.

SVG uses the same raw-byte verification before a strict XML sanitizer creates the viewing representation. DTDs and external XML resolution are prohibited. A small allowlist retains passive SVG geometry, text, definitions, gradients, masks, patterns, markers, symbols, and local fragment reuse; active elements, foreign namespaces, event attributes, style attributes, external links, and external CSS URLs are removed. The unsanitized SVG is never sent to the WebView.

Audio/video playback is capability-scoped rather than path-based. The workspace first validates an active playable record, a regular non-reparse managed file, byte size, and complete SHA-256 digest. The GUI then creates a random 256-bit session grant and gives HTML media controls only a same-origin `/slopfactory-media/<token>` address. `BlazorWebView.WebResourceRequested` intercepts that address, accepts only `GET` and `HEAD`, implements one bounded HTTP byte range with `200`, `206`, or `416` semantics, and opens a non-seekable length-limited stream. It returns `nosniff` and `no-store` headers and never discloses a managed path. Grants are bound to a library ID and content hash and are revoked when the component reloads or is disposed. JavaScript coordinates one active player, playback rate, and teardown; no media is autoplayed.

Raster viewing also performs header-level safety validation before WebView decode: dimensions are capped at 16,384 per axis and 100 million total pixels, while GIF frame count and estimated decoded animation memory are bounded. Exceeding a limit rejects only the preview and does not change the record or bytes.
Animated GIFs use the cached static thumbnail as their initial and paused representation. The original animated data URI is not attached to the DOM until the user explicitly plays it, and navigation restores the paused representation.

`PreviewCacheService` stores PNG thumbnails outside libraries under MAUI's device cache directory. Cache identities hash library ID, content hash, preview type, target size, and renderer version. Two workers generate previews on demand; raster images use MAUI Graphics and MP4 posters use `StorageFile` thumbnails on Windows or `MediaMetadataRetriever` on Android. A cache hit advances its filesystem LRU time. A device-wide 64 MiBâ€“8 GiB preference defaults to 1 GiB on Windows and 256 MiB on Android; writes and limit changes evict oldest entries. Cache status and explicit clearing are exposed in Library settings. Cache files contain no authoritative data and can disappear normally.
