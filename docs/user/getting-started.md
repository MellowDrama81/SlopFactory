# Getting started

## First launch

SlopFactory creates and opens an empty default library on first launch. The library contains a permanent root and a permanent **Generated** folder.

- On Windows, the development build stores the default library under the current user's local application-data directory at `Mellow\SlopFactory\Library`.
- On Android, it stores the default library in internal app-specific storage.

The library is local to that location. Its database and media are not application-level encrypted, so the signed-in Windows account or unlocked Android device is the local security boundary.

## Changing libraries

Open **Library settings** to see the active library's name and location or change its display name. On Windows, enter an absolute path on a local fixed or removable drive. On Android, choose one of the currently available internal or external app-specific storage locations; arbitrary shared-storage paths are not accepted.

An empty selected directory becomes a new library. A location containing a valid SlopFactory library is opened, while a non-empty unrelated directory is rejected. Switching remembers the new active location for the next launch and never copies, moves, or deletes the previous library.

Library settings keeps a device-local **Recent libraries** list showing each library's display name, storage path, last-opened time, and current availability. An available remembered library can be opened directly. Selecting a new path for the same library ID relinks it when the former path is unavailable; if both copies are available, SlopFactory rejects the duplicate-ID conflict rather than treating them as independent libraries. A location inside or containing another known library is also rejected.

**Forget** removes an inactive library from this device's recent list and clears its regenerable preview-cache namespace. It does not modify or delete the library directory, its records, or its managed files. Opening the valid directory again registers it again.

## Importing files

1. Open **Library**.
2. Open the folder that should receive the files.
3. Select **Import files**.
4. Choose one or more files in the operating-system picker.

SlopFactory then opens **Review import** without copying anything into the library. Choose any available recent library as the target, choose an active destination folder within it, and remove unwanted selections. **Import all byte-identical files** applies a default choice to the current review, while each item also has its own **Import this item if it is a duplicate** control. Changing the target opens that library normally without moving data. **Import files** begins only after this review.

SlopFactory copies every successful import into managed storage and calculates a SHA-256 content hash. It never treats the selected external file as its managed copy.

When the same bytes already exist in the library, the default import skips the duplicate and reports it in the completion summary. An active match can be opened directly. A recycled match offers **Restore existing**, which uses the normal restore checks for names, managed content, and link dependencies; a record pending permanent deletion is identified as unavailable for restore. A failed file does not undo files that already imported successfully.

Hashing and managed copying show the current file, item count, stage, and byte progress. **Cancel remaining import** removes the active staging copy, keeps files which already committed, and marks the active and not-yet-started items cancelled. The per-file results distinguish imported, duplicate-skipped, failed, and cancelled items. Imported records and skipped matches provide direct open actions.

On Android, **Share to SlopFactory** accepts one or several files. On Windows, supported local file types can use **Open with SlopFactory**, and files or folders can be dragged onto the application. A dropped folder's visible, regular-file hierarchy is shown in review and recreated as virtual folders below the selected destination; hidden and redirected entries are excluded. Incoming provider streams are first copied into a private device-cache staging area so SlopFactory does not need a long-lived external permission. This does not create a library record: a banner points to the same **Review import** screen, and removing or cancelling a staged item deletes its temporary copy. Successfully processed and failed staged items are also cleaned after the reviewed operation. An expired permission or unreadable provider item is reported without exposing its external URI.

## Creating folders

Select **New folder**, enter a name, and select **Create**. Folders are virtual library records: they organize files without exposing or rearranging the internal managed-media directory.
