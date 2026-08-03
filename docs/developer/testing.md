# Build and test

## Prerequisites

- .NET SDK 10.0.302 or a compatible latest patch selected by `global.json`
- .NET MAUI Windows and Android workloads
- Windows SDK for the Windows target
- Android SDK for the Android target

## Restore

```powershell
dotnet restore SlopFactory.slnx --configfile NuGet.Config
```

## Tests

```powershell
dotnet test tests\Mellow.SlopFactory.Tests\Mellow.SlopFactory.Tests.csproj --no-restore
```

The current 40 tests cover library initialization, manifest/database creation, exclusive locking, invalid non-empty directory and invalid managed-entry rejection, managed import, hashing, progress and cancellation cleanup, duplicate-import handling, streamed in-library duplication and metadata ownership, file and folder rename/move invariants, strict and bounded UTF-8 viewing, complete-file buffered text search, safe bounded Markdown rendering, verified raster-image reads and pre-decode dimension rejection, SVG active-content sanitization, verified bounded media-range reads, AAC and FLAC signature classification, atomic edited-text copies and structured validation, transactional metadata timestamps and rename, original-filename and typed-metadata search privacy, stable browser filtering and paging, complete editable-link lifecycle and endpoint ownership, aggregate recycle summaries and original locations, ordered batch restore and empty-bin processing, independent batch-failure handling, pre-restore name-conflict and selection-aware link-dependency review, missing managed-content blockers, clean and damaged recycled-file integrity scanning, missing/orphan reporting without repair, cancelled partial scans, scan/read/mutation concurrency and waiting-mutation cancellation, persisted retryable file/folder deletion failures, permanent managed-file and folder-subtree deletion, and version 1 through version 3 upgrades to schema v4 with rollback cleanup.

## Platform builds

```powershell
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-windows10.0.19041.0
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-android
```
