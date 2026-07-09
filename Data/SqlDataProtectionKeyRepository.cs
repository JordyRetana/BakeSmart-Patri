using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;
using System.Xml.Linq;

namespace BakeSmartPatri.Data;

public sealed class SqlDataProtectionKeyRepository : IXmlRepository
{
    private const string KeyPrefix = "dataProtectionKey:";
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

        var elements = new List<XElement>();
        using var connection = CreateConnection();
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var xml = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(xml))
                elements.Add(XElement.Parse(xml));
        }

        return elements;
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

        using var connection = CreateConnection();
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Key", $"{KeyPrefix}{friendlyName}");
        command.Parameters.AddWithValue("@Value", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }

    private SqlConnection CreateConnection()
    {
        var settings = new SqlConnectionStringBuilder(_connectionString)
        {
            ConnectRetryCount = 3,
            ConnectRetryInterval = 2
        };

        return new SqlConnection(settings.ConnectionString);
    }
}
