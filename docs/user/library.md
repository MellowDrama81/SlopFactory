# Managing the local library

On Windows, libraries must use a fixed or removable local drive. Network/UNC paths, unsupported volume types, and redirected directory entries are rejected because SlopFactory requires reliable local locking and atomic file operations. On Android, choose one of the offered app-specific storage locations.

Before using a Windows location which appears to be inside OneDrive, Dropbox, or Google Drive, SlopFactory requires a separate confirmation. Cloud synchronisation is not supported and can corrupt or duplicate a library if it modifies files concurrently.

Windows locations marked by the operating system as online-only placeholders are rejected until they are made fully available on the local device.

On Windows, **Open library location** is available in Library settings after a warning that the directory and its opaque filenames are application-managed. It opens the location for inspection only; it does not release the library lock or make external edits supported.

## Browsing

The Library page lists child folders and active files in the current folder. The breadcrumb path moves directly to any ancestor. Each file shows its detected media type and byte size. Choose **List** or **Grid** without changing any files.

The file browser searches display names, retained original filenames, and typed user metadata. Choose whether the query covers only the current folder or the entire library. JSON metadata search considers property names and scalar string, finite-number, and Boolean values; JSON punctuation and formatting are not search terms. SlopFactory does not search file bodies or show file-content snippets.

Results can be filtered by detected media category, origin, and an inclusive local import-date range, then sorted by name, newest import, newest library modification, largest size, or media type. Results are shown in pages of 48 files. A match label explains whether a name or metadata caused a hit. Non-sensitive metadata may identify its matching key, while a sensitive entry uses only **Matched user metadata** and never exposes the key or value.

Enable **Filter by typed metadata** to select one key, its required type, and an operator appropriate to that type. Text comparisons ignore case; numbers, dates, and offset-aware date-times support equality and range operators; Booleans support equality; and JSON supports existence plus structural equality. JSON structural equality ignores object-property order and formatting, treats numerically equivalent notation as equal, and preserves array order. A JSON `null` value exists and remains distinct from a missing key. The comparison field is concealed and retained only for the current library session. Results report how many otherwise eligible files lack the key or use it with another type; values are never coerced implicitly.

Opening a file and returning to the Library page restores the current folder, query, scope, filters, sort, page, and list/grid choice for the current application session. Switching libraries starts a separate default browser state.

Raster images and MP4 videos acquire static thumbnails in the background. Until one is ready, or when the platform decoder cannot safely create one, the file keeps its type icon and remains usable. The preview is regenerable device data rather than part of the library.

Choose **Manage** beside a file or non-permanent folder to change its display name or move it to another folder. Moving or renaming a library item does not move or rename the application-managed bytes. A folder cannot be moved into itself or below one of its descendants, and an existing active name is never silently replaced.

Choose **Duplicate** beside an active file to create an independent copy in a selected library folder. The copy receives a new identity and managed file, retains the source's user metadata, and does not inherit its editable file links. Name conflicts must be resolved before the copy is created.

Select files on any result page to build one selection across pages. The selection bar can select the current page, clear the selection, or open a review for moving, recycling, or changing user metadata. A move review shows the destination and affected count. A recycle review explains that each file's metadata and owned links follow it into the recycle bin.

Import rechecks each selected regular file immediately after hashing and before its managed copy is committed. A source that disappears, changes during preparation, is a folder, or is a redirected/symbolic-link entry is rejected individually; unrelated selected files can still complete.

When the platform reports available capacity, SlopFactory checks it against the selected file's known size before beginning the managed copy. This is an advisory early warning: storage can still change during an import, in which case the failed item is cleaned up and reported without changing already committed library data.

Choose **Duplicate** in the selection bar to copy selected files into one destination folder. Each file is handled independently, keeps its user metadata, receives a new identity and managed file, and does not copy editable links. Conflicting names use the normal numeric suffix. The result reports failures without removing unrelated successful copies.

Bulk metadata review lists keys common to every selected file, identifies mixed types or values, and never reveals an existing sensitive value. Adding or replacing a typed key shows how many existing entries will be overwritten; removal shows how many files currently contain the key. Confirmation processes each file independently and reports any failures by file, so one conflict or unavailable record does not undo successful changes to the others.

