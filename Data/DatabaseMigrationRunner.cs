using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace BakeSmartPatri.Data;

internal static partial class DatabaseMigrationRunner
{
    private const int CommandTimeout = 180;

    public static async Task<int> CheckConnectionsAsync()
    {
        var settings = ReadSettings();
        if (settings is null) return 2;

        try
        {
            await CheckAsync("LOCAL", settings.LocalConnection);
            await CheckAsync("AZURE", settings.RemoteConnection);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    public static async Task<int> MigrateAsync()
    {
        var settings = ReadSettings();
        if (settings is null) return 2;

        var scriptPath = Environment.GetEnvironmentVariable("BAKESMART_SCHEMA_SCRIPT")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "Database", "BakeSmartPatri.sql");

        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"Schema script not found: {scriptPath}");
            return 2;
        }

        try
        {
            await using var local = new SqlConnection(settings.LocalConnection);
            await using var remote = new SqlConnection(settings.RemoteConnection);
            await OpenWithRetryAsync(local, "LOCAL", maxAttempts: 1);
            await OpenWithRetryAsync(remote, "AZURE", maxAttempts: 12);

            var sourceTables = await GetTablesAsync(local, null);
            if (sourceTables.Count == 0)
                throw new InvalidOperationException("The local database contains no user tables.");

            var sourceCounts = await ReadCountsAsync(local, null, sourceTables);
            Console.WriteLine($"Local database ready: {sourceTables.Count} tables, {sourceCounts.Values.Sum()} rows.");

            var remoteBefore = await GetTablesAsync(remote, null);
            var remoteBeforeCounts = await ReadCountsAsync(remote, null, remoteBefore);
            var summaryPath = Path.Combine(Directory.GetCurrentDirectory(), "Database", "AzurePreMigrationSummary.json");
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(new
            {
                capturedAtUtc = DateTime.UtcNow,
                database = remote.Database,
                tables = remoteBeforeCounts
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Remote summary saved: {summaryPath}");

            await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync();
            try
            {
                await DropRemoteObjectsAsync(remote, transaction);
                await ApplySchemaAsync(remote, transaction, await File.ReadAllTextAsync(scriptPath));
                var targetTables = await GetTablesAsync(remote, transaction);
                var missing = sourceTables.Where(table => !targetTables.Contains(table, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException($"The Azure schema is missing: {string.Join(", ", missing)}");

                await DisableConstraintsAsync(remote, transaction, targetTables);
                await ClearTablesAsync(remote, transaction, targetTables);

                foreach (var table in sourceTables)
                {
                    var columns = await GetColumnsAsync(local, table);
                    await CopyTableAsync(local, remote, transaction, table, columns);
                    Console.WriteLine($"Copied {table}: {sourceCounts[table]} rows.");
                }

                await ReseedIdentitiesAsync(remote, transaction, sourceTables);
                await EnableConstraintsAsync(remote, transaction, targetTables);

                var targetCounts = await ReadCountsAsync(remote, transaction, sourceTables);
                var mismatches = sourceCounts
                    .Where(pair => !targetCounts.TryGetValue(pair.Key, out var count) || count != pair.Value)
                    .Select(pair => $"{pair.Key}: local={pair.Value}, azure={targetCounts.GetValueOrDefault(pair.Key)}")
                    .ToArray();

                if (mismatches.Length > 0)
                    throw new InvalidOperationException($"Row validation failed: {string.Join("; ", mismatches)}");

                await transaction.CommitAsync();
                await WriteAzureSettingsAsync(settings.RemoteConnection);
                Console.WriteLine($"MIGRATION_OK tables={sourceTables.Count} rows={sourceCounts.Values.Sum()}");
                return 0;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"MIGRATION_FAILED {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static MigrationSettings? ReadSettings()
    {
        var local = Environment.GetEnvironmentVariable("BAKESMART_LOCAL");
        var remote = Environment.GetEnvironmentVariable("BAKESMART_REMOTE");
        if (!string.IsNullOrWhiteSpace(local) && !string.IsNullOrWhiteSpace(remote))
            return new MigrationSettings(local, remote);

        Console.Error.WriteLine("Set BAKESMART_LOCAL and BAKESMART_REMOTE before running the migration.");
        return null;
    }

    private static async Task CheckAsync(string label, string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await OpenWithRetryAsync(connection, label, label == "AZURE" ? 12 : 1);
        await using var command = new SqlCommand("SELECT DB_NAME(), COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;", connection)
        {
            CommandTimeout = CommandTimeout
        };
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Console.WriteLine($"{label}: database={reader.GetString(0)} tables={reader.GetInt32(1)}");
    }

    private static async Task OpenWithRetryAsync(SqlConnection connection, string label, int maxAttempts)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await connection.OpenAsync();
                return;
            }
            catch (SqlException exception) when (attempt < maxAttempts && IsTransientAzureError(exception))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(5 + attempt * 5, 30));
                Console.WriteLine($"{label}: base temporalmente no disponible. Reintento {attempt}/{maxAttempts} en {delay.TotalSeconds:0} s...");
                await Task.Delay(delay);
            }
        }
    }

    private static bool IsTransientAzureError(SqlException exception)
    {
        var transientNumbers = new HashSet<int>
        {
            -2, 20, 64, 233, 40613, 40197, 40501, 40540, 10928, 10929,
            49918, 49919, 49920, 10053, 10054, 10060
        };
        return exception.Errors.Cast<SqlError>().Any(error => transientNumbers.Contains(error.Number));
    }

    private static async Task ApplySchemaAsync(SqlConnection connection, SqlTransaction transaction, string script)
    {
        var batches = GoBatchRegex().Split(script);
        foreach (var rawBatch in batches)
        {
            var batch = rawBatch.Trim();
            if (string.IsNullOrWhiteSpace(batch) ||
                batch.Contains("CREATE DATABASE BakeSmartPatri", StringComparison.OrdinalIgnoreCase) ||
                UseDatabaseRegex().IsMatch(batch))
                continue;

            await ExecuteAsync(connection, transaction, batch);
        }
    }

    private static async Task DropRemoteObjectsAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var dropStatements = new[]
        {
            """
            SELECT N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' +
                   QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys;
            """,
            """
            SELECT N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.views WHERE is_ms_shipped = 0;
            """,
            """
            SELECT N'DROP PROCEDURE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.procedures WHERE is_ms_shipped = 0;
            """,
            """
            SELECT N'DROP FUNCTION ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.objects WHERE type IN ('FN', 'IF', 'TF', 'FS', 'FT') AND is_ms_shipped = 0;
            """,
            """
            SELECT N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.tables WHERE is_ms_shipped = 0;
            """
        };

        foreach (var query in dropStatements)
        {
            var statements = new List<string>();
            await using (var command = new SqlCommand(query, connection, transaction) { CommandTimeout = CommandTimeout })
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) statements.Add(reader.GetString(0));
            }

