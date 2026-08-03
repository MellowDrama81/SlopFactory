# Getting started

## First launch

SlopFactory creates and opens an empty default library on first launch. The library contains a permanent root and a permanent **Generated** folder.

- On Windows, the development build stores the default library under the current user's local application-data directory at `Mellow\SlopFactory\Library`.
- On Android, it stores the default library in internal app-specific storage.

The library is local to that location. Its database and media are not application-level encrypted, so the signed-in Windows account or unlocked Android device is the local security boundary.

## Changing libraries

Open **Library settings** to see the active library's name and location or change its display name. On Windows, enter an absolute path on a local fixed or removable drive. On Android, choose one of the currently available internal or external app-specific storage locations; arbitrary shared-storage paths are not accepted.

An empty selected directory becomes a new library. A location containing a valid SlopFactory library is opened, while a non-empty unrelated directory is rejected. Switching remembers the new active location for the next launch and never copies, moves, or deletes the previous library.

## Importing files

1. Open **Library**.
2. Open the folder that should receive the files.
3. Select **Import files**.
4. Choose one or more files in the operating-system picker.

SlopFactory copies every successful import into managed storage and calculates a SHA-256 content hash. It never treats the selected external file as its managed copy.

When the same bytes already exist in the library, the default import skips the duplicate and reports it in the completion summary. A failed file does not undo files that already imported successfully.

## Creating folders

Select **New folder**, enter a name, and select **Create**. Folders are virtual library records: they organize files without exposing or rearranging the internal managed-media directory.