Use **Review mark sensitive** or **Review make ordinary** with a metadata key to change that flag across a selection without re-entering values. The review counts entries and missing keys without displaying concealed values. Making metadata ordinary explicitly warns that its values become visible in file details and are eligible for ordinary metadata export when that feature is available.

## File details and metadata

Open a file to view its immutable system information, including its SHA-256 content hash, media type, origin, import time, and current state.

If a display-name extension conflicts with the media type detected from the imported bytes, file details retain a persistent warning. Renaming a file never changes its detected type, viewer choice, provider compatibility, or safety classification.

SlopFactory retains the filename supplied at import separately from the editable display name. Renaming a file does not change that original filename, and either name can locate the file through Library search. Copies retain the imported ancestor's original filename; an edited text copy starts with its own chosen filename.

Supported UTF-8 text, Markdown, JSON, XML, CSV, and common source-code files open in a read-only, wrapping text viewer. Text can be selected and copied normally. To keep the interface responsive, the plain-text viewer displays at most the first 1,048,576 characters and identifies a truncated display; the complete managed file remains unchanged.

**Find in file** searches the complete managed text, including content beyond that displayed prefix. Search can match case or ignore it, counts every occurrence, and makes up to the first 200 matches available with bounded context and previous/next navigation. It does not create a library-wide file-content index.

Markdown files up to 262,144 characters can switch between plain text and **Rendered Markdown**. The renderer accepts common headings, paragraphs, lists, quotations, fenced code, emphasis, links, and image references, but it never passes source HTML into the WebView. Source HTML is displayed as text, image references remain inert text, and remote resources are never loaded. If bounded rendering cannot complete, SlopFactory keeps the plain-text view available.

Rendered Markdown links are inert. SlopFactory lists their labels and complete destinations separately; **Review link** shows the destination again and requires confirmation before asking the operating system to open it. Unsupported or potentially executable URI schemes are never offered as external actions.

For a text file that fits in the built-in editor, choose **Edit as Copy** to change its content without modifying the original. Choose the new display name and destination, and either preserve the detected source format or explicitly save plain text or Markdown. Preserved JSON and XML are validated before saving. The new managed file is UTF-8 without a byte-order mark and has origin **Edited Copy**.

User metadata is not copied by default. **Copy user metadata** includes non-sensitive entries; when sensitive entries exist, a separate unchecked option shows their count and must be selected to include them. Sensitive values are never displayed by that option, and reveal state is not transferred.

PNG, JPEG, WebP, and GIF files up to 32 MiB open in the built-in raster image viewer. Controls switch between fit and actual size, zoom from 25% to 400%, pan oversized images, and rotate the view in 90-degree steps. Rotation and other viewing controls never rewrite the managed bytes. The viewer verifies the current byte size and SHA-256 hash before display.

For supported raster images, file details also show dimensions read from a bounded technical-metadata probe after verifying the managed bytes. SVG dimensions remain unavailable rather than being inferred from potentially complex markup. SlopFactory does not extract location, device, author, face, or other descriptive embedded metadata.

SVG files use the same viewing controls only after SlopFactory parses and sanitizes them. The sanitizer removes scripts, event handlers, foreign elements and namespaces, embedded styles, and non-local references before the image enters the WebView. Sanitization changes only the temporary viewing representation, not the original managed SVG.

Supported audio files provide play, pause, seek, time, volume, mute, and playback-speed controls. MP3, WAV, and AAC/M4A are built in; FLAC and Opus playback depends on the codecs available to the Windows or Android media stack. Supported video uses MP4 with H.264 video and AAC audio and also provides full-screen controls. Embedded caption or subtitle choices appear when the platform exposes them.

SlopFactory verifies the complete managed media file against its recorded size and SHA-256 hash before enabling playback. Playback then streams bounded byte ranges from managed storage through a short-lived internal address; the application never places the library path in the page. Media does not autoplay, starting one player pauses another, and leaving the file page stops playback and revokes its internal address. Viewer controls never modify managed bytes.

Before a raster image reaches the browser decoder, SlopFactory validates its encoded dimensions, total pixel count, and bounded animation complexity. A file beyond those limits remains intact and active but shows **Preview Too Complex or Large** instead of being decoded.

GIF animations open on a static cached frame. **Play animation** explicitly loads the animation; **Pause animation** returns to the static frame. Reopening the viewer starts paused again.

