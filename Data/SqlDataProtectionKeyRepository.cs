using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using System.Xml.Linq;

namespace BakeSmartPatri.Data;

public sealed class SqlDataProtectionKeyRepository : IXmlRepository
{
    private const string KeyPrefix = "dataProtectionKey:";
    private const int CommandTimeoutSeconds = 3;
    private const int MaxAttempts = 2;
    private readonly string _connectionString;
    private readonly bool _useMySql;

    public SqlDataProtectionKeyRepository(string connectionString)
    {
        _connectionString = connectionString.Trim().Trim('\uFEFF');
        _useMySql =
            _connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase) ||
            _connectionString.Contains("SslMode=", StringComparison.OrdinalIgnoreCase) ||
            _connectionString.Contains("Allow User Variables=", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var sql = _useMySql
            ? """
              SELECT SettingValue
              FROM ConfiguracionesAplicacion
              WHERE SettingKey LIKE 'dataProtectionKey:%';
              """
            : """
              SELECT SettingValue
              FROM dbo.ConfiguracionesAplicacion
              WHERE SettingKey LIKE N'dataProtectionKey:%';
              """;

        return WithRetry(() =>
        {
            var elements = new List<XElement>();
            using var connection = CreateConnection();
            connection.Open();
            EnsureTable(connection);

            using var command = CreateCommand(sql, connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var xml = Convert.ToString(reader.GetValue(0));
                if (!string.IsNullOrWhiteSpace(xml))
                    elements.Add(XElement.Parse(xml));
            }

            return (IReadOnlyCollection<XElement>)elements;
        });
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var sql = _useMySql
            ? """
              INSERT INTO ConfiguracionesAplicacion (SettingKey, SettingValue)
              VALUES (@Key, @Value)
              ON DUPLICATE KEY UPDATE SettingValue = VALUES(SettingValue);
              """
            : """
              MERGE dbo.ConfiguracionesAplicacion AS target
              USING (SELECT @Key AS SettingKey) AS source
              ON target.SettingKey = source.SettingKey
              WHEN MATCHED THEN
                  UPDATE SET SettingValue = @Value
              WHEN NOT MATCHED THEN
                  INSERT (SettingKey, SettingValue)
                  VALUES (@Key, @Value);
              """;

        WithRetry(() =>
        {
            using var connection = CreateConnection();
            connection.Open();
            EnsureTable(connection);

            using var command = CreateCommand(sql, connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            AddParameter(command, "@Key", $"{KeyPrefix}{friendlyName}");
            AddParameter(command, "@Value", element.ToString(SaveOptions.DisableFormatting));
            command.ExecuteNonQuery();
            return true;
        });
    }

    private System.Data.Common.DbConnection CreateConnection()
    {
        if (_useMySql)
        {
            var mysqlSettings = new MySqlConnectionStringBuilder(_connectionString)
            {
                ConnectionTimeout = 8,
                DefaultCommandTimeout = CommandTimeoutSeconds,
                AllowUserVariables = true,
                SslMode = MySqlSslMode.Required
            };
            return new MySqlConnection(mysqlSettings.ConnectionString);
        }

        var settings = new SqlConnectionStringBuilder(_connectionString)
        {
            ConnectTimeout = 8,
            ConnectRetryCount = 1,
            ConnectRetryInterval = 1
        };

        return new SqlConnection(settings.ConnectionString);
    }

    private void EnsureTable(System.Data.Common.DbConnection connection)
    {
        var sql = _useMySql
            ? """
              CREATE TABLE IF NOT EXISTS ConfiguracionesAplicacion
              (
                  SettingKey varchar(120) NOT NULL PRIMARY KEY,
                  SettingValue longtext NOT NULL
              ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
              """
            : """
              IF OBJECT_ID(N'dbo.ConfiguracionesAplicacion', N'U') IS NULL
              BEGIN
                  CREATE TABLE dbo.ConfiguracionesAplicacion
                  (
                      SettingKey nvarchar(120) NOT NULL CONSTRAINT PK_AppSettings PRIMARY KEY,
                      SettingValue nvarchar(max) NOT NULL
                  );
              END;
              """;

        using var command = CreateCommand(sql, connection);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.ExecuteNonQuery();
    }

    private System.Data.Common.DbCommand CreateCommand(string sql, System.Data.Common.DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static T WithRetry<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (SqlException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(200 * attempt);
            }
            catch (MySqlException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(200 * attempt);
            }
            catch (TimeoutException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}
