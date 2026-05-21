using Microsoft.Data.SqlClient;
using System.Data;

namespace BakeSmartPatri.Data;

public sealed class SqlStore
{
    private readonly IConfiguration _configuration;

    public SqlStore(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsEnabled => _configuration.GetValue<bool>("Features:UseSqlDatabase");

    private SqlConnection CreateConnection()
    {
        var connectionString = _configuration.GetConnectionString("BakeSmartDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:BakeSmartDb no está configurado.");

        return new SqlConnection(connectionString);
    }

    public async Task<object> HealthAsync()
    {
        if (!IsEnabled)
        {
            return new
            {
                enabled = false,
                status = "local-demo",
                message = "SQL está configurado pero apagado. Cambia Features:UseSqlDatabase a true para usar la base de datos."
            };
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT DB_NAME()", connection);
        var database = Convert.ToString(await command.ExecuteScalarAsync());

        return new
        {
            enabled = true,
            status = "connected",
            database
        };
    }

    public async Task<IReadOnlyList<object>> OrdersAsync()
    {
        const string sql = """
            SELECT
                o.Id,
                c.FullName AS Cliente,
                o.Status AS Estado,
                o.DeliveryDate AS Entrega,
                o.Total,
                o.Channel AS Canal,
                o.PaymentStatus,
                o.PaymentMethod,
                o.Address,
                o.DestinationLat,
                o.DestinationLng,
                STRING_AGG(CONCAT(oi.Quantity, ' x ', p.Description), ', ') AS Productos
            FROM dbo.Orders o
            INNER JOIN dbo.Customers c ON c.Id = o.CustomerId
            INNER JOIN dbo.OrderItems oi ON oi.OrderId = o.Id
            INNER JOIN dbo.Products p ON p.Id = oi.ProductId
            GROUP BY o.Id, c.FullName, o.Status, o.DeliveryDate, o.Total, o.Channel, o.PaymentStatus, o.PaymentMethod, o.Address, o.DestinationLat, o.DestinationLng
            ORDER BY o.CreatedAt DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("Id"),
            cliente = reader.GetString("Cliente"),
            producto = reader.GetString("Productos"),
            estado = reader.GetString("Estado"),
            entrega = reader.GetDateTime("Entrega").ToString("yyyy-MM-dd"),
            total = reader.GetDecimal("Total"),
            canal = reader.GetString("Canal"),
            paymentStatus = reader.GetString("PaymentStatus"),
            paymentMethod = reader.GetString("PaymentMethod"),
            address = reader.GetString("Address"),
            destinationLat = reader.GetDecimal("DestinationLat"),
            destinationLng = reader.GetDecimal("DestinationLng")
        });
    }

    public async Task<IReadOnlyList<object>> InventoryAsync()
    {
        const string sql = """
            SELECT
                Id,
                Code,
                Description,
                ProductType,
                Unit,
                Category,
                Subcategory,
                Price,
                Stock,
                MinStock,
                Active
            FROM dbo.Products
            ORDER BY ProductType, Category, Description;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("Id"),
            sku = reader.GetString("Code"),
            item = reader.GetString("Description"),
            type = reader.GetString("ProductType"),
            unidad = reader.GetString("Unit"),
            category = reader.GetString("Category"),
            subcategory = reader.GetNullableString("Subcategory"),
            costo = reader.GetDecimal("Price"),
            stock = reader.GetDecimal("Stock"),
            min = reader.GetDecimal("MinStock"),
            active = reader.GetBoolean("Active")
        });
    }

    public async Task<object> DashboardAsync()
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM dbo.Orders WHERE CAST(CreatedAt AS date) = CAST(GETDATE() AS date)) AS OrdersToday,
                (SELECT COUNT(*) FROM dbo.Orders WHERE Status IN (N'Confirmado', N'En producción', N'Listo')) AS InProduction,
                (SELECT COALESCE(SUM(Total), 0) FROM dbo.Sales WHERE CAST(CreatedAt AS date) = CAST(GETDATE() AS date)) AS SalesToday,
                (SELECT COUNT(*) FROM dbo.Products WHERE Stock <= MinStock AND Active = 1) AS LowStock;
            """;

        var rows = await QueryAsync(sql, reader => new DashboardRow(
            reader.GetInt32("OrdersToday"),
            reader.GetInt32("InProduction"),
            reader.GetDecimal("SalesToday"),
            reader.GetInt32("LowStock")
        ));

        var row = rows.FirstOrDefault() ?? new DashboardRow(0, 0, 0, 0);
        return new
        {
            kpis = new[]
            {
                new { label = "Pedidos hoy", value = (object)row.OrdersToday, delta = "SQL" },
                new { label = "En producción", value = (object)row.InProduction, delta = "activos" },
                new { label = "Ventas (₡)", value = (object)row.SalesToday, delta = "hoy" },
                new { label = "Alertas inventario", value = (object)row.LowStock, delta = "stock bajo" }
            }
        };
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqlDataReader, T> map)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private sealed record DashboardRow(int OrdersToday, int InProduction, decimal SalesToday, int LowStock);
}

internal static class SqlReaderExtensions
{
    public static int GetInt32(this SqlDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));
    public static string GetString(this SqlDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    public static bool GetBoolean(this SqlDataReader reader, string name) => reader.GetBoolean(reader.GetOrdinal(name));
    public static decimal GetDecimal(this SqlDataReader reader, string name) => reader.GetDecimal(reader.GetOrdinal(name));
    public static DateTime GetDateTime(this SqlDataReader reader, string name) => reader.GetDateTime(reader.GetOrdinal(name));

    public static string? GetNullableString(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