**Library settings** shows current preview-cache use and its device-wide limit. The default is 1 GiB on Windows and 256 MiB on Android; it can be set from 64 MiB to 8 GiB. Least-recently-used entries are removed when needed. **Clear preview cache** removes only regenerable thumbnails and posters, never original files, records, metadata, or links.

**Rebuild library previews** first clears the active library's regenerable preview entries, then regenerates eligible image thumbnails and video posters with progress. A decoder failure is reported as an unavailable preview; it never alters the original file or library record.

You can attach typed user metadata as text, number, Boolean, date, date-time, or JSON. Metadata keys are case-insensitively unique per file. The `slopfactory.` prefix is reserved for system data.

**Date** is a calendar date. **Date-time** requires an explicit UTC offset: its original offset-bearing value is retained for faithful editing, while file details also show the same instant in the device's local time. Date-time filtering and comparison use the UTC instant, so equivalent values match across devices.

Mark an entry **Sensitive** to conceal its value in the interface. **Reveal for session** reveals only that entry in the running application session. This flag is a display safeguard, not encryption.

The first attempt to create or bulk-mark sensitive metadata explains that the setting affects SlopFactory display, search-state, and future export safeguards but does not encrypt the stored value. You must acknowledge that explanation once on the device before the change proceeds.

Metadata keys can be renamed without changing their stored type, sensitivity flag, or value.

Changing a display name or user metadata never creates a content copy.
Metadata additions, updates, renames, and removals advance the file's **Library modified** time without changing its authoritative import time or original bytes.

## Managed-content health

File details show a content-health status and provide **Verify now**. SlopFactory checks that the application-managed path is a regular file, then compares its byte size and SHA-256 hash with the library record. Built-in text, image, audio, and video use performs the same verification before exposing bytes.

If the managed file is absent, the record becomes **Missing**. If bytes differ or the managed path was replaced by an unsafe redirected entry, it becomes **Content changed**. These statuses do not recycle the record: its folder identity, user metadata, and file links remain available, and the broken record can still be recycled normally. Changed bytes are not silently accepted and normal built-in viewing remains blocked. Putting back bytes with the exact recorded size and hash and verifying again restores **Healthy** status.

For a regular changed file, **Inspect changed bytes** calculates and displays its current hash, size, and detected type without accepting or changing it. Supported UTF-8 text can also be read in the bounded, read-only text inspector. Redirected, hard-linked, missing, or unsafe paths cannot be inspected.

For a missing or changed file, **Choose replacement file** opens a review comparing the immutable recorded-original hash, size, and detected media type with the candidate. A changed file also provides **Accept current bytes as replacement**. The candidate is inspected again when you confirm, so a file changed after review is rejected.

An exact original restores **Healthy** status without marking the file as replaced. Different bytes require an explicit permanent-replacement confirmation and produce **Content replaced** status. The file keeps its stable identity, folder, links, and user metadata by default; the review counts ordinary and sensitive metadata without displaying sensitive keys or values. An unchecked option can clear all user metadata in the same transaction as accepting different content. There is no retained content version or undo after a differing replacement succeeds.

File details continue to show the immutable original identity beside the current replacement identity and replacement time. Healthy files cannot be overwritten through this repair workflow; import unrelated content as a new file instead.

While a library is open, SlopFactory watches its managed-media directory as a best-effort early warning. Events are debounced and mapped through opaque managed filenames. Expected file creation from a completed import or copy is revalidated as healthy and remains silent. A missing or changed record produces a global **Managed content needs review** notice linking to file details. Filesystem watcher overflow or a failed revalidation recommends the explicit full integrity scan because operating-system watcher events can be coalesced or missed.

The manifest and SQLite database receive stricter treatment. A detected change waits for any current SlopFactory mutation, pauses later mutations at the same boundary, and revalidates the required entries, exact open-library identity, and database integrity. If that validation fails—or those critical files can no longer be monitored—the active library is closed instead of continuing against uncertain state. Its remembered location remains visible so you can inspect or relink it deliberately; SlopFactory does not silently create or open another library.

On Windows, SlopFactory also rejects a managed file when it detects that the file is hard-linked. A hard link can make the same bytes reachable outside the library, so viewing, replacement, restoration, and permanent deletion stop for review even if the file’s hash still matches. A full integrity scan reports it as an unsafe managed entry.

## Copied libraries

