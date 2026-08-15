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

            CREATE TABLE connections (
                id TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                label_key TEXT NOT NULL,
                provider_type INTEGER NOT NULL,
                base_url TEXT NOT NULL,
                credential_header_name TEXT NOT NULL,
                auth_prefix TEXT NOT NULL,
                has_credential INTEGER NOT NULL DEFAULT 0,
                last_test_status INTEGER NOT NULL DEFAULT 0,
                last_tested_at TEXT NULL,
                last_test_message TEXT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                recycled_at TEXT NULL,
                catalogue_retrieved_at TEXT NULL,
                catalogue_possibly_stale INTEGER NOT NULL DEFAULT 0,
                timeout_seconds INTEGER NULL,
                generic_models_enabled INTEGER NOT NULL DEFAULT 1,
                generic_models_path TEXT NULL,
                generic_text_enabled INTEGER NOT NULL DEFAULT 1,
                generic_text_path TEXT NULL,
                generic_image_enabled INTEGER NOT NULL DEFAULT 1,
                generic_image_path TEXT NULL,
                credential_revision_id TEXT NULL,
                credential_requires_repair INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX ux_connections_active_label ON connections(label_key) WHERE state = 0;

            CREATE TABLE connection_credential_revisions (
                connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,
                revision_id TEXT NOT NULL,
                purpose INTEGER NOT NULL CHECK(purpose IN (0,1)),
                created_at TEXT NOT NULL,
                PRIMARY KEY(connection_id, revision_id)
            );
            CREATE INDEX ix_connection_credential_revisions_connection ON connection_credential_revisions(connection_id, purpose);

            CREATE TABLE connection_headers (
                connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY(connection_id, name)
            );

            CREATE TABLE connection_model_catalogue (
                connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,
                provider_model_id TEXT NOT NULL,
                display_label TEXT NULL,
                PRIMARY KEY(connection_id, provider_model_id)
            );

            CREATE TABLE models (
                id TEXT PRIMARY KEY,
                connection_id TEXT NOT NULL REFERENCES connections(id),
                label TEXT NOT NULL,
                label_key TEXT NOT NULL,
                provider_model_id TEXT NOT NULL,
                mode INTEGER NOT NULL,
                supports_system_instructions INTEGER NOT NULL DEFAULT 0,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                recycled_at TEXT NULL,
                needs_review INTEGER NOT NULL DEFAULT 0,
                text_format INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX ux_models_active_label ON models(label_key) WHERE state = 0;
            CREATE INDEX ix_models_connection_state ON models(connection_id, state);

            CREATE TABLE generation_records (
                id TEXT PRIMARY KEY,
                model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,
                model_label TEXT NOT NULL,
                provider_model_id TEXT NOT NULL,
                provider_type INTEGER NOT NULL,
                mode INTEGER NOT NULL,
                prompt TEXT NOT NULL,
                system_instructions TEXT NULL,
                result_count INTEGER NOT NULL,
                status INTEGER NOT NULL,
                error_message TEXT NULL,
                destination_folder_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                prompt_tokens INTEGER NULL,
                completion_tokens INTEGER NULL,
                source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                prompt_improvement_record_id TEXT NULL REFERENCES prompt_improvement_records(id) ON DELETE SET NULL,
                text_format INTEGER NULL,
                state INTEGER NOT NULL DEFAULT 0,
                recycled_at TEXT NULL,
                tombstone_source_display_name TEXT NULL,
                tombstone_source_media_type TEXT NULL,
                tombstone_source_content_hash TEXT NULL,
                settings_temperature REAL NULL,
                settings_top_p REAL NULL,
                settings_max_tokens INTEGER NULL,
                settings_frequency_penalty REAL NULL,
                settings_presence_penalty REAL NULL,
                secondary_source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                secondary_tombstone_display_name TEXT NULL,
                secondary_tombstone_media_type TEXT NULL,
                secondary_tombstone_content_hash TEXT NULL,
                tertiary_source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                tertiary_tombstone_display_name TEXT NULL,
                tertiary_tombstone_media_type TEXT NULL,
                tertiary_tombstone_content_hash TEXT NULL,
                safety_blocked_count INTEGER NOT NULL DEFAULT 0,
                actual_cost REAL NULL,
                actual_cost_currency TEXT NULL
            );
            CREATE INDEX ix_generation_records_created ON generation_records(created_at);

            CREATE TABLE prompt_improvement_records (
                id TEXT PRIMARY KEY,
                model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,
                model_label TEXT NOT NULL,
                provider_model_id TEXT NOT NULL,
                provider_type INTEGER NOT NULL,
                raw_prompt TEXT NOT NULL,
                guidance TEXT NULL,
                template_version TEXT NOT NULL,
                status INTEGER NOT NULL,
                error_message TEXT NULL,
                candidates_json TEXT NOT NULL,
                prompt_tokens INTEGER NULL,
                completion_tokens INTEGER NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL
            );
            CREATE INDEX ix_prompt_improvement_records_created ON prompt_improvement_records(created_at);

            CREATE TABLE generation_results (
                id TEXT PRIMARY KEY,
                generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,
                file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                position INTEGER NOT NULL,
                tombstone_display_name TEXT NULL,
                tombstone_media_type TEXT NULL,
                tombstone_content_hash TEXT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                result_error_message TEXT NULL
            );
            CREATE INDEX ix_generation_results_generation ON generation_results(generation_id);

            CREATE TABLE saved_generation_settings (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                title_key TEXT NOT NULL,
                model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,
                model_label TEXT NOT NULL,
                mode INTEGER NOT NULL,
                prompt TEXT NOT NULL,
                system_instructions TEXT NULL,
                result_count INTEGER NOT NULL,
                destination_folder_id TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                recycled_at TEXT NULL,
                source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                needs_review INTEGER NOT NULL DEFAULT 0,
                revision INTEGER NOT NULL DEFAULT 1,
                settings_temperature REAL NULL,
                settings_top_p REAL NULL,
                settings_max_tokens INTEGER NULL,
                settings_frequency_penalty REAL NULL,
                settings_presence_penalty REAL NULL,
                secondary_source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                tertiary_source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX ux_saved_settings_active_title ON saved_generation_settings(title_key) WHERE state = 0;
            CREATE INDEX ix_saved_settings_model ON saved_generation_settings(model_id, state);

            CREATE TABLE generation_drafts (
                id TEXT PRIMARY KEY,
                custom_title TEXT NULL,
                tab_order INTEGER NOT NULL,
                model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,
                prompt TEXT NOT NULL DEFAULT '',
                system_instructions TEXT NULL,
                source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                result_count INTEGER NOT NULL DEFAULT 1,
                destination_folder_id TEXT NOT NULL,
                improvement_model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,
                improvement_guidance TEXT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                settings_temperature REAL NULL,
                settings_top_p REAL NULL,
                settings_max_tokens INTEGER NULL,
                settings_frequency_penalty REAL NULL,
                settings_presence_penalty REAL NULL,
                secondary_source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,
                tertiary_source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL
            );
            CREATE INDEX ix_generation_drafts_order ON generation_drafts(tab_order);

            CREATE TABLE async_remote_jobs (
                id TEXT PRIMARY KEY,
                draft_id TEXT NOT NULL,
                provider_type INTEGER NOT NULL,
                connection_id TEXT NOT NULL,
                provider_job_id TEXT NOT NULL,
                phase INTEGER NOT NULL,
                idempotency_key TEXT NULL,
                submitted_at TEXT NOT NULL,
                last_polled_at TEXT NULL,
                monitoring_deadline TEXT NULL,
                generation_record_id TEXT NULL REFERENCES generation_records(id) ON DELETE CASCADE,
                position INTEGER NULL
            );
            CREATE INDEX ix_async_remote_jobs_connection ON async_remote_jobs(connection_id);
            CREATE INDEX ix_async_remote_jobs_phase ON async_remote_jobs(phase);
            CREATE INDEX ix_async_remote_jobs_generation ON async_remote_jobs(generation_record_id);

            CREATE TABLE pending_unverified_results (
                id TEXT PRIMARY KEY,
                generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                staged_file_name TEXT NOT NULL,
                byte_size INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                detected_media_type TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX ix_pending_unverified_results_generation ON pending_unverified_results(generation_id);
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
        if (fromVersion < 9)
        {
            await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS connections (id TEXT PRIMARY KEY,label TEXT NOT NULL,label_key TEXT NOT NULL,provider_type INTEGER NOT NULL,base_url TEXT NOT NULL,credential_header_name TEXT NOT NULL,auth_prefix TEXT NOT NULL,has_credential INTEGER NOT NULL DEFAULT 0,last_test_status INTEGER NOT NULL DEFAULT 0,last_tested_at TEXT NULL,last_test_message TEXT NULL,state INTEGER NOT NULL,created_at TEXT NOT NULL,modified_at TEXT NOT NULL,recycled_at TEXT NULL); CREATE UNIQUE INDEX IF NOT EXISTS ux_connections_active_label ON connections(label_key) WHERE state = 0;", cancellationToken, transaction).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS models (id TEXT PRIMARY KEY,connection_id TEXT NOT NULL REFERENCES connections(id),label TEXT NOT NULL,label_key TEXT NOT NULL,provider_model_id TEXT NOT NULL,mode INTEGER NOT NULL,supports_system_instructions INTEGER NOT NULL DEFAULT 0,state INTEGER NOT NULL,created_at TEXT NOT NULL,modified_at TEXT NOT NULL,recycled_at TEXT NULL); CREATE UNIQUE INDEX IF NOT EXISTS ux_models_active_label ON models(label_key) WHERE state = 0; CREATE INDEX IF NOT EXISTS ix_models_connection_state ON models(connection_id, state);", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 10)
        {
            await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS generation_records (id TEXT PRIMARY KEY,model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,model_label TEXT NOT NULL,provider_model_id TEXT NOT NULL,provider_type INTEGER NOT NULL,mode INTEGER NOT NULL,prompt TEXT NOT NULL,result_count INTEGER NOT NULL,status INTEGER NOT NULL,error_message TEXT NULL,destination_folder_id TEXT NOT NULL,created_at TEXT NOT NULL,completed_at TEXT NULL); CREATE INDEX IF NOT EXISTS ix_generation_records_created ON generation_records(created_at);", cancellationToken, transaction).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS generation_results (id TEXT PRIMARY KEY,generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,file_id TEXT NOT NULL REFERENCES files(id),position INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS ix_generation_results_generation ON generation_results(generation_id);", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 11)
        {
            await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS saved_generation_settings (id TEXT PRIMARY KEY,title TEXT NOT NULL,title_key TEXT NOT NULL,model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,model_label TEXT NOT NULL,mode INTEGER NOT NULL,prompt TEXT NOT NULL,result_count INTEGER NOT NULL,destination_folder_id TEXT NOT NULL,state INTEGER NOT NULL,created_at TEXT NOT NULL,modified_at TEXT NOT NULL,recycled_at TEXT NULL); CREATE UNIQUE INDEX IF NOT EXISTS ux_saved_settings_active_title ON saved_generation_settings(title_key) WHERE state = 0; CREATE INDEX IF NOT EXISTS ix_saved_settings_model ON saved_generation_settings(model_id, state);", cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 12)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "system_instructions", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "saved_generation_settings", "system_instructions", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 13)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "prompt_tokens", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "completion_tokens", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 14)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "saved_generation_settings", "source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 15)
        {
            await AddColumnIfMissingAsync(connection, transaction, "connections", "catalogue_retrieved_at", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "catalogue_possibly_stale", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection,
                "CREATE TABLE IF NOT EXISTS connection_model_catalogue (connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,provider_model_id TEXT NOT NULL,display_label TEXT NULL,PRIMARY KEY(connection_id, provider_model_id));",
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 16)
        {
            await AddColumnIfMissingAsync(connection, transaction, "connections", "timeout_seconds", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 17)
        {
            await ExecuteNonQueryAsync(connection,
                "CREATE TABLE IF NOT EXISTS connection_headers (connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,name TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(connection_id, name));",
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 18)
        {
            await AddColumnIfMissingAsync(connection, transaction, "connections", "generic_models_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "generic_models_path", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "generic_text_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "generic_text_path", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "generic_image_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "generic_image_path", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 19)
        {
            await ExecuteNonQueryAsync(connection,
                "CREATE TABLE IF NOT EXISTS prompt_improvement_records (id TEXT PRIMARY KEY,model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,model_label TEXT NOT NULL,provider_model_id TEXT NOT NULL,provider_type INTEGER NOT NULL,raw_prompt TEXT NOT NULL,guidance TEXT NULL,template_version TEXT NOT NULL,status INTEGER NOT NULL,error_message TEXT NULL,candidates_json TEXT NOT NULL,prompt_tokens INTEGER NULL,completion_tokens INTEGER NULL,created_at TEXT NOT NULL,completed_at TEXT NULL); CREATE INDEX IF NOT EXISTS ix_prompt_improvement_records_created ON prompt_improvement_records(created_at);",
                cancellationToken, transaction).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "prompt_improvement_record_id", "TEXT NULL REFERENCES prompt_improvement_records(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 20)
        {
            await AddColumnIfMissingAsync(connection, transaction, "models", "needs_review", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "saved_generation_settings", "needs_review", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 21)
        {
            await AddColumnIfMissingAsync(connection, transaction, "models", "text_format", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "text_format", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 22)
        {
            await ExecuteNonQueryAsync(connection,
                "CREATE TABLE IF NOT EXISTS generation_drafts (id TEXT PRIMARY KEY,custom_title TEXT NULL,tab_order INTEGER NOT NULL,model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,prompt TEXT NOT NULL DEFAULT '',system_instructions TEXT NULL,source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,result_count INTEGER NOT NULL DEFAULT 1,destination_folder_id TEXT NOT NULL,improvement_model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,improvement_guidance TEXT NULL,created_at TEXT NOT NULL,modified_at TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_generation_drafts_order ON generation_drafts(tab_order);",
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 23)
        {
            await AddColumnIfMissingAsync(connection, transaction, "connections", "credential_revision_id", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "connections", "credential_requires_repair", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection,
                "CREATE TABLE IF NOT EXISTS connection_credential_revisions (connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,revision_id TEXT NOT NULL,purpose INTEGER NOT NULL CHECK(purpose IN (0,1)),created_at TEXT NOT NULL,PRIMARY KEY(connection_id, revision_id)); CREATE INDEX IF NOT EXISTS ix_connection_credential_revisions_connection ON connection_credential_revisions(connection_id, purpose);",
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 24)
        {
            await AddColumnIfMissingAsync(connection, transaction, "saved_generation_settings", "revision", "INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 25)
        {
            await ExecuteNonQueryAsync(connection,
                """
                ALTER TABLE generation_results RENAME TO generation_results_old_v24;
                CREATE TABLE generation_results (id TEXT PRIMARY KEY,generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,position INTEGER NOT NULL);
                INSERT INTO generation_results(id,generation_id,file_id,position) SELECT id,generation_id,file_id,position FROM generation_results_old_v24;
                DROP TABLE generation_results_old_v24;
                CREATE INDEX IF NOT EXISTS ix_generation_results_generation ON generation_results(generation_id);
                """,
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 26)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "state", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "recycled_at", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tombstone_source_display_name", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tombstone_source_media_type", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tombstone_source_content_hash", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection,
                """
                ALTER TABLE generation_results RENAME TO generation_results_old_v25;
                CREATE TABLE generation_results (id TEXT PRIMARY KEY,generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,position INTEGER NOT NULL,tombstone_display_name TEXT NULL,tombstone_media_type TEXT NULL,tombstone_content_hash TEXT NULL);
                INSERT INTO generation_results(id,generation_id,file_id,position) SELECT id,generation_id,file_id,position FROM generation_results_old_v25;
                DROP TABLE generation_results_old_v25;
                CREATE INDEX IF NOT EXISTS ix_generation_results_generation ON generation_results(generation_id);
                """,
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 27)
        {
            foreach (var table in new[] { "generation_records", "saved_generation_settings", "generation_drafts" })
            {
                await AddColumnIfMissingAsync(connection, transaction, table, "settings_temperature", "REAL NULL", cancellationToken).ConfigureAwait(false);
                await AddColumnIfMissingAsync(connection, transaction, table, "settings_top_p", "REAL NULL", cancellationToken).ConfigureAwait(false);
                await AddColumnIfMissingAsync(connection, transaction, table, "settings_max_tokens", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
                await AddColumnIfMissingAsync(connection, transaction, table, "settings_frequency_penalty", "REAL NULL", cancellationToken).ConfigureAwait(false);
                await AddColumnIfMissingAsync(connection, transaction, table, "settings_presence_penalty", "REAL NULL", cancellationToken).ConfigureAwait(false);
            }
        }
        if (fromVersion < 28)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_drafts", "secondary_source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_drafts", "tertiary_source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "saved_generation_settings", "secondary_source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "saved_generation_settings", "tertiary_source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "secondary_source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "secondary_tombstone_display_name", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "secondary_tombstone_media_type", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "secondary_tombstone_content_hash", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tertiary_source_file_id", "TEXT NULL REFERENCES files(id) ON DELETE SET NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tertiary_tombstone_display_name", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tertiary_tombstone_media_type", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "tertiary_tombstone_content_hash", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 29)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "safety_blocked_count", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 30)
        {
            await ExecuteNonQueryAsync(connection,
                """
                CREATE TABLE IF NOT EXISTS async_remote_jobs (id TEXT PRIMARY KEY,draft_id TEXT NOT NULL,provider_type INTEGER NOT NULL,connection_id TEXT NOT NULL,provider_job_id TEXT NOT NULL,phase INTEGER NOT NULL,idempotency_key TEXT NULL,submitted_at TEXT NOT NULL,last_polled_at TEXT NULL,monitoring_deadline TEXT NULL);
                CREATE INDEX IF NOT EXISTS ix_async_remote_jobs_connection ON async_remote_jobs(connection_id);
                CREATE INDEX IF NOT EXISTS ix_async_remote_jobs_phase ON async_remote_jobs(phase);
                """,
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 31)
        {
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "actual_cost", "REAL NULL", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_records", "actual_cost_currency", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 32)
        {
            // Every existing row represents an already-committed file, so DEFAULT 0 (Committed) is
            // correct for pre-existing data without a data migration pass.
            await AddColumnIfMissingAsync(connection, transaction, "generation_results", "status", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "generation_results", "result_error_message", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        }
        if (fromVersion < 33)
        {
            await ExecuteNonQueryAsync(connection,
                """
                CREATE TABLE IF NOT EXISTS pending_unverified_results (
                    id TEXT PRIMARY KEY,
                    generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,
                    position INTEGER NOT NULL,
                    staged_file_name TEXT NOT NULL,
                    byte_size INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    detected_media_type TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_pending_unverified_results_generation ON pending_unverified_results(generation_id);
                """,
                cancellationToken, transaction).ConfigureAwait(false);
        }
        if (fromVersion < 34)
        {
            await AddColumnIfMissingAsync(connection, transaction, "async_remote_jobs", "generation_record_id", "TEXT NULL REFERENCES generation_records(id) ON DELETE CASCADE", cancellationToken).ConfigureAwait(false);
            await AddColumnIfMissingAsync(connection, transaction, "async_remote_jobs", "position", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS ix_async_remote_jobs_generation ON async_remote_jobs(generation_record_id);", cancellationToken, transaction).ConfigureAwait(false);
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

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.id,c.label,c.state,c.recycled_at,
                    (SELECT COUNT(*) FROM models m WHERE m.connection_id=c.id),
                    (SELECT COUNT(*) FROM saved_generation_settings s JOIN models m2 ON m2.id=s.model_id WHERE m2.connection_id=c.id)
                FROM connections c
                WHERE c.state IN (1,2);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.Connection, reader.GetString(0)),
                    reader.GetString(1), "Connections", (LibraryRecordState)reader.GetInt32(2), Parse(reader.GetString(3)),
                    0, 0, 0, null, reader.GetInt32(4), reader.GetInt32(5)));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.id,m.label,c.label,m.state,m.recycled_at,
                    (SELECT COUNT(*) FROM saved_generation_settings s WHERE s.model_id=m.id)
                FROM models m
                JOIN connections c ON c.id=m.connection_id
                WHERE m.state IN (1,2) AND c.state NOT IN (1,2);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.Model, reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2), (LibraryRecordState)reader.GetInt32(3), Parse(reader.GetString(4)),
                    0, 0, 0, null, 0, reader.GetInt32(5)));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.id,s.title,COALESCE(m.label,'Unassigned model'),s.state,s.recycled_at
                FROM saved_generation_settings s
                LEFT JOIN models m ON m.id=s.model_id
                LEFT JOIN connections c ON c.id=m.connection_id
                WHERE s.state IN (1,2)
                  AND (m.id IS NULL OR m.state NOT IN (1,2))
                  AND (c.id IS NULL OR c.state NOT IN (1,2));
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.SavedSetting, reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2), (LibraryRecordState)reader.GetInt32(3), Parse(reader.GetString(4)),
                    0, 0, 0, null));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,model_label,state,recycled_at FROM generation_records WHERE state IN (1,2);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RecycleBinEntry(
                    new RecycleBinItemReference(RecycleBinItemKind.GenerationRecord, reader.GetString(0)),
                    reader.GetString(1), "Generation History", (LibraryRecordState)reader.GetInt32(2), Parse(reader.GetString(3)),
                    0, 0, 0, null));
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
            case RecycleBinItemKind.Connection:
            {
                var conn = await GetConnectionAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (conn.State != LibraryRecordState.Recycled) blockers.Add("Only a recycled connection can be restored.");
                await using var conflict = connection.CreateCommand();
                conflict.CommandText = "SELECT EXISTS(SELECT 1 FROM connections candidate JOIN connections restoring ON restoring.id=$id WHERE candidate.label_key=restoring.label_key AND candidate.state=0 AND candidate.id<>restoring.id);";
                conflict.Parameters.AddWithValue("$id", reference.Id);
                if (Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                {
                    blockers.Add($"An active connection labelled '{conn.Label}' already exists.");
                }
                break;
            }
            case RecycleBinItemKind.Model:
            {
                var model = await GetModelAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (model.State != LibraryRecordState.Recycled) blockers.Add("Only a recycled model can be restored.");
                var owningConnection = await GetConnectionAsync(connection, model.ConnectionId, cancellationToken).ConfigureAwait(false);
                if (owningConnection.State != LibraryRecordState.Active) blockers.Add($"Its owning connection '{owningConnection.Label}' must be restored first.");
                await using var conflict = connection.CreateCommand();
                conflict.CommandText = "SELECT EXISTS(SELECT 1 FROM models candidate JOIN models restoring ON restoring.id=$id WHERE candidate.label_key=restoring.label_key AND candidate.state=0 AND candidate.id<>restoring.id);";
                conflict.Parameters.AddWithValue("$id", reference.Id);
                if (Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                {
                    blockers.Add($"An active model labelled '{model.Label}' already exists.");
                }
                break;
            }
            case RecycleBinItemKind.SavedSetting:
            {
                var setting = await GetSavedSettingAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (setting.State != LibraryRecordState.Recycled) blockers.Add("Only a recycled saved setting can be restored.");
                await using var conflict = connection.CreateCommand();
                conflict.CommandText = "SELECT EXISTS(SELECT 1 FROM saved_generation_settings candidate JOIN saved_generation_settings restoring ON restoring.id=$id WHERE candidate.title_key=restoring.title_key AND candidate.state=0 AND candidate.id<>restoring.id);";
                conflict.Parameters.AddWithValue("$id", reference.Id);
                if (Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                {
                    blockers.Add($"An active saved setting titled '{setting.Title}' already exists.");
                }
                break;
            }
            case RecycleBinItemKind.GenerationRecord:
            {
                var record = await GetGenerationRecordAsync(connection, reference.Id, cancellationToken).ConfigureAwait(false);
                if (record.State != LibraryRecordState.Recycled) blockers.Add("Only a recycled generation record can be restored.");
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

    public async Task DeleteEmptyActiveFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM folders WHERE id=$id AND state=0 AND parent_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM folders child WHERE child.parent_id=$id) AND NOT EXISTS(SELECT 1 FROM files WHERE folder_id=$id);",
            cancellationToken, null, ("$id", folderId)).ConfigureAwait(false);
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
        await ExecuteNonQueryAsync(connection, "UPDATE generation_records SET tombstone_source_display_name=$name,tombstone_source_media_type=$media,tombstone_source_content_hash=$hash WHERE source_file_id=$id;", cancellationToken, transaction, ("$id", fileId), ("$name", file.DisplayName), ("$media", file.MediaType), ("$hash", file.ContentHash)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE generation_records SET secondary_tombstone_display_name=$name,secondary_tombstone_media_type=$media,secondary_tombstone_content_hash=$hash WHERE secondary_source_file_id=$id;", cancellationToken, transaction, ("$id", fileId), ("$name", file.DisplayName), ("$media", file.MediaType), ("$hash", file.ContentHash)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE generation_records SET tertiary_tombstone_display_name=$name,tertiary_tombstone_media_type=$media,tertiary_tombstone_content_hash=$hash WHERE tertiary_source_file_id=$id;", cancellationToken, transaction, ("$id", fileId), ("$name", file.DisplayName), ("$media", file.MediaType), ("$hash", file.ContentHash)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE generation_results SET tombstone_display_name=$name,tombstone_media_type=$media,tombstone_content_hash=$hash WHERE file_id=$id;", cancellationToken, transaction, ("$id", fileId), ("$name", file.DisplayName), ("$media", file.MediaType), ("$hash", file.ContentHash)).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<Connection>> GetActiveConnectionsAsync(CancellationToken cancellationToken) =>
        await ListConnectionsAsync(LibraryRecordState.Active, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Connection>> GetRecycledConnectionsAsync(CancellationToken cancellationToken) =>
        await ListConnectionsAsync(LibraryRecordState.Recycled, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<Connection>> ListConnectionsAsync(LibraryRecordState state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<Connection>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = ConnectionSelect + " WHERE state=$state ORDER BY label_key;";
            command.Parameters.AddWithValue("$state", (int)state);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadConnection(reader));
        }
        for (var i = 0; i < results.Count; i++)
        {
            var headers = await LoadConnectionHeadersAsync(connection, results[i].Id, null, cancellationToken).ConfigureAwait(false);
            results[i] = results[i] with { AdditionalHeaders = headers };
        }
        return results;
    }

    public async Task<Connection> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Connection> CreateConnectionAsync(string label, ProviderType providerType, string baseUrl, string credentialHeaderName, string authPrefix, int? timeoutSeconds, IReadOnlyList<ConnectionHeader>? additionalHeaders, GenericConnectionModalitySettings? genericModalitySettings, CancellationToken cancellationToken)
    {
        var normalizedLabel = LibraryRules.NormalizeShortLabel(label, "Connection label");
        var normalizedBaseUrl = LibraryRules.NormalizeConnectionBaseUrl(baseUrl);
        var normalizedHeaderName = LibraryRules.NormalizeShortLabel(credentialHeaderName, "Credential header name");
        var normalizedAuthPrefix = authPrefix?.Trim() ?? string.Empty;
        var normalizedTimeoutSeconds = LibraryRules.NormalizeConnectionTimeoutSeconds(timeoutSeconds);
        var normalizedHeaders = LibraryRules.NormalizeConnectionHeaders(additionalHeaders, normalizedHeaderName);
        var normalizedModalitySettings = LibraryRules.NormalizeGenericModalitySettings(genericModalitySettings);
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO connections(id,label,label_key,provider_type,base_url,credential_header_name,auth_prefix,has_credential,last_test_status,state,created_at,modified_at,timeout_seconds,generic_models_enabled,generic_models_path,generic_text_enabled,generic_text_path,generic_image_enabled,generic_image_path) VALUES($id,$label,$key,$provider,$url,$header,$prefix,0,0,0,$now,$now,$timeout,$modelsEnabled,$modelsPath,$textEnabled,$textPath,$imageEnabled,$imagePath);",
                cancellationToken, transaction,
                ("$id", id), ("$label", normalizedLabel), ("$key", LibraryRules.ComparisonKey(normalizedLabel)), ("$provider", (int)providerType),
                ("$url", normalizedBaseUrl), ("$header", normalizedHeaderName), ("$prefix", normalizedAuthPrefix), ("$now", Format(now)),
                ("$timeout", (object?)normalizedTimeoutSeconds ?? DBNull.Value),
                ("$modelsEnabled", normalizedModalitySettings.ModelsEnabled), ("$modelsPath", (object?)normalizedModalitySettings.ModelsPathOverride ?? DBNull.Value),
                ("$textEnabled", normalizedModalitySettings.TextGenerationEnabled), ("$textPath", (object?)normalizedModalitySettings.TextGenerationPathOverride ?? DBNull.Value),
                ("$imageEnabled", normalizedModalitySettings.ImageGenerationEnabled), ("$imagePath", (object?)normalizedModalitySettings.ImageGenerationPathOverride ?? DBNull.Value)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active connection labelled '{normalizedLabel}' already exists.");
        }

        await ReplaceConnectionHeadersAsync(connection, transaction, id, normalizedHeaders, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Connection(id, normalizedLabel, providerType, normalizedBaseUrl, normalizedHeaderName, normalizedAuthPrefix, false, ConnectionTestStatus.Untested, null, null, LibraryRecordState.Active, now, now, null, normalizedTimeoutSeconds, normalizedHeaders, normalizedModalitySettings);
    }

    public async Task<Connection> UpdateConnectionAsync(string connectionId, string label, string baseUrl, string credentialHeaderName, string authPrefix, int? timeoutSeconds, IReadOnlyList<ConnectionHeader>? additionalHeaders, GenericConnectionModalitySettings? genericModalitySettings, CancellationToken cancellationToken)
    {
        var normalizedLabel = LibraryRules.NormalizeShortLabel(label, "Connection label");
        var normalizedBaseUrl = LibraryRules.NormalizeConnectionBaseUrl(baseUrl);
        var normalizedHeaderName = LibraryRules.NormalizeShortLabel(credentialHeaderName, "Credential header name");
        var normalizedAuthPrefix = authPrefix?.Trim() ?? string.Empty;
        var normalizedTimeoutSeconds = LibraryRules.NormalizeConnectionTimeoutSeconds(timeoutSeconds);
        var normalizedHeaders = LibraryRules.NormalizeConnectionHeaders(additionalHeaders, normalizedHeaderName);
        var normalizedModalitySettings = LibraryRules.NormalizeGenericModalitySettings(genericModalitySettings);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active connection can be edited.");
        var modified = DateTimeOffset.UtcNow;
        try
        {
            await ExecuteNonQueryAsync(connection,
                "UPDATE connections SET label=$label,label_key=$key,base_url=$url,credential_header_name=$header,auth_prefix=$prefix,timeout_seconds=$timeout,generic_models_enabled=$modelsEnabled,generic_models_path=$modelsPath,generic_text_enabled=$textEnabled,generic_text_path=$textPath,generic_image_enabled=$imageEnabled,generic_image_path=$imagePath,last_test_status=0,last_tested_at=NULL,last_test_message=NULL,modified_at=$modified WHERE id=$id AND state=0;",
                cancellationToken, transaction,
                ("$label", normalizedLabel), ("$key", LibraryRules.ComparisonKey(normalizedLabel)), ("$url", normalizedBaseUrl), ("$header", normalizedHeaderName),
                ("$prefix", normalizedAuthPrefix), ("$timeout", (object?)normalizedTimeoutSeconds ?? DBNull.Value),
                ("$modelsEnabled", normalizedModalitySettings.ModelsEnabled), ("$modelsPath", (object?)normalizedModalitySettings.ModelsPathOverride ?? DBNull.Value),
                ("$textEnabled", normalizedModalitySettings.TextGenerationEnabled), ("$textPath", (object?)normalizedModalitySettings.TextGenerationPathOverride ?? DBNull.Value),
                ("$imageEnabled", normalizedModalitySettings.ImageGenerationEnabled), ("$imagePath", (object?)normalizedModalitySettings.ImageGenerationPathOverride ?? DBNull.Value),
                ("$modified", Format(modified)), ("$id", connectionId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active connection labelled '{normalizedLabel}' already exists.");
        }

        await ReplaceConnectionHeadersAsync(connection, transaction, connectionId, normalizedHeaders, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return existing with
        {
            Label = normalizedLabel, BaseUrl = normalizedBaseUrl, CredentialHeaderName = normalizedHeaderName, AuthPrefix = normalizedAuthPrefix,
            TimeoutSeconds = normalizedTimeoutSeconds, AdditionalHeaders = normalizedHeaders, GenericModalitySettings = normalizedModalitySettings,
            LastTestStatus = ConnectionTestStatus.Untested, LastTestedAt = null, LastTestMessage = null, ModifiedAt = modified
        };
    }

    public async Task<Connection> SetConnectionCredentialStateAsync(string connectionId, bool hasCredential, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE connections SET has_credential=$has,modified_at=$modified WHERE id=$id;",
            cancellationToken, null, ("$has", hasCredential), ("$modified", Format(modified)), ("$id", connectionId)).ConfigureAwait(false);
        return existing with { HasCredential = hasCredential, ModifiedAt = modified };
    }

    public async Task<Connection> SetConnectionTestResultAsync(string connectionId, bool success, string message, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var status = success ? ConnectionTestStatus.Success : ConnectionTestStatus.Failed;
        await ExecuteNonQueryAsync(connection, "UPDATE connections SET last_test_status=$status,last_tested_at=$now,last_test_message=$message,modified_at=$now WHERE id=$id;",
            cancellationToken, null, ("$status", (int)status), ("$now", Format(now)), ("$message", message), ("$id", connectionId)).ConfigureAwait(false);
        return existing with { LastTestStatus = status, LastTestedAt = now, LastTestMessage = message, ModifiedAt = now };
    }

    public async Task<Connection> ChangeConnectionProviderTypeAsync(string connectionId, ProviderType providerType, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active connection can be edited.");
        if (existing.ProviderType == providerType) return existing;

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM models WHERE connection_id=$id AND state=0;";
            countCommand.Parameters.AddWithValue("$id", connectionId);
            if (Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                throw new LibraryValidationException("The provider type cannot be changed while dependent models exist.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection,
            "UPDATE connections SET provider_type=$provider,generic_models_enabled=1,generic_models_path=NULL,generic_text_enabled=1,generic_text_path=NULL,generic_image_enabled=1,generic_image_path=NULL,last_test_status=0,last_tested_at=NULL,last_test_message=NULL,modified_at=$now WHERE id=$id;",
            cancellationToken, null, ("$provider", (int)providerType), ("$now", Format(now)), ("$id", connectionId)).ConfigureAwait(false);

        return existing with
        {
            ProviderType = providerType, GenericModalitySettings = GenericConnectionModalitySettings.Default,
            LastTestStatus = ConnectionTestStatus.Untested, LastTestedAt = null, LastTestMessage = null, ModifiedAt = now
        };
    }

    public async Task<ModelCatalogue> GetModelCatalogueAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        return await ReadModelCatalogueAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelCatalogue> RefreshModelCatalogueAsync(string connectionId, IReadOnlyList<ProviderModelInfo> discoveredModels, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM connection_model_catalogue WHERE connection_id=$id;", cancellationToken, transaction, ("$id", connectionId)).ConfigureAwait(false);
        foreach (var entry in discoveredModels)
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO connection_model_catalogue(connection_id,provider_model_id,display_label) VALUES($cid,$pid,$label);",
                cancellationToken, transaction, ("$cid", connectionId), ("$pid", entry.ProviderModelId), ("$label", (object?)entry.DisplayLabel ?? DBNull.Value)).ConfigureAwait(false);
        }
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE connections SET catalogue_retrieved_at=$now,catalogue_possibly_stale=0 WHERE id=$id;",
            cancellationToken, transaction, ("$now", Format(now)), ("$id", connectionId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ModelCatalogue(now, false, discoveredModels);
    }

    public async Task<ModelCatalogue> MarkModelCatalogueRefreshFailedAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE connections SET catalogue_possibly_stale=1 WHERE id=$id;", cancellationToken, null, ("$id", connectionId)).ConfigureAwait(false);
        return await ReadModelCatalogueAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecycleConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active connection can be recycled.");
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection, "UPDATE connections SET state=1,recycled_at=$now,modified_at=$now WHERE id=$id;", cancellationToken, transaction, ("$now", now), ("$id", connectionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE models SET state=1,recycled_at=$now,modified_at=$now WHERE connection_id=$id AND state=0;", cancellationToken, transaction, ("$now", now), ("$id", connectionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "UPDATE saved_generation_settings SET state=1,recycled_at=$now,modified_at=$now WHERE state=0 AND model_id IN (SELECT id FROM models WHERE connection_id=$id);",
            cancellationToken, transaction, ("$now", now), ("$id", connectionId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled connection can be restored.");
        var now = Format(DateTimeOffset.UtcNow);
        try
        {
            await ExecuteNonQueryAsync(connection, "UPDATE connections SET state=0,recycled_at=NULL,modified_at=$now WHERE id=$id;", cancellationToken, transaction, ("$now", now), ("$id", connectionId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active connection labelled '{existing.Label}' already exists.");
        }
        await ExecuteNonQueryAsync(connection, "UPDATE models SET state=0,recycled_at=NULL,modified_at=$now WHERE connection_id=$id AND state=1;", cancellationToken, transaction, ("$now", now), ("$id", connectionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "UPDATE saved_generation_settings SET state=0,recycled_at=NULL,modified_at=$now WHERE state=1 AND model_id IN (SELECT id FROM models WHERE connection_id=$id);",
            cancellationToken, transaction, ("$now", now), ("$id", connectionId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PermanentlyDeleteConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled connection can be permanently deleted.");
        await ExecuteNonQueryAsync(connection, "DELETE FROM saved_generation_settings WHERE model_id IN (SELECT id FROM models WHERE connection_id=$id);", cancellationToken, transaction, ("$id", connectionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM models WHERE connection_id=$id;", cancellationToken, transaction, ("$id", connectionId)).ConfigureAwait(false);
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM connections WHERE id=$id;", cancellationToken, transaction, ("$id", connectionId)).ConfigureAwait(false);
        if (deleted == 0) throw new RecordNotFoundException("Connection not found.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> BeginCredentialCandidateAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        var revisionId = LibraryRules.NewId();
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO connection_credential_revisions(connection_id,revision_id,purpose,created_at) VALUES($cid,$rid,0,$now);",
            cancellationToken, null, ("$cid", connectionId), ("$rid", revisionId), ("$now", now)).ConfigureAwait(false);
        return revisionId;
    }

    public async Task DiscardCredentialCandidateAsync(string connectionId, string revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM connection_credential_revisions WHERE connection_id=$cid AND revision_id=$rid AND purpose=0;",
            cancellationToken, null, ("$cid", connectionId), ("$rid", revisionId)).ConfigureAwait(false);
    }

    public async Task<CredentialPromotionResult> PromoteCredentialRevisionAsync(string connectionId, string revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken, transaction).ConfigureAwait(false);

        var flipped = await ExecuteNonQueryWithCountAsync(connection,
            "UPDATE connection_credential_revisions SET purpose=1 WHERE connection_id=$cid AND revision_id=$rid;",
            cancellationToken, transaction, ("$cid", connectionId), ("$rid", revisionId)).ConfigureAwait(false);
        if (flipped == 0) throw new RecordNotFoundException("Credential revision not found.");

        var superseded = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT revision_id FROM connection_credential_revisions WHERE connection_id=$cid AND purpose=1 AND revision_id<>$rid;";
            command.Parameters.AddWithValue("$cid", connectionId);
            command.Parameters.AddWithValue("$rid", revisionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) superseded.Add(reader.GetString(0));
        }

        await ExecuteNonQueryAsync(connection, "DELETE FROM connection_credential_revisions WHERE connection_id=$cid AND revision_id<>$rid;",
            cancellationToken, transaction, ("$cid", connectionId), ("$rid", revisionId)).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection,
            "UPDATE connections SET credential_revision_id=$rid,has_credential=1,credential_requires_repair=0,modified_at=$now WHERE id=$cid;",
            cancellationToken, transaction, ("$rid", revisionId), ("$now", Format(now)), ("$cid", connectionId)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var updated = existing with { CredentialRevisionId = revisionId, HasCredential = true, CredentialRequiresRepair = false, ModifiedAt = now };
        return new CredentialPromotionResult(updated, superseded);
    }

    public async Task<Connection> MarkCredentialRequiresRepairAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE connections SET credential_requires_repair=1,modified_at=$now WHERE id=$id;",
            cancellationToken, null, ("$now", Format(now)), ("$id", connectionId)).ConfigureAwait(false);
        return existing with { CredentialRequiresRepair = true, ModifiedAt = now };
    }

    public async Task<IReadOnlyList<CredentialLedgerConnectionSnapshot>> GetCredentialLedgerSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var revisionsByConnection = new Dictionary<string, List<CredentialLedgerRevision>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT connection_id,revision_id,purpose FROM connection_credential_revisions;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var connectionId = reader.GetString(0);
                if (!revisionsByConnection.TryGetValue(connectionId, out var list))
                {
                    list = new List<CredentialLedgerRevision>();
                    revisionsByConnection[connectionId] = list;
                }
                list.Add(new CredentialLedgerRevision(reader.GetString(1), (CredentialRevisionPurpose)reader.GetInt32(2)));
            }
        }

        var snapshots = new List<CredentialLedgerConnectionSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,has_credential,credential_revision_id FROM connections;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var connectionId = reader.GetString(0);
                var hasCredential = reader.GetBoolean(1);
                var committedRevisionId = reader.IsDBNull(2) ? null : reader.GetString(2);
                revisionsByConnection.TryGetValue(connectionId, out var revisions);
                snapshots.Add(new CredentialLedgerConnectionSnapshot(connectionId, hasCredential, committedRevisionId,
                    (IReadOnlyList<CredentialLedgerRevision>?)revisions ?? Array.Empty<CredentialLedgerRevision>()));
            }
        }

        return snapshots;
    }

    public async Task DeleteCredentialLedgerRowAsync(string connectionId, string revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM connection_credential_revisions WHERE connection_id=$cid AND revision_id=$rid;",
            cancellationToken, null, ("$cid", connectionId), ("$rid", revisionId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Model>> GetActiveModelsAsync(CancellationToken cancellationToken) =>
        await ListModelsAsync(LibraryRecordState.Active, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Model>> GetRecycledModelsAsync(CancellationToken cancellationToken) =>
        await ListModelsAsync(LibraryRecordState.Recycled, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<Model>> ListModelsAsync(LibraryRecordState state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ModelSelect + " WHERE state=$state ORDER BY label_key;";
        command.Parameters.AddWithValue("$state", (int)state);
        var results = new List<Model>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadModel(reader));
        return results;
    }

    public async Task<Model> GetModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetModelAsync(connection, modelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Model> CreateModelAsync(string label, string connectionId, string providerModelId, GenerationMode mode, bool supportsSystemInstructions, TextResultFormat textFormat, CancellationToken cancellationToken)
    {
        var normalizedLabel = LibraryRules.NormalizeShortLabel(label, "Model label");
        var normalizedProviderModelId = LibraryRules.NormalizeShortLabel(providerModelId, "Provider model ID");
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var owningConnection = await GetConnectionAsync(connection, connectionId, cancellationToken).ConfigureAwait(false);
        if (owningConnection.State != LibraryRecordState.Active) throw new LibraryValidationException("Models can only be added to an active connection.");
        try
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO models(id,connection_id,label,label_key,provider_model_id,mode,supports_system_instructions,state,created_at,modified_at,text_format) VALUES($id,$connection,$label,$key,$providerModel,$mode,$sysInstr,0,$now,$now,$textFormat);",
                cancellationToken, null,
                ("$id", id), ("$connection", connectionId), ("$label", normalizedLabel), ("$key", LibraryRules.ComparisonKey(normalizedLabel)),
                ("$providerModel", normalizedProviderModelId), ("$mode", (int)mode), ("$sysInstr", supportsSystemInstructions), ("$now", Format(now)),
                ("$textFormat", (int)textFormat)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active model labelled '{normalizedLabel}' already exists.");
        }

        return new Model(id, connectionId, normalizedLabel, normalizedProviderModelId, mode, supportsSystemInstructions, LibraryRecordState.Active, now, now, null, false, textFormat);
    }

    public async Task<Model> UpdateModelAsync(string modelId, string label, string providerModelId, GenerationMode mode, bool supportsSystemInstructions, TextResultFormat textFormat, CancellationToken cancellationToken)
    {
        var normalizedLabel = LibraryRules.NormalizeShortLabel(label, "Model label");
        var normalizedProviderModelId = LibraryRules.NormalizeShortLabel(providerModelId, "Provider model ID");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetModelAsync(connection, modelId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active model can be edited.");
        var needsReview = existing.NeedsReview || !string.Equals(existing.ProviderModelId, normalizedProviderModelId, StringComparison.Ordinal) || existing.Mode != mode;
        var modified = DateTimeOffset.UtcNow;
        try
        {
            await ExecuteNonQueryAsync(connection,
                "UPDATE models SET label=$label,label_key=$key,provider_model_id=$providerModel,mode=$mode,supports_system_instructions=$sysInstr,needs_review=$needsReview,text_format=$textFormat,modified_at=$modified WHERE id=$id AND state=0;",
                cancellationToken, transaction,
                ("$label", normalizedLabel), ("$key", LibraryRules.ComparisonKey(normalizedLabel)), ("$providerModel", normalizedProviderModelId),
                ("$mode", (int)mode), ("$sysInstr", supportsSystemInstructions), ("$needsReview", needsReview), ("$textFormat", (int)textFormat), ("$modified", Format(modified)), ("$id", modelId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active model labelled '{normalizedLabel}' already exists.");
        }

        if (needsReview && !existing.NeedsReview)
        {
            await ExecuteNonQueryAsync(connection, "UPDATE saved_generation_settings SET needs_review=1,modified_at=$modified WHERE model_id=$id AND state=0;",
                cancellationToken, transaction, ("$modified", Format(modified)), ("$id", modelId)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing with { Label = normalizedLabel, ProviderModelId = normalizedProviderModelId, Mode = mode, SupportsSystemInstructions = supportsSystemInstructions, NeedsReview = needsReview, TextFormat = textFormat, ModifiedAt = modified };
    }

    public async Task<Model> MarkModelReviewedAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetModelAsync(connection, modelId, cancellationToken, transaction).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, "UPDATE models SET needs_review=0,modified_at=$modified WHERE id=$id;",
            cancellationToken, transaction, ("$modified", Format(modified)), ("$id", modelId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE saved_generation_settings SET needs_review=0,modified_at=$modified WHERE model_id=$id AND state=0;",
            cancellationToken, transaction, ("$modified", Format(modified)), ("$id", modelId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing with { NeedsReview = false, ModifiedAt = modified };
    }

    public async Task RecycleModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetModelAsync(connection, modelId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active model can be recycled.");
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection, "UPDATE models SET state=1,recycled_at=$now,modified_at=$now WHERE id=$id;", cancellationToken, transaction, ("$now", now), ("$id", modelId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "UPDATE saved_generation_settings SET state=1,recycled_at=$now,modified_at=$now WHERE state=0 AND model_id=$id;", cancellationToken, transaction, ("$now", now), ("$id", modelId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetModelAsync(connection, modelId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled model can be restored.");
        var owningConnection = await GetConnectionAsync(connection, existing.ConnectionId, cancellationToken, transaction).ConfigureAwait(false);
        if (owningConnection.State != LibraryRecordState.Active) throw new LibraryValidationException("Restore the owning connection before restoring this model.");
        var now = Format(DateTimeOffset.UtcNow);
        try
        {
            await ExecuteNonQueryAsync(connection, "UPDATE models SET state=0,recycled_at=NULL,modified_at=$now WHERE id=$id;", cancellationToken, transaction, ("$now", now), ("$id", modelId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"An active model labelled '{existing.Label}' already exists.");
        }
        await ExecuteNonQueryAsync(connection, "UPDATE saved_generation_settings SET state=0,recycled_at=NULL,modified_at=$now WHERE state=1 AND model_id=$id;", cancellationToken, transaction, ("$now", now), ("$id", modelId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PermanentlyDeleteModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetModelAsync(connection, modelId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled model can be permanently deleted.");
        await ExecuteNonQueryAsync(connection, "DELETE FROM saved_generation_settings WHERE model_id=$id;", cancellationToken, transaction, ("$id", modelId)).ConfigureAwait(false);
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM models WHERE id=$id;", cancellationToken, transaction, ("$id", modelId)).ConfigureAwait(false);
        if (deleted == 0) throw new RecordNotFoundException("Model not found.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SavedSettingSelect = "SELECT id,title,model_id,model_label,mode,prompt,system_instructions,result_count,destination_folder_id,state,created_at,modified_at,recycled_at,source_file_id,needs_review,revision,settings_temperature,settings_top_p,settings_max_tokens,settings_frequency_penalty,settings_presence_penalty,secondary_source_file_id,tertiary_source_file_id FROM saved_generation_settings";

    public async Task<IReadOnlyList<SavedGenerationSetting>> GetActiveSavedSettingsAsync(CancellationToken cancellationToken) =>
        await ListSavedSettingsAsync(LibraryRecordState.Active, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SavedGenerationSetting>> GetRecycledSavedSettingsAsync(CancellationToken cancellationToken) =>
        await ListSavedSettingsAsync(LibraryRecordState.Recycled, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<SavedGenerationSetting>> ListSavedSettingsAsync(LibraryRecordState state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SavedSettingSelect + " WHERE state=$state ORDER BY title_key;";
        command.Parameters.AddWithValue("$state", (int)state);
        var results = new List<SavedGenerationSetting>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadSavedSetting(reader));
        return results;
    }

    public async Task<SavedGenerationSetting> GetSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetSavedSettingAsync(connection, savedSettingId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedGenerationSetting> CreateSavedSettingAsync(string title, string? modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions, string? sourceFileId, GenerationSettings? settings, string? secondarySourceFileId, string? tertiarySourceFileId, CancellationToken cancellationToken)
    {
        var normalizedTitle = LibraryRules.NormalizeShortLabel(title, "Settings title");
        LibraryRules.ValidateGenerationTextLength(prompt, "Prompt");
        if (systemInstructions is not null) LibraryRules.ValidateGenerationTextLength(systemInstructions, "System instructions");
        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        LibraryRules.ValidateSourceFileIds(sourceFileId, secondarySourceFileId, tertiarySourceFileId);
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (modelLabel, mode) = modelId is null
            ? (string.Empty, GenerationMode.Text)
            : await ResolveModelSnapshotAsync(connection, modelId, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(connection,
                "INSERT INTO saved_generation_settings(id,title,title_key,model_id,model_label,mode,prompt,system_instructions,result_count,destination_folder_id,state,created_at,modified_at,source_file_id,settings_temperature,settings_top_p,settings_max_tokens,settings_frequency_penalty,settings_presence_penalty,secondary_source_file_id,tertiary_source_file_id) VALUES($id,$title,$key,$model,$modelLabel,$mode,$prompt,$sysInstr,$count,$folder,0,$now,$now,$source,$settingsTemperature,$settingsTopP,$settingsMaxTokens,$settingsFrequencyPenalty,$settingsPresencePenalty,$secondarySource,$tertiarySource);",
                cancellationToken, null,
                [("$id", id), ("$title", normalizedTitle), ("$key", LibraryRules.ComparisonKey(normalizedTitle)), ("$model", modelId is null ? DBNull.Value : modelId),
                ("$modelLabel", modelLabel), ("$mode", (int)mode), ("$prompt", prompt), ("$sysInstr", systemInstructions is null ? DBNull.Value : systemInstructions),
                ("$count", resultCount), ("$folder", destinationFolderId), ("$now", Format(now)), ("$source", sourceFileId is null ? DBNull.Value : sourceFileId),
                ("$secondarySource", secondarySourceFileId is null ? DBNull.Value : secondarySourceFileId), ("$tertiarySource", tertiarySourceFileId is null ? DBNull.Value : tertiarySourceFileId),
                .. GenerationSettingsParameters(normalizedSettings)]).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"Saved settings titled '{normalizedTitle}' already exist.");
        }

        return new SavedGenerationSetting(id, normalizedTitle, modelId, modelLabel, mode, prompt, systemInstructions, resultCount, destinationFolderId, LibraryRecordState.Active, now, now, null, sourceFileId, Settings: normalizedSettings, SecondarySourceFileId: secondarySourceFileId, TertiarySourceFileId: tertiarySourceFileId);
    }

    public async Task<SavedGenerationSetting> UpdateSavedSettingAsync(string savedSettingId, int expectedRevision, string title, string? modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions, string? sourceFileId, GenerationSettings? settings, string? secondarySourceFileId, string? tertiarySourceFileId, CancellationToken cancellationToken)
    {
        var normalizedTitle = LibraryRules.NormalizeShortLabel(title, "Settings title");
        LibraryRules.ValidateGenerationTextLength(prompt, "Prompt");
        if (systemInstructions is not null) LibraryRules.ValidateGenerationTextLength(systemInstructions, "System instructions");
        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        LibraryRules.ValidateSourceFileIds(sourceFileId, secondarySourceFileId, tertiarySourceFileId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetSavedSettingAsync(connection, savedSettingId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only active saved settings can be edited.");
        if (existing.Revision != expectedRevision) throw new SavedSettingRevisionConflictException(existing);
        var (modelLabel, mode) = modelId is null
            ? (existing.ModelLabel, existing.Mode)
            : await ResolveModelSnapshotAsync(connection, modelId, cancellationToken).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        var newRevision = existing.Revision + 1;
        try
        {
            await ExecuteNonQueryAsync(connection,
                "UPDATE saved_generation_settings SET title=$title,title_key=$key,model_id=$model,model_label=$modelLabel,mode=$mode,prompt=$prompt,system_instructions=$sysInstr,result_count=$count,destination_folder_id=$folder,modified_at=$modified,source_file_id=$source,revision=$revision,settings_temperature=$settingsTemperature,settings_top_p=$settingsTopP,settings_max_tokens=$settingsMaxTokens,settings_frequency_penalty=$settingsFrequencyPenalty,settings_presence_penalty=$settingsPresencePenalty,secondary_source_file_id=$secondarySource,tertiary_source_file_id=$tertiarySource WHERE id=$id AND state=0;",
                cancellationToken, null,
                [("$title", normalizedTitle), ("$key", LibraryRules.ComparisonKey(normalizedTitle)), ("$model", modelId is null ? DBNull.Value : modelId),
                ("$modelLabel", modelLabel), ("$mode", (int)mode), ("$prompt", prompt), ("$sysInstr", systemInstructions is null ? DBNull.Value : systemInstructions),
                ("$count", resultCount), ("$folder", destinationFolderId), ("$modified", Format(modified)), ("$source", sourceFileId is null ? DBNull.Value : sourceFileId),
                ("$revision", newRevision), ("$id", savedSettingId),
                ("$secondarySource", secondarySourceFileId is null ? DBNull.Value : secondarySourceFileId), ("$tertiarySource", tertiarySourceFileId is null ? DBNull.Value : tertiarySourceFileId),
                .. GenerationSettingsParameters(normalizedSettings)]).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"Saved settings titled '{normalizedTitle}' already exist.");
        }

        return existing with { Title = normalizedTitle, ModelId = modelId, ModelLabel = modelLabel, Mode = mode, Prompt = prompt, SystemInstructions = systemInstructions, ResultCount = resultCount, DestinationFolderId = destinationFolderId, ModifiedAt = modified, SourceFileId = sourceFileId, Revision = newRevision, Settings = normalizedSettings, SecondarySourceFileId = secondarySourceFileId, TertiarySourceFileId = tertiarySourceFileId };
    }

    public async Task RecycleSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetSavedSettingAsync(connection, savedSettingId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only active saved settings can be recycled.");
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection, "UPDATE saved_generation_settings SET state=1,recycled_at=$now,modified_at=$now WHERE id=$id;", cancellationToken, null, ("$now", now), ("$id", savedSettingId)).ConfigureAwait(false);
    }

    public async Task RestoreSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetSavedSettingAsync(connection, savedSettingId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only recycled saved settings can be restored.");
        var now = Format(DateTimeOffset.UtcNow);
        try
        {
            await ExecuteNonQueryAsync(connection, "UPDATE saved_generation_settings SET state=0,recycled_at=NULL,modified_at=$now WHERE id=$id;", cancellationToken, null, ("$now", now), ("$id", savedSettingId)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new NameConflictException($"Saved settings titled '{existing.Title}' already exist.");
        }
    }

    public async Task PermanentlyDeleteSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetSavedSettingAsync(connection, savedSettingId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only recycled saved settings can be permanently deleted.");
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM saved_generation_settings WHERE id=$id;", cancellationToken, null, ("$id", savedSettingId)).ConfigureAwait(false);
        if (deleted == 0) throw new RecordNotFoundException("Saved generation settings not found.");
    }

    private static async Task<(string ModelLabel, GenerationMode Mode)> ResolveModelSnapshotAsync(SqliteConnection connection, string modelId, CancellationToken cancellationToken)
    {
        var model = await GetModelAsync(connection, modelId, cancellationToken).ConfigureAwait(false);
        return (model.Label, model.Mode);
    }

    private static async Task<SavedGenerationSetting> GetSavedSettingAsync(SqliteConnection connection, string savedSettingId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SavedSettingSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", savedSettingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Saved generation settings not found.");
        return ReadSavedSetting(reader);
    }

    private static SavedGenerationSetting ReadSavedSetting(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), (GenerationMode)reader.GetInt32(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7), reader.GetString(8), (LibraryRecordState)reader.GetInt32(9),
        Parse(reader.GetString(10)), Parse(reader.GetString(11)), reader.IsDBNull(12) ? null : Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.GetBoolean(14), reader.GetInt32(15), ReadGenerationSettings(reader, 16),
        reader.IsDBNull(21) ? null : reader.GetString(21), reader.IsDBNull(22) ? null : reader.GetString(22));

    private static GenerationSettings ReadGenerationSettings(SqliteDataReader reader, int startIndex) => new(
        reader.IsDBNull(startIndex) ? null : reader.GetDouble(startIndex),
        reader.IsDBNull(startIndex + 1) ? null : reader.GetDouble(startIndex + 1),
        reader.IsDBNull(startIndex + 2) ? null : reader.GetInt32(startIndex + 2),
        reader.IsDBNull(startIndex + 3) ? null : reader.GetDouble(startIndex + 3),
        reader.IsDBNull(startIndex + 4) ? null : reader.GetDouble(startIndex + 4));

    private static (string Name, object Value)[] GenerationSettingsParameters(GenerationSettings settings) =>
    [
        ("$settingsTemperature", settings.Temperature is null ? DBNull.Value : settings.Temperature.Value),
        ("$settingsTopP", settings.TopP is null ? DBNull.Value : settings.TopP.Value),
        ("$settingsMaxTokens", settings.MaxTokens is null ? DBNull.Value : settings.MaxTokens.Value),
        ("$settingsFrequencyPenalty", settings.FrequencyPenalty is null ? DBNull.Value : settings.FrequencyPenalty.Value),
        ("$settingsPresencePenalty", settings.PresencePenalty is null ? DBNull.Value : settings.PresencePenalty.Value)
    ];

    private const string GenerationDraftSelect = "SELECT id,custom_title,tab_order,model_id,prompt,system_instructions,source_file_id,result_count,destination_folder_id,improvement_model_id,improvement_guidance,created_at,modified_at,settings_temperature,settings_top_p,settings_max_tokens,settings_frequency_penalty,settings_presence_penalty,secondary_source_file_id,tertiary_source_file_id FROM generation_drafts";

    public async Task<IReadOnlyList<GenerationDraft>> GetDraftsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = GenerationDraftSelect + " ORDER BY tab_order;";
        var results = new List<GenerationDraft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadDraft(reader));
        return results;
    }

    public async Task<GenerationDraft> GetDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetDraftAsync(connection, draftId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationDraft> CreateDraftAsync(string destinationFolderId, CancellationToken cancellationToken)
    {
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var tabOrder = await NextDraftTabOrderAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO generation_drafts(id,custom_title,tab_order,model_id,prompt,system_instructions,source_file_id,result_count,destination_folder_id,improvement_model_id,improvement_guidance,created_at,modified_at) VALUES($id,NULL,$order,NULL,'',NULL,NULL,1,$folder,NULL,NULL,$now,$now);",
            cancellationToken, null,
            ("$id", id), ("$order", tabOrder), ("$folder", destinationFolderId), ("$now", Format(now))).ConfigureAwait(false);
        return new GenerationDraft(id, null, tabOrder, null, string.Empty, null, null, 1, destinationFolderId, null, null, now, now);
    }

    public async Task<GenerationDraft> ReplaceDraftStateAsync(string draftId, string? customTitle, string? modelId, string prompt, string? systemInstructions, string? sourceFileId, int resultCount, string destinationFolderId, string? improvementModelId, string? improvementGuidance, GenerationSettings? settings, string? secondarySourceFileId, string? tertiarySourceFileId, CancellationToken cancellationToken)
    {
        var normalizedTitle = LibraryRules.NormalizeDraftCustomTitle(customTitle);
        LibraryRules.ValidateGenerationTextLength(prompt, "Prompt");
        if (systemInstructions is not null) LibraryRules.ValidateGenerationTextLength(systemInstructions, "System instructions");
        if (improvementGuidance is not null) LibraryRules.ValidateGenerationTextLength(improvementGuidance, "Improvement guidance");
        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        LibraryRules.ValidateSourceFileIds(sourceFileId, secondarySourceFileId, tertiarySourceFileId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetDraftAsync(connection, draftId, cancellationToken).ConfigureAwait(false);
        var modified = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection,
            "UPDATE generation_drafts SET custom_title=$title,model_id=$model,prompt=$prompt,system_instructions=$sysInstr,source_file_id=$source,result_count=$count,destination_folder_id=$folder,improvement_model_id=$improvementModel,improvement_guidance=$improvementGuidance,modified_at=$modified,settings_temperature=$settingsTemperature,settings_top_p=$settingsTopP,settings_max_tokens=$settingsMaxTokens,settings_frequency_penalty=$settingsFrequencyPenalty,settings_presence_penalty=$settingsPresencePenalty,secondary_source_file_id=$secondarySource,tertiary_source_file_id=$tertiarySource WHERE id=$id;",
            cancellationToken, null,
            [("$title", normalizedTitle is null ? DBNull.Value : normalizedTitle), ("$model", modelId is null ? DBNull.Value : modelId), ("$prompt", prompt),
            ("$sysInstr", systemInstructions is null ? DBNull.Value : systemInstructions), ("$source", sourceFileId is null ? DBNull.Value : sourceFileId),
            ("$count", resultCount), ("$folder", destinationFolderId), ("$improvementModel", improvementModelId is null ? DBNull.Value : improvementModelId),
            ("$improvementGuidance", improvementGuidance is null ? DBNull.Value : improvementGuidance), ("$modified", Format(modified)), ("$id", draftId),
            ("$secondarySource", secondarySourceFileId is null ? DBNull.Value : secondarySourceFileId), ("$tertiarySource", tertiarySourceFileId is null ? DBNull.Value : tertiarySourceFileId),
            .. GenerationSettingsParameters(normalizedSettings)]).ConfigureAwait(false);
        return existing with
        {
            CustomTitle = normalizedTitle, ModelId = modelId, Prompt = prompt, SystemInstructions = systemInstructions, SourceFileId = sourceFileId,
            ResultCount = resultCount, DestinationFolderId = destinationFolderId, ImprovementModelId = improvementModelId, ImprovementGuidance = improvementGuidance,
            ModifiedAt = modified, Settings = normalizedSettings, SecondarySourceFileId = secondarySourceFileId, TertiarySourceFileId = tertiarySourceFileId
        };
    }

    public async Task<GenerationDraft> DuplicateDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetDraftAsync(connection, draftId, cancellationToken).ConfigureAwait(false);
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        var newOrder = existing.TabOrder + 1;
        await ExecuteNonQueryAsync(connection, "UPDATE generation_drafts SET tab_order=tab_order+1 WHERE tab_order>=$order;", cancellationToken, null, ("$order", newOrder)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO generation_drafts(id,custom_title,tab_order,model_id,prompt,system_instructions,source_file_id,result_count,destination_folder_id,improvement_model_id,improvement_guidance,created_at,modified_at,settings_temperature,settings_top_p,settings_max_tokens,settings_frequency_penalty,settings_presence_penalty,secondary_source_file_id,tertiary_source_file_id) VALUES($id,NULL,$order,$model,$prompt,$sysInstr,$source,$count,$folder,$improvementModel,$improvementGuidance,$now,$now,$settingsTemperature,$settingsTopP,$settingsMaxTokens,$settingsFrequencyPenalty,$settingsPresencePenalty,$secondarySource,$tertiarySource);",
            cancellationToken, null,
            [("$id", id), ("$order", newOrder), ("$model", existing.ModelId is null ? DBNull.Value : existing.ModelId), ("$prompt", existing.Prompt),
            ("$sysInstr", existing.SystemInstructions is null ? DBNull.Value : existing.SystemInstructions), ("$source", existing.SourceFileId is null ? DBNull.Value : existing.SourceFileId),
            ("$count", existing.ResultCount), ("$folder", existing.DestinationFolderId), ("$improvementModel", existing.ImprovementModelId is null ? DBNull.Value : existing.ImprovementModelId),
            ("$improvementGuidance", existing.ImprovementGuidance is null ? DBNull.Value : existing.ImprovementGuidance), ("$now", Format(now)),
            ("$secondarySource", existing.SecondarySourceFileId is null ? DBNull.Value : existing.SecondarySourceFileId), ("$tertiarySource", existing.TertiarySourceFileId is null ? DBNull.Value : existing.TertiarySourceFileId),
            .. GenerationSettingsParameters(existing.Settings)]).ConfigureAwait(false);
        return new GenerationDraft(id, null, newOrder, existing.ModelId, existing.Prompt, existing.SystemInstructions, existing.SourceFileId, existing.ResultCount, existing.DestinationFolderId, existing.ImprovementModelId, existing.ImprovementGuidance, now, now, existing.Settings, existing.SecondarySourceFileId, existing.TertiarySourceFileId);
    }

    public async Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM generation_drafts WHERE id=$id;", cancellationToken, null, ("$id", draftId)).ConfigureAwait(false);
        if (deleted == 0) throw new RecordNotFoundException("Generation draft not found.");
    }

    public async Task<IReadOnlyList<GenerationDraft>> ReorderDraftsAsync(IReadOnlyList<string> orderedDraftIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedDraftIds);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existingIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM generation_drafts;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) existingIds.Add(reader.GetString(0));
        }
        if (orderedDraftIds.Count != existingIds.Count || !new HashSet<string>(orderedDraftIds, StringComparer.Ordinal).SetEquals(existingIds))
        {
            throw new LibraryValidationException("The draft order must contain exactly the current set of drafts.");
        }

        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var index = 0; index < orderedDraftIds.Count; index++)
            {
                await ExecuteNonQueryAsync(connection, "UPDATE generation_drafts SET tab_order=$order WHERE id=$id;", cancellationToken, transaction, ("$order", index), ("$id", orderedDraftIds[index])).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var results = new List<GenerationDraft>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = GenerationDraftSelect + " ORDER BY tab_order;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadDraft(reader));
        }
        return results;
    }

    private static async Task<int> NextDraftTabOrderAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(tab_order), -1) + 1 FROM generation_drafts;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<GenerationDraft> GetDraftAsync(SqliteConnection connection, string draftId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = GenerationDraftSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Generation draft not found.");
        return ReadDraft(reader);
    }

    private static GenerationDraft ReadDraft(SqliteDataReader reader) => new(
        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7),
        reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        Parse(reader.GetString(11)), Parse(reader.GetString(12)), ReadGenerationSettings(reader, 13),
        reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19));

    private const string AsyncRemoteJobSelect = "SELECT id,draft_id,provider_type,connection_id,provider_job_id,phase,idempotency_key,submitted_at,last_polled_at,monitoring_deadline,generation_record_id,position FROM async_remote_jobs";

    public async Task<AsyncRemoteJobRecord> CreateAsyncRemoteJobAsync(string draftId, ProviderType providerType, string connectionId, string providerJobId, string? idempotencyKey, DateTimeOffset? monitoringDeadline, CancellationToken cancellationToken)
    {
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO async_remote_jobs(id,draft_id,provider_type,connection_id,provider_job_id,phase,idempotency_key,submitted_at,last_polled_at,monitoring_deadline) VALUES($id,$draft,$provider,$conn,$job,$phase,$idem,$now,NULL,$deadline);",
            cancellationToken, null,
            ("$id", id), ("$draft", draftId), ("$provider", (int)providerType), ("$conn", connectionId), ("$job", providerJobId),
            ("$phase", (int)AsyncRemoteJobPhase.Submitted), ("$idem", idempotencyKey is null ? DBNull.Value : idempotencyKey),
            ("$now", Format(now)), ("$deadline", monitoringDeadline is null ? DBNull.Value : Format(monitoringDeadline.Value))).ConfigureAwait(false);
        return new AsyncRemoteJobRecord(id, draftId, providerType, connectionId, providerJobId, AsyncRemoteJobPhase.Submitted, idempotencyKey, now, null, monitoringDeadline);
    }

    public async Task<IReadOnlyList<AsyncRemoteJobRecord>> GetPendingAsyncRemoteJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = AsyncRemoteJobSelect + " WHERE phase IN ($submitted,$processing,$paused,$awaitingDownload) ORDER BY submitted_at;";
        command.Parameters.AddWithValue("$submitted", (int)AsyncRemoteJobPhase.Submitted);
        command.Parameters.AddWithValue("$processing", (int)AsyncRemoteJobPhase.Processing);
        command.Parameters.AddWithValue("$paused", (int)AsyncRemoteJobPhase.MonitoringPaused);
        command.Parameters.AddWithValue("$awaitingDownload", (int)AsyncRemoteJobPhase.CompletedAwaitingDownload);
        var results = new List<AsyncRemoteJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadAsyncRemoteJob(reader));
        return results;
    }

    public async Task<IReadOnlyList<AsyncRemoteJobRecord>> GetAsyncRemoteJobsForConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = AsyncRemoteJobSelect + " WHERE connection_id=$conn ORDER BY submitted_at;";
        command.Parameters.AddWithValue("$conn", connectionId);
        var results = new List<AsyncRemoteJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadAsyncRemoteJob(reader));
        return results;
    }

    public async Task<AsyncRemoteJobRecord> UpdateAsyncRemoteJobPhaseAsync(string asyncJobId, AsyncRemoteJobPhase phase, DateTimeOffset? lastPolledAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteNonQueryWithCountAsync(connection,
            "UPDATE async_remote_jobs SET phase=$phase,last_polled_at=$polled WHERE id=$id;",
            cancellationToken, null,
            ("$phase", (int)phase), ("$polled", lastPolledAt is null ? DBNull.Value : Format(lastPolledAt.Value)), ("$id", asyncJobId)).ConfigureAwait(false);
        if (updated == 0) throw new RecordNotFoundException("Asynchronous remote job not found.");
        await using var command = connection.CreateCommand();
        command.CommandText = AsyncRemoteJobSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", asyncJobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Asynchronous remote job not found.");
        return ReadAsyncRemoteJob(reader);
    }

    public async Task DeleteAsyncRemoteJobAsync(string asyncJobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM async_remote_jobs WHERE id=$id;", cancellationToken, null, ("$id", asyncJobId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AsyncRemoteJobRecord>> GetAsyncRemoteJobsForGenerationRecordAsync(string generationRecordId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = AsyncRemoteJobSelect + " WHERE generation_record_id=$gen ORDER BY position;";
        command.Parameters.AddWithValue("$gen", generationRecordId);
        var results = new List<AsyncRemoteJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadAsyncRemoteJob(reader));
        return results;
    }

    public async Task<AsyncRemoteJobRecord> LinkAsyncRemoteJobToGenerationResultAsync(string asyncJobId, string generationRecordId, int position, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteNonQueryWithCountAsync(connection,
            "UPDATE async_remote_jobs SET generation_record_id=$gen,position=$pos WHERE id=$id;",
            cancellationToken, null,
            ("$gen", generationRecordId), ("$pos", position), ("$id", asyncJobId)).ConfigureAwait(false);
        if (updated == 0) throw new RecordNotFoundException("Asynchronous remote job not found.");
        await using var command = connection.CreateCommand();
        command.CommandText = AsyncRemoteJobSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", asyncJobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Asynchronous remote job not found.");
        return ReadAsyncRemoteJob(reader);
    }

    private static AsyncRemoteJobRecord ReadAsyncRemoteJob(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), (ProviderType)reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
        (AsyncRemoteJobPhase)reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11));

    private const string PendingUnverifiedResultSelect = "SELECT id,generation_id,position,staged_file_name,byte_size,content_hash,detected_media_type,created_at FROM pending_unverified_results";

    public async Task<PendingUnverifiedResult> CreatePendingUnverifiedResultAsync(string generationRecordId, int position, string stagedFileName, long byteSize, string contentHash, string detectedMediaType, CancellationToken cancellationToken)
    {
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO pending_unverified_results(id,generation_id,position,staged_file_name,byte_size,content_hash,detected_media_type,created_at) VALUES($id,$gen,$pos,$name,$size,$hash,$media,$now);",
            cancellationToken, null,
            ("$id", id), ("$gen", generationRecordId), ("$pos", position), ("$name", stagedFileName),
            ("$size", byteSize), ("$hash", contentHash), ("$media", detectedMediaType), ("$now", Format(now))).ConfigureAwait(false);
        return new PendingUnverifiedResult(id, generationRecordId, position, stagedFileName, byteSize, contentHash, detectedMediaType, now);
    }

    public async Task<IReadOnlyList<PendingUnverifiedResult>> GetPendingUnverifiedResultsAsync(string generationRecordId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PendingUnverifiedResultSelect + " WHERE generation_id=$gen ORDER BY position;";
        command.Parameters.AddWithValue("$gen", generationRecordId);
        var results = new List<PendingUnverifiedResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadPendingUnverifiedResult(reader));
        return results;
    }

    public async Task<PendingUnverifiedResult> GetPendingUnverifiedResultAsync(string generationRecordId, int position, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PendingUnverifiedResultSelect + " WHERE generation_id=$gen AND position=$pos;";
        command.Parameters.AddWithValue("$gen", generationRecordId);
        command.Parameters.AddWithValue("$pos", position);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Pending unverified result not found.");
        return ReadPendingUnverifiedResult(reader);
    }

    public async Task DeletePendingUnverifiedResultAsync(string pendingResultId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM pending_unverified_results WHERE id=$id;", cancellationToken, null, ("$id", pendingResultId)).ConfigureAwait(false);
    }

    public async Task UpdateGenerationResultEntryAsync(string generationRecordId, int position, GenerationResultStatus status, string? fileId, string? errorMessage, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteNonQueryWithCountAsync(connection,
            "UPDATE generation_results SET status=$status,file_id=$file,result_error_message=$msg WHERE generation_id=$gen AND position=$pos;",
            cancellationToken, null,
            ("$status", (int)status), ("$file", fileId is null ? DBNull.Value : fileId), ("$msg", errorMessage is null ? DBNull.Value : errorMessage),
            ("$gen", generationRecordId), ("$pos", position)).ConfigureAwait(false);
        if (updated == 0) throw new RecordNotFoundException("Generation result entry not found.");
    }

    private static PendingUnverifiedResult ReadPendingUnverifiedResult(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetInt64(4), reader.GetString(5), reader.GetString(6), Parse(reader.GetString(7)));

    private const string GenerationRecordSelect = "SELECT id,model_id,model_label,provider_model_id,provider_type,mode,prompt,system_instructions,result_count,status,error_message,destination_folder_id,created_at,completed_at,prompt_tokens,completion_tokens,source_file_id,prompt_improvement_record_id,text_format,state,recycled_at,tombstone_source_display_name,tombstone_source_media_type,tombstone_source_content_hash,settings_temperature,settings_top_p,settings_max_tokens,settings_frequency_penalty,settings_presence_penalty,secondary_source_file_id,secondary_tombstone_display_name,secondary_tombstone_media_type,secondary_tombstone_content_hash,tertiary_source_file_id,tertiary_tombstone_display_name,tertiary_tombstone_media_type,tertiary_tombstone_content_hash,safety_blocked_count,actual_cost,actual_cost_currency FROM generation_records";

    public async Task<IReadOnlyList<GenerationRecord>> GetGenerationHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<GenerationRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = GenerationRecordSelect + " WHERE state=0 ORDER BY created_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(ReadGenerationRecord(reader));
        }
        var results = new List<GenerationRecord>(records.Count);
        foreach (var record in records)
        {
            var (fileIds, tombstones, entries) = await GetGenerationResultsAsync(connection, record.Id, cancellationToken).ConfigureAwait(false);
            results.Add(record with { ResultFileIds = fileIds, TombstonedResults = tombstones, Results = entries });
        }
        return results;
    }

    public async Task<GenerationRecord> GetGenerationRecordAsync(string generationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        GenerationRecord record;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = GenerationRecordSelect + " WHERE id=$id;";
            command.Parameters.AddWithValue("$id", generationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Generation record not found.");
            record = ReadGenerationRecord(reader);
        }
        var (fileIds, tombstones, entries) = await GetGenerationResultsAsync(connection, generationId, cancellationToken).ConfigureAwait(false);
        return record with { ResultFileIds = fileIds, TombstonedResults = tombstones, Results = entries };
    }

    public async Task<IReadOnlyList<GenerationRecord>> GetRecycledGenerationHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<GenerationRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = GenerationRecordSelect + " WHERE state=1 ORDER BY recycled_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(ReadGenerationRecord(reader));
        }
        var results = new List<GenerationRecord>(records.Count);
        foreach (var record in records)
        {
            var (fileIds, tombstones, entries) = await GetGenerationResultsAsync(connection, record.Id, cancellationToken).ConfigureAwait(false);
            results.Add(record with { ResultFileIds = fileIds, TombstonedResults = tombstones, Results = entries });
        }
        return results;
    }

    private static async Task<GenerationRecord> GetGenerationRecordAsync(SqliteConnection connection, string generationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = GenerationRecordSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", generationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Generation record not found.");
        return ReadGenerationRecord(reader);
    }

    public async Task RecycleGenerationRecordAsync(string generationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetGenerationRecordAsync(connection, generationId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active generation record can be recycled.");
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteNonQueryAsync(connection, "UPDATE generation_records SET state=1,recycled_at=$now WHERE id=$id;", cancellationToken, null, ("$now", now), ("$id", generationId)).ConfigureAwait(false);
    }

    public async Task RestoreGenerationRecordAsync(string generationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetGenerationRecordAsync(connection, generationId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled generation record can be restored.");
        await ExecuteNonQueryAsync(connection, "UPDATE generation_records SET state=0,recycled_at=NULL WHERE id=$id;", cancellationToken, null, ("$id", generationId)).ConfigureAwait(false);
    }

    public async Task PermanentlyDeleteGenerationRecordAsync(string generationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetGenerationRecordAsync(connection, generationId, cancellationToken).ConfigureAwait(false);
        if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled generation record can be permanently deleted.");
        var deleted = await ExecuteNonQueryWithCountAsync(connection, "DELETE FROM generation_records WHERE id=$id;", cancellationToken, null, ("$id", generationId)).ConfigureAwait(false);
        if (deleted == 0) throw new RecordNotFoundException("Generation record not found.");
    }

    public async Task<GenerationRecord> CreateGenerationRecordAsync(Model model, ProviderType providerType, string prompt, string? systemInstructions, int resultCount, GenerationStatus status, string? errorMessage, string destinationFolderId, IReadOnlyList<string> resultFileIds, int? promptTokens, int? completionTokens, string? sourceFileId, string? promptImprovementRecordId, TextResultFormat? textFormat, GenerationSettings? settings, string? secondarySourceFileId, string? tertiarySourceFileId, int safetyBlockedCount, CancellationToken cancellationToken, double? actualCost = null, string? actualCostCurrency = null, IReadOnlyList<GenerationResultEntry>? results = null)
    {
        // Callers that don't track per-position outcomes (Text generation) get a simple Committed
        // entry per successfully committed file and nothing for a shortfall — that shortfall is
        // already surfaced through SafetyBlockedCount and the committed-vs-requested comparison, so
        // synthesizing a duplicate generic "missing" entry here would be redundant.
        var resolvedResults = results ?? resultFileIds.Select((fileId, index) => new GenerationResultEntry(index, GenerationResultStatus.Committed, fileId, null)).ToArray();

        LibraryRules.ValidateGenerationTextLength(prompt, "Prompt");
        if (systemInstructions is not null) LibraryRules.ValidateGenerationTextLength(systemInstructions, "System instructions");
        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        LibraryRules.ValidateSourceFileIds(sourceFileId, secondarySourceFileId, tertiarySourceFileId);
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO generation_records(id,model_id,model_label,provider_model_id,provider_type,mode,prompt,system_instructions,result_count,status,error_message,destination_folder_id,created_at,completed_at,prompt_tokens,completion_tokens,source_file_id,prompt_improvement_record_id,text_format,settings_temperature,settings_top_p,settings_max_tokens,settings_frequency_penalty,settings_presence_penalty,secondary_source_file_id,tertiary_source_file_id,safety_blocked_count,actual_cost,actual_cost_currency) VALUES($id,$model,$label,$providerModel,$provider,$mode,$prompt,$sysInstr,$count,$status,$error,$folder,$created,$completed,$promptTokens,$completionTokens,$source,$improvement,$textFormat,$settingsTemperature,$settingsTopP,$settingsMaxTokens,$settingsFrequencyPenalty,$settingsPresencePenalty,$secondarySource,$tertiarySource,$safetyBlocked,$actualCost,$actualCostCurrency);",
            cancellationToken, transaction,
            [("$id", id), ("$model", model.Id), ("$label", model.Label), ("$providerModel", model.ProviderModelId), ("$provider", (int)providerType),
            ("$mode", (int)model.Mode), ("$prompt", prompt), ("$sysInstr", systemInstructions is null ? DBNull.Value : systemInstructions), ("$count", resultCount), ("$status", (int)status),
            ("$error", errorMessage is null ? DBNull.Value : errorMessage), ("$folder", destinationFolderId), ("$created", Format(now)), ("$completed", Format(now)),
            ("$promptTokens", promptTokens is null ? DBNull.Value : promptTokens.Value), ("$completionTokens", completionTokens is null ? DBNull.Value : completionTokens.Value),
            ("$source", sourceFileId is null ? DBNull.Value : sourceFileId),
            ("$improvement", promptImprovementRecordId is null ? DBNull.Value : promptImprovementRecordId),
            ("$textFormat", textFormat is null ? DBNull.Value : (int)textFormat.Value),
            ("$secondarySource", secondarySourceFileId is null ? DBNull.Value : secondarySourceFileId), ("$tertiarySource", tertiarySourceFileId is null ? DBNull.Value : tertiarySourceFileId),
            ("$safetyBlocked", safetyBlockedCount),
            ("$actualCost", actualCost is null ? DBNull.Value : actualCost.Value), ("$actualCostCurrency", actualCostCurrency is null ? DBNull.Value : actualCostCurrency),
            .. GenerationSettingsParameters(normalizedSettings)]).ConfigureAwait(false);

        foreach (var entry in resolvedResults)
        {
            await ExecuteNonQueryAsync(connection, "INSERT INTO generation_results(id,generation_id,file_id,position,status,result_error_message) VALUES($resultId,$generation,$file,$position,$status,$resultError);",
                cancellationToken, transaction,
                ("$resultId", LibraryRules.NewId()), ("$generation", id), ("$file", entry.FileId is null ? DBNull.Value : entry.FileId),
                ("$position", entry.Position), ("$status", (int)entry.Status), ("$resultError", entry.ErrorMessage is null ? DBNull.Value : entry.ErrorMessage)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GenerationRecord(id, model.Id, model.Label, model.ProviderModelId, providerType, model.Mode, prompt, systemInstructions, resultCount, status, errorMessage, destinationFolderId, now, now, resultFileIds, promptTokens, completionTokens, sourceFileId, promptImprovementRecordId, textFormat, Settings: normalizedSettings, SecondarySourceFileId: secondarySourceFileId, TertiarySourceFileId: tertiarySourceFileId, SafetyBlockedCount: safetyBlockedCount, ActualCost: actualCost, ActualCostCurrency: actualCostCurrency, Results: resolvedResults);
    }

    private const string PromptImprovementRecordSelect = "SELECT id,model_id,model_label,provider_model_id,provider_type,raw_prompt,guidance,template_version,status,error_message,candidates_json,prompt_tokens,completion_tokens,created_at,completed_at FROM prompt_improvement_records";

    public async Task<IReadOnlyList<PromptImprovementRecord>> GetPromptImprovementHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PromptImprovementRecordSelect + " ORDER BY created_at DESC;";
        var results = new List<PromptImprovementRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadPromptImprovementRecord(reader));
        return results;
    }

    public async Task<PromptImprovementRecord> CreatePromptImprovementRecordAsync(Model model, ProviderType providerType, string rawPrompt, string? guidance, string templateVersion, GenerationStatus status, string? errorMessage, IReadOnlyList<string> candidates, int? promptTokens, int? completionTokens, CancellationToken cancellationToken)
    {
        LibraryRules.ValidateGenerationTextLength(rawPrompt, "Prompt");
        if (guidance is not null) LibraryRules.ValidateGenerationTextLength(guidance, "Improvement guidance");
        foreach (var candidate in candidates) LibraryRules.ValidateGenerationTextLength(candidate, "Improved prompt");
        var id = LibraryRules.NewId();
        var now = DateTimeOffset.UtcNow;
        var candidatesJson = JsonSerializer.Serialize(candidates);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO prompt_improvement_records(id,model_id,model_label,provider_model_id,provider_type,raw_prompt,guidance,template_version,status,error_message,candidates_json,prompt_tokens,completion_tokens,created_at,completed_at) VALUES($id,$model,$label,$providerModel,$provider,$prompt,$guidance,$template,$status,$error,$candidates,$promptTokens,$completionTokens,$created,$completed);",
            cancellationToken, null,
            ("$id", id), ("$model", model.Id), ("$label", model.Label), ("$providerModel", model.ProviderModelId), ("$provider", (int)providerType),
            ("$prompt", rawPrompt), ("$guidance", guidance is null ? DBNull.Value : guidance), ("$template", templateVersion), ("$status", (int)status),
            ("$error", errorMessage is null ? DBNull.Value : errorMessage), ("$candidates", candidatesJson),
            ("$promptTokens", promptTokens is null ? DBNull.Value : promptTokens.Value), ("$completionTokens", completionTokens is null ? DBNull.Value : completionTokens.Value),
            ("$created", Format(now)), ("$completed", Format(now))).ConfigureAwait(false);

        return new PromptImprovementRecord(id, model.Id, model.Label, model.ProviderModelId, providerType, rawPrompt, guidance, templateVersion, status, errorMessage, candidates, promptTokens, completionTokens, now, now);
    }

    private static PromptImprovementRecord ReadPromptImprovementRecord(SqliteDataReader reader) => new(
        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3), (ProviderType)reader.GetInt32(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), (GenerationStatus)reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9), JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? [],
        reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetInt32(12),
        Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : Parse(reader.GetString(14)));

    private static async Task<(IReadOnlyList<string> ResultFileIds, IReadOnlyList<FileIdentitySnapshot> TombstonedResults, IReadOnlyList<GenerationResultEntry> Results)> GetGenerationResultsAsync(SqliteConnection connection, string generationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_id,tombstone_display_name,tombstone_media_type,tombstone_content_hash,position,status,result_error_message FROM generation_results WHERE generation_id=$id ORDER BY position;";
        command.Parameters.AddWithValue("$id", generationId);
        var ids = new List<string>();
        var tombstones = new List<FileIdentitySnapshot>();
        var entries = new List<GenerationResultEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fileId = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (fileId is not null) ids.Add(fileId);
            else if (!reader.IsDBNull(1)) tombstones.Add(new FileIdentitySnapshot(reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            entries.Add(new GenerationResultEntry(reader.GetInt32(4), (GenerationResultStatus)reader.GetInt32(5), fileId, reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return (ids, tombstones, entries);
    }

    private static GenerationRecord ReadGenerationRecord(SqliteDataReader reader)
    {
        var sourceFileId = reader.IsDBNull(16) ? null : reader.GetString(16);
        var sourceTombstoneName = reader.IsDBNull(21) ? null : reader.GetString(21);
        var sourceTombstone = sourceFileId is null && sourceTombstoneName is not null
            ? new FileIdentitySnapshot(sourceTombstoneName, reader.GetString(22), reader.GetString(23))
            : null;
        var secondarySourceFileId = reader.IsDBNull(29) ? null : reader.GetString(29);
        var secondaryTombstoneName = reader.IsDBNull(30) ? null : reader.GetString(30);
        var secondarySourceTombstone = secondarySourceFileId is null && secondaryTombstoneName is not null
            ? new FileIdentitySnapshot(secondaryTombstoneName, reader.GetString(31), reader.GetString(32))
            : null;
        var tertiarySourceFileId = reader.IsDBNull(33) ? null : reader.GetString(33);
        var tertiaryTombstoneName = reader.IsDBNull(34) ? null : reader.GetString(34);
        var tertiarySourceTombstone = tertiarySourceFileId is null && tertiaryTombstoneName is not null
            ? new FileIdentitySnapshot(tertiaryTombstoneName, reader.GetString(35), reader.GetString(36))
            : null;
        return new(
            reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3), (ProviderType)reader.GetInt32(4),
            (GenerationMode)reader.GetInt32(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8), (GenerationStatus)reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11), Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : Parse(reader.GetString(13)),
            Array.Empty<string>(), reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.IsDBNull(15) ? null : reader.GetInt32(15), sourceFileId,
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : (TextResultFormat)reader.GetInt32(18),
            (LibraryRecordState)reader.GetInt32(19), reader.IsDBNull(20) ? null : Parse(reader.GetString(20)), sourceTombstone,
            Settings: ReadGenerationSettings(reader, 24),
            SecondarySourceFileId: secondarySourceFileId, SecondarySourceFileTombstone: secondarySourceTombstone,
            TertiarySourceFileId: tertiarySourceFileId, TertiarySourceFileTombstone: tertiarySourceTombstone,
            SafetyBlockedCount: reader.GetInt32(37),
            ActualCost: reader.IsDBNull(38) ? null : reader.GetDouble(38),
            ActualCostCurrency: reader.IsDBNull(39) ? null : reader.GetString(39));
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

    private const string ConnectionSelect = "SELECT id,label,provider_type,base_url,credential_header_name,auth_prefix,has_credential,last_test_status,last_tested_at,last_test_message,state,created_at,modified_at,recycled_at,timeout_seconds,generic_models_enabled,generic_models_path,generic_text_enabled,generic_text_path,generic_image_enabled,generic_image_path,credential_revision_id,credential_requires_repair FROM connections";
    private const string ModelSelect = "SELECT id,connection_id,label,provider_model_id,mode,supports_system_instructions,state,created_at,modified_at,recycled_at,needs_review,text_format FROM models";

    private static async Task<Connection> GetConnectionAsync(SqliteConnection connection, string connectionId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        Connection result;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = ConnectionSelect + " WHERE id=$id;";
            command.Parameters.AddWithValue("$id", connectionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Connection not found.");
            result = ReadConnection(reader);
        }

        var headers = await LoadConnectionHeadersAsync(connection, connectionId, transaction, cancellationToken).ConfigureAwait(false);
        return result with { AdditionalHeaders = headers };
    }

    private static async Task<IReadOnlyList<ConnectionHeader>> LoadConnectionHeadersAsync(SqliteConnection connection, string connectionId, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name,value FROM connection_headers WHERE connection_id=$id ORDER BY name;";
        command.Parameters.AddWithValue("$id", connectionId);
        var results = new List<ConnectionHeader>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ConnectionHeader(reader.GetString(0), reader.GetString(1)));
        }
        return results;
    }

    private static async Task ReplaceConnectionHeadersAsync(SqliteConnection connection, SqliteTransaction transaction, string connectionId, IReadOnlyList<ConnectionHeader> headers, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "DELETE FROM connection_headers WHERE connection_id=$id;", cancellationToken, transaction, ("$id", connectionId)).ConfigureAwait(false);
        foreach (var header in headers)
        {
            await ExecuteNonQueryAsync(connection, "INSERT INTO connection_headers(connection_id,name,value) VALUES($id,$name,$value);",
                cancellationToken, transaction, ("$id", connectionId), ("$name", header.Name), ("$value", header.Value)).ConfigureAwait(false);
        }
    }

    private static async Task<Model> GetModelAsync(SqliteConnection connection, string modelId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ModelSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", modelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Model not found.");
        return ReadModel(reader);
    }

    private static Connection ReadConnection(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), (ProviderType)reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetBoolean(6), (ConnectionTestStatus)reader.GetInt32(7), reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9),
        (LibraryRecordState)reader.GetInt32(10), Parse(reader.GetString(11)), Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : Parse(reader.GetString(13)),
        reader.IsDBNull(14) ? null : reader.GetInt32(14), null,
        new GenericConnectionModalitySettings(reader.GetBoolean(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetBoolean(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.GetBoolean(19), reader.IsDBNull(20) ? null : reader.GetString(20)),
        reader.IsDBNull(21) ? null : reader.GetString(21), reader.GetBoolean(22));

    private static async Task<ModelCatalogue> ReadModelCatalogueAsync(SqliteConnection connection, string connectionId, CancellationToken cancellationToken)
    {
        DateTimeOffset? retrievedAt;
        bool possiblyStale;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT catalogue_retrieved_at,catalogue_possibly_stale FROM connections WHERE id=$id;";
            command.Parameters.AddWithValue("$id", connectionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new RecordNotFoundException("Connection not found.");
            retrievedAt = reader.IsDBNull(0) ? null : Parse(reader.GetString(0));
            possiblyStale = reader.GetBoolean(1);
        }

        var entries = new List<ProviderModelInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT provider_model_id,display_label FROM connection_model_catalogue WHERE connection_id=$id ORDER BY provider_model_id;";
            command.Parameters.AddWithValue("$id", connectionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new ProviderModelInfo(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        return new ModelCatalogue(retrievedAt, possiblyStale, entries);
    }

    private static Model ReadModel(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), (GenerationMode)reader.GetInt32(4), reader.GetBoolean(5),
        (LibraryRecordState)reader.GetInt32(6), Parse(reader.GetString(7)), Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
        reader.GetBoolean(10), (TextResultFormat)reader.GetInt32(11));

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

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var hasColumn = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal)) hasColumn = true;
            }
        }
        if (!hasColumn)
        {
            await ExecuteNonQueryAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken, transaction).ConfigureAwait(false);
        }
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
