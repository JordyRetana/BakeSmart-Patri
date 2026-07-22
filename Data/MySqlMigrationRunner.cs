using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace BakeSmartPatri.Data;

internal static class MySqlMigrationRunner
{
    private const int CommandTimeout = 180;

    public static async Task<int> MigrateAsync()
    {
        var source = Environment.GetEnvironmentVariable("BAKESMART_SQLSERVER");
        var target = Environment.GetEnvironmentVariable("BAKESMART_MYSQL");

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("Set BAKESMART_SQLSERVER and BAKESMART_MYSQL before running --migrate-to-mysql.");
            return 2;
        }

        try
        {
            await using var sql = new SqlConnection(source);
            await using var mysql = new MySqlConnection(NormalizeMySqlConnectionString(target));

            await sql.OpenAsync();
            await OpenMySqlWithRetryAsync(mysql);

            var tables = await ReadSqlTablesAsync(sql);
            if (tables.Count == 0)
                throw new InvalidOperationException("The SQL Server database contains no user tables.");

            var schema = await ReadSqlSchemaAsync(sql, tables);
            var counts = await ReadSqlCountsAsync(sql, tables);

            await using var transaction = await mysql.BeginTransactionAsync();
            try
            {
                await ExecuteMySqlAsync(mysql, transaction, "SET FOREIGN_KEY_CHECKS = 0;");
                foreach (var table in tables.AsEnumerable().Reverse())
                    await ExecuteMySqlAsync(mysql, transaction, $"DROP TABLE IF EXISTS `{table.Name}`;");

                foreach (var table in tables)
                {
                    await ExecuteMySqlAsync(mysql, transaction, BuildCreateTableSql(table, schema[table.Name]));
                    Console.WriteLine($"Created MySQL table {table.Name}.");
                }

                foreach (var table in tables)
                {
                    await CopyTableAsync(sql, mysql, transaction, table, schema[table.Name]);
                    Console.WriteLine($"Copied {table.Name}: {counts[table.Name]} rows.");
                }

                foreach (var table in tables)
                    await ReseedAutoIncrementAsync(mysql, transaction, table, schema[table.Name]);

                await ExecuteMySqlAsync(mysql, transaction, "SET FOREIGN_KEY_CHECKS = 1;");
                await transaction.CommitAsync();

                var targetCounts = await ReadMySqlCountsAsync(mysql, tables);
                var mismatch = counts
                    .Where(pair => !targetCounts.TryGetValue(pair.Key, out var count) || count != pair.Value)
                    .Select(pair => $"{pair.Key}: sqlserver={pair.Value}, mysql={targetCounts.GetValueOrDefault(pair.Key)}")
                    .ToArray();

                if (mismatch.Length > 0)
                    throw new InvalidOperationException($"Row validation failed: {string.Join("; ", mismatch)}");

                Console.WriteLine($"MYSQL_MIGRATION_OK tables={tables.Count} rows={counts.Values.Sum()}");
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
            Console.Error.WriteLine($"MYSQL_MIGRATION_FAILED {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            ConnectionTimeout = Math.Min(Math.Max(new MySqlConnectionStringBuilder(connectionString).ConnectionTimeout, 5u), 10u),
            DefaultCommandTimeout = CommandTimeout,
            AllowUserVariables = true,
            SslMode = MySqlSslMode.Required
        };

        return builder.ConnectionString;
    }

    private static async Task OpenMySqlWithRetryAsync(MySqlConnection connection)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync();
                return;
            }
            catch when (attempt < 12)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(5 + attempt * 3, 25));
                Console.WriteLine($"Aiven MySQL is not ready yet. Retry {attempt}/12 in {delay.TotalSeconds:0}s...");
                await Task.Delay(delay);
            }
        }
    }

    private static async Task<IReadOnlyList<TableInfo>> ReadSqlTablesAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT s.name AS SchemaName, t.name AS TableName
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY t.create_date, t.object_id;
            """;

        var tables = new List<TableInfo>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeout };
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1)));
        return tables;
    }

    private static async Task<Dictionary<string, List<ColumnInfo>>> ReadSqlSchemaAsync(SqlConnection connection, IReadOnlyList<TableInfo> tables)
    {
        const string sql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                c.name AS ColumnName,
                ty.name AS SqlType,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.indexes i
                INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY t.create_date, t.object_id, c.column_id;
            """;

        var result = tables.ToDictionary(t => t.Name, _ => new List<ColumnInfo>(), StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeout };
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tableName = reader.GetString(1);
            if (!result.TryGetValue(tableName, out var columns))
                continue;

            columns.Add(new ColumnInfo(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt16(4),
                reader.GetByte(5),
                reader.GetByte(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                Convert.ToBoolean(reader.GetInt32(9))));
        }

        return result;
    }

    private static string BuildCreateTableSql(TableInfo table, IReadOnlyList<ColumnInfo> columns)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CREATE TABLE `{table.Name}` (");

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            builder.Append("  `").Append(column.Name).Append("` ").Append(ToMySqlType(column));

            if (!column.IsNullable || column.IsPrimaryKey)
                builder.Append(" NOT NULL");

            if (column.IsIdentity)
                builder.Append(" AUTO_INCREMENT");

            if (index < columns.Count - 1 || columns.Any(c => c.IsPrimaryKey))
                builder.Append(',');

            builder.AppendLine();
        }

        var primaryKeys = columns.Where(c => c.IsPrimaryKey).Select(c => $"`{c.Name}`").ToArray();
        if (primaryKeys.Length > 0)
            builder.Append("  PRIMARY KEY (").Append(string.Join(", ", primaryKeys)).AppendLine(")");

        builder.AppendLine(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        return builder.ToString();
    }

    private static string ToMySqlType(ColumnInfo column)
    {
        return column.SqlType.ToLowerInvariant() switch
        {
            "int" => "INT",
            "bigint" => "BIGINT",
            "smallint" => "SMALLINT",
            "tinyint" => "TINYINT",
            "bit" => "TINYINT(1)",
            "decimal" or "numeric" => $"DECIMAL({column.Precision},{column.Scale})",
            "money" => "DECIMAL(19,4)",
            "float" => "DOUBLE",
            "real" => "FLOAT",
            "date" => "DATE",
            "datetime" or "datetime2" or "smalldatetime" => "DATETIME(6)",
            "time" => "TIME(6)",
            "uniqueidentifier" => "CHAR(36)",
            "nvarchar" or "nchar" => column.MaxLength < 0 ? "LONGTEXT" : $"VARCHAR({Math.Max((int)column.MaxLength / 2, 1)})",
            "varchar" or "char" => column.MaxLength < 0 ? "LONGTEXT" : $"VARCHAR({Math.Max((int)column.MaxLength, 1)})",
            "text" or "ntext" => "LONGTEXT",
            "varbinary" or "binary" => column.MaxLength < 0 ? "LONGBLOB" : $"VARBINARY({Math.Max((int)column.MaxLength, 1)})",
            "image" => "LONGBLOB",
            _ => "LONGTEXT"
        };
    }

    private static async Task<Dictionary<string, long>> ReadSqlCountsAsync(SqlConnection connection, IReadOnlyList<TableInfo> tables)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM [{table.Schema}].[{table.Name}];", connection) { CommandTimeout = CommandTimeout };
            result[table.Name] = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        return result;
    }

    private static async Task<Dictionary<string, long>> ReadMySqlCountsAsync(MySqlConnection connection, IReadOnlyList<TableInfo> tables)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            await using var command = new MySqlCommand($"SELECT COUNT(*) FROM `{table.Name}`;", connection) { CommandTimeout = CommandTimeout };
            result[table.Name] = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        return result;
    }

    private static async Task CopyTableAsync(SqlConnection source, MySqlConnection target, MySqlTransaction transaction, TableInfo table, IReadOnlyList<ColumnInfo> columns)
    {
        var columnNames = columns.Select(c => c.Name).ToArray();
        var sourceSql = $"SELECT {string.Join(", ", columnNames.Select(c => $"[{c}]"))} FROM [{table.Schema}].[{table.Name}];";
        await using var sourceCommand = new SqlCommand(sourceSql, source) { CommandTimeout = CommandTimeout };
        await using var reader = await sourceCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

        var insertSql = $"""
            INSERT INTO `{table.Name}` ({string.Join(", ", columnNames.Select(c => $"`{c}`"))})
            VALUES ({string.Join(", ", columnNames.Select((_, index) => $"@p{index}"))});
            """;

        await using var insertCommand = new MySqlCommand(insertSql, target, transaction) { CommandTimeout = CommandTimeout };
        for (var i = 0; i < columns.Count; i++)
            insertCommand.Parameters.Add(new MySqlParameter($"@p{i}", DBNull.Value));

        while (await reader.ReadAsync())
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var value = await reader.IsDBNullAsync(i) ? DBNull.Value : reader.GetValue(i);
                if (value is Guid guid)
                    value = guid.ToString();
                insertCommand.Parameters[i].Value = value;
            }

            await insertCommand.ExecuteNonQueryAsync();
        }
    }

    private static async Task ReseedAutoIncrementAsync(MySqlConnection connection, MySqlTransaction transaction, TableInfo table, IReadOnlyList<ColumnInfo> columns)
    {
        var identity = columns.FirstOrDefault(c => c.IsIdentity);
        if (identity is null)
            return;

        await using var command = new MySqlCommand($"SELECT COALESCE(MAX(`{identity.Name}`), 0) + 1 FROM `{table.Name}`;", connection, transaction);
        var next = Convert.ToInt64(await command.ExecuteScalarAsync());
        await ExecuteMySqlAsync(connection, transaction, $"ALTER TABLE `{table.Name}` AUTO_INCREMENT = {Math.Max(next, 1)};");
    }

    private static async Task ExecuteMySqlAsync(MySqlConnection connection, MySqlTransaction transaction, string sql)
    {
        await using var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeout };
        await command.ExecuteNonQueryAsync();
    }

    private sealed record TableInfo(string Schema, string Name);

    private sealed record ColumnInfo(
        string Name,
        string SqlType,
        short MaxLength,
        byte Precision,
        byte Scale,
        bool IsNullable,
        bool IsIdentity,
        bool IsPrimaryKey);
}
