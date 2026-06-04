using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using System.Text;

using BakeSmartPatri.Models;

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
            throw new InvalidOperationException("ConnectionStrings:BakeSmartDb no esta configurado.");

        return new SqlConnection(connectionString);
    }

    public async Task<object> HealthAsync()
    {
        if (!IsEnabled)
        {
            return new
            {
                enabled = false,
                status = "sql-disabled",
                message = "La conexion principal esta configurada pero apagada. Activa Features:UseSqlDatabase para usar los datos del sistema."
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
                o.OrderId,
                c.FullName AS CustomerName,
                c.Email AS CustomerEmail,
                os.Name AS OrderStatus,
                o.DeliveryDate,
                o.Total,
                oc.Name AS Channel,
                ps.Name AS PaymentStatus,
                pm.Name AS PaymentMethod,
                COALESCE(ca.AddressLine, o.DestinationLabel) AS Address,
                o.DestinationLatitude,
                o.DestinationLongitude,
                STRING_AGG(CONCAT(oi.Quantity, ' x ', p.Name), ', ') AS Products
            FROM dbo.Pedidos o
            INNER JOIN dbo.Clientes c ON c.CustomerId = o.CustomerId
            LEFT JOIN dbo.DireccionesCliente ca ON ca.CustomerAddressId = o.CustomerAddressId
            INNER JOIN dbo.CanalesPedido oc ON oc.OrderChannelId = o.OrderChannelId
            INNER JOIN dbo.EstadosPedido os ON os.OrderStatusId = o.OrderStatusId
            INNER JOIN dbo.EstadosPago ps ON ps.PaymentStatusId = o.PaymentStatusId
            INNER JOIN dbo.MetodosPago pm ON pm.PaymentMethodId = o.PaymentMethodId
            INNER JOIN dbo.DetallePedido oi ON oi.OrderId = o.OrderId
            INNER JOIN dbo.Productos p ON p.ProductId = oi.ProductId
            GROUP BY o.OrderId, c.FullName, c.Email, os.Name, o.DeliveryDate, o.Total, oc.Name, ps.Name, pm.Name,
                     ca.AddressLine, o.DestinationLabel, o.DestinationLatitude, o.DestinationLongitude, o.CreatedAt
            ORDER BY o.CreatedAt DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("OrderId"),
            cliente = reader.GetString("CustomerName"),
            customerEmail = reader.GetString("CustomerEmail"),
            producto = reader.GetString("Products"),
            estado = reader.GetString("OrderStatus"),
            entrega = reader.GetDateTime("DeliveryDate").ToString("yyyy-MM-dd"),
            total = reader.GetDecimal("Total"),
            canal = reader.GetString("Channel"),
            paymentStatus = reader.GetString("PaymentStatus"),
            paymentMethod = reader.GetString("PaymentMethod"),
            address = reader.GetString("Address"),
            destinationLat = reader.GetDecimal("DestinationLatitude"),
            destinationLng = reader.GetDecimal("DestinationLongitude")
        });
    }

    public async Task<IReadOnlyList<object>> InventoryAsync()
    {
        const string sql = """
            SELECT
                p.ProductId,
                p.Code,
                p.Name,
                pt.Name AS ProductType,
                um.Code AS UnitCode,
                parent.Name AS Category,
                pc.Name AS Subcategory,
                p.UnitPrice,
                p.UnitCost,
                COALESCE(SUM(ib.Quantity), 0) AS Stock,
                p.MinStock,
                p.IsActive
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN dbo.UnidadesMedida um ON um.UnitMeasureId = p.UnitMeasureId
            INNER JOIN dbo.CategoriasProducto pc ON pc.ProductCategoryId = p.ProductCategoryId
            LEFT JOIN dbo.CategoriasProducto parent ON parent.ProductCategoryId = pc.ParentCategoryId
            LEFT JOIN dbo.ExistenciasInventario ib ON ib.ProductId = p.ProductId
            GROUP BY p.ProductId, p.Code, p.Name, pt.Name, um.Code, parent.Name, pc.Name,
                     p.UnitPrice, p.UnitCost, p.MinStock, p.IsActive
            ORDER BY pt.Name, COALESCE(parent.Name, pc.Name), p.Name;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("ProductId"),
            sku = reader.GetString("Code"),
            item = reader.GetString("Name"),
            type = reader.GetString("ProductType"),
            unidad = reader.GetString("UnitCode"),
            category = reader.GetNullableString("Category") ?? reader.GetString("Subcategory"),
            subcategory = reader.GetNullableString("Subcategory"),
            costo = reader.GetDecimal("UnitCost"),
            price = reader.GetDecimal("UnitPrice"),
            stock = reader.GetDecimal("Stock"),
            min = reader.GetDecimal("MinStock"),
            active = reader.GetBoolean("IsActive")
        });
    }

    public async Task<IReadOnlyList<CatalogCategoryViewModel>> CatalogCategoriesAsync()
    {
        const string sql = """
            SELECT DISTINCT
                COALESCE(parent.ProductCategoryId, pc.ProductCategoryId) AS ProductCategoryId,
                COALESCE(parent.Name, pc.Name) AS CategoryName
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN dbo.CategoriasProducto pc ON pc.ProductCategoryId = p.ProductCategoryId
            LEFT JOIN dbo.CategoriasProducto parent ON parent.ProductCategoryId = pc.ParentCategoryId
            WHERE p.IsActive = 1
              AND pt.Name = N'Producto terminado'
            ORDER BY CategoryName;
            """;

        return await QueryAsync(sql, reader =>
        {
            var name = reader.GetString("CategoryName");
            return new CatalogCategoryViewModel(reader.GetInt32("ProductCategoryId"), name, IconForCategory(name));
        });
    }

    public async Task<IReadOnlyList<CatalogProductViewModel>> CatalogProductsAsync()
    {
        const string sql = """
            SELECT
                p.ProductId,
                p.Code,
                p.Name,
                p.Description,
                COALESCE(parent.Name, pc.Name) AS Category,
                pc.Name AS Subcategory,
                p.UnitPrice,
                COALESCE(SUM(ib.Quantity), 0) AS Stock,
                um.Code AS UnitCode,
                COALESCE(img.ImageUrl, N'/img/products/producto-sin-imagen.svg') AS ImageUrl,
                COALESCE(img.AltText, p.Name) AS AltText,
                p.IsActive
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN dbo.UnidadesMedida um ON um.UnitMeasureId = p.UnitMeasureId
            INNER JOIN dbo.CategoriasProducto pc ON pc.ProductCategoryId = p.ProductCategoryId
            LEFT JOIN dbo.CategoriasProducto parent ON parent.ProductCategoryId = pc.ParentCategoryId
            LEFT JOIN dbo.ExistenciasInventario ib ON ib.ProductId = p.ProductId
            OUTER APPLY (
                SELECT TOP 1 ImageUrl, AltText
                FROM dbo.ImagenesProducto pi
                WHERE pi.ProductId = p.ProductId
                ORDER BY pi.IsPrimary DESC, pi.SortOrder, pi.ProductImageId
            ) img
            WHERE p.IsActive = 1
              AND pt.Name = N'Producto terminado'
            GROUP BY p.ProductId, p.Code, p.Name, p.Description, parent.Name, pc.Name,
                     p.UnitPrice, um.Code, img.ImageUrl, img.AltText, p.IsActive
            ORDER BY COALESCE(parent.Name, pc.Name), p.Name;
            """;

        return await QueryAsync(sql, MapCatalogProduct);
    }

    public async Task<CatalogProductDetailsViewModel?> CatalogProductDetailsAsync(int productId)
    {
        var products = await CatalogProductsAsync();
        var product = products.FirstOrDefault(p => p.Id == productId);
        if (product is null)
            return null;

        const string imageSql = """
            SELECT ImageUrl, AltText, SortOrder, IsPrimary
            FROM dbo.ImagenesProducto
            WHERE ProductId = @ProductId
            ORDER BY IsPrimary DESC, SortOrder, ProductImageId;
            """;

        var images = await QueryAsync(imageSql, reader => new CatalogProductImageViewModel(
            reader.GetString("ImageUrl"),
            reader.GetString("AltText"),
            reader.GetInt32("SortOrder"),
            reader.GetBoolean("IsPrimary")),
            new SqlParameter("@ProductId", productId));

        if (images.Count == 0)
            images = [new CatalogProductImageViewModel(product.ImageUrl, product.AltText, 1, true)];

        var related = products
            .Where(p => p.Id != product.Id && p.Category == product.Category)
            .Take(3)
            .ToList();

        if (related.Count < 3)
        {
            related = related
                .Concat(products.Where(p => p.Id != product.Id && related.All(r => r.Id != p.Id)))
                .Take(3)
                .ToList();
        }

        return new CatalogProductDetailsViewModel(product, images, related);
    }

    public async Task<IReadOnlyList<string>> PaymentMethodNamesAsync()
    {
        const string sql = """
            SELECT Name
            FROM dbo.MetodosPago
            ORDER BY PaymentMethodId;
            """;

        return await QueryAsync(sql, reader => reader.GetString("Name"));
    }

    public async Task<object> DashboardAsync()
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM dbo.Pedidos WHERE CAST(CreatedAt AS date) = CAST(GETDATE() AS date)) AS OrdersToday,
                (
                    SELECT COUNT(*)
                    FROM dbo.Pedidos o
                    INNER JOIN dbo.EstadosPedido os ON os.OrderStatusId = o.OrderStatusId
                    WHERE os.Name IN (N'Confirmado', N'En produccion', N'Listo')
                ) AS InProduction,
                (SELECT COALESCE(SUM(Total), 0) FROM dbo.Ventas WHERE CAST(CreatedAt AS date) = CAST(GETDATE() AS date)) AS SalesToday,
                (
                    SELECT COUNT(*)
                    FROM dbo.Productos p
                    OUTER APPLY (
                        SELECT COALESCE(SUM(ib.Quantity), 0) AS Stock
                        FROM dbo.ExistenciasInventario ib
                        WHERE ib.ProductId = p.ProductId
                    ) b
                    WHERE b.Stock <= p.MinStock
                      AND p.IsActive = 1
                ) AS LowStock;
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
                new { label = "Pedidos hoy", value = (object)row.OrdersToday, delta = "hoy" },
                new { label = "En produccion", value = (object)row.InProduction, delta = "activos" },
                new { label = "Ventas (CRC)", value = (object)row.SalesToday, delta = "hoy" },
                new { label = "Alertas inventario", value = (object)row.LowStock, delta = "stock bajo" }
            }
        };
    }

    public async Task<IReadOnlyList<object>> CustomersAsync()
    {
        const string sql = """
            SELECT
                c.CustomerId,
                c.FullName,
                c.Email,
                c.Phone,
                c.IsFrequent,
                c.TotalSpent,
                COALESCE(ca.AddressLine, N'') AS AddressLine
            FROM dbo.Clientes c
            OUTER APPLY (
                SELECT TOP 1 AddressLine
                FROM dbo.DireccionesCliente ca
                WHERE ca.CustomerId = c.CustomerId
                ORDER BY ca.IsDefault DESC, ca.CustomerAddressId
            ) ca
            ORDER BY c.FullName;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("CustomerId"),
            fullName = reader.GetString("FullName"),
            email = reader.GetString("Email"),
            phone = reader.GetNullableString("Phone") ?? "",
            frequent = reader.GetBoolean("IsFrequent"),
            totalSpent = reader.GetDecimal("TotalSpent"),
            address = reader.GetString("AddressLine")
        });
    }

    public async Task<IReadOnlyList<object>> PromotionsAsync()
    {
        const string sql = """
            SELECT PromotionId, Name, StartDate, EndDate, DiscountRate, IsActive
            FROM dbo.Promociones
            ORDER BY IsActive DESC, EndDate DESC, Name;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("PromotionId"),
            name = reader.GetString("Name"),
            startDate = reader.GetDateTime("StartDate").ToString("yyyy-MM-dd"),
            endDate = reader.GetDateTime("EndDate").ToString("yyyy-MM-dd"),
            discount = reader.GetDecimal("DiscountRate"),
            active = reader.GetBoolean("IsActive")
        });
    }

    public async Task<IReadOnlyList<object>> UsersAsync()
    {
        const string sql = """
            SELECT u.UserId, u.FirstName, u.LastName, u.Email, u.Phone, u.AddressLine, u.IsActive, u.CreatedAt, r.RoleName
            FROM dbo.Usuarios u
            INNER JOIN dbo.Roles r ON r.RoleId = u.RoleId
            ORDER BY u.FirstName, u.LastName;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("UserId"),
            firstName = reader.GetString("FirstName"),
            lastName = reader.GetString("LastName"),
            email = reader.GetString("Email"),
            phone = reader.GetNullableString("Phone") ?? "",
            address = reader.GetNullableString("AddressLine") ?? "",
            role = reader.GetString("RoleName"),
            active = reader.GetBoolean("IsActive"),
            createdAt = reader.GetDateTime("CreatedAt").ToString("o")
        });
    }

    public async Task<AuthUser?> AuthenticateAsync(string email, string password)
    {
        const string sql = """
            SELECT TOP 1 u.Email, u.FirstName, u.LastName, u.PasswordHash, r.RoleName
            FROM dbo.Usuarios u
            INNER JOIN dbo.Roles r ON r.RoleId = u.RoleId
            WHERE LOWER(u.Email) = LOWER(@Email)
              AND u.IsActive = 1;
            """;

        var rows = await QueryAsync(sql, reader => new
        {
            email = reader.GetString("Email"),
            firstName = reader.GetString("FirstName"),
            lastName = reader.GetString("LastName"),
            passwordHash = reader.GetString("PasswordHash"),
            role = reader.GetString("RoleName")
        }, new SqlParameter("@Email", email));

        var user = rows.FirstOrDefault();
        if (user is null)
            return null;

        if (!VerifyPassword(user.passwordHash, password))
            return null;

        return new AuthUser(user.email, user.role, $"{user.firstName} {user.lastName}".Trim());
    }

    public async Task RegisterCustomerAsync(RegisterCustomerInput input)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @RoleId int = (SELECT RoleId FROM dbo.Roles WHERE RoleName = N'Cliente');

            IF @RoleId IS NULL
                THROW 50001, 'No existe el rol Cliente.', 1;

            IF EXISTS (SELECT 1 FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@Email))
                THROW 50002, 'Ya existe un usuario con ese correo.', 1;

            INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
            VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, @AddressLine, 1, SYSUTCDATETIME());

            DECLARE @UserId int = SCOPE_IDENTITY();

            INSERT INTO dbo.Clientes (UserId, FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
            VALUES (@UserId, CONCAT(@FirstName, N' ', @LastName), @Email, @Phone, 0, 0, SYSUTCDATETIME());

            DECLARE @CustomerId int = SCOPE_IDENTITY();

            IF NULLIF(@AddressLine, N'') IS NOT NULL
            BEGIN
                INSERT INTO dbo.DireccionesCliente (CustomerId, Label, AddressLine, Latitude, Longitude, IsDefault)
                VALUES (@CustomerId, N'Principal', @AddressLine, 9.932500, -84.079600, 1);
            END

            INSERT INTO dbo.BitacoraAuditoria (UserId, LogType, Detail, CreatedAt)
            VALUES (@UserId, N'REGISTRO_USUARIO', N'Registro de cliente desde formulario web', SYSUTCDATETIME());

            COMMIT TRAN;
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@FirstName", input.FirstName.Trim()),
            new SqlParameter("@LastName", input.LastName.Trim()),
            new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
            new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
            new SqlParameter("@PasswordHash", HashPassword(input.Password)),
            new SqlParameter("@AddressLine", (object?)input.AddressLine?.Trim() ?? DBNull.Value));
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            120_000,
            HashAlgorithmName.SHA256,
            32);

        return $"PBKDF2-SHA256$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string storedHash, string password)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2-SHA256")
            return false;

        if (!int.TryParse(parts[1], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public async Task<IReadOnlyList<object>> RolesAsync()
    {
        const string sql = """
            SELECT RoleId, RoleName, Description, IsSystemRole
            FROM dbo.Roles
            ORDER BY RoleName;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("RoleId"),
            name = reader.GetString("RoleName"),
            description = reader.GetString("Description"),
            system = reader.GetBoolean("IsSystemRole"),
            permissions = Array.Empty<string>()
        });
    }

    public async Task<IReadOnlyList<object>> PaymentMethodsAsync()
    {
        const string sql = """
            SELECT PaymentMethodId, Name, CommissionRate, IsActive
            FROM dbo.MetodosPago
            ORDER BY Name;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("PaymentMethodId"),
            name = reader.GetString("Name"),
            commissionRate = reader.GetDecimal("CommissionRate"),
            active = reader.GetBoolean("IsActive"),
            account = reader.GetString("Name")
        });
    }

    public async Task<object> PosConfigAsync()
    {
        var methods = await PaymentMethodsAsync();
        const string sql = "SELECT SettingKey, SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey IN (N'iva', N'frequentCustomerDiscount', N'originName', N'originLatitude', N'originLongitude');";
        var settings = await QueryAsync(sql, reader => new
        {
            key = reader.GetString("SettingKey"),
            value = reader.GetString("SettingValue")
        });

        decimal setting(string key, decimal fallback)
        {
            var value = settings.FirstOrDefault(x => x.key == key)?.value;
            return decimal.TryParse(value, out var parsed) ? parsed : fallback;
        }

        string settingText(string key, string fallback) =>
            settings.FirstOrDefault(x => x.key == key)?.value ?? fallback;

        return new
        {
            iva = setting("iva", 0.13m),
            frequentCustomerDiscount = setting("frequentCustomerDiscount", 0.05m),
            originName = settingText("originName", "BakeSmart Patri"),
            originLatitude = setting("originLatitude", 9.9142m),
            originLongitude = setting("originLongitude", -84.0734m),
            paymentMethods = methods
        };
    }

    public async Task<IReadOnlyList<object>> InventoryMovementsAsync()
    {
        const string sql = """
            SELECT TOP 80
                im.CreatedAt,
                p.Code,
                p.Name,
                im.MovementType,
                im.Quantity,
                um.Code AS UnitCode,
                im.Note,
                COALESCE(CONCAT(u.FirstName, N' ', u.LastName), N'Sistema') AS Responsible
            FROM dbo.MovimientosInventario im
            INNER JOIN dbo.Productos p ON p.ProductId = im.ProductId
            INNER JOIN dbo.UnidadesMedida um ON um.UnitMeasureId = p.UnitMeasureId
            LEFT JOIN dbo.Usuarios u ON u.UserId = im.ResponsibleUserId
            ORDER BY im.CreatedAt DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            createdAt = reader.GetDateTime("CreatedAt").ToString("o"),
            code = reader.GetString("Code"),
            productName = reader.GetString("Name"),
            type = reader.GetString("MovementType"),
            quantity = reader.GetDecimal("Quantity"),
            unit = reader.GetString("UnitCode"),
            note = reader.GetNullableString("Note") ?? "",
            responsible = reader.GetString("Responsible")
        });
    }

    public async Task<IReadOnlyList<object>> AuditLogsAsync()
    {
        const string sql = """
            SELECT TOP 60 LogType, Detail, CreatedAt
            FROM dbo.BitacoraAuditoria
            ORDER BY CreatedAt DESC, AuditLogId DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            type = reader.GetString("LogType"),
            detail = reader.GetString("Detail"),
            createdAt = reader.GetDateTime("CreatedAt").ToString("o")
        });
    }

    public async Task<object> ReportsAsync(string type, DateTime? start, DateTime? end)
    {
        return type switch
        {
            "sales" => await SalesReportAsync(start, end),
            "inventory" => await InventoryReportAsync(),
            "users" => await UsersReportAsync(),
            "promotions" => await PromotionsReportAsync(start, end),
            "cashClosures" => await CashClosuresReportAsync(start, end),
            "orders" => await OrdersReportAsync(start, end),
            _ => new { rows = Array.Empty<object>(), total = 0 }
        };
    }

    public async Task UpdateOrderStatusAsync(int orderId, string status)
    {
        const string sql = """
            UPDATE o
            SET OrderStatusId = os.OrderStatusId
            FROM dbo.Pedidos o
            INNER JOIN dbo.EstadosPedido os ON os.Name = @Status OR REPLACE(os.Name, N'ó', N'o') = REPLACE(@Status, N'ó', N'o')
            WHERE o.OrderId = @OrderId;

            INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
            SELECT @OrderId, os.OrderStatusId, CONCAT(N'Estado actualizado a ', os.Name), SYSUTCDATETIME()
            FROM dbo.EstadosPedido os
            WHERE os.Name = @Status OR REPLACE(os.Name, N'ó', N'o') = REPLACE(@Status, N'ó', N'o');
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@OrderId", orderId),
            new SqlParameter("@Status", status));
    }

    public async Task MarkOrderPaidAsync(int orderId, string method)
    {
        const string sql = """
            UPDATE o
            SET PaymentStatusId = ps.PaymentStatusId,
                PaymentMethodId = pm.PaymentMethodId
            FROM dbo.Pedidos o
            INNER JOIN dbo.EstadosPago ps ON ps.Name = N'Pagado'
            INNER JOIN dbo.MetodosPago pm ON pm.Name = @Method
            WHERE o.OrderId = @OrderId;
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@OrderId", orderId),
            new SqlParameter("@Method", method));
    }

    private async Task<object> SalesReportAsync(DateTime? start, DateTime? end)
    {
        const string sql = """
            SELECT s.SaleId, s.CreatedAt, c.FullName, pm.Name AS PaymentMethod, s.Subtotal, s.Tax, s.Total
            FROM dbo.Ventas s
            INNER JOIN dbo.Pedidos o ON o.OrderId = s.OrderId
            INNER JOIN dbo.Clientes c ON c.CustomerId = o.CustomerId
            INNER JOIN dbo.MetodosPago pm ON pm.PaymentMethodId = s.PaymentMethodId
            WHERE (@Start IS NULL OR CAST(s.CreatedAt AS date) >= @Start)
              AND (@End IS NULL OR CAST(s.CreatedAt AS date) <= @End)
            ORDER BY s.CreatedAt DESC;
            """;

        var rows = await QueryAsync(sql, reader => new
        {
            fecha = reader.GetDateTime("CreatedAt").ToString("yyyy-MM-dd"),
            cliente = reader.GetString("FullName"),
            metodo = reader.GetString("PaymentMethod"),
            subtotal = reader.GetDecimal("Subtotal"),
            impuesto = reader.GetDecimal("Tax"),
            total = reader.GetDecimal("Total")
        }, DateParameters(start, end));

        return new { rows, totalIncome = rows.Sum(x => x.total), totalTransactions = rows.Count };
    }

    private async Task<object> InventoryReportAsync()
    {
        var rows = await InventoryAsync();
        return new { rows, lowStock = rows.Count(), negativeStock = 0 };
    }

    private async Task<object> UsersReportAsync()
    {
        var rows = await UsersAsync();
        return new { rows, activeUsers = rows.Count() };
    }

    private async Task<object> PromotionsReportAsync(DateTime? start, DateTime? end)
    {
        var rows = await PromotionsAsync();
        return new { rows, activePromotions = rows.Count() };
    }

    private async Task<object> CashClosuresReportAsync(DateTime? start, DateTime? end)
    {
        const string sql = """
            SELECT cs.CashSessionId, cs.OpenedAt, cs.ClosedAt, cs.OpeningAmount, cs.ClosingAmount, cs.Status,
                   COALESCE(SUM(csp.Amount), 0) AS TotalSales
            FROM dbo.SesionesCaja cs
            LEFT JOIN dbo.PagosSesionCaja csp ON csp.CashSessionId = cs.CashSessionId
            WHERE (@Start IS NULL OR CAST(cs.OpenedAt AS date) >= @Start)
              AND (@End IS NULL OR CAST(cs.OpenedAt AS date) <= @End)
            GROUP BY cs.CashSessionId, cs.OpenedAt, cs.ClosedAt, cs.OpeningAmount, cs.ClosingAmount, cs.Status
            ORDER BY cs.OpenedAt DESC;
            """;

        var rows = await QueryAsync(sql, reader => new
        {
            caja = reader.GetInt32("CashSessionId"),
            apertura = reader.GetDateTime("OpenedAt").ToString("yyyy-MM-dd HH:mm"),
            cierre = reader.GetNullableDateTime("ClosedAt")?.ToString("yyyy-MM-dd HH:mm") ?? "",
            montoInicial = reader.GetDecimal("OpeningAmount"),
            montoFinal = reader.GetNullableDecimal("ClosingAmount") ?? 0,
            estado = reader.GetString("Status"),
            totalVentas = reader.GetDecimal("TotalSales")
        }, DateParameters(start, end));

        return new { rows, totalSales = rows.Sum(x => x.totalVentas) };
    }

    private async Task<object> OrdersReportAsync(DateTime? start, DateTime? end)
    {
        var orders = await OrdersAsync();
        return new { rows = orders, totalOrders = orders.Count };
    }

    private static CatalogProductViewModel MapCatalogProduct(SqlDataReader reader) =>
        new(
            reader.GetInt32("ProductId"),
            reader.GetString("Code"),
            reader.GetString("Name"),
            reader.GetNullableString("Description") ?? "",
            reader.GetString("Category"),
            reader.GetNullableString("Subcategory"),
            reader.GetDecimal("UnitPrice"),
            reader.GetDecimal("Stock"),
            reader.GetString("UnitCode"),
            reader.GetString("ImageUrl"),
            reader.GetString("AltText"),
            reader.GetBoolean("IsActive"));

    private static string IconForCategory(string name)
    {
        var normalized = RemoveDiacritics(name).ToLowerInvariant();
        if (normalized.Contains("pastel")) return "fa-cake-candles";
        if (normalized.Contains("cupcake")) return "fa-cake-candles";
        if (normalized.Contains("postre")) return "fa-ice-cream";
        if (normalized.Contains("galleta")) return "fa-cookie";
        if (normalized.Contains("bebida")) return "fa-mug-hot";
        return "fa-box-open";
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private async Task ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlParameter[] DateParameters(DateTime? start, DateTime? end) =>
    [
        new SqlParameter("@Start", (object?)start?.Date ?? DBNull.Value),
        new SqlParameter("@End", (object?)end?.Date ?? DBNull.Value)
    ];

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqlDataReader, T> map, params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        if (parameters.Length > 0)
            command.Parameters.AddRange(parameters);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public sealed record AuthUser(string Email, string Role, string DisplayName);
    public sealed record RegisterCustomerInput(string FirstName, string LastName, string Email, string? Phone, string? AddressLine, string Password);

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

    public static DateTime? GetNullableDateTime(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    public static decimal? GetNullableDecimal(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }
}