            foreach (var statement in statements)
                await ExecuteAsync(connection, transaction, statement);
        }
    }

    private static async Task<List<string>> GetTablesAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        const string sql = """
            SELECT QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
              AND NOT (s.name = N'dbo' AND t.name = N'sysdiagrams')
            ORDER BY s.name, t.name;
            """;
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeout };
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<Dictionary<string, long>> ReadCountsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IEnumerable<string> tables)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table};", connection, transaction)
            {
                CommandTimeout = CommandTimeout
            };
            counts[table] = Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        return counts;
    }

    private static async Task<List<string>> GetColumnsAsync(SqlConnection connection, string quotedTable)
    {
        var parts = quotedTable.Replace("[", "").Replace("]", "").Split('.');
        const string sql = """
            SELECT QUOTENAME(c.name)
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = @Schema AND t.name = @Table
              AND c.is_computed = 0 AND ty.name NOT IN (N'timestamp', N'rowversion')
            ORDER BY c.column_id;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeout };
        command.Parameters.AddWithValue("@Schema", parts[0]);
        command.Parameters.AddWithValue("@Table", parts[1]);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task CopyTableAsync(
        SqlConnection local,
        SqlConnection remote,
        SqlTransaction transaction,
        string table,
        IReadOnlyCollection<string> columns)
    {
        if (columns.Count == 0) return;
        var targetCollations = await GetTargetCollationsAsync(remote, transaction, table);
        var sourceExpressions = columns.Select(column =>
        {
            var plainColumn = column.Trim('[', ']');
            if (!targetCollations.TryGetValue(plainColumn, out var collation) || string.IsNullOrWhiteSpace(collation))
                return column;
            if (!SafeIdentifierRegex().IsMatch(collation))
                throw new InvalidOperationException($"Invalid target collation: {collation}");
            return $"{column} COLLATE {collation} AS {column}";
        });

        await using var sourceCommand = new SqlCommand($"SELECT {string.Join(",", sourceExpressions)} FROM {table};", local)
        {
            CommandTimeout = CommandTimeout
        };
        await using var reader = await sourceCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        using var bulk = new SqlBulkCopy(remote, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock, transaction)
        {
            DestinationTableName = table,
            BatchSize = 500,
            BulkCopyTimeout = CommandTimeout,
            EnableStreaming = true
        };
        foreach (var column in columns)
        {
            var plainColumn = column.Trim('[', ']');
            bulk.ColumnMappings.Add(plainColumn, plainColumn);
        }
        await bulk.WriteToServerAsync(reader);
    }

    private static async Task<Dictionary<string, string>> GetTargetCollationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string quotedTable)
    {
        const string sql = """
            SELECT c.name, c.collation_name
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@Table) AND c.collation_name IS NOT NULL;
            """;
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeout };
        command.Parameters.AddWithValue("@Table", quotedTable.Replace("[", "").Replace("]", ""));
        await using var reader = await command.ExecuteReaderAsync();
        var collations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) collations[reader.GetString(0)] = reader.GetString(1);
        return collations;
    }

    private static async Task WriteAzureSettingsAsync(string connectionString)
    {
        var settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.Azure.json");
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new Dictionary<string, string> { ["BakeSmartDb"] = connectionString },
            Features = new Dictionary<string, bool> { ["UseSqlDatabase"] = true }
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(settingsPath, json);
        Console.WriteLine($"Azure application settings activated: {settingsPath}");
    }

    private static async Task DisableConstraintsAsync(SqlConnection connection, SqlTransaction transaction, IEnumerable<string> tables)
    {
        foreach (var table in tables) await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} NOCHECK CONSTRAINT ALL;");
    }

    private static async Task EnableConstraintsAsync(SqlConnection connection, SqlTransaction transaction, IEnumerable<string> tables)
    {
        foreach (var table in tables) await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} WITH CHECK CHECK CONSTRAINT ALL;");
    }

    private static async Task ClearTablesAsync(SqlConnection connection, SqlTransaction transaction, IEnumerable<string> tables)
    {
        foreach (var table in tables) await ExecuteAsync(connection, transaction, $"DELETE FROM {table};");
    }

    private static async Task ReseedIdentitiesAsync(SqlConnection connection, SqlTransaction transaction, IEnumerable<string> tables)
    {
        foreach (var table in tables)
        {
            var hasIdentity = Convert.ToInt32(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(@Table);",
                new SqlParameter("@Table", table.Replace("[", "").Replace("]", "")))) > 0;
            if (!hasIdentity) continue;

            var identityColumn = Convert.ToString(await ScalarAsync(connection, transaction,
                "SELECT QUOTENAME(name) FROM sys.identity_columns WHERE object_id = OBJECT_ID(@Table);",
                new SqlParameter("@Table", table.Replace("[", "").Replace("]", ""))));
            if (string.IsNullOrWhiteSpace(identityColumn)) continue;

            var max = Convert.ToInt64(await ScalarAsync(connection, transaction,
                $"SELECT COALESCE(MAX(CONVERT(bigint, {identityColumn})), 0) FROM {table};"));
            await ExecuteAsync(connection, transaction, $"DBCC CHECKIDENT ('{table.Replace("'", "''")}', RESEED, {max});");
        }
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeout };
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeout };
        await command.ExecuteNonQueryAsync();
    }

    private sealed record MigrationSettings(string LocalConnection, string RemoteConnection);

    [GeneratedRegex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchRegex();

    [GeneratedRegex(@"^\s*USE\s+\[?BakeSmartPatri\]?\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex UseDatabaseRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex SafeIdentifierRegex();
}
