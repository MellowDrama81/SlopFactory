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

## SQLite schema version 2

The schema contains:

- `library_info`: library identity, display name, schema version, and permanent folder IDs;
- `folders`: the virtual folder tree and recycle state;
- `files`: display identity, managed filename, SHA-256 hash, byte size, detected type, provenance, timestamps, and recycle state;
- `metadata_entries`: typed user metadata owned by a file;
- `file_links`: directed, labelled file relationships, including whether recycling was an explicit user action or was inherited from an endpoint.

Foreign keys are enabled for every connection. Mutating aggregate operations use transactions. Persistent timestamps are UTC round-trip strings, and IDs are opaque 128-bit values encoded as lowercase 32-character hexadecimal strings.

Active file and folder names are unique within their parent using normalized invariant comparison keys. Managed filenames derive from the opaque file ID and a content-detected safe extension, independently of the display name.

## Schema upgrades

Opening a version 1 library upgrades it to version 2 before normal access. After obtaining the exclusive lock, SlopFactory checkpoints SQLite, creates `library.sqlite3.upgrade-backup`, applies the database change in a transaction, and atomically updates the manifest. Success removes the rollback copy. Failure restores the original database and manifest and leaves the library closed. Media bytes are not copied during a schema upgrade, and libraries declaring a newer schema are rejected.

## Import commit protocol

1. Inspect and hash the external source.
2. Check the library for the same SHA-256 algorithm, digest, and byte size.
3. Stream the source into a uniquely named file under `.staging` while hashing it again.
4. Reject the import if the source changed between inspection and copy.
5. Move the complete staging file into `media`.
6. Commit its database record.
7. Remove the managed file if the database commit fails.

Files are never loaded completely into memory by the import or hashing implementation.

## In-library duplication

Duplication streams the managed source through a staging file while calculating SHA-256 again. The calculated digest and byte count must match the source record before the staged bytes are committed under a new opaque managed name. The new `UserCopy` file row and copied user-metadata rows commit in one database transaction; failure removes the new managed bytes. User-created links are not copied.

## Edited text copies

`CreateEditedTextCopyAsync` encodes edited content as strict UTF-8 without a byte-order mark and enforces a 4 MiB UTF-8 editor boundary. Preserved JSON and XML content passes bounded structured validation before any file is written; DTD processing and external XML resolution are disabled. Plain-text and Markdown choices assign controlled `.txt` and `.md` managed extensions.

The bytes are written under `.staging`, hashed, and moved to a new opaque managed name. The `EditedCopy` file row and any explicitly selected metadata rows commit together. Ordinary metadata copying excludes sensitive entries unless the caller supplies the separate sensitive-metadata consent. A failed database commit removes the new managed bytes, and the original source is never opened for writing.

Metadata mutations and the owning file's `modified_at` update share one SQLite transaction. They do not rewrite `imported_at`, `source_last_modified`, or managed content.

## Recycle semantics implemented so far

File and folder recycling is a logical state change. Folder recycling uses recursive SQLite CTEs so the subtree changes in one transaction. Link state is recalculated atomically: an endpoint-owned link is active only when both endpoints are active and returns automatically when both endpoints are restored. Explicit link recycling is tracked separately so endpoint restoration cannot undo the user's deletion; those links support restore and permanent deletion from the recycle bin.

Permanent file deletion first marks the database row `PendingPermanentDeletion`, removes the validated managed path, and then removes the database aggregate. A missing managed file is already removed; a directory or reparse point substituted at that path is rejected. Failures deliberately leave the pending row for an explicit retry instead of making it restorable again.

Permanent folder deletion marks the entire folder subtree and its files pending in one transaction, then removes each regular managed file. After all paths are absent, another transaction deletes descendant file aggregates before the folder tree so foreign-key ownership remains valid. A partial physical deletion is retryable: already-removed paths are skipped, remaining paths are attempted again, and the database aggregate stays pending until completion.

Recycle-bin queries return only top-level folder aggregates and independently recycled files. Files owned by a recycled or pending folder subtree remain queryable for integrity and deletion work but are not presented as separate user-managed recycle entries.
