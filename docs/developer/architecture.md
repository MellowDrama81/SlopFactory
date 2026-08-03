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

The workspace is the atomic application boundary for folder browsing, imports, file and folder rename/move operations, bounded text reads, metadata, link creation/relabel/reversal, recycle/restore, and permanent file deletion. Folder moves validate the complete descendant chain before updating the parent identifier; display-only organization never changes a file's internal managed name or byte location.

The built-in text reader accepts strict UTF-8 with or without its byte-order mark. Invalid UTF-8 and UTF-16 byte-order marks are rejected with a user-facing validation error. UI display is capped at 1,048,576 characters as a memory-safety boundary, not as a library storage limit.
