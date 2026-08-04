# Library format and persistence

## Root layout

```text
<library>/
├── slopfactory-library.json
├── library.sqlite3
├── library.sqlite3-shm        # present while WAL is active
├── library.sqlite3-wal        # present while WAL is active
├── .slopfactory.lock
├── .staging/
└── media/
```

The manifest contains the format identity `mellow.slopfactory.library`, manifest version, persistent library ID, display name, and SQLite schema version. The database repeats the library identity so the two files must validate together.

The lock file is opened with exclusive sharing while a workspace is active. Its mere presence is not treated as an active lock, so a stale file after a crash can be reopened when no process holds it.

## SQLite schema version 4

The schema contains:

- `library_info`: library identity, display name, schema version, and permanent folder IDs;
- `folders`: the virtual folder tree and recycle state;
- `files`: editable display identity, retained original filename, managed filename, SHA-256 hash, byte size, detected type, provenance, timestamps, and recycle state;
- `metadata_entries`: typed user metadata owned by a file;
- `file_links`: directed, labelled file relationships, including whether recycling was an explicit user action or was inherited from an endpoint;
- `permanent_deletion_failures`: the most recent sanitized failure and UTC timestamp for a pending file or folder aggregate.

Foreign keys are enabled for every connection. Mutating aggregate operations use transactions. Persistent timestamps are UTC round-trip strings, and IDs are opaque 128-bit values encoded as lowercase 32-character hexadecimal strings.

Active file and folder names are unique within their parent using normalized invariant comparison keys. Managed filenames derive from the opaque file ID and a content-detected safe extension, independently of the display name.

## Schema upgrades

Opening an older library upgrades it to version 4 before normal access. Version 2 introduced explicit-link recycling ownership; version 3 added permanent-deletion failure records; version 4 adds the retained original filename used by library search. Libraries upgraded from version 3 backfill that field from the display name because the earlier format did not retain a distinct imported name. After obtaining the exclusive lock, SlopFactory checkpoints SQLite, creates `library.sqlite3.upgrade-backup`, applies every required database change in one transaction, and atomically updates the manifest. Success removes the rollback copy. Failure restores the original database and manifest and leaves the library closed. Media bytes are not copied during a schema upgrade, and libraries declaring a newer schema are rejected.

## Library browsing queries

`BrowseFilesAsync` validates a page size of 1–200 records and queries only active file rows. Callers choose current-folder or entire-library scope, a detected media category, optional origin and UTC import boundaries, a stable sort, and an offset. Every sort ends with normalized name and opaque ID tie-breakers so adjacent pages cannot reorder equal values.

Name and metadata search uses escaped parameterized `LIKE` expressions; `%`, `_`, and backslash in user input remain literal characters. JSON metadata is traversed with SQLite JSON functions and contributes only property names and scalar string, finite-number, and Boolean values. The query never reads managed file bodies. Its projection returns safe match reasons rather than snippets: a non-sensitive metadata match may return one key, while a sensitive match returns only a generic reason.

Typed metadata filtering normalizes the key and validates the operator/value pair before SQL construction. Every value remains a bound parameter. Separate aggregate subqueries count missing keys and incompatible stored types among files which pass the other browser criteria. Unicode text comparisons use registered ordinal-ignore-case functions, decimal numbers and `DateTimeOffset` instants use exact registered comparators, ISO dates compare lexically, and JSON uses a bounded, validated structural comparator. JSON objects compare by ordinal property name without order significance, arrays retain order, and arbitrary JSON number notation is normalized to an integer significand plus base-10 power for exact equality. Filter match reasons never contain comparison values.

The GUI retains folder, query, filters, sort, offset, and view mode in the active library-state service. That state survives component navigation during the application session and is reset when the active library changes.

## Import commit protocol

1. Inspect and hash the external source.
2. Check the library for the same SHA-256 algorithm, digest, and byte size.
3. Stream the source into a uniquely named file under `.staging` while hashing it again.
4. Reject the import if the source changed between inspection and copy.
5. Move the complete staging file into `media`.
6. Commit its database record.
7. Remove the managed file if the database commit fails.

Files are never loaded completely into memory by the import or hashing implementation.

`ImportWithProgressAsync` reports item/stage and byte progress during both the initial source digest and the independent copy-and-hash pass. Cancellation during a file deletes its unique staging and not-yet-committed managed paths, returns that item and all remaining candidates as `Cancelled`, and retains earlier committed imports. Duplicate skips and ordinary failures remain independent results. The GUI freezes the selected path set behind a review, destination, and duplicate-policy choice before calling this operation.

## In-library duplication

Duplication streams the managed source through a staging file while calculating SHA-256 again. The calculated digest and byte count must match the source record before the staged bytes are committed under a new opaque managed name. The new `UserCopy` file row and copied user-metadata rows commit in one database transaction; failure removes the new managed bytes. User-created links are not copied.

## Edited text copies

`CreateEditedTextCopyAsync` encodes edited content as strict UTF-8 without a byte-order mark and enforces a 4 MiB UTF-8 editor boundary. Preserved JSON and XML content passes bounded structured validation before any file is written; DTD processing and external XML resolution are disabled. Plain-text and Markdown choices assign controlled `.txt` and `.md` managed extensions.

The bytes are written under `.staging`, hashed, and moved to a new opaque managed name. The `EditedCopy` file row and any explicitly selected metadata rows commit together. Ordinary metadata copying excludes sensitive entries unless the caller supplies the separate sensitive-metadata consent. A failed database commit removes the new managed bytes, and the original source is never opened for writing.

