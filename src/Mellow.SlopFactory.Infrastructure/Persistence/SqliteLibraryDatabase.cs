using System.Globalization;
using System.Numerics;
using System.Text.Json;
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
                original_name TEXT NOT NULL,
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
                recycled_at TEXT NULL,
                content_state INTEGER NOT NULL DEFAULT 0
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

            CREATE TABLE file_content_provenance (
                file_id TEXT PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
                original_content_hash TEXT NOT NULL,
                original_byte_size INTEGER NOT NULL CHECK(original_byte_size >= 0),
                original_media_type TEXT NOT NULL,
                replaced_at TEXT NULL
            );

            CREATE TABLE file_derivation_provenance (
                file_id TEXT PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
                source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                origin INTEGER NOT NULL,
                deleted_source_name TEXT NULL,
                deleted_source_media_type TEXT NULL,
                deleted_source_content_hash TEXT NULL
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

            CREATE TABLE permanent_deletion_failures (
                record_kind INTEGER NOT NULL CHECK(record_kind IN (0,1)),
                record_id TEXT NOT NULL,
                sanitized_error TEXT NOT NULL,
                failed_at TEXT NOT NULL,
                PRIMARY KEY(record_kind, record_id)
            );
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
        if (fromVersion < 3)
        {
            await ExecuteNonQueryAsync(connection, "CREATE TABLE permanent_deletion_failures (record_kind INTEGER NOT NULL CHECK(record_kind IN (0,1)),record_id TEXT NOT NULL,sanitized_error TEXT NOT NULL,failed_at TEXT NOT NULL,PRIMARY KEY(record_kind,record_id));", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 4)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE files ADD COLUMN original_name TEXT NOT NULL DEFAULT ''; UPDATE files SET original_name=display_name WHERE original_name='';", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 5)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE files ADD COLUMN content_state INTEGER NOT NULL DEFAULT 0;", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 6)
        {
            await ExecuteNonQueryAsync(connection, "CREATE TABLE file_content_provenance (file_id TEXT PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,original_content_hash TEXT NOT NULL,original_byte_size INTEGER NOT NULL CHECK(original_byte_size >= 0),original_media_type TEXT NOT NULL,replaced_at TEXT NULL);", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 7)
        {
            await ExecuteNonQueryAsync(connection, "CREATE TABLE file_derivation_provenance (file_id TEXT PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,origin INTEGER NOT NULL);", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 8)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE file_derivation_provenance ADD COLUMN deleted_source_name TEXT NULL; ALTER TABLE file_derivation_provenance ADD COLUMN deleted_source_media_type TEXT NULL; ALTER TABLE file_derivation_provenance ADD COLUMN deleted_source_content_hash TEXT NULL;", cancellationToken, transaction).ConfigureAwait(false);
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

    public async Task UpdateLibraryIdAsync(string libraryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE library_info SET library_id=$id WHERE singleton=1;", cancellationToken, null, ("$id", libraryId)).ConfigureAwait(false);
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
            command.CommandText = "SELECT id,folder_id,display_name,original_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at,content_state FROM files WHERE folder_id=$folder AND state=0 ORDER BY name_key;";
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
        command.CommandText = "SELECT id,folder_id,display_name,original_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at,content_state FROM files WHERE state=$state ORDER BY recycled_at DESC,name_key;";
        command.Parameters.AddWithValue("$state", (int)state);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFile(reader));
        return results;
    }

    public Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken cancellationToken) => GetFilesByStateAsync(LibraryRecordState.Active, cancellationToken);

    public async Task<LibraryFileBrowseResult> BrowseFilesAsync(LibraryFileBrowseQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var folder = await GetFolderAsync(connection, query.FolderId, cancellationToken, transaction).ConfigureAwait(false);
        if (folder.State != LibraryRecordState.Active) throw new RecordNotFoundException("The current folder is not active.");
        var metadataFilter = query.MetadataFilter is null ? null : LibraryRules.ValidateMetadataFilter(query.MetadataFilter);

        var hasSearch = !string.IsNullOrWhiteSpace(query.SearchText);
        var searchPattern = hasSearch ? $"%{EscapeLikePattern(query.SearchText)}%" : string.Empty;
        const string metadataMatch = """
            (m.key LIKE $search ESCAPE '\' COLLATE NOCASE
             OR (m.kind<>5 AND m.serialized_value LIKE $search ESCAPE '\' COLLATE NOCASE)
             OR (m.kind=5 AND EXISTS (
                 SELECT 1 FROM json_tree(m.serialized_value) node
                 WHERE (typeof(node.key)='text' AND CAST(node.key AS TEXT) LIKE $search ESCAPE '\' COLLATE NOCASE)
                    OR (node.type IN ('text','integer','real','true','false')
                        AND (CASE node.type WHEN 'true' THEN 'true' WHEN 'false' THEN 'false' ELSE CAST(node.atom AS TEXT) END)
                            LIKE $search ESCAPE '\' COLLATE NOCASE)
             )))
            """;

        var conditions = new List<string> { "f.state=0" };
        if (query.Scope == LibraryBrowseScope.CurrentFolder) conditions.Add("f.folder_id=$folder");
        if (hasSearch)
        {
            conditions.Add($"(f.display_name LIKE $search ESCAPE '\\' COLLATE NOCASE OR f.original_name LIKE $search ESCAPE '\\' COLLATE NOCASE OR EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND {metadataMatch}))");
        }
        conditions.Add(query.MediaKind switch
        {
            LibraryMediaKind.Text => "(f.media_type LIKE 'text/%' OR f.media_type IN ('application/json','application/xml'))",
            LibraryMediaKind.Image => "f.media_type LIKE 'image/%'",
            LibraryMediaKind.Audio => "f.media_type LIKE 'audio/%'",
            LibraryMediaKind.Video => "f.media_type LIKE 'video/%'",
            LibraryMediaKind.Other => "(f.media_type NOT LIKE 'text/%' AND f.media_type NOT LIKE 'image/%' AND f.media_type NOT LIKE 'audio/%' AND f.media_type NOT LIKE 'video/%' AND f.media_type NOT IN ('application/json','application/xml'))",
            _ => "1=1"
        });
        if (query.Origin is not null) conditions.Add("f.origin=$origin");
        if (query.ImportedFromInclusive is not null) conditions.Add("f.imported_at>=$from");
        if (query.ImportedBeforeExclusive is not null) conditions.Add("f.imported_at<$before");
        var baseWhere = string.Join(" AND ", conditions);
        if (metadataFilter is not null) conditions.Add(MetadataFilterCondition(metadataFilter));
        var where = string.Join(" AND ", conditions);

        void AddParameters(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$folder", query.FolderId);
            command.Parameters.AddWithValue("$search", searchPattern);
            if (query.Origin is not null) command.Parameters.AddWithValue("$origin", (int)query.Origin.Value);
            if (query.ImportedFromInclusive is not null) command.Parameters.AddWithValue("$from", Format(query.ImportedFromInclusive.Value));
            if (query.ImportedBeforeExclusive is not null) command.Parameters.AddWithValue("$before", Format(query.ImportedBeforeExclusive.Value));
            if (metadataFilter is not null)
            {
                command.Parameters.AddWithValue("$metadataKey", LibraryRules.ComparisonKey(metadataFilter.Key));
                command.Parameters.AddWithValue("$metadataKind", (int)metadataFilter.Kind);
                command.Parameters.AddWithValue("$metadataValue", metadataFilter.ComparisonValue is null ? DBNull.Value : metadataFilter.ComparisonValue);
            }
        }

        var metadataMissingCount = 0;
        var metadataIncompatibleTypeCount = 0;
        if (metadataFilter is not null)
        {
            await using var metadataCountCommand = connection.CreateCommand();
            metadataCountCommand.Transaction = transaction;
            metadataCountCommand.CommandText = $"""
                SELECT
                    COALESCE(SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND m.key_key=$metadataKey) THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND m.key_key=$metadataKey)
                                      AND NOT EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND m.key_key=$metadataKey AND m.kind=$metadataKind)
                                 THEN 1 ELSE 0 END),0)
                FROM files f WHERE {baseWhere};
                """;
            AddParameters(metadataCountCommand);
            await using var metadataCountReader = await metadataCountCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await metadataCountReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            metadataMissingCount = metadataCountReader.GetInt32(0);
            metadataIncompatibleTypeCount = metadataCountReader.GetInt32(1);
        }

        int totalCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = $"SELECT COUNT(*) FROM files f WHERE {where};";
            AddParameters(countCommand);
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var orderBy = query.Sort switch
        {
            LibraryFileSort.ImportedNewest => "f.imported_at DESC,f.name_key,f.id",
            LibraryFileSort.ModifiedNewest => "f.modified_at DESC,f.name_key,f.id",
            LibraryFileSort.SizeLargest => "f.byte_size DESC,f.name_key,f.id",
            LibraryFileSort.MediaType => "f.media_type COLLATE NOCASE,f.name_key,f.id",
            _ => "f.name_key,f.id"
        };
        var items = new List<LibraryFileBrowseItem>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            var matchProjection = hasSearch
                ? $"""
                    CASE WHEN f.display_name LIKE $search ESCAPE '\' COLLATE NOCASE THEN 1 ELSE 0 END,
                    CASE WHEN f.original_name LIKE $search ESCAPE '\' COLLATE NOCASE THEN 1 ELSE 0 END,
                    COALESCE((SELECT m.key FROM metadata_entries m WHERE m.file_id=f.id AND m.is_sensitive=0 AND {metadataMatch} ORDER BY m.key_key LIMIT 1),''),
                    CASE WHEN EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND m.is_sensitive=1 AND {metadataMatch}) THEN 1 ELSE 0 END
                    """
                : "0,0,'',0";
            command.CommandText = $"""
                SELECT f.id,f.folder_id,f.display_name,f.original_name,f.managed_name,f.content_hash,f.byte_size,f.media_type,f.origin,f.state,
                       f.imported_at,f.modified_at,f.source_last_modified,f.recycled_at,f.content_state,{matchProjection}
                FROM files f
                WHERE {where}
                ORDER BY {orderBy}
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(command);
            command.Parameters.AddWithValue("$limit", query.PageSize);
            command.Parameters.AddWithValue("$offset", query.Offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var file = ReadFile(reader);
                var reasons = new List<string>(3);
                if (reader.GetBoolean(15)) reasons.Add("Matched display name");
                if (reader.GetBoolean(16) && !string.Equals(file.DisplayName, file.OriginalFileName, StringComparison.OrdinalIgnoreCase)) reasons.Add("Matched original filename");
                var metadataKey = reader.GetString(17);
                if (metadataKey.Length > 0) reasons.Add($"Matched user metadata: {metadataKey}");
                else if (reader.GetBoolean(18)) reasons.Add("Matched user metadata");
                if (metadataFilter is not null) reasons.Add("Matched user metadata filter");
                items.Add(new LibraryFileBrowseItem(file, reasons));
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LibraryFileBrowseResult(items, totalCount, query.Offset, query.PageSize, metadataMissingCount, metadataIncompatibleTypeCount);
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesForIntegrityScanAsync(CancellationToken cancellationToken)
    {
        var results = new List<FileRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,folder_id,display_name,original_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at,content_state FROM files WHERE state<>2 ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFile(reader));
        return results;
    }

    public async Task<IReadOnlyList<string>> CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = reader.GetString(0);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) findings.Add(result);
        }
        return findings;
    }

    public async Task<IReadOnlyList<FileRecord>> GetTopLevelDeletedFilesAsync(CancellationToken cancellationToken)
    {
        var results = new List<FileRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id,f.folder_id,f.display_name,f.original_name,f.managed_name,f.content_hash,f.byte_size,f.media_type,f.origin,f.state,f.imported_at,f.modified_at,f.source_last_modified,f.recycled_at,f.content_state
            FROM files f
            WHERE f.state IN (1,2)
              AND NOT EXISTS (
                WITH RECURSIVE ancestors(id,parent_id,state) AS (
                    SELECT id,parent_id,state FROM folders WHERE id=f.folder_id
                    UNION ALL
                    SELECT p.id,p.parent_id,p.state FROM folders p JOIN ancestors a ON a.parent_id=p.id
                )
                SELECT 1 FROM ancestors WHERE state IN (1,2)
              )
            ORDER BY f.recycled_at DESC,f.name_key;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFile(reader));
        return results;
    }

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

    public async Task<IReadOnlyList<FolderRecord>> GetTopLevelDeletedFoldersAsync(CancellationToken cancellationToken)
    {
        var results = new List<FolderRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id,f.parent_id,f.name,f.state,f.created_at,f.modified_at,f.recycled_at
            FROM folders f
            WHERE f.state IN (1,2)
              AND NOT EXISTS (SELECT 1 FROM folders p WHERE p.id=f.parent_id AND p.state IN (1,2))
            ORDER BY f.recycled_at DESC,f.name_key;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFolder(reader));
        return results;
    }

    public async Task<IReadOnlyList<RecycleBinEntry>> GetRecycleBinEntriesAsync(CancellationToken cancellationToken)
    {
        var results = new List<RecycleBinEntry>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE folder_paths(id,path) AS (
                    SELECT id,name FROM folders WHERE parent_id IS NULL
                    UNION ALL
                    SELECT child.id,folder_paths.path || ' / ' || child.name
                    FROM folders child JOIN folder_paths ON child.parent_id=folder_paths.id
                )
                SELECT folder.id,folder.name,COALESCE(parent_path.path,'Library'),folder.state,folder.recycled_at,
                    (WITH RECURSIVE descendants(id) AS (
                        SELECT folder.id UNION ALL SELECT child.id FROM folders child JOIN descendants ON child.parent_id=descendants.id
                    ) SELECT COUNT(*) FROM descendants),
                    (WITH RECURSIVE descendants(id) AS (
                        SELECT folder.id UNION ALL SELECT child.id FROM folders child JOIN descendants ON child.parent_id=descendants.id
                    ) SELECT COUNT(*) FROM files WHERE folder_id IN (SELECT id FROM descendants)),
                    (WITH RECURSIVE descendants(id) AS (
                        SELECT folder.id UNION ALL SELECT child.id FROM folders child JOIN descendants ON child.parent_id=descendants.id
                    ) SELECT COUNT(DISTINCT link.id) FROM file_links link
                       WHERE link.source_file_id IN (SELECT id FROM files WHERE folder_id IN (SELECT id FROM descendants))
                          OR link.target_file_id IN (SELECT id FROM files WHERE folder_id IN (SELECT id FROM descendants))),
                    failure.sanitized_error,failure.failed_at
                FROM folders folder
                LEFT JOIN folder_paths parent_path ON parent_path.id=folder.parent_id
                LEFT JOIN permanent_deletion_failures failure ON failure.record_kind=0 AND failure.record_id=folder.id
                WHERE folder.state IN (1,2)
                  AND NOT EXISTS (SELECT 1 FROM folders parent WHERE parent.id=folder.parent_id AND parent.state IN (1,2));
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.Folder, reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2), (LibraryRecordState)reader.GetInt32(3), Parse(reader.GetString(4)),
                    reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : new PermanentDeletionFailure(reader.GetString(8), Parse(reader.GetString(9)))));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE folder_paths(id,path) AS (
                    SELECT id,name FROM folders WHERE parent_id IS NULL
                    UNION ALL
                    SELECT child.id,folder_paths.path || ' / ' || child.name
                    FROM folders child JOIN folder_paths ON child.parent_id=folder_paths.id
                )
                SELECT file.id,file.display_name,folder_paths.path,file.state,file.recycled_at,
                    (SELECT COUNT(*) FROM file_links link WHERE link.source_file_id=file.id OR link.target_file_id=file.id),
                    failure.sanitized_error,failure.failed_at
                FROM files file JOIN folder_paths ON folder_paths.id=file.folder_id
                LEFT JOIN permanent_deletion_failures failure ON failure.record_kind=1 AND failure.record_id=file.id
                WHERE file.state IN (1,2)
                  AND NOT EXISTS (
                    WITH RECURSIVE ancestors(id,parent_id,state) AS (
                        SELECT id,parent_id,state FROM folders WHERE id=file.folder_id
                        UNION ALL
                        SELECT parent.id,parent.parent_id,parent.state FROM folders parent JOIN ancestors ON ancestors.parent_id=parent.id
                    ) SELECT 1 FROM ancestors WHERE state IN (1,2));
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.File, reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2), (LibraryRecordState)reader.GetInt32(3), Parse(reader.GetString(4)),
                    0, 1, reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : new PermanentDeletionFailure(reader.GetString(6), Parse(reader.GetString(7)))));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT link.id,link.label,source.display_name || ' -> ' || target.display_name,link.state,link.recycled_at
                FROM file_links link
                JOIN files source ON source.id=link.source_file_id
                JOIN files target ON target.id=link.target_file_id
                WHERE link.explicitly_recycled=1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.FileLink, reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2), (LibraryRecordState)reader.GetInt32(3), Parse(reader.GetString(4)),
                    0, 0, 1, null));
            }
        }

        return results.OrderByDescending(item => item.RecycledAt).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesOwnedByRecycleBinItemAsync(RecycleBinItemReference reference, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (reference.Kind == RecycleBinItemKind.File)
        {
            return [await GetFileAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false)];
        }
        if (reference.Kind != RecycleBinItemKind.Folder) return [];

        var results = new List<FileRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE descendants(id) AS (
                SELECT $id UNION ALL SELECT child.id FROM folders child JOIN descendants ON child.parent_id=descendants.id
            )
            SELECT file.id,file.folder_id,file.display_name,file.original_name,file.managed_name,file.content_hash,file.byte_size,file.media_type,file.origin,file.state,file.imported_at,file.modified_at,file.source_last_modified,file.recycled_at,file.content_state
            FROM files file WHERE file.folder_id IN (SELECT id FROM descendants) ORDER BY file.id;
            """;
        command.Parameters.AddWithValue("$id", reference.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFile(reader));
        return results;
    }

    public async Task<IReadOnlyList<string>> GetRestoreBlockersAsync(RecycleBinItemReference reference, IReadOnlySet<string> selectedFileIds, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        switch (reference.Kind)
        {
            case RecycleBinItemKind.File:
            {
                var file = await GetFileAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (file.State != LibraryRecordState.Recycled) blockers.Add("Only a recycled file can be restored.");
                var parent = await GetFolderAsync(connection, file.FolderId, cancellationToken).ConfigureAwait(false);
                if (parent.State != LibraryRecordState.Active) blockers.Add($"Its original folder '{parent.Name}' must be restored first.");
                await using var conflict = connection.CreateCommand();
                conflict.CommandText = """
                    SELECT EXISTS(
                        SELECT 1 FROM files candidate JOIN files restoring ON restoring.id=$id
                        WHERE candidate.folder_id=restoring.folder_id AND candidate.name_key=restoring.name_key AND candidate.state=0 AND candidate.id<>restoring.id
                        UNION ALL
                        SELECT 1 FROM folders candidate JOIN files restoring ON restoring.id=$id
                        WHERE candidate.parent_id=restoring.folder_id AND candidate.name_key=restoring.name_key AND candidate.state=0);
                    """;
                conflict.Parameters.AddWithValue("$id", reference.Id);
                if (Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                {
                    blockers.Add($"An active item named '{file.DisplayName}' already exists in the original folder.");
                }
                break;
            }
            case RecycleBinItemKind.Folder:
            {
                var folder = await GetFolderAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (folder.State != LibraryRecordState.Recycled) blockers.Add("Only a recycled folder can be restored.");
                if (folder.ParentId is not null)
                {
                    var parent = await GetFolderAsync(connection, folder.ParentId, cancellationToken).ConfigureAwait(false);
                    if (parent.State != LibraryRecordState.Active) blockers.Add($"Its original parent folder '{parent.Name}' must be restored first.");
                }
                await using var conflict = connection.CreateCommand();
                conflict.CommandText = """
                    WITH RECURSIVE descendants(id) AS (
                        SELECT $id UNION ALL SELECT child.id FROM folders child JOIN descendants ON child.parent_id=descendants.id
                    )
                    SELECT EXISTS(
                        SELECT 1 FROM folders restoring JOIN folders candidate
                          ON candidate.parent_id=restoring.parent_id AND candidate.name_key=restoring.name_key
                        WHERE restoring.id IN (SELECT id FROM descendants) AND candidate.state=0 AND candidate.id<>restoring.id
                        UNION ALL
                        SELECT 1 FROM folders restoring JOIN files candidate
                          ON candidate.folder_id=restoring.parent_id AND candidate.name_key=restoring.name_key
                        WHERE restoring.id IN (SELECT id FROM descendants) AND candidate.state=0
                        UNION ALL
                        SELECT 1 FROM files restoring JOIN files candidate
                          ON candidate.folder_id=restoring.folder_id AND candidate.name_key=restoring.name_key
                        WHERE restoring.folder_id IN (SELECT id FROM descendants) AND candidate.state=0 AND candidate.id<>restoring.id
                        UNION ALL
                        SELECT 1 FROM files restoring JOIN folders candidate
                          ON candidate.parent_id=restoring.folder_id AND candidate.name_key=restoring.name_key
                        WHERE restoring.folder_id IN (SELECT id FROM descendants) AND candidate.state=0);
                    """;
                conflict.Parameters.AddWithValue("$id", reference.Id);
                if (Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                {
                    blockers.Add("One or more names in this folder hierarchy conflict with active items at their original locations.");
                }
                break;
            }
            case RecycleBinItemKind.FileLink:
            {
                var link = await GetLinkAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (!link.ExplicitlyRecycled) blockers.Add("Only an explicitly recycled link can be restored.");
                var source = await GetFileAsync(connection, link.SourceFileId, cancellationToken).ConfigureAwait(false);
                var target = await GetFileAsync(connection, link.TargetFileId, cancellationToken).ConfigureAwait(false);
                if (source.State != LibraryRecordState.Active && !selectedFileIds.Contains(source.Id)) blockers.Add($"Source file '{source.DisplayName}' must be restored first or included in this selection.");
                if (target.State != LibraryRecordState.Active && !selectedFileIds.Contains(target.Id)) blockers.Add($"Target file '{target.DisplayName}' must be restored first or included in this selection.");
                break;
            }
            default:
                blockers.Add("The recycle-bin item type is not supported.");
                break;
        }
        return blockers;
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
        command.CommandText = "SELECT id,folder_id,display_name,original_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at,content_state FROM files WHERE content_hash=$hash AND byte_size=$size ORDER BY state, imported_at;";
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
            "INSERT INTO files(id,folder_id,display_name,original_name,name_key,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at) VALUES($id,$folder,$name,$original,$key,$managed,$hash,$size,$media,$origin,$state,$imported,$modified,$source,$recycled);",
            cancellationToken, null,
            ("$id", file.Id), ("$folder", file.FolderId), ("$name", file.DisplayName), ("$original", file.OriginalFileName), ("$key", LibraryRules.ComparisonKey(file.DisplayName)),
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
            "INSERT INTO files(id,folder_id,display_name,original_name,name_key,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at) VALUES($id,$folder,$name,$original,$key,$managed,$hash,$size,$media,$origin,0,$imported,$modified,NULL,NULL);",
            cancellationToken, transaction,
            ("$id", duplicate.Id), ("$folder", duplicate.FolderId), ("$name", duplicate.DisplayName), ("$original", duplicate.OriginalFileName), ("$key", LibraryRules.ComparisonKey(duplicate.DisplayName)),
            ("$managed", duplicate.ManagedName), ("$hash", duplicate.ContentHash), ("$size", duplicate.ByteSize), ("$media", duplicate.MediaType),
            ("$origin", (int)duplicate.Origin), ("$imported", Format(duplicate.ImportedAt)), ("$modified", Format(duplicate.ModifiedAt))).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO metadata_entries(id,file_id,key,key_key,kind,serialized_value,is_sensitive) SELECT lower(hex(randomblob(16))),$duplicate,key,key_key,kind,serialized_value,is_sensitive FROM metadata_entries WHERE file_id=$source;",
            cancellationToken, transaction, ("$duplicate", duplicate.Id), ("$source", sourceFileId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "INSERT INTO file_derivation_provenance(file_id,source_file_id,origin) VALUES($file,$source,$origin);", cancellationToken, transaction, ("$file", duplicate.Id), ("$source", sourceFileId), ("$origin", (int)duplicate.Origin)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return duplicate;
    }

    public async Task<FileRecord> InsertEditedTextCopyAsync(string sourceFileId, FileRecord copy, bool copyUserMetadata, bool includeSensitiveMetadata, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await GetFileAsync(connection, sourceFileId, cancellationToken, transaction).ConfigureAwait(false);
        var destination = await GetFolderAsync(connection, copy.FolderId, cancellationToken, transaction).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active || destination.State != LibraryRecordState.Active)
        {
            throw new LibraryValidationException("The source file and destination folder must be active.");
        }
        await EnsureNameAvailableAsync(connection, copy.FolderId, LibraryRules.ComparisonKey(copy.DisplayName), null, null, cancellationToken, transaction).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO files(id,folder_id,display_name,original_name,name_key,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at) VALUES($id,$folder,$name,$original,$key,$managed,$hash,$size,$media,$origin,0,$imported,$modified,NULL,NULL);",
            cancellationToken, transaction,
            ("$id", copy.Id), ("$folder", copy.FolderId), ("$name", copy.DisplayName), ("$original", copy.OriginalFileName), ("$key", LibraryRules.ComparisonKey(copy.DisplayName)),
            ("$managed", copy.ManagedName), ("$hash", copy.ContentHash), ("$size", copy.ByteSize), ("$media", copy.MediaType),
            ("$origin", (int)copy.Origin), ("$imported", Format(copy.ImportedAt)), ("$modified", Format(copy.ModifiedAt))).ConfigureAwait(false);
        if (copyUserMetadata)
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO metadata_entries(id,file_id,key,key_key,kind,serialized_value,is_sensitive) SELECT lower(hex(randomblob(16))),$copy,key,key_key,kind,serialized_value,is_sensitive FROM metadata_entries WHERE file_id=$source AND ($includeSensitive=1 OR is_sensitive=0);",
                cancellationToken, transaction, ("$copy", copy.Id), ("$source", sourceFileId), ("$includeSensitive", includeSensitiveMetadata ? 1 : 0)).ConfigureAwait(false);
        }
        await ExecuteNonQueryAsync(connection, "INSERT INTO file_derivation_provenance(file_id,source_file_id,origin) VALUES($file,$source,$origin);", cancellationToken, transaction, ("$file", copy.Id), ("$source", sourceFileId), ("$origin", (int)copy.Origin)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return copy;
    }

    public async Task<FileRecord> GetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetFileAsync(connection, fileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileRecord> SetFileContentStateAsync(string fileId, FileContentState contentState, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be revalidated.");
        if (file.ContentState == contentState) return file;
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE files SET content_state=$state,modified_at=$modified WHERE id=$id AND state=0;", cancellationToken, transaction,
            ("$state", (int)contentState), ("$modified", Format(modified)), ("$id", fileId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return file with { ContentState = contentState, ModifiedAt = modified };
    }

    public async Task<FileContentProvenance> GetFileContentProvenanceAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT original_content_hash,original_byte_size,original_media_type,replaced_at FROM file_content_provenance WHERE file_id=$id;";
        command.Parameters.AddWithValue("$id", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new FileContentProvenance(file.ContentHash, file.ByteSize, file.MediaType, null);
        return new FileContentProvenance(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : Parse(reader.GetString(3)));
    }

    public async Task<FileDerivationProvenance?> GetFileDerivationProvenanceAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_file_id,origin,deleted_source_name,deleted_source_media_type,deleted_source_content_hash FROM file_derivation_provenance WHERE file_id=$id;";
        command.Parameters.AddWithValue("$id", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var snapshot = reader.IsDBNull(2) ? null : new FileIdentitySnapshot(reader.GetString(2), reader.GetString(3), reader.GetString(4));
        return new FileDerivationProvenance(reader.IsDBNull(0) ? null : reader.GetString(0), (FileOrigin)reader.GetInt32(1), snapshot);
    }

    public async Task<FileRecord> AcceptFileContentAsync(string fileId, string contentHash, long byteSize, string mediaType, bool restoresOriginal, bool clearUserMetadata, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active || file.ContentState is not (FileContentState.Missing or FileContentState.Changed))
        {
            throw new LibraryValidationException("Managed content can be replaced only for a missing or changed active file.");
        }
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO file_content_provenance(file_id,original_content_hash,original_byte_size,original_media_type,replaced_at) VALUES($id,$hash,$size,$media,NULL) ON CONFLICT(file_id) DO NOTHING;",
            cancellationToken, transaction, ("$id", file.Id), ("$hash", file.ContentHash), ("$size", file.ByteSize), ("$media", file.MediaType)).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        DateTimeOffset? replacementTime = restoresOriginal ? null : modified;
        await ExecuteNonQueryAsync(connection,
            "UPDATE files SET content_hash=$hash,byte_size=$size,media_type=$media,content_state=$contentState,modified_at=$modified WHERE id=$id; UPDATE file_content_provenance SET replaced_at=$replaced WHERE file_id=$id;",
            cancellationToken, transaction,
            ("$hash", contentHash), ("$size", byteSize), ("$media", mediaType), ("$contentState", restoresOriginal ? (int)FileContentState.Healthy : (int)FileContentState.Replaced),
            ("$modified", Format(modified)), ("$id", file.Id), ("$replaced", replacementTime is null ? DBNull.Value : Format(replacementTime.Value))).ConfigureAwait(false);
        if (clearUserMetadata) await ExecuteNonQueryAsync(connection, "DELETE FROM metadata_entries WHERE file_id=$id;", cancellationToken, transaction, ("$id", file.Id)).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return file with
        {
            ContentHash = contentHash,
            ByteSize = byteSize,
            MediaType = mediaType,
            ContentState = restoresOriginal ? FileContentState.Healthy : FileContentState.Replaced,
            ModifiedAt = modified
        };
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM metadata_entries WHERE file_id=$file AND key_key<>$key;";
            countCommand.Parameters.AddWithValue("$file", fileId);
            countCommand.Parameters.AddWithValue("$key", LibraryRules.ComparisonKey(normalizedKey));
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count >= LibraryRules.MaximumMetadataEntriesPerFile) throw new LibraryValidationException("The file already has the maximum number of metadata entries.");
        }
        var id = LibraryRules.NewId();
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO metadata_entries(id,file_id,key,key_key,kind,serialized_value,is_sensitive) VALUES($id,$file,$key,$keyKey,$kind,$value,$sensitive) ON CONFLICT(file_id,key_key) DO UPDATE SET key=excluded.key,kind=excluded.kind,serialized_value=excluded.serialized_value,is_sensitive=excluded.is_sensitive;",
            cancellationToken, transaction,
            ("$id", id), ("$file", fileId), ("$key", normalizedKey), ("$keyKey", LibraryRules.ComparisonKey(normalizedKey)), ("$kind", (int)kind), ("$value", validValue), ("$sensitive", isSensitive ? 1 : 0)).ConfigureAwait(false);
        await TouchFileAsync(connection, transaction, fileId, cancellationToken).ConfigureAwait(false);
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT id,file_id,key,kind,serialized_value,is_sensitive FROM metadata_entries WHERE file_id=$file AND key_key=$key;";
        query.Parameters.AddWithValue("$file", fileId);
        query.Parameters.AddWithValue("$key", LibraryRules.ComparisonKey(normalizedKey));
        MetadataEntry result;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = new MetadataEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), (MetadataValueKind)reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task SetMetadataSensitivityAsync(string fileId, string key, bool isSensitive, CancellationToken cancellationToken)
    {
        var normalizedKey = LibraryRules.NormalizeMetadataKey(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Metadata can be changed only on an active file.");
        var changed = await ExecuteNonQueryWithCountAsync(connection, "UPDATE metadata_entries SET is_sensitive=$sensitive WHERE file_id=$file AND key_key=$key;", cancellationToken, transaction,
            ("$sensitive", isSensitive ? 1 : 0), ("$file", fileId), ("$key", LibraryRules.ComparisonKey(normalizedKey))).ConfigureAwait(false);
        if (changed == 0) throw new RecordNotFoundException("Metadata entry not found.");
        await TouchFileAsync(connection, transaction, fileId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM metadata_entries WHERE file_id=$file AND key_key=$key;", cancellationToken, transaction,
            ("$file", fileId), ("$key", LibraryRules.ComparisonKey(LibraryRules.NormalizeMetadataKey(key)))).ConfigureAwait(false);
        if (removed > 0) await TouchFileAsync(connection, transaction, fileId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            await TouchFileAsync(connection, transaction, fileId, cancellationToken).ConfigureAwait(false);
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
        var folder = await GetFolderAsync(connection, folderId, cancellationToken, transaction).ConfigureAwait(false);
        if (folder.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled folder can be restored.");
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE file_derivation_provenance SET source_file_id=NULL,deleted_source_name=$name,deleted_source_media_type=$media,deleted_source_content_hash=$hash WHERE source_file_id=$id;", cancellationToken, transaction, ("$id", fileId), ("$name", file.DisplayName), ("$media", file.MediaType), ("$hash", file.ContentHash)).ConfigureAwait(false);
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM files WHERE id=$id AND state=2;", cancellationToken, transaction, ("$id", fileId)).ConfigureAwait(false);
        if (deleted == 0) throw new LibraryValidationException("The pending file aggregate could not be found for permanent deletion.");
        await ExecuteNonQueryAsync(connection, "DELETE FROM permanent_deletion_failures WHERE record_kind=1 AND record_id=$id;", cancellationToken, transaction, ("$id", fileId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordPermanentDeletionFailureAsync(RecycleBinItemReference reference, string sanitizedError, CancellationToken cancellationToken)
    {
        if (reference.Kind is not (RecycleBinItemKind.Folder or RecycleBinItemKind.File)) return;
        var normalized = string.IsNullOrWhiteSpace(sanitizedError) ? "Permanent deletion failed." : sanitizedError.Trim();
        if (normalized.Length > 1_024) normalized = normalized[..1_024];
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO permanent_deletion_failures(record_kind,record_id,sanitized_error,failed_at) VALUES($kind,$id,$error,$failed) ON CONFLICT(record_kind,record_id) DO UPDATE SET sanitized_error=excluded.sanitized_error,failed_at=excluded.failed_at;",
            cancellationToken, null, ("$kind", (int)reference.Kind), ("$id", reference.Id), ("$error", normalized), ("$failed", Format(DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    public async Task<FileRecord> PrepareFileDeletionAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var file = await GetFileAsync(connection, fileId, cancellationToken, transaction).ConfigureAwait(false);
        if (file.State is not (LibraryRecordState.Recycled or LibraryRecordState.PendingPermanentDeletion)) throw new LibraryValidationException("Only a recycled or pending file can be permanently deleted.");
        if (file.State == LibraryRecordState.Recycled)
        {
            await ExecuteNonQueryAsync(connection, "UPDATE files SET state=2 WHERE id=$id;", cancellationToken, transaction, ("$id", fileId)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return file with { State = LibraryRecordState.PendingPermanentDeletion };
    }

    public async Task<IReadOnlyList<FileRecord>> PrepareFolderDeletionAsync(string folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var folder = await GetFolderAsync(connection, folderId, cancellationToken, transaction).ConfigureAwait(false);
        if (folder.State is not (LibraryRecordState.Recycled or LibraryRecordState.PendingPermanentDeletion))
        {
            throw new LibraryValidationException("Only a recycled or pending folder can be permanently deleted.");
        }
        const string descendants = "WITH RECURSIVE descendants(id) AS (SELECT $id UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id) ";
        await ExecuteNonQueryAsync(connection, descendants + "UPDATE files SET state=2 WHERE folder_id IN (SELECT id FROM descendants) AND state IN (1,2);",
            cancellationToken, transaction, ("$id", folderId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, descendants + "UPDATE folders SET state=2 WHERE id IN (SELECT id FROM descendants) AND state IN (1,2);",
            cancellationToken, transaction, ("$id", folderId)).ConfigureAwait(false);

        var files = new List<FileRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = descendants + "SELECT f.id,f.folder_id,f.display_name,f.original_name,f.managed_name,f.content_hash,f.byte_size,f.media_type,f.origin,f.state,f.imported_at,f.modified_at,f.source_last_modified,f.recycled_at,f.content_state FROM files f WHERE f.folder_id IN (SELECT id FROM descendants) ORDER BY f.id;";
            command.Parameters.AddWithValue("$id", folderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) files.Add(ReadFile(reader));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return files;
    }

    public async Task DeleteFolderRecordAsync(string folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string descendants = "WITH RECURSIVE descendants(id) AS (SELECT $id UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id) ";
        await ExecuteNonQueryAsync(connection, descendants + "DELETE FROM permanent_deletion_failures WHERE (record_kind=0 AND record_id IN (SELECT id FROM descendants)) OR (record_kind=1 AND record_id IN (SELECT file.id FROM files file WHERE file.folder_id IN (SELECT id FROM descendants)));",
            cancellationToken, transaction, ("$id", folderId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, descendants + "DELETE FROM files WHERE folder_id IN (SELECT id FROM descendants);",
            cancellationToken, transaction, ("$id", folderId)).ConfigureAwait(false);
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM folders WHERE id=$id AND state=2;", cancellationToken, transaction, ("$id", folderId)).ConfigureAwait(false);
        if (deleted == 0) throw new LibraryValidationException("The pending folder aggregate could not be found for permanent deletion.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        try
        {
            await ExecuteNonQueryAsync(connection,
                state == LibraryRecordState.Active
                    ? "UPDATE files SET state=0,recycled_at=NULL,modified_at=$now WHERE id=$id;"
                    : "UPDATE files SET state=1,recycled_at=$now,modified_at=$now WHERE id=$id;",
                cancellationToken, transaction, ("$id", fileId), ("$now", now)).ConfigureAwait(false);
            await RefreshLinkStatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
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

    private static Task TouchFileAsync(SqliteConnection connection, SqliteTransaction transaction, string fileId, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(connection, "UPDATE files SET modified_at=$modified WHERE id=$id;", cancellationToken, transaction,
            ("$modified", Format(DateTimeOffset.UtcNow)), ("$id", fileId));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<string, string, bool>("slopfactory_text_equals", (left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        connection.CreateFunction<string, string, bool>("slopfactory_text_contains", (value, search) => value.Contains(search, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        connection.CreateFunction<string, string, int>("slopfactory_number_compare", CompareMetadataNumbers, isDeterministic: true);
        connection.CreateFunction<string, string, int>("slopfactory_datetime_compare", CompareMetadataDateTimes, isDeterministic: true);
        connection.CreateFunction<string, string, bool>("slopfactory_json_equal", JsonStructurallyEquals, isDeterministic: true);
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
        command.CommandText = "SELECT id,folder_id,display_name,original_name,managed_name,content_hash,byte_size,media_type,origin,state,imported_at,modified_at,source_last_modified,recycled_at,content_state FROM files WHERE id=$id;";
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
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7),
        (FileOrigin)reader.GetInt32(8), (LibraryRecordState)reader.GetInt32(9), Parse(reader.GetString(10)), Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : Parse(reader.GetString(13)), (FileContentState)reader.GetInt32(14));

    private static FileLink ReadLink(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), (LibraryRecordState)reader.GetInt32(4), Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Parse(reader.GetString(6)), reader.GetBoolean(7));

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string EscapeLikePattern(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string MetadataFilterCondition(UserMetadataFilter filter)
    {
        if (filter.Operator == MetadataFilterOperator.DoesNotExist)
        {
            return "NOT EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND m.key_key=$metadataKey)";
        }
        var comparison = filter.Operator switch
        {
            MetadataFilterOperator.Exists => "1=1",
            MetadataFilterOperator.Contains => "slopfactory_text_contains(m.serialized_value,$metadataValue)",
            MetadataFilterOperator.StructurallyEquals => "slopfactory_json_equal(m.serialized_value,$metadataValue)",
            MetadataFilterOperator.Equals when filter.Kind == MetadataValueKind.Text => "slopfactory_text_equals(m.serialized_value,$metadataValue)",
            MetadataFilterOperator.DoesNotEqual when filter.Kind == MetadataValueKind.Text => "NOT slopfactory_text_equals(m.serialized_value,$metadataValue)",
            MetadataFilterOperator.Equals when filter.Kind == MetadataValueKind.Number => "slopfactory_number_compare(m.serialized_value,$metadataValue)=0",
            MetadataFilterOperator.DoesNotEqual when filter.Kind == MetadataValueKind.Number => "slopfactory_number_compare(m.serialized_value,$metadataValue)<>0",
            MetadataFilterOperator.LessThan when filter.Kind == MetadataValueKind.Number => "slopfactory_number_compare(m.serialized_value,$metadataValue)<0",
            MetadataFilterOperator.LessThanOrEqual when filter.Kind == MetadataValueKind.Number => "slopfactory_number_compare(m.serialized_value,$metadataValue)<=0",
            MetadataFilterOperator.GreaterThan when filter.Kind == MetadataValueKind.Number => "slopfactory_number_compare(m.serialized_value,$metadataValue)>0",
            MetadataFilterOperator.GreaterThanOrEqual when filter.Kind == MetadataValueKind.Number => "slopfactory_number_compare(m.serialized_value,$metadataValue)>=0",
            MetadataFilterOperator.Equals when filter.Kind == MetadataValueKind.Boolean => "slopfactory_text_equals(m.serialized_value,$metadataValue)",
            MetadataFilterOperator.DoesNotEqual when filter.Kind == MetadataValueKind.Boolean => "NOT slopfactory_text_equals(m.serialized_value,$metadataValue)",
            MetadataFilterOperator.Equals when filter.Kind == MetadataValueKind.Date => "m.serialized_value=$metadataValue",
            MetadataFilterOperator.DoesNotEqual when filter.Kind == MetadataValueKind.Date => "m.serialized_value<>$metadataValue",
            MetadataFilterOperator.LessThan when filter.Kind == MetadataValueKind.Date => "m.serialized_value<$metadataValue",
            MetadataFilterOperator.LessThanOrEqual when filter.Kind == MetadataValueKind.Date => "m.serialized_value<=$metadataValue",
            MetadataFilterOperator.GreaterThan when filter.Kind == MetadataValueKind.Date => "m.serialized_value>$metadataValue",
            MetadataFilterOperator.GreaterThanOrEqual when filter.Kind == MetadataValueKind.Date => "m.serialized_value>=$metadataValue",
            MetadataFilterOperator.Equals when filter.Kind == MetadataValueKind.DateTime => "slopfactory_datetime_compare(m.serialized_value,$metadataValue)=0",
            MetadataFilterOperator.DoesNotEqual when filter.Kind == MetadataValueKind.DateTime => "slopfactory_datetime_compare(m.serialized_value,$metadataValue)<>0",
            MetadataFilterOperator.LessThan when filter.Kind == MetadataValueKind.DateTime => "slopfactory_datetime_compare(m.serialized_value,$metadataValue)<0",
            MetadataFilterOperator.LessThanOrEqual when filter.Kind == MetadataValueKind.DateTime => "slopfactory_datetime_compare(m.serialized_value,$metadataValue)<=0",
            MetadataFilterOperator.GreaterThan when filter.Kind == MetadataValueKind.DateTime => "slopfactory_datetime_compare(m.serialized_value,$metadataValue)>0",
            MetadataFilterOperator.GreaterThanOrEqual when filter.Kind == MetadataValueKind.DateTime => "slopfactory_datetime_compare(m.serialized_value,$metadataValue)>=0",
            MetadataFilterOperator.DoesNotEqual when filter.Kind == MetadataValueKind.Json => "NOT slopfactory_json_equal(m.serialized_value,$metadataValue)",
            _ => throw new LibraryValidationException("The metadata filter operator is not supported for this type.")
        };
        return $"EXISTS (SELECT 1 FROM metadata_entries m WHERE m.file_id=f.id AND m.key_key=$metadataKey AND m.kind=$metadataKind AND {comparison})";
    }

    private static int CompareMetadataNumbers(string left, string right) => decimal.Parse(left, NumberStyles.Float, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right, NumberStyles.Float, CultureInfo.InvariantCulture));
    private static int CompareMetadataDateTimes(string left, string right) => DateTimeOffset.Parse(left, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).CompareTo(DateTimeOffset.Parse(right, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static bool JsonStructurallyEquals(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElementsEqual(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number) return JsonNumbersEqual(left.GetRawText(), right.GetRawText());
            return false;
        }
        return left.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectsEqual(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonElementsEqual(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => JsonNumbersEqual(left.GetRawText(), right.GetRawText()),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false
        };
    }

    private static bool JsonObjectsEqual(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        return leftProperties.Count == rightProperties.Count && leftProperties.All(property => rightProperties.TryGetValue(property.Key, out var value) && JsonElementsEqual(property.Value, value));
    }

    private static bool JsonNumbersEqual(string left, string right) => NormalizeJsonNumber(left) == NormalizeJsonNumber(right);

    private static (BigInteger Significand, BigInteger Power) NormalizeJsonNumber(string value)
    {
        var negative = value.Length > 0 && value[0] == '-';
        var unsigned = negative ? value[1..] : value;
        var exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(unsigned[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var decimalIndex = mantissa.IndexOf('.');
        var fractionDigits = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return (BigInteger.Zero, BigInteger.Zero);
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        if (trailingZeros > 0) digits = digits[..^trailingZeros];
        var significand = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (negative) significand = -significand;
        return (significand, exponent - fractionDigits + trailingZeros);
    }

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
