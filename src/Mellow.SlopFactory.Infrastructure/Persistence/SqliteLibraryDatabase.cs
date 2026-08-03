using System.Globalization;
using Microsoft.Data.Sqlite;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Persistence;

internal sealed class SqliteLibraryDatabase
{
    private readonly string _connectionString;

    static SqliteLibraryDatabase()
    {
        SQLitePCL.Batteries.Init();
    }

    public SqliteLibraryDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public static async Task InitializeAsync(string databasePath, LibraryManifest manifest, string rootFolderId, string generatedFolderId, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string schema = """
            CREATE TABLE library_info (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                library_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                root_folder_id TEXT NOT NULL,
                generated_folder_id TEXT NOT NULL
            );

            CREATE TABLE folders (
                id TEXT PRIMARY KEY,
                parent_id TEXT NULL REFERENCES folders(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                name_key TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                recycled_at TEXT NULL
            );
            CREATE UNIQUE INDEX ux_folders_active_name ON folders(parent_id, name_key) WHERE state = 0;

            CREATE TABLE files (
                id TEXT PRIMARY KEY,
                folder_id TEXT NOT NULL REFERENCES folders(id),
                display_name TEXT NOT NULL,
                name_key TEXT NOT NULL,
                managed_name TEXT NOT NULL UNIQUE,
                content_hash TEXT NOT NULL,
                byte_size INTEGER NOT NULL CHECK(byte_size >= 0),
                media_type TEXT NOT NULL,
                origin INTEGER NOT NULL,
                state INTEGER NOT NULL,
                imported_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                source_last_modified TEXT NULL,
                recycled_at TEXT NULL
            );
            CREATE UNIQUE INDEX ux_files_active_name ON files(folder_id, name_key) WHERE state = 0;
            CREATE INDEX ix_files_content_hash ON files(content_hash, byte_size);
            CREATE INDEX ix_files_folder_state ON files(folder_id, state);

            CREATE TABLE metadata_entries (
                id TEXT PRIMARY KEY,
                file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                key TEXT NOT NULL,
                key_key TEXT NOT NULL,
                kind INTEGER NOT NULL,
                serialized_value TEXT NOT NULL,
                is_sensitive INTEGER NOT NULL,
                UNIQUE(file_id, key_key)
            );

            CREATE TABLE file_links (
                id TEXT PRIMARY KEY,
                source_file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                target_file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                label TEXT NOT NULL,
                label_key TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                recycled_at TEXT NULL,
                explicitly_recycled INTEGER NOT NULL DEFAULT 0,
                CHECK(source_file_id <> target_file_id),
                UNIQUE(source_file_id, target_file_id, label_key)
            );
            CREATE INDEX ix_file_links_source ON file_links(source_file_id, state);
            CREATE INDEX ix_file_links_target ON file_links(target_file_id, state);
            """;
        await ExecuteNonQueryAsync(connection, schema, cancellationToken, transaction).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO folders(id,parent_id,name,name_key,state,created_at,modified_at) VALUES($id,NULL,$name,$key,0,$now,$now);",
            cancellationToken,
            transaction,
            ("$id", rootFolderId), ("$name", "Library"), ("$key", LibraryRules.ComparisonKey("Library")), ("$now", now)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO folders(id,parent_id,name,name_key,state,created_at,modified_at) VALUES($id,$parent,$name,$key,0,$now,$now);",
            cancellationToken,
            transaction,
            ("$id", generatedFolderId), ("$parent", rootFolderId), ("$name", "Generated"), ("$key", LibraryRules.ComparisonKey("Generated")), ("$now", now)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO library_info(singleton,library_id,display_name,schema_version,root_folder_id,generated_folder_id) VALUES(1,$library,$name,$schema,$root,$generated);",
            cancellationToken,
            transaction,
            ("$library", manifest.LibraryId), ("$name", manifest.DisplayName), ("$schema", manifest.SchemaVersion), ("$root", rootFolderId), ("$generated", generatedFolderId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task CheckpointForBackupAsync(string databasePath, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
    }

    public static async Task UpgradeAsync(string databasePath, int fromVersion, CancellationToken cancellationToken)
    {
        if (fromVersion < 1 || fromVersion > LibraryRules.SchemaVersion) throw new LibraryValidationException($"Schema version {fromVersion} cannot be upgraded.");
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (fromVersion < 2)
        {
            var hasColumn = false;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA table_info(file_links);";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(reader.GetString(1), "explicitly_recycled", StringComparison.Ordinal)) hasColumn = true;
                }
            }
            if (!hasColumn)
            {
                await ExecuteNonQueryAsync(connection, "ALTER TABLE file_links ADD COLUMN explicitly_recycled INTEGER NOT NULL DEFAULT 0;", cancellationToken, transaction).ConfigureAwait(false);
            }
        }
        await ExecuteNonQueryAsync(connection, "UPDATE library_info SET schema_version=$version WHERE singleton=1;", cancellationToken, transaction, ("$version", LibraryRules.SchemaVersion)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryDescriptor> ValidateAndDescribeAsync(LibraryManifest manifest, string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT library_id,display_name,schema_version,root_folder_id,generated_folder_id FROM library_info WHERE singleton=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new LibraryValidationException("The library database has no identity record.");
        }
        var libraryId = reader.GetString(0);
        var displayName = reader.GetString(1);
        var schemaVersion = reader.GetInt32(2);
        if (!string.Equals(libraryId, manifest.LibraryId, StringComparison.Ordinal) || schemaVersion != manifest.SchemaVersion || !string.Equals(displayName, manifest.DisplayName, StringComparison.Ordinal))
        {
            throw new LibraryValidationException("The library manifest and database identity do not match.");
        }
        return new LibraryDescriptor(libraryId, displayName, rootPath, reader.GetString(3), reader.GetString(4), schemaVersion);
    }

    public async Task<LibraryFolderContents> GetFolderContentsAsync(string folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var folder = await GetFolderAsync(connection, folderId, cancellationToken).ConfigureAwait(false);
        if (folder.State != LibraryRecordState.Active)
        {
            throw new RecordNotFoundException("The requested folder is not active.");
        }

        var folders = new List<FolderRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,parent_id,name,state,created_at,modified_at,recycled_at FROM folders WHERE parent_id=$parent AND state=0 ORDER BY name_key;";
            command.Parameters.AddWithValue("$parent", folderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                folders.Add(ReadFolder(reader));
            }
        }

        var files = new List<FileRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,folder_id,display_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at FROM files WHERE folder_id=$folder AND state=0 ORDER BY name_key;";
            command.Parameters.AddWithValue("$folder", folderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                files.Add(ReadFile(reader));
            }
        }
        return new LibraryFolderContents(folder, folders, files);
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesByStateAsync(LibraryRecordState state, CancellationToken cancellationToken)
    {
        var results = new List<FileRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,folder_id,display_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at FROM files WHERE state=$state ORDER BY recycled_at DESC,name_key;";
        command.Parameters.AddWithValue("$state", (int)state);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFile(reader));
        return results;
    }

    public Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken cancellationToken) => GetFilesByStateAsync(LibraryRecordState.Active, cancellationToken);

    public async Task<IReadOnlyList<FolderRecord>> GetFoldersByStateAsync(LibraryRecordState state, CancellationToken cancellationToken)
    {
        var results = new List<FolderRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,parent_id,name,state,created_at,modified_at,recycled_at FROM folders WHERE state=$state ORDER BY recycled_at DESC,name_key;";
        command.Parameters.AddWithValue("$state", (int)state);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFolder(reader));
        return results;
    }

    public async Task<FolderRecord> CreateFolderAsync(string parentId, string name, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeDisplayName(name, "Folder name");
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var parent = await GetFolderAsync(connection, parentId, cancellationToken).ConfigureAwait(false);
        if (parent.State != LibraryRecordState.Active) throw new RecordNotFoundException("The destination folder is not active.");
        await EnsureNameAvailableAsync(connection, parentId, LibraryRules.ComparisonKey(normalized), null, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO folders(id,parent_id,name,name_key,state,created_at,modified_at) VALUES($id,$parent,$name,$key,0,$now,$now);",
                cancellationToken, null,
                ("$id", id), ("$parent", parentId), ("$name", normalized), ("$key", LibraryRules.ComparisonKey(normalized)), ("$now", Format(now))).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active item named '{normalized}' already exists in the folder.");
        }
        return new FolderRecord(id, parentId, normalized, LibraryRecordState.Active, now, now, null);
    }

    public async Task<FolderRecord> RenameFolderAsync(string folderId, string name, string rootFolderId, string generatedFolderId, CancellationToken cancellationToken)
    {
        if (folderId == rootFolderId || folderId == generatedFolderId) throw new LibraryValidationException("Permanent library folders cannot be renamed.");
        var normalized = LibraryRules.NormalizeDisplayName(name, "Folder name");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var folder = await GetFolderAsync(connection, folderId, cancellationToken).ConfigureAwait(false);
        if (folder.State != LibraryRecordState.Active || folder.ParentId is null) throw new LibraryValidationException("Only an active subfolder can be renamed.");
        var key = LibraryRules.ComparisonKey(normalized);
        await EnsureNameAvailableAsync(connection, folder.ParentId, key, null, folderId, cancellationToken).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection,
            "UPDATE folders SET name=$name,name_key=$key,modified_at=$modified WHERE id=$id AND state=0;",
            cancellationToken, null, ("$name", normalized), ("$key", key), ("$modified", Format(modified)), ("$id", folderId)).ConfigureAwait(false);
        return folder with { Name = normalized, ModifiedAt = modified };
    }

    public async Task<FolderRecord> MoveFolderAsync(string folderId, string destinationFolderId, string rootFolderId, string generatedFolderId, CancellationToken cancellationToken)
    {
        if (folderId == rootFolderId || folderId == generatedFolderId) throw new LibraryValidationException("Permanent library folders cannot be moved.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var folder = await GetFolderAsync(connection, folderId, cancellationToken, transaction).ConfigureAwait(false);
        var destination = await GetFolderAsync(connection, destinationFolderId, cancellationToken, transaction).ConfigureAwait(false);
        if (folder.State != LibraryRecordState.Active || destination.State != LibraryRecordState.Active) throw new LibraryValidationException("Both folders must be active.");
        if (folder.ParentId == destinationFolderId) return folder;

        await using (var cycleCommand = connection.CreateCommand())
        {
            cycleCommand.Transaction = transaction;
            cycleCommand.CommandText = "WITH RECURSIVE descendants(id) AS (SELECT $id UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id) SELECT EXISTS(SELECT 1 FROM descendants WHERE id=$destination);";
            cycleCommand.Parameters.AddWithValue("$id", folderId);
            cycleCommand.Parameters.AddWithValue("$destination", destinationFolderId);
            if (Convert.ToInt32(await cycleCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                throw new LibraryValidationException("A folder cannot be moved into itself or one of its descendants.");
            }
        }

        await EnsureNameAvailableAsync(connection, destinationFolderId, LibraryRules.ComparisonKey(folder.Name), null, folderId, cancellationToken, transaction).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE folders SET parent_id=$parent,modified_at=$modified WHERE id=$id AND state=0;",
            cancellationToken, transaction, ("$parent", destinationFolderId), ("$modified", Format(modified)), ("$id", folderId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return folder with { ParentId = destinationFolderId, ModifiedAt = modified };
    }

    public async Task<FileRecord> RenameFileAsync(string fileId, string displayName, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeDisplayName(displayName, "File name");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be renamed.");
        var key = LibraryRules.ComparisonKey(normalized);
        await EnsureNameAvailableAsync(connection, file.FolderId, key, fileId, null, cancellationToken).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection,
            "UPDATE files SET display_name=$name,name_key=$key,modified_at=$modified WHERE id=$id AND state=0;",
            cancellationToken, null, ("$name", normalized), ("$key", key), ("$modified", Format(modified)), ("$id", fileId)).ConfigureAwait(false);
        return file with { DisplayName = normalized, ModifiedAt = modified };
    }

    public async Task<FileRecord> MoveFileAsync(string fileId, string destinationFolderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        var destination = await GetFolderAsync(connection, destinationFolderId, cancellationToken, transaction).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active || destination.State != LibraryRecordState.Active) throw new LibraryValidationException("The file and destination folder must be active.");
        if (file.FolderId == destinationFolderId) return file;
        await EnsureNameAvailableAsync(connection, destinationFolderId, LibraryRules.ComparisonKey(file.DisplayName), fileId, null, cancellationToken, transaction).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE files SET folder_id=$folder,modified_at=$modified WHERE id=$id AND state=0;",
            cancellationToken, transaction, ("$folder", destinationFolderId), ("$modified", Format(modified)), ("$id", fileId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return file with { FolderId = destinationFolderId, ModifiedAt = modified };
    }

    public async Task<IReadOnlyList<FileRecord>> FindByHashAsync(string hash, long byteSize, CancellationToken cancellationToken)
    {
        var results = new List<FileRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,folder_id,display_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at FROM files WHERE content_hash=$hash AND byte_size=$size ORDER BY state, imported_at;";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$size", byteSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFile(reader));
        return results;
    }

    public async Task<string> ResolveAvailableFileNameAsync(string folderId, string requestedName, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeDisplayName(requestedName, "File name");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var stem = Path.GetFileNameWithoutExtension(normalized);
        var extension = Path.GetExtension(normalized);
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = suffix == 1 ? normalized : $"{stem} ({suffix}){extension}";
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM files WHERE folder_id=$folder AND state=0 AND name_key=$key UNION ALL SELECT 1 FROM folders WHERE parent_id=$folder AND state=0 AND name_key=$key);";
            command.Parameters.AddWithValue("$folder", folderId);
            command.Parameters.AddWithValue("$key", LibraryRules.ComparisonKey(candidate));
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
            {
                return candidate;
            }
        }
        throw new NameConflictException("No available numeric-suffix name could be found.");
    }

    public async Task<FileRecord> InsertImportedFileAsync(FileRecord file, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO files(id,folder_id,display_name,name_key,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at) VALUES($id,$folder,$name,$key,$managed,$hash,$size,$media,$origin,$state,$imported,$modified,$source,$recycled);",
            cancellationToken, null,
            ("$id", file.Id), ("$folder", file.FolderId), ("$name", file.DisplayName), ("$key", LibraryRules.ComparisonKey(file.DisplayName)),
            ("$managed", file.ManagedName), ("$hash", file.ContentHash), ("$size", file.ByteSize), ("$media", file.MediaType),
            ("$origin", (int)file.Origin), ("$state", (int)file.State), ("$imported", Format(file.ImportedAt)), ("$modified", Format(file.ModifiedAt)),
            ("$source", file.SourceLastModified is null ? DBNull.Value : Format(file.SourceLastModified.Value)), ("$recycled", DBNull.Value)).ConfigureAwait(false);
        return file;
    }

    public async Task<FileRecord> InsertDuplicateFileAsync(string sourceFileId, FileRecord duplicate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await GetFileAsync(connection, sourceFileId, cancellationToken, transaction).ConfigureAwait(false);
        var destination = await GetFolderAsync(connection, duplicate.FolderId, cancellationToken, transaction).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active || destination.State != LibraryRecordState.Active)
        {
            throw new LibraryValidationException("The source file and destination folder must be active.");
        }
        await EnsureNameAvailableAsync(connection, duplicate.FolderId, LibraryRules.ComparisonKey(duplicate.DisplayName), null, null, cancellationToken, transaction).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO files(id,folder_id,display_name,name_key,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at) VALUES($id,$folder,$name,$key,$managed,$hash,$size,$media,$origin,0,$imported,$modified,NULL,NULL);",
            cancellationToken, transaction,
            ("$id", duplicate.Id), ("$folder", duplicate.FolderId), ("$name", duplicate.DisplayName), ("$key", LibraryRules.ComparisonKey(duplicate.DisplayName)),
            ("$managed", duplicate.ManagedName), ("$hash", duplicate.ContentHash), ("$size", duplicate.ByteSize), ("$media", duplicate.MediaType),
            ("$origin", (int)duplicate.Origin), ("$imported", Format(duplicate.ImportedAt)), ("$modified", Format(duplicate.ModifiedAt))).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO metadata_entries(id,file_id,key,key_key,kind,serialized_value,is_sensitive) SELECT lower(hex(randomblob(16))),$duplicate,key,key_key,kind,serialized_value,is_sensitive FROM metadata_entries WHERE file_id=$source;",
            cancellationToken, transaction, ("$duplicate", duplicate.Id), ("$source", sourceFileId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return duplicate;
    }

    public async Task<FileRecord> GetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetFileAsync(connection, fileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MetadataEntry>> GetMetadataAsync(string fileId, CancellationToken cancellationToken)
    {
        var results = new List<MetadataEntry>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await GetFileAsync(connection, fileId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,file_id,key,kind,serialized_value,is_sensitive FROM metadata_entries WHERE file_id=$file ORDER BY key_key;";
        command.Parameters.AddWithValue("$file", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MetadataEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), (MetadataValueKind)reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5)));
        }
        return results;
    }

    public async Task<MetadataEntry> SetMetadataAsync(string fileId, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken)
    {
        var normalizedKey = LibraryRules.NormalizeMetadataKey(key);
        var validValue = LibraryRules.ValidateMetadataValue(kind, serializedValue);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await GetFileAsync(connection, fileId, cancellationToken).ConfigureAwait(false);
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM metadata_entries WHERE file_id=$file AND key_key<>$key;";
            countCommand.Parameters.AddWithValue("$file", fileId);
            countCommand.Parameters.AddWithValue("$key", LibraryRules.ComparisonKey(normalizedKey));
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count >= LibraryRules.MaximumMetadataEntriesPerFile) throw new LibraryValidationException("The file already has the maximum number of metadata entries.");
        }
        var id = LibraryRules.NewId();
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO metadata_entries(id,file_id,key,key_key,kind,serialized_value,is_sensitive) VALUES($id,$file,$key,$keyKey,$kind,$value,$sensitive) ON CONFLICT(file_id,key_key) DO UPDATE SET key=excluded.key,kind=excluded.kind,serialized_value=excluded.serialized_value,is_sensitive=excluded.is_sensitive;",
            cancellationToken, null,
            ("$id", id), ("$file", fileId), ("$key", normalizedKey), ("$keyKey", LibraryRules.ComparisonKey(normalizedKey)), ("$kind", (int)kind), ("$value", validValue), ("$sensitive", isSensitive ? 1 : 0)).ConfigureAwait(false);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT id,file_id,key,kind,serialized_value,is_sensitive FROM metadata_entries WHERE file_id=$file AND key_key=$key;";
        query.Parameters.AddWithValue("$file", fileId);
        query.Parameters.AddWithValue("$key", LibraryRules.ComparisonKey(normalizedKey));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new MetadataEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), (MetadataValueKind)reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5));
    }

    public async Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM metadata_entries WHERE file_id=$file AND key_key=$key;", cancellationToken, null,
            ("$file", fileId), ("$key", LibraryRules.ComparisonKey(LibraryRules.NormalizeMetadataKey(key)))).ConfigureAwait(false);
    }

    public async Task<MetadataEntry> RenameMetadataAsync(string fileId, string currentKey, string newKey, CancellationToken cancellationToken)
    {
        var currentKeyValue = LibraryRules.ComparisonKey(LibraryRules.NormalizeMetadataKey(currentKey));
        var normalizedNewKey = LibraryRules.NormalizeMetadataKey(newKey);
        var newKeyValue = LibraryRules.ComparisonKey(normalizedNewKey);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE metadata_entries SET key=$newKey,key_key=$newKeyValue WHERE file_id=$file AND key_key=$currentKey;";
        command.Parameters.AddWithValue("$newKey", normalizedNewKey);
        command.Parameters.AddWithValue("$newKeyValue", newKeyValue);
        command.Parameters.AddWithValue("$file", fileId);
        command.Parameters.AddWithValue("$currentKey", currentKeyValue);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) throw new RecordNotFoundException("Metadata entry not found.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"Metadata key '{normalizedNewKey}' already exists on this file.");
        }
        var entries = await GetMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
        return entries.Single(entry => LibraryRules.ComparisonKey(entry.Key) == newKeyValue);
    }

    public async Task<IReadOnlyList<FileLink>> GetLinksAsync(string fileId, CancellationToken cancellationToken)
    {
        var results = new List<FileLink>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source_file_id,target_file_id,label,state,created_at,recycled_at,explicitly_recycled FROM file_links WHERE source_file_id=$file OR target_file_id=$file ORDER BY label_key;";
        command.Parameters.AddWithValue("$file", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadLink(reader));
        return results;
    }

    public async Task<FileLink> CreateLinkAsync(string sourceFileId, string targetFileId, string label, CancellationToken cancellationToken)
    {
        if (sourceFileId == targetFileId) throw new LibraryValidationException("A file cannot link to itself.");
        var normalized = LibraryRules.NormalizeLinkLabel(label);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var source = await GetFileAsync(connection, sourceFileId, cancellationToken).ConfigureAwait(false);
        var target = await GetFileAsync(connection, targetFileId, cancellationToken).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active || target.State != LibraryRecordState.Active) throw new LibraryValidationException("Both linked files must be active.");
        var link = new FileLink(LibraryRules.NewId(), sourceFileId, targetFileId, normalized, LibraryRecordState.Active, DateTimeOffset.UtcNow, null, false);
        try
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO file_links(id,source_file_id,target_file_id,label,label_key,state,created_at,recycled_at,explicitly_recycled) VALUES($id,$source,$target,$label,$key,0,$created,NULL,0);",
                cancellationToken, null,
                ("$id", link.Id), ("$source", sourceFileId), ("$target", targetFileId), ("$label", normalized), ("$key", LibraryRules.ComparisonKey(normalized)), ("$created", Format(link.CreatedAt))).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException("That directed link and label already exist.");
        }
        return link;
    }

    public async Task<FileLink> RelabelLinkAsync(string linkId, string label, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeLinkLabel(label);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var link = await GetLinkAsync(connection, linkId, cancellationToken).ConfigureAwait(false);
        if (link.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active link can be relabelled.");
        try
        {
            await ExecuteNonQueryAsync(connection, "UPDATE file_links SET label=$label,label_key=$key WHERE id=$id AND state=0;",
                cancellationToken, null, ("$label", normalized), ("$key", LibraryRules.ComparisonKey(normalized)), ("$id", linkId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException("That directed link and label already exist.");
        }
        return link with { Label = normalized };
    }

    public async Task<FileLink> ReverseLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var link = await GetLinkAsync(connection, linkId, cancellationToken).ConfigureAwait(false);
        if (link.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active link can be reversed.");
        try
        {
            await ExecuteNonQueryAsync(connection, "UPDATE file_links SET source_file_id=$source,target_file_id=$target WHERE id=$id AND state=0;",
                cancellationToken, null, ("$source", link.TargetFileId), ("$target", link.SourceFileId), ("$id", linkId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException("Reversing this link would duplicate an existing directed link.");
        }
        return link with { SourceFileId = link.TargetFileId, TargetFileId = link.SourceFileId };
    }

    public async Task<IReadOnlyList<FileLink>> GetExplicitlyRecycledLinksAsync(CancellationToken cancellationToken)
    {
        var results = new List<FileLink>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source_file_id,target_file_id,label,state,created_at,recycled_at,explicitly_recycled FROM file_links WHERE explicitly_recycled=1 ORDER BY recycled_at DESC,label_key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadLink(reader));
        return results;
    }

    public async Task RecycleLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var link = await GetLinkAsync(connection, linkId, cancellationToken).ConfigureAwait(false);
        if (link.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active link can be recycled explicitly.");
        await ExecuteNonQueryAsync(connection, "UPDATE file_links SET state=1,explicitly_recycled=1,recycled_at=$now WHERE id=$id;",
            cancellationToken, null, ("$now", Format(DateTimeOffset.UtcNow)), ("$id", linkId)).ConfigureAwait(false);
    }

    public async Task RestoreLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var link = await GetLinkAsync(connection, linkId, cancellationToken, transaction).ConfigureAwait(false);
        if (!link.ExplicitlyRecycled) throw new LibraryValidationException("Only an explicitly recycled link can be restored from the recycle bin.");
        var source = await GetFileAsync(connection, link.SourceFileId, cancellationToken, transaction).ConfigureAwait(false);
        var target = await GetFileAsync(connection, link.TargetFileId, cancellationToken, transaction).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active || target.State != LibraryRecordState.Active)
        {
            throw new LibraryValidationException("Both endpoint files must be restored before this link can be restored.");
        }
        await ExecuteNonQueryAsync(connection, "UPDATE file_links SET state=0,explicitly_recycled=0,recycled_at=NULL WHERE id=$id;",
            cancellationToken, transaction, ("$id", linkId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PermanentlyDeleteLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM file_links WHERE id=$id AND explicitly_recycled=1;", cancellationToken, null, ("$id", linkId)).ConfigureAwait(false);
        if (affected == 0) throw new LibraryValidationException("Only an explicitly recycled link can be permanently deleted.");
    }

    public Task RecycleFileAsync(string fileId, CancellationToken cancellationToken) => SetFileStateAndLinksAsync(fileId, LibraryRecordState.Recycled, cancellationToken);

    public Task RestoreFileAsync(string fileId, CancellationToken cancellationToken) => SetFileStateAndLinksAsync(fileId, LibraryRecordState.Active, cancellationToken);

    public async Task RecycleFolderAsync(string folderId, string rootFolderId, string generatedFolderId, CancellationToken cancellationToken)
    {
        if (folderId == rootFolderId || folderId == generatedFolderId) throw new LibraryValidationException("Permanent library folders cannot be recycled.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = Format(DateTimeOffset.UtcNow);
        const string descendantCte = "WITH RECURSIVE descendants(id) AS (SELECT $id UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id) ";
        await ExecuteNonQueryAsync(connection, descendantCte + "UPDATE files SET state=1,recycled_at=$now,modified_at=$now WHERE folder_id IN (SELECT id FROM descendants) AND state=0;", cancellationToken, transaction, ("$id", folderId), ("$now", now)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, descendantCte + "UPDATE folders SET state=1,recycled_at=$now,modified_at=$now WHERE id IN (SELECT id FROM descendants) AND state=0;", cancellationToken, transaction, ("$id", folderId), ("$now", now)).ConfigureAwait(false);
        await RefreshLinkStatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string ancestorsCte = "WITH RECURSIVE ancestors(id) AS (SELECT $id UNION ALL SELECT f.parent_id FROM folders f JOIN ancestors a ON f.id=a.id WHERE f.parent_id IS NOT NULL), descendants(id) AS (SELECT $id UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id) ";
        var now = Format(DateTimeOffset.UtcNow);
        try
        {
            await ExecuteNonQueryAsync(connection, ancestorsCte + "UPDATE folders SET state=0,recycled_at=NULL,modified_at=$now WHERE id IN (SELECT id FROM ancestors) OR id IN (SELECT id FROM descendants);", cancellationToken, transaction, ("$id", folderId), ("$now", now)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "WITH RECURSIVE descendants(id) AS (SELECT $id UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id) UPDATE files SET state=0,recycled_at=NULL,modified_at=$now WHERE folder_id IN (SELECT id FROM descendants);", cancellationToken, transaction, ("$id", folderId), ("$now", now)).ConfigureAwait(false);
            await RefreshLinkStatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException("The folder cannot be restored until active name conflicts are resolved.");
        }
    }

    public async Task DeleteFileRecordAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM files WHERE id=$id AND state=2;", cancellationToken, null, ("$id", fileId)).ConfigureAwait(false);
    }

    public async Task<FileRecord> PrepareFileDeletionAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled file can be permanently deleted.");
        await ExecuteNonQueryAsync(connection, "UPDATE files SET state=2 WHERE id=$id;", cancellationToken, transaction, ("$id", fileId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return file with { State = LibraryRecordState.PendingPermanentDeletion };
    }

    public async Task RevertFileDeletionAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE files SET state=1 WHERE id=$id AND state=2;", cancellationToken, null, ("$id", fileId)).ConfigureAwait(false);
    }

    public async Task RenameLibraryAsync(string displayName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE library_info SET display_name=$name WHERE singleton=1;", cancellationToken, null, ("$name", displayName)).ConfigureAwait(false);
    }

    private async Task SetFileStateAndLinksAsync(string fileId, LibraryRecordState state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        if (state == LibraryRecordState.Active && file.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled file can be restored.");
        if (state == LibraryRecordState.Recycled && file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be recycled.");
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection,
            state == LibraryRecordState.Active
                ? "UPDATE files SET state=0,recycled_at=NULL,modified_at=$now WHERE id=$id;"
                : "UPDATE files SET state=1,recycled_at=$now,modified_at=$now WHERE id=$id;",
            cancellationToken, transaction, ("$id", fileId), ("$now", now)).ConfigureAwait(false);
        await RefreshLinkStatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException("The file cannot be restored until its active name conflict is resolved.");
        }
    }

    private static async Task RefreshLinkStatesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection,
            "UPDATE file_links SET state=CASE WHEN explicitly_recycled=0 AND EXISTS(SELECT 1 FROM files s WHERE s.id=source_file_id AND s.state=0) AND EXISTS(SELECT 1 FROM files t WHERE t.id=target_file_id AND t.state=0) THEN 0 ELSE 1 END, recycled_at=CASE WHEN explicitly_recycled=0 AND EXISTS(SELECT 1 FROM files s WHERE s.id=source_file_id AND s.state=0) AND EXISTS(SELECT 1 FROM files t WHERE t.id=target_file_id AND t.state=0) THEN NULL ELSE COALESCE(recycled_at,$now) END;",
            cancellationToken, transaction, ("$now", now)).ConfigureAwait(false);
    }

    private static async Task EnsureNameAvailableAsync(
        SqliteConnection connection,
        string folderId,
        string nameKey,
        string? excludedFileId,
        string? excludedFolderId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM files WHERE folder_id=$folder AND state=0 AND name_key=$key AND ($file IS NULL OR id<>$file) UNION ALL SELECT 1 FROM folders WHERE parent_id=$folder AND state=0 AND name_key=$key AND ($child IS NULL OR id<>$child));";
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$key", nameKey);
        command.Parameters.AddWithValue("$file", excludedFileId is null ? DBNull.Value : excludedFileId);
        command.Parameters.AddWithValue("$child", excludedFolderId is null ? DBNull.Value : excludedFolderId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
        {
            throw new NameConflictException("An active item with that name already exists in the destination folder.");
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<FolderRecord> GetFolderAsync(SqliteConnection connection, string folderId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,parent_id,name,state,created_at,modified_at,recycled_at FROM folders WHERE id=$id;";
        command.Parameters.AddWithValue("$id", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Folder not found.");
        return ReadFolder(reader);
    }

    private static async Task<FileRecord> GetFileAsync(SqliteConnection connection, string fileId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,folder_id,display_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at FROM files WHERE id=$id;";
        command.Parameters.AddWithValue("$id", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("File not found.");
        return ReadFile(reader);
    }

    private static async Task<FileLink> GetLinkAsync(SqliteConnection connection, string linkId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,source_file_id,target_file_id,label,state,created_at,recycled_at,explicitly_recycled FROM file_links WHERE id=$id;";
        command.Parameters.AddWithValue("$id", linkId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("File link not found.");
        return ReadLink(reader);
    }

    private static FolderRecord ReadFolder(SqliteDataReader reader) => new(
        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), (LibraryRecordState)reader.GetInt32(3),
        Parse(reader.GetString(4)), Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Parse(reader.GetString(6)));

    private static FileRecord ReadFile(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
        (FileOrigin)reader.GetInt32(7), (LibraryRecordState)reader.GetInt32(8), Parse(reader.GetString(9)), Parse(reader.GetString(10)),
        reader.IsDBNull(11) ? null : Parse(reader.GetString(11)), reader.IsDBNull(12) ? null : Parse(reader.GetString(12)));

    private static FileLink ReadLink(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), (LibraryRecordState)reader.GetInt32(4), Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Parse(reader.GetString(6)), reader.GetBoolean(7));

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteNonQueryWithCountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
