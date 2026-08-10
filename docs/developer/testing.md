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

The current 174 tests cover platform-version policy, initialization, manifest/database creation, copied-library adoption, local-storage path rejection, hard-link rejection, and open-library identity revalidation, exclusive locking, invalid library entries, recursive import inventory and frozen-source revalidation, primary-stream and zone handling, verified single/bulk/recovery export, external-open isolation, resumable integrity checkpoints, cross-library review isolation, safe managed import, hashing, progress, cancellation cleanup, single and bulk duplicate handling with direct provenance chains and deletion snapshots, folder/file organization, reviewed multi-file operations, bulk metadata sensitivity and type normalization, typed metadata ownership and filtering, redacted JSON validation, bounded media probing, privacy-minimised integrity-report serialization, responsive/focus UI assets, dialog focus restoration, and localization-resource wiring.

They also cover detected-media-type viewer allow-listing and preview-unavailable handling, bounded text, Markdown, raster-image (including temporary JPEG EXIF orientation with unchanged managed bytes and hash), SVG and media viewing; managed-content health, safe changed-byte inspection, and replacement; editable links; aggregate recycling, restoration and retryable permanent deletion; coherent integrity scans; culture-specific UI-resource fixtures; and version 1 through version 5, and version 14 through version 17, upgrades to the current schema (v18) with rollback cleanup. Preview-cache rebuilding is verified through the Windows and Android application builds.

They also cover connection and model validation, label uniqueness, recycle/restore/permanent-delete cascades between connections and their dependent models, model-catalogue refresh persisting discovered entries with a retrieval timestamp, a failed refresh marking the retained catalogue possibly-stale without clearing it or its timestamp, connection timeout override validation/persistence independent of test status, additional-connection-header validation (count/length/duplicate/reserved-name/credential-header rejection) and persistence, generic-connection per-modality settings defaulting to all-enabled/no-override plus relative-path-override validation, and provider-type changes being rejected while active dependent models exist but succeeding (and resetting generic-modality settings) once none remain (`ConnectionModelTests`), version 14 through version 17 upgrades to the current schema (v18) adding the model-catalogue cache, connection-timeout column, connection-headers table, and per-modality settings columns with rollback cleanup (`LibraryWorkspaceTests`), and the OpenAI and generic OpenAI-compatible provider adapters' connection-test, model-listing, chat-completion text-generation and images/generations behavior against a fake `HttpMessageHandler`, including authentication failure, an unreachable host, a missing model-listing endpoint, rate limiting, multi-candidate parsing for both text and images, a configured timeout throwing `ProviderAdapterException` distinctly from a bare cancellation, real caller-driven cancellation during a timed request still propagating as `TaskCanceledException`, additional headers being sent alongside the credential header, a per-modality relative-path override being used to build the request URL, and a disabled modality throwing `ProviderAdapterException` without the fake handler ever being invoked (`ProviderAdapterTests`, using a delay-aware `FakeHttpMessageHandler` overload that observes the request's cancellation token). `GenerationRecordTests` cover committing generated text and images (including real file-signature media-type detection for a PNG payload) as managed files linked to a generation-history record, and the no-files/sanitized-error path for a failed attempt of either mode. `SavedGenerationSettingTests` cover title uniqueness, model snapshotting, cascade recycle/restore/permanent-delete of saved settings from both their owning model and, through it, their owning connection, and persisting/clearing system instructions. `ProviderAdapterTests` also verify the chat-completion request includes a `system` message only when system instructions are supplied, that reported prompt/completion token usage is parsed when present and absent otherwise, and that supplying a source image switches the user message to the OpenAI vision multi-part content shape with a correctly encoded data URI. `GenerationRecordTests` and `SavedGenerationSettingTests` verify system instructions, token usage and a source-file reference all persist and reload correctly, including that permanently deleting the referenced source file clears the reference on existing generation history rather than leaving it dangling. The Connections, Models, Generate, generation-history and saved-settings pages have the same localization-guard coverage as other pages, verifying every application-owned UI string is resolved through `IStringLocalizer<UiStrings>` rather than hard-coded, including the generation-history **Use Again** link, its status/mode/model filters, the Generate page's third `/generate/history/{HistoryId}` route, its **Cancel** action, its **Improve Prompt** panel, the **Credentials Required**/**Unverified** connection-status text on both the Connections list and `/generate`, the per-model system-instructions capability note, and the insecure-HTTP base-URL warning on both the Connections list and `ConnectionEdit`. The cancellation flow, the prompt-improvement round trip, the credential/verification status gating, the system-instructions capability gating, and the live HTTP base-URL warning are all UI-interaction behavior not covered by an automated test; they are candidates for the manual test matrix. `LibraryRules.NormalizeConnectionBaseUrl`'s underlying validation (HTTPS-required, loopback/private HTTP allowance, embedded-credential rejection) is covered by `ConnectionModelTests`.

## Localization verification

`UiStrings.resx` is the neutral-English resource set. `UiStrings.en-AU.resx` is a small culture-specific fixture used to verify that localized values resolve instead of falling back to resource-key names. When adding application-owned UI text, add a stable descriptive key to the neutral resource set, preserve user data and diagnostic text as formatted arguments, and update the source-level localization guard for the affected component.

## Platform builds

```powershell
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-windows10.0.22621.0
dotnet build src\Mellow.SlopFactory.Gui\Mellow.SlopFactory.Gui.csproj --no-restore -f net10.0-android
```

## Manual verification

Run the applicable device checks in the repository's [manual test matrix](../../manual_tests.md) alongside the automated suite. Record the required platform, build, and results as described there.
