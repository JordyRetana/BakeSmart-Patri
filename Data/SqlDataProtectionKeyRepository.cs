using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;
using System.Xml.Linq;

namespace BakeSmartPatri.Data;

public sealed class SqlDataProtectionKeyRepository : IXmlRepository
{
    private const string KeyPrefix = "dataProtectionKey:";
    private const int CommandTimeoutSeconds = 10;
    private const int MaxAttempts = 3;
    private readonly string _connectionString;

    public SqlDataProtectionKeyRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        const string sql = """
            SELECT SettingValue
            FROM dbo.ConfiguracionesAplicacion
            WHERE SettingKey LIKE N'dataProtectionKey:%';
            """;

        return WithRetry(() =>
        {
            EnsureTable();

            var elements = new List<XElement>();
            using var connection = CreateConnection();
            connection.Open();
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = CommandTimeoutSeconds
            };
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var xml = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(xml))
                    elements.Add(XElement.Parse(xml));
            }

            return (IReadOnlyCollection<XElement>)elements;
        });
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        const string sql = """
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
            EnsureTable();

            using var connection = CreateConnection();
            connection.Open();
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@Key", $"{KeyPrefix}{friendlyName}");
            command.Parameters.AddWithValue("@Value", element.ToString(SaveOptions.DisableFormatting));
            command.ExecuteNonQuery();
            return true;
        });
    }

    private SqlConnection CreateConnection()
    {
        var settings = new SqlConnectionStringBuilder(_connectionString)
        {
            ConnectTimeout = 8,
            ConnectRetryCount = 3,
            ConnectRetryInterval = 2
        };

        return new SqlConnection(settings.ConnectionString);
    }

    private void EnsureTable()
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.ConfiguracionesAplicacion', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ConfiguracionesAplicacion
                (
                    SettingKey nvarchar(120) NOT NULL CONSTRAINT PK_AppSettings PRIMARY KEY,
                    SettingValue nvarchar(max) NOT NULL
                );
            END;
            """;

        using var connection = CreateConnection();
        connection.Open();
        using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = CommandTimeoutSeconds
        };
        command.ExecuteNonQuery();
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
            catch (TimeoutException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}