SlopFactory rejects opening a second available location with the same library ID as an existing library. This prevents two directory copies from being mistaken for synchronized libraries. If you intentionally copied a library and need the copy to diverge, choose **Adopt copied library** after the duplicate-location warning and confirm it. Adoption gives the selected copy a new persistent library ID without copying, moving, or deleting its managed files, folders, metadata, links, or local history. The original and adopted copies then remain permanently independent.

## File links

The file-details page can create a directed, labelled link from the current file to another active file. A file cannot link to itself, while the same pair of files may have multiple links with different labels. Existing active links can be relabelled, reversed, or moved to the recycle bin. SlopFactory blocks a relabel or reversal if it would duplicate an existing directed link.

An explicitly recycled link appears as its own recycle-bin item and can be restored only while both endpoint files are active. A link made inactive because an endpoint file was recycled is owned by that file lifecycle instead: it does not appear as an unrelated recycle-bin item and automatically becomes active when both files return. Permanently deleting either endpoint removes the link.

## Recycle bin

Recycling a file hides it from the active library while leaving its managed bytes in place. Its metadata remains attached. Recycling a folder also recycles all folders and files below it. The recycle bin shows that deleted subtree once as a top-level folder aggregate instead of exposing every owned file and descendant as unrelated entries.

Open **Recycle bin** to restore a file, folder, or explicitly recycled file link. Restoring a folder restores its descendant hierarchy. An individual file, complete folder subtree, or link can also be permanently deleted.

Permanent deletion first changes the file or folder aggregate to **Pending Permanent Deletion**, where it cannot be restored. Managed bytes are removed before their database aggregates. If deletion is interrupted or a managed path is unsafe, the pending item remains visible with **Retry permanent deletion**; bytes already missing are treated as removed so an interrupted operation can finish safely. The most recent sanitized failure explanation and its time remain visible after page reloads and application restarts. A later failure replaces that explanation, while successful permanent deletion removes it with the aggregate.

The page can search names and original locations, filter by files, folders, or file links, and sort by deletion time, name, or entity type. Select individual entries or all currently shown entries to restore or permanently delete a group. **Empty recycle bin** processes every current entry. Selection is retained across filters until it is cleared or processed.

Every permanent-deletion action first shows the selected top-level aggregates and totals for affected folders, files, and links. Permanent deletion cannot be undone. Batch items are processed independently: if one fails, unrelated items continue, and the page lists each failed item while leaving it available for retry.

Restore actions also open a review before making changes. The review lists each folder hierarchy, file with attached metadata, and file link which will be restored, plus endpoint-owned links which may reactivate automatically. It blocks an item when an active file or folder already uses a restored name, an original parent is unavailable, managed file content is missing or was replaced by an unsafe filesystem object, or a link endpoint is unavailable. Selecting a recycled endpoint file or its owning folder together with an explicit link satisfies that dependency when the endpoint aggregate has no blocker. Unblocked selections can proceed independently while blocked items remain unchanged for later review.

## Integrity scan

Open **Library settings** and choose **Start full integrity scan** to explicitly inspect the current library. The scan validates the manifest and SQLite database, checks required storage directories, and reads and hashes every active and recycled managed file. It reports missing files, size or content-hash mismatches, unsafe filesystem entries, inaccessible content, and regular files in managed storage which have no database record.

The scan is read-only: it never deletes an orphan, adopts changed bytes, creates a record, changes a file's state, or repairs the database. Findings display issue categories, opaque record IDs, and relevant byte counts without displaying filenames, managed paths, content hashes, metadata, or file contents. Progress remains visible while hashing. **Cancel scan** stops at a safe boundary and keeps the findings collected so far, clearly labelled as an incomplete partial result.

After a scan, **Export findings as JSON** opens the device's save/share flow. The versioned default report contains the library ID, schema version, scan timestamps and completion status, plus each finding's category, opaque record ID, byte counts, and summary. The page previews those fields before export; display names, original filenames, paths, hashes, metadata, prompts, credentials, and file bytes are not included.

To keep the report coherent, the scan waits for the current library change to finish and then pauses new imports, edits, metadata/link changes, folder changes, recycle operations, permanent deletions, and library renames until the scan finishes or is cancelled. A complete bulk recycle operation holds the same boundary, so a scan cannot begin between its individual items. Read-only library browsing and verified file viewing remain available. A queued change respects its own cancellation request while it waits.