Metadata mutations and the owning file's `modified_at` update share one SQLite transaction. They do not rewrite `imported_at`, `source_last_modified`, or managed content.

## Text search and Markdown rendering

`SearchTextFileAsync` streams strict UTF-8 through bounded character buffers and scans the complete active managed file without building a content index or loading the file into memory. Searches are single-line, limited to 256 Unicode scalars, optionally case-sensitive, and count all overlapping occurrences. Only a caller-bounded set of snippets is retained; the default UI retains 200 while continuing to count later matches. Buffer overlap preserves matches crossing read boundaries and provides bounded surrounding context.

`RenderMarkdownFileAsync` is limited to complete Markdown inputs of at most 262,144 characters. `SafeMarkdownRenderer` recognizes a deliberately bounded set of block and inline constructs and emits only a fixed HTML element vocabulary. Every source text and destination is HTML-encoded. Raw HTML is rendered as text, image syntax produces an inert textual reference, and links render without `href` attributes. Allowed HTTP, HTTPS, and mail destinations are returned separately for the GUI's explicit review and operating-system handoff.

The Blazor WebView document also applies a restrictive content-security policy which blocks frames, objects, non-application scripts, form submission, and automatic remote resources. A rendering rejection leaves the verified plain-text viewer available.

## Recycle semantics implemented so far

File and folder recycling is a logical state change. Folder recycling uses recursive SQLite CTEs so the subtree changes in one transaction. Link state is recalculated atomically: an endpoint-owned link is active only when both endpoints are active and returns automatically when both endpoints are restored. Explicit link recycling is tracked separately so endpoint restoration cannot undo the user's deletion; those links support restore and permanent deletion from the recycle bin.

Permanent file deletion first marks the database row `PendingPermanentDeletion`, removes the validated managed path, and then removes the database aggregate. A missing managed file is already removed; a directory or reparse point substituted at that path is rejected. Failures deliberately leave the pending row for an explicit retry instead of making it restorable again.

Permanent folder deletion marks the entire folder subtree and its files pending in one transaction, then removes each regular managed file. After all paths are absent, another transaction deletes descendant file aggregates before the folder tree so foreign-key ownership remains valid. A partial physical deletion is retryable: already-removed paths are skipped, remaining paths are attempted again, and the database aggregate stays pending until completion.

A known file/folder deletion failure is sanitized before it is upserted into `permanent_deletion_failures`; exact filesystem paths and uncontrolled platform exception text are not retained. The recycle-bin projection joins the latest failure so its explanation and timestamp survive reopening the library. A retry replaces the row if it fails again. Successful file deletion clears its failure in the same transaction as the file row; successful folder deletion clears failures for the complete owned subtree before deleting those aggregates in the same transaction.

Recycle-bin queries return only top-level folder aggregates and independently recycled files. Files owned by a recycled or pending folder subtree remain queryable for integrity and deletion work but are not presented as separate user-managed recycle entries.

`GetRecycleBinEntriesAsync` projects those aggregates and explicitly recycled links into one read model. Each entry includes its entity type, original folder path or endpoints, deletion state and time, and counts of folders, files, and links affected by the aggregate. Recursive folder counts and original paths are computed in SQLite without exposing descendant records as separate recycle-bin entries.

Batch restore and permanent-delete operations de-duplicate references and process each top-level aggregate independently. Restore orders files and folders before explicit links so link endpoints can become available first. Permanent deletion orders explicit links before files and folders so an independently recycled link is not reported missing after an endpoint cascade. Known per-item filesystem and validation failures are sanitized and returned in an operation result; cancellation still stops the batch immediately. Empty-bin processing uses the same permanent-delete path rather than a separate deletion implementation.

Restore preview is selection-aware. SQLite checks active file/folder name collisions at every original location and reports unavailable original parents. The workspace verifies that every affected managed path still resolves to a regular, non-reparse file before either preview approval or direct restoration. Files owned by selected, unblocked file/folder aggregates are treated as future-active endpoints while explicit-link dependencies are evaluated. The preview returns effects and blocking reasons per top-level item; batch restoration receives only approved references, retains its files/folders-before-links ordering, and still revalidates each invariant during mutation.

## Integrity scanning

`RunIntegrityScanAsync` is an explicit, non-mutating full scan. It rereads and validates the manifest, executes SQLite `PRAGMA quick_check`, verifies required directories, and enumerates only the top level of managed media storage. Database records in active, recycled, missing, changed, or replaced states are included; records pending permanent deletion are excluded because absent bytes can be an expected intermediate state.

Each recorded path must be a regular non-reparse file. The scanner compares its byte size and streams it through SHA-256, including recycled files. Unrecorded regular files are reported as orphans, while directories and redirected entries are reported as unsafe. It never follows such entries, traverses below managed storage, changes database health states, adopts content, or deletes anything. Findings contain issue type, optional opaque record ID, and byte counts, but not display names, managed paths, digests, metadata, or content. Cancellation is caught at scan boundaries and returned as an incomplete report containing the findings accumulated so far.

The workspace owns a single asynchronous mutation gate. Every current mutating API acquires it with the caller's cancellation token; read APIs do not. A full scan acquires the same gate before manifest validation and holds it through its final media entry, releasing it in `finally` on success, failure, or cancellation. Multi-item recycle operations acquire the gate once and call private mutation cores, which prevents both interleaved scans and self-deadlock. This is an in-process coherence boundary rather than a substitute for the library's cross-process exclusive lock.
