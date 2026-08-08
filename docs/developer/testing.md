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

The current 56 tests cover initialization, manifest/database creation, copied-library adoption, local-storage path rejection, hard-link rejection, and open-library identity revalidation, exclusive locking, invalid library entries, safe managed import, hashing, progress, cancellation cleanup, single and bulk duplicate handling, folder/file organization, reviewed multi-file operations, bulk metadata sensitivity changes, typed metadata ownership and filtering, and privacy-minimised integrity-report serialization.

They also cover bounded text, Markdown, raster-image, SVG and media viewing; managed-content health, safe changed-byte inspection, and replacement; editable links; aggregate recycling, restoration and retryable permanent deletion; coherent integrity scans; and version 1 through version 5 upgrades to schema v6 with rollback cleanup.

## Platform builds

```powershell
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-windows10.0.19041.0
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-android
```
