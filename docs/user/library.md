# Managing the local library

## Browsing

The Library page lists child folders and files in the current folder. The breadcrumb path moves directly to any ancestor. Each file row shows its detected media type and byte size, and the file list can be sorted by name, import date, modification date, size, or media type.

Choose **Manage** beside a file or non-permanent folder to change its display name or move it to another folder. Moving or renaming a library item does not move or rename the application-managed bytes. A folder cannot be moved into itself or below one of its descendants, and an existing active name is never silently replaced.

Choose **Duplicate** beside an active file to create an independent copy in a selected library folder. The copy receives a new identity and managed file, retains the source's user metadata, and does not inherit its editable file links. Name conflicts must be resolved before the copy is created.

## File details and metadata

Open a file to view its immutable system information, including its SHA-256 content hash, media type, origin, import time, and current state.

Supported UTF-8 text, Markdown, JSON, XML, CSV, and common source-code files open in a read-only, wrapping text viewer. Text can be selected and copied normally. To keep the interface responsive, the viewer displays at most the first 1,048,576 characters and identifies a truncated display; the complete managed file remains unchanged.

For a text file that fits in the built-in editor, choose **Edit as Copy** to change its content without modifying the original. Choose the new display name and destination, and either preserve the detected source format or explicitly save plain text or Markdown. Preserved JSON and XML are validated before saving. The new managed file is UTF-8 without a byte-order mark and has origin **Edited Copy**.

User metadata is not copied by default. **Copy user metadata** includes non-sensitive entries; when sensitive entries exist, a separate unchecked option shows their count and must be selected to include them. Sensitive values are never displayed by that option, and reveal state is not transferred.

PNG, JPEG, WebP, and GIF files up to 32 MiB open in the built-in raster image viewer. Controls switch between fit and actual size, zoom from 25% to 400%, pan oversized images, and rotate the view in 90-degree steps. Rotation and other viewing controls never rewrite the managed bytes. The viewer verifies the current byte size and SHA-256 hash before display.

SVG files use the same viewing controls only after SlopFactory parses and sanitizes them. The sanitizer removes scripts, event handlers, foreign elements and namespaces, embedded styles, and non-local references before the image enters the WebView. Sanitization changes only the temporary viewing representation, not the original managed SVG.

You can attach typed user metadata as text, number, Boolean, date, date-time, or JSON. Metadata keys are case-insensitively unique per file. The `slopfactory.` prefix is reserved for system data.

Mark an entry **Sensitive** to conceal its value in the interface. **Reveal for session** reveals only that entry in the running application session. This flag is a display safeguard, not encryption.

Metadata keys can be renamed without changing their stored type, sensitivity flag, or value.

Changing a display name or user metadata never creates a content copy.
Metadata additions, updates, renames, and removals advance the file's **Library modified** time without changing its authoritative import time or original bytes.

## File links

The file-details page can create a directed, labelled link from the current file to another active file. A file cannot link to itself, while the same pair of files may have multiple links with different labels. Existing active links can be relabelled, reversed, or moved to the recycle bin. SlopFactory blocks a relabel or reversal if it would duplicate an existing directed link.

An explicitly recycled link appears as its own recycle-bin item and can be restored only while both endpoint files are active. A link made inactive because an endpoint file was recycled is owned by that file lifecycle instead: it does not appear as an unrelated recycle-bin item and automatically becomes active when both files return. Permanently deleting either endpoint removes the link.

## Recycle bin

Recycling a file hides it from the active library while leaving its managed bytes in place. Its metadata remains attached. Recycling a folder also recycles all folders and files below it. The recycle bin shows that deleted subtree once as a top-level folder aggregate instead of exposing every owned file and descendant as unrelated entries.

Open **Recycle bin** to restore a file, folder, or explicitly recycled file link. Restoring a folder restores its descendant hierarchy. An individual file, complete folder subtree, or link can also be permanently deleted.

Permanent deletion first changes the file or folder aggregate to **Pending Permanent Deletion**, where it cannot be restored. Managed bytes are removed before their database aggregates. If deletion is interrupted or a managed path is unsafe, the pending item remains visible with **Retry permanent deletion**; bytes already missing are treated as removed so an interrupted operation can finish safely.

The page can search names and original locations, filter by files, folders, or file links, and sort by deletion time, name, or entity type. Select individual entries or all currently shown entries to restore or permanently delete a group. **Empty recycle bin** processes every current entry. Selection is retained across filters until it is cleared or processed.

Every permanent-deletion action first shows the selected top-level aggregates and totals for affected folders, files, and links. Permanent deletion cannot be undone. Batch items are processed independently: if one fails, unrelated items continue, and the page lists each failed item while leaving it available for retry.

Restore actions also open a review before making changes. The review lists each folder hierarchy, file with attached metadata, and file link which will be restored, plus endpoint-owned links which may reactivate automatically. It blocks an item when an active file or folder already uses a restored name, an original parent is unavailable, managed file content is missing or was replaced by an unsafe filesystem object, or a link endpoint is unavailable. Selecting a recycled endpoint file or its owning folder together with an explicit link satisfies that dependency when the endpoint aggregate has no blocker. Unblocked selections can proceed independently while blocked items remain unchanged for later review.

## Integrity scan

Open **Library settings** and choose **Start full integrity scan** to explicitly inspect the current library. The scan validates the manifest and SQLite database, checks required storage directories, and reads and hashes every active and recycled managed file. It reports missing files, size or content-hash mismatches, unsafe filesystem entries, inaccessible content, and regular files in managed storage which have no database record.

The scan is read-only: it never deletes an orphan, adopts changed bytes, creates a record, changes a file's state, or repairs the database. Findings display issue categories, opaque record IDs, and relevant byte counts without displaying filenames, managed paths, content hashes, metadata, or file contents. Progress remains visible while hashing. **Cancel scan** stops at a safe boundary and keeps the findings collected so far, clearly labelled as an incomplete partial result.
