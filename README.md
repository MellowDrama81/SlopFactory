# SlopFactory

SlopFactory is a local-first application for organizing files and creating text, image, audio, and video content with configurable AI providers. It is designed as a single-user application for Windows and Android, with each library stored entirely in a user-selected location on the local device.

Imported files are copied into application-managed storage. Library records, folders, metadata, file relationships, and generation history are stored in SQLite, while API credentials remain in the operating system's secure storage. Libraries are independent, portable between supported Windows installations where the filesystem permits it, and are not synchronized or backed up by SlopFactory.

The current development build implements the local-library foundation, including:

- configurable local libraries and persistent library identity;
- managed file import, hashing, duplication, rename, and move operations;
- virtual folders, breadcrumbs, and file sorting;
- a read-only UTF-8 text viewer and non-destructive **Edit as Copy** workflow;
- a verified image viewer with fit, zoom, pan, viewing rotation, and sanitized SVG;
- typed user metadata and sensitive-value concealment;
- directed file links with recycle and restore behavior;
- searchable aggregate recycling with conflict-aware bulk recovery, retryable permanent deletion, and empty-bin processing;
- transactional SQLite schema upgrades; and
- shared .NET MAUI application projects for Windows and Android.

AI-provider connections, generation workflows, additional media viewers, advanced search, export, and release-resilience features remain under development. The outstanding product requirements are tracked in [plan.md](plan.md).

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
