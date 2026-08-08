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

The current 69 tests cover platform-version policy, initialization, manifest/database creation, copied-library adoption, local-storage path rejection, hard-link rejection, and open-library identity revalidation, exclusive locking, invalid library entries, safe managed import, hashing, progress, cancellation cleanup, single and bulk duplicate handling with direct provenance chains and deletion snapshots, folder/file organization, reviewed multi-file operations, bulk metadata sensitivity changes, typed metadata ownership and filtering, redacted JSON validation, and privacy-minimised integrity-report serialization.

They also cover detected-media-type viewer allow-listing and preview-unavailable handling, bounded text, Markdown, raster-image (including temporary JPEG EXIF orientation with unchanged managed bytes and hash), SVG and media viewing; managed-content health, safe changed-byte inspection, and replacement; editable links; aggregate recycling, restoration and retryable permanent deletion; coherent integrity scans; and version 1 through version 5 upgrades to schema v7 with rollback cleanup. Preview-cache rebuilding is verified through the Windows and Android application builds.

## Platform builds

```powershell
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-windows10.0.22621.0
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-android
```
