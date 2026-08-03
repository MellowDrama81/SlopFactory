# Managing the local library

## Browsing

The Library page lists child folders and files in the current folder. The breadcrumb path moves directly to any ancestor. Each file row shows its detected media type and byte size, and the file list can be sorted by name, import date, modification date, size, or media type.

Choose **Manage** beside a file or non-permanent folder to change its display name or move it to another folder. Moving or renaming a library item does not move or rename the application-managed bytes. A folder cannot be moved into itself or below one of its descendants, and an existing active name is never silently replaced.

Choose **Duplicate** beside an active file to create an independent copy in a selected library folder. The copy receives a new identity and managed file, retains the source's user metadata, and does not inherit its editable file links. Name conflicts must be resolved before the copy is created.

## File details and metadata

Open a file to view its immutable system information, including its SHA-256 content hash, media type, origin, import time, and current state.

Supported UTF-8 text, Markdown, JSON, XML, CSV, and common source-code files open in a read-only, wrapping text viewer. Text can be selected and copied normally. To keep the interface responsive, the viewer displays at most the first 1,048,576 characters and identifies a truncated display; the complete managed file remains unchanged.

You can attach typed user metadata as text, number, Boolean, date, date-time, or JSON. Metadata keys are case-insensitively unique per file. The `slopfactory.` prefix is reserved for system data.

Mark an entry **Sensitive** to conceal its value in the interface. **Reveal for session** reveals only that entry in the running application session. This flag is a display safeguard, not encryption.

Metadata keys can be renamed without changing their stored type, sensitivity flag, or value.

## File links

The file-details page can create a directed, labelled link from the current file to another active file. A file cannot link to itself, while the same pair of files may have multiple links with different labels. Existing active links can be relabelled, reversed, or moved to the recycle bin. SlopFactory blocks a relabel or reversal if it would duplicate an existing directed link.

An explicitly recycled link appears as its own recycle-bin item and can be restored only while both endpoint files are active. A link made inactive because an endpoint file was recycled is owned by that file lifecycle instead: it does not appear as an unrelated recycle-bin item and automatically becomes active when both files return. Permanently deleting either endpoint removes the link.

## Recycle bin

Recycling a file hides it from the active library while leaving its managed bytes in place. Its metadata remains attached. Recycling a folder also recycles all folders and files below it.

Open **Recycle bin** to restore a file, folder, or explicitly recycled file link. Restoring a folder restores its descendant hierarchy. A recycled individual file or link can also be permanently deleted.

Permanent deletion cannot be undone. The broader bulk-selection, empty-bin, and conflict-review workflows are still under development.
