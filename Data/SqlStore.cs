using Microsoft.Data.SqlClient;
using MySqlConnector;
using System.Text.RegularExpressions;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using BakeSmartPatri.Models;

namespace BakeSmartPatri.Data;

public sealed partial class SqlStore
{
    private const int ConnectTimeoutSeconds = 8;
    private const int CommandTimeoutSeconds = 10;
    private const int MaxTransientAttempts = 3;
    private readonly IConfiguration _configuration;

    public SqlStore(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsEnabled => ReadBool(_configuration, "Features:UseSqlDatabase");
    private bool UseMySql
    {
        get
        {
            var provider =
                (_configuration["Database:Provider"] ?? _configuration["DatabaseProvider"] ?? string.Empty)
                .Trim()
                .Trim('\uFEFF');

            if (string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
                return true;

            var connectionString = (_configuration.GetConnectionString("BakeSmartDb") ?? string.Empty)
                .Trim()
                .Trim('\uFEFF');

            return connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("SslMode=", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("Allow User Variables=", StringComparison.OrdinalIgnoreCase);
        }
    }

    private DbConnection CreateConnection()
    {
        var connectionString = _configuration.GetConnectionString("BakeSmartDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:BakeSmartDb no esta configurado.");
        connectionString = connectionString.Trim().Trim('\uFEFF');

        if (UseMySql)
        {
            var mySqlSettings = new MySqlConnectionStringBuilder(connectionString)
            {
                ConnectionTimeout = Math.Min(Math.Max(new MySqlConnectionStringBuilder(connectionString).ConnectionTimeout, 5u), (uint)ConnectTimeoutSeconds),
                DefaultCommandTimeout = CommandTimeoutSeconds,
                AllowUserVariables = true,
                SslMode = MySqlSslMode.Required
            };
            return new MySqlConnection(mySqlSettings.ConnectionString);
        }

        var sqlSettings = new SqlConnectionStringBuilder(connectionString);
        sqlSettings.ConnectTimeout = Math.Min(
            sqlSettings.ConnectTimeout > 0 ? sqlSettings.ConnectTimeout : ConnectTimeoutSeconds,
            ConnectTimeoutSeconds);
        sqlSettings.ConnectRetryCount = Math.Max(3, sqlSettings.ConnectRetryCount);
        sqlSettings.ConnectRetryInterval = 2;
        return new SqlConnection(sqlSettings.ConnectionString);
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

        await using var command = CreateCommand(connection, UseMySql ? "SELECT DATABASE()" : "SELECT DB_NAME()");
        var database = Convert.ToString(await command.ExecuteScalarAsync());

        var productCount = await CountRowsAsync(connection, UseMySql ? "Productos" : "dbo.Productos");
        var orderCount = await CountRowsAsync(connection, UseMySql ? "Pedidos" : "dbo.Pedidos");
        var userCount = await CountRowsAsync(connection, UseMySql ? "Usuarios" : "dbo.Usuarios");

        return new
        {
            enabled = true,
            status = "ok",
            databaseStatus = "online",
            database,
            server = connection.DataSource,
            checkedAtUtc = DateTime.UtcNow,
            keepAlive = new
            {
                query = "SELECT COUNT(*)",
                tables = new
                {
                    products = productCount,
                    orders = orderCount,
                    users = userCount
                }
            }
        };
    }

    private async Task<long> CountRowsAsync(DbConnection connection, string tableName)
    {
        await using var command = CreateCommand(connection, $"SELECT COUNT(*) FROM {tableName}");
        command.CommandTimeout = CommandTimeoutSeconds;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<object>> OrdersAsync(string? customerEmail = null)
    {
        static int StepForStatus(string? status)
        {
            var normalized = RemoveDiacritics(status ?? "").ToUpperInvariant();
            if (normalized.Contains("ENTREGADO")) return 5;
            if (normalized.Contains("CAMINO")) return 4;
            if (normalized.Contains("LISTO")) return 3;
            if (normalized.Contains("PRODUCCION")) return 2;
            if (normalized.Contains("CONFIRMADO")) return 1;
            return 0;
        }

        const string sql = """
            SELECT
                o.OrderId,
                o.CreatedAt,
                c.FullName AS CustomerName,
                c.Email AS CustomerEmail,
                c.Phone AS CustomerPhone,
                os.Name AS OrderStatus,
                o.DeliveryDate,
                o.Total,
                oc.Name AS Channel,
                ps.Name AS PaymentStatus,
                pm.Name AS PaymentMethod,
                o.Notes,
                COALESCE(ca.AddressLine, o.DestinationLabel) AS Address,
                o.DestinationLatitude,
                o.DestinationLongitude,
                o.CurrentLatitude,
                o.CurrentLongitude,
                o.TrackingStep,
                MIN(oi.ProductId) AS FirstProductId,
                MIN(oi.Quantity) AS FirstQuantity,
                MIN(oi.UnitPrice) AS FirstUnitPrice,
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
            WHERE (@CustomerEmail IS NULL OR c.Email = @CustomerEmail)
            GROUP BY o.OrderId, c.FullName, c.Email, c.Phone, os.Name, o.DeliveryDate, o.Total, oc.Name, ps.Name, pm.Name, o.Notes,
                     ca.AddressLine, o.DestinationLabel, o.DestinationLatitude, o.DestinationLongitude,
                     o.CurrentLatitude, o.CurrentLongitude, o.TrackingStep, o.CreatedAt
            ORDER BY o.CreatedAt DESC;
            """;

        var query = UseMySql
            ? sql.Replace("STRING_AGG(CONCAT(oi.Quantity, ' x ', p.Name), ', ')", "GROUP_CONCAT(CONCAT(oi.Quantity, ' x ', p.Name) SEPARATOR ', ')", StringComparison.OrdinalIgnoreCase)
            : sql;

        return await QueryAsync(query, reader =>
        {
            var orderStatus = reader.GetString("OrderStatus");
            var storedStep = reader.GetInt32("TrackingStep");
            var currentStep = Math.Max(storedStep, StepForStatus(orderStatus));
            var notes = reader.GetNullableString("Notes");

            return new
            {
            id = reader.GetInt32("OrderId"),
            createdAt = DateTime.SpecifyKind(reader.GetDateTime("CreatedAt"), DateTimeKind.Utc).ToString("O"),
            cliente = reader.GetString("CustomerName"),
            customerEmail = reader.GetString("CustomerEmail"),
            customerPhone = reader.GetNullableString("CustomerPhone") ?? string.Empty,
            producto = reader.GetString("Products"),
            productId = reader.GetInt32("FirstProductId"),
            quantity = reader.GetDecimal("FirstQuantity"),
            unitPrice = reader.GetDecimal("FirstUnitPrice"),
            estado = orderStatus,
            entrega = reader.GetDateTime("DeliveryDate").ToString("yyyy-MM-dd"),
            total = reader.GetDecimal("Total"),
            canal = reader.GetString("Channel"),
            paymentStatus = reader.GetString("PaymentStatus"),
            paymentMethod = reader.GetString("PaymentMethod"),
            notes,
            isCustomOrder = !string.IsNullOrWhiteSpace(notes) && notes.Contains("Tipo de encargo", StringComparison.OrdinalIgnoreCase),
            address = reader.GetString("Address"),
            destinationLat = reader.GetDecimal("DestinationLatitude"),
            destinationLng = reader.GetDecimal("DestinationLongitude"),
            tracking = new
            {
                currentLat = reader.GetDecimal("CurrentLatitude"),
                currentLng = reader.GetDecimal("CurrentLongitude"),
                destinationLat = reader.GetDecimal("DestinationLatitude"),
                destinationLng = reader.GetDecimal("DestinationLongitude"),
                currentStep,
                steps = new[] { "Pendiente pago", "Confirmado", "En produccion", "Listo", "En camino", "Entregado" }
            }
            };
        }, new SqlParameter("@CustomerEmail", string.IsNullOrWhiteSpace(customerEmail) ? DBNull.Value : customerEmail.Trim().ToLowerInvariant()));
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
                p.IsActive,
                COALESCE(MIN(img.ImageUrl), '/img/products/producto-sin-imagen.svg') AS ImageUrl
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN dbo.UnidadesMedida um ON um.UnitMeasureId = p.UnitMeasureId
            INNER JOIN dbo.CategoriasProducto pc ON pc.ProductCategoryId = p.ProductCategoryId
            LEFT JOIN dbo.CategoriasProducto parent ON parent.ProductCategoryId = pc.ParentCategoryId
            LEFT JOIN dbo.ExistenciasInventario ib ON ib.ProductId = p.ProductId
            LEFT JOIN dbo.ImagenesProducto img ON img.ProductId = p.ProductId AND img.IsPrimary = 1
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
            active = reader.GetBoolean("IsActive"),
            imageUrl = reader.GetString("ImageUrl")
        });
    }

    public async Task<IReadOnlyList<object>> ProductCategoryOptionsAsync()
    {
        const string sql = """
            SELECT parent.ProductCategoryId AS CategoryId,
                   parent.Name AS CategoryName,
                   child.ProductCategoryId AS SubcategoryId,
                   child.Name AS SubcategoryName
            FROM dbo.CategoriasProducto parent
            LEFT JOIN dbo.CategoriasProducto child ON child.ParentCategoryId = parent.ProductCategoryId
            WHERE parent.ParentCategoryId IS NULL
            ORDER BY parent.Name, child.Name;
            """;
        var rows = await QueryAsync(sql, reader => new
        {
            categoryId = reader.GetInt32("CategoryId"),
            category = reader.GetString("CategoryName"),
            subcategoryId = reader.IsDBNull(reader.GetOrdinal("SubcategoryId")) ? (int?)null : reader.GetInt32("SubcategoryId"),
            subcategory = reader.GetNullableString("SubcategoryName")
        });
        return rows.Cast<object>().ToList();
    }

    public async Task<int> SaveInventoryProductAsync(InventoryProductInput input, string? userEmail = null)
    {
        // Validar duplicado de cÃ³digo
        // Ejecutar estas consultas independientes en paralelo evita varias esperas
        // consecutivas contra la base de datos remota.
        var existingCode = input.Id is null
            ? await CodeExistsAsync(input.Code.Trim())
            : await CodeExistsExcludingAsync(input.Code.Trim(), input.Id.Value);

        if (existingCode)
            throw new InvalidOperationException($"Ya existe un producto con el cÃ³digo '{input.Code.Trim()}'.");

        var typeId = await EnsureProductTypeAsync(input.Type);
        var unitId = await EnsureUnitMeasureAsync(input.Unit);
        var categoryId = await EnsureProductCategoryAsync(input.Category, input.Subcategory);
        var locationId = await EnsureInventoryLocationAsync();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        int productId;

        try
        {
            if (input.Id is > 0)
            {
                var updateProduct = UseMySql
                    ? """
                      UPDATE Productos
                      SET ProductTypeId = @ProductTypeId,
                          ProductCategoryId = @ProductCategoryId,
                          UnitMeasureId = @UnitMeasureId,
                          Code = @Code,
                          Name = @Name,
                          Description = @Description,
                          UnitPrice = @UnitPrice,
                          UnitCost = @UnitCost,
                          MinStock = @MinStock,
                          IsActive = 1
                      WHERE ProductId = @ProductId;
                      """
                    : """
                    UPDATE dbo.Productos
                    SET ProductTypeId = @ProductTypeId,
                        ProductCategoryId = @ProductCategoryId,
                        UnitMeasureId = @UnitMeasureId,
                        Code = @Code,
                        Name = @Name,
                        Description = @Description,
                        UnitPrice = @UnitPrice,
                        UnitCost = @UnitCost,
                        MinStock = @MinStock,
                        IsActive = 1
                    WHERE ProductId = @ProductId;
                    """;

                await ExecuteInTransactionAsync(connection, transaction, updateProduct,
                    new SqlParameter("@ProductTypeId", typeId),
                    new SqlParameter("@ProductCategoryId", categoryId),
                    new SqlParameter("@UnitMeasureId", unitId),
                    new SqlParameter("@Code", input.Code.Trim()),
                    new SqlParameter("@Name", input.Description.Trim()),
                    new SqlParameter("@Description", input.Description.Trim()),
                    new SqlParameter("@UnitPrice", input.Price),
                    new SqlParameter("@UnitCost", input.Price),
                    new SqlParameter("@MinStock", input.MinStock),
                    new SqlParameter("@ProductId", input.Id.Value));

                productId = input.Id.Value;
            }
            else
            {
                var insertProduct = UseMySql
                    ? """
                      INSERT INTO Productos
                          (ProductTypeId, ProductCategoryId, UnitMeasureId, Code, Name, Description, UnitPrice, UnitCost, MinStock, IsActive, CreatedAt)
                      VALUES
                          (@ProductTypeId, @ProductCategoryId, @UnitMeasureId, @Code, @Name, @Description, @UnitPrice, @UnitCost, @MinStock, 1, UTC_TIMESTAMP());
                      """
                    : """
                    INSERT INTO dbo.Productos
                        (ProductTypeId, ProductCategoryId, UnitMeasureId, Code, Name, Description, UnitPrice, UnitCost, MinStock, IsActive, CreatedAt)
                    OUTPUT INSERTED.ProductId
                    VALUES
                        (@ProductTypeId, @ProductCategoryId, @UnitMeasureId, @Code, @Name, @Description, @UnitPrice, @UnitCost, @MinStock, 1, SYSUTCDATETIME());
                    """;

                var insertParameters = new[]
                {
                    new SqlParameter("@ProductTypeId", typeId),
                    new SqlParameter("@ProductCategoryId", categoryId),
                    new SqlParameter("@UnitMeasureId", unitId),
                    new SqlParameter("@Code", input.Code.Trim()),
                    new SqlParameter("@Name", input.Description.Trim()),
                    new SqlParameter("@Description", input.Description.Trim()),
                    new SqlParameter("@UnitPrice", input.Price),
                    new SqlParameter("@UnitCost", input.Price),
                    new SqlParameter("@MinStock", input.MinStock)
                };

                if (UseMySql)
                {
                    await ExecuteInTransactionAsync(connection, transaction, insertProduct, insertParameters);
                    productId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, "SELECT LAST_INSERT_ID();"));
                }
                else
                {
                    productId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, insertProduct, insertParameters));
                }
            }

            await SetInventoryBalanceAsync(connection, transaction, productId, locationId, input.Stock);
            if (input.Stock > 0)
                await AddInventoryMovementAsync(connection, transaction, productId, locationId, "AJUSTE", input.Stock, "Registro/actualizacion de producto");
            if (!string.IsNullOrWhiteSpace(input.ImageUrl))
                await SavePrimaryProductImageAsync(connection, transaction, productId, input.ImageUrl.Trim(), input.Description.Trim());

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        // La auditorÃ­a no debe convertir un guardado confirmado en un error visible.
        try
        {
            var action = input.Id is > 0 ? "actualizado" : "creado";
            await AddAuditLogAsync($"INVENTARIO_PRODUCTO_{action.ToUpperInvariant()}", $"Producto '{input.Code}' {action}: {input.Description}", userEmail);
        }
        catch { }

        return productId;
    }

    public async Task ToggleInventoryProductAsync(int productId, string? userEmail = null)
    {
        const string sql = """
            UPDATE dbo.Productos
            SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END
            WHERE ProductId = @ProductId;
            """;

        await ExecuteAsync(sql, new SqlParameter("@ProductId", productId));

        var status = await GetProductActiveStatusAsync(productId);
        var action = status ? "activado" : "desactivado";
        await AddAuditLogAsync($"INVENTARIO_{action.ToUpperInvariant()}", $"Producto ID {productId} {action}", userEmail);
    }

    private static async Task SavePrimaryProductImageAsync(DbConnection connection, DbTransaction transaction, int productId, string imageUrl, string altText)
    {
        if (connection is MySqlConnection)
        {
            await ExecuteInTransactionAsync(connection, transaction, "UPDATE ImagenesProducto SET IsPrimary = 0 WHERE ProductId = @ProductId;",
                new SqlParameter("@ProductId", productId));
            var existingId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                "SELECT ProductImageId FROM ImagenesProducto WHERE ProductId = @ProductId AND ImageUrl = @ImageUrl LIMIT 1;",
                new SqlParameter("@ProductId", productId), new SqlParameter("@ImageUrl", imageUrl)) ?? 0);
            if (existingId > 0)
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE ImagenesProducto SET AltText = @AltText, IsPrimary = 1, SortOrder = 1 WHERE ProductImageId = @ImageId;",
                    new SqlParameter("@AltText", altText), new SqlParameter("@ImageId", existingId));
            }
            else
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "INSERT INTO ImagenesProducto (ProductId, ImageUrl, AltText, SortOrder, IsPrimary) VALUES (@ProductId, @ImageUrl, @AltText, 1, 1);",
                    new SqlParameter("@ProductId", productId), new SqlParameter("@ImageUrl", imageUrl), new SqlParameter("@AltText", altText));
            }
            return;
        }

        await ExecuteInTransactionAsync(connection, transaction, """
            UPDATE dbo.ImagenesProducto SET IsPrimary = 0 WHERE ProductId = @ProductId;
            IF EXISTS (SELECT 1 FROM dbo.ImagenesProducto WHERE ProductId = @ProductId AND ImageUrl = @ImageUrl)
                UPDATE dbo.ImagenesProducto SET AltText = @AltText, IsPrimary = 1, SortOrder = 1 WHERE ProductId = @ProductId AND ImageUrl = @ImageUrl;
            ELSE
                INSERT INTO dbo.ImagenesProducto (ProductId, ImageUrl, AltText, SortOrder, IsPrimary) VALUES (@ProductId, @ImageUrl, @AltText, 1, 1);
            """, new SqlParameter("@ProductId", productId), new SqlParameter("@ImageUrl", imageUrl), new SqlParameter("@AltText", altText));
    }

    private async Task<bool> CodeExistsAsync(string code)
    {
        const string sql = "SELECT COUNT(1) FROM dbo.Productos WHERE Code = @Code";
        var count = Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Code", code)));
        return count > 0;
    }

    private async Task<bool> CodeExistsExcludingAsync(string code, int excludeId)
    {
        const string sql = "SELECT COUNT(1) FROM dbo.Productos WHERE Code = @Code AND ProductId <> @ExcludeId";
        var count = Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Code", code), new SqlParameter("@ExcludeId", excludeId)));
        return count > 0;
    }

    private async Task<bool> GetProductActiveStatusAsync(int productId)
    {
        const string sql = "SELECT IsActive FROM dbo.Productos WHERE ProductId = @ProductId";
        var result = await ScalarAsync(sql, new SqlParameter("@ProductId", productId));
        return result is not null && Convert.ToBoolean(result);
    }

    public async Task RegisterInventoryMovementAsync(InventoryMovementInput input, string? userEmail = null)
    {
        var movementType = input.Type.Trim().ToUpperInvariant();
        if (movementType is not ("ENTRADA" or "SALIDA" or "AJUSTE"))
            throw new InvalidOperationException("Tipo de movimiento invalido.");

        var locationId = await EnsureInventoryLocationAsync();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            const string balanceSql = """
                SELECT COALESCE(Quantity, 0)
                FROM dbo.ExistenciasInventario
                WHERE ProductId = @ProductId AND InventoryLocationId = @LocationId;
                """;

            var current = Convert.ToDecimal(await ScalarInTransactionAsync(connection, transaction, balanceSql,
                new SqlParameter("@ProductId", input.ProductId),
                new SqlParameter("@LocationId", locationId)) ?? 0m);

            var next = movementType == "SALIDA" ? current - input.Quantity : current + input.Quantity;
            if (next < 0)
                throw new InvalidOperationException("La salida supera la existencia disponible.");

            await SetInventoryBalanceAsync(connection, transaction, input.ProductId, locationId, next);
            await AddInventoryMovementAsync(connection, transaction, input.ProductId, locationId, movementType, input.Quantity, input.Note);
            await transaction.CommitAsync();

            await AddAuditLogAsync($"INVENTARIO_MOVIMIENTO_{movementType}", $"Movimiento {movementType}: Producto ID {input.ProductId}, Cantidad {input.Quantity}", userEmail);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
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
              AND pt.Name IN (N'Producto terminado', N'Producto vendible')
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
        var sql = UseMySql
            ? """
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
                COALESCE(img.ImageUrl, '/img/products/producto-sin-imagen.svg') AS ImageUrl,
                COALESCE(img.AltText, p.Name) AS AltText,
                p.IsActive
            FROM Productos p
            INNER JOIN TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN UnidadesMedida um ON um.UnitMeasureId = p.UnitMeasureId
            INNER JOIN CategoriasProducto pc ON pc.ProductCategoryId = p.ProductCategoryId
            LEFT JOIN CategoriasProducto parent ON parent.ProductCategoryId = pc.ParentCategoryId
            LEFT JOIN ExistenciasInventario ib ON ib.ProductId = p.ProductId
            LEFT JOIN (
                SELECT pi.ProductId, pi.ImageUrl, pi.AltText
                FROM ImagenesProducto pi
                INNER JOIN (
                    SELECT ProductId, MIN(ProductImageId) AS ProductImageId
                    FROM ImagenesProducto
                    GROUP BY ProductId
                ) pick ON pick.ProductId = pi.ProductId AND pick.ProductImageId = pi.ProductImageId
            ) img ON img.ProductId = p.ProductId
            WHERE p.IsActive = 1
              AND pt.Name IN ('Producto terminado', 'Producto vendible')
            GROUP BY p.ProductId, p.Code, p.Name, p.Description, parent.Name, pc.Name,
                     p.UnitPrice, um.Code, img.ImageUrl, img.AltText, p.IsActive
            ORDER BY COALESCE(parent.Name, pc.Name), p.Name;
            """
            : """
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
              AND pt.Name IN (N'Producto terminado', N'Producto vendible')
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

        return (await QueryAsync(sql, reader => reader.GetString("Name")))
            .Where(name => name.Equals("Efectivo", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("SINPE", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("PayPal", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<object> DashboardAsync()
    {
        var sql = UseMySql
            ? """
            SELECT
                (SELECT COUNT(*) FROM Pedidos WHERE DATE(CreatedAt) = DATE(UTC_TIMESTAMP())) AS OrdersToday,
                (
                    SELECT COUNT(*)
                    FROM Pedidos o
                    INNER JOIN EstadosPedido os ON os.OrderStatusId = o.OrderStatusId
                    WHERE os.Name IN ('Confirmado', 'En produccion', 'Listo')
                ) AS InProduction,
                (SELECT COALESCE(SUM(Total), 0) FROM Ventas WHERE DATE(CreatedAt) = DATE(UTC_TIMESTAMP())) AS SalesToday,
                (
                    SELECT COUNT(*)
                    FROM Productos p
                    LEFT JOIN (
                        SELECT ProductId, COALESCE(SUM(Quantity), 0) AS Stock
                        FROM ExistenciasInventario
                        GROUP BY ProductId
                    ) b ON b.ProductId = p.ProductId
                    WHERE COALESCE(b.Stock, 0) <= p.MinStock
                      AND p.IsActive = 1
                ) AS LowStock;
            """
            : """
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
        var sql = UseMySql
            ? """
            SELECT
                c.CustomerId,
                c.FullName,
                c.Email,
                c.Phone,
                c.IsFrequent,
                c.TotalSpent,
                COALESCE(ca.AddressLine, '') AS AddressLine
            FROM Clientes c
            LEFT JOIN (
                SELECT d.CustomerId, d.AddressLine
                FROM DireccionesCliente d
                INNER JOIN (
                    SELECT CustomerId, MAX(CustomerAddressId) AS CustomerAddressId
                    FROM DireccionesCliente
                    GROUP BY CustomerId
                ) pick ON pick.CustomerId = d.CustomerId AND pick.CustomerAddressId = d.CustomerAddressId
            ) ca ON ca.CustomerId = c.CustomerId
            ORDER BY c.FullName;
            """
            : """
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
        await EnsureCommerceSchemaAsync();
        var sql = UseMySql
            ? """
              SELECT p.PromotionId, p.Name, p.StartDate, p.EndDate, p.DiscountRate, p.IsActive,
                     COALESCE((SELECT GROUP_CONCAT(pp.ProductId ORDER BY pp.ProductId) FROM ProductosPromocion pp WHERE pp.PromotionId = p.PromotionId), '') AS ProductIds,
                     COALESCE((SELECT GROUP_CONCAT(pc.CustomerId ORDER BY pc.CustomerId) FROM PromocionesClientes pc WHERE pc.PromotionId = p.PromotionId), '') AS CustomerIds
              FROM Promociones p
              ORDER BY p.IsActive DESC, p.EndDate DESC, p.Name;
              """
            : """
              SELECT p.PromotionId, p.Name, p.StartDate, p.EndDate, p.DiscountRate, p.IsActive,
                     COALESCE((SELECT STRING_AGG(CONVERT(varchar(20), pp.ProductId), ',') FROM dbo.ProductosPromocion pp WHERE pp.PromotionId = p.PromotionId), '') AS ProductIds,
                     COALESCE((SELECT STRING_AGG(CONVERT(varchar(20), pc.CustomerId), ',') FROM dbo.PromocionesClientes pc WHERE pc.PromotionId = p.PromotionId), '') AS CustomerIds
              FROM dbo.Promociones p
              ORDER BY p.IsActive DESC, p.EndDate DESC, p.Name;
              """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("PromotionId"),
            name = reader.GetString("Name"),
            startDate = reader.GetDateTime("StartDate").ToString("yyyy-MM-dd"),
            endDate = reader.GetDateTime("EndDate").ToString("yyyy-MM-dd"),
            discount = reader.GetDecimal("DiscountRate"),
            active = reader.GetBoolean("IsActive"),
            productIds = ParseIdList(reader.GetString("ProductIds")),
            customerIds = ParseIdList(reader.GetString("CustomerIds"))
        });
    }

    private static int[] ParseIdList(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

    private async Task EnsureCommerceSchemaAsync()
    {
        if (UseMySql)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS PromocionesClientes (
                    PromotionId int NOT NULL,
                    CustomerId int NOT NULL,
                    PRIMARY KEY (PromotionId, CustomerId)
                );
                CREATE TABLE IF NOT EXISTS Combos (
                    ComboId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    Name varchar(140) NOT NULL,
                    Description varchar(400) NULL,
                    SpecialPrice decimal(18,2) NOT NULL,
                    ImageUrl varchar(500) NULL,
                    IsActive tinyint(1) NOT NULL DEFAULT 1,
                    CreatedAt datetime NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                CREATE TABLE IF NOT EXISTS ComboProductos (
                    ComboId int NOT NULL,
                    ProductId int NOT NULL,
                    Quantity decimal(18,2) NOT NULL,
                    PRIMARY KEY (ComboId, ProductId)
                );
                CREATE TABLE IF NOT EXISTS VentaCombos (
                    SaleComboId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    SaleId int NOT NULL,
                    ComboId int NOT NULL,
                    Quantity decimal(18,2) NOT NULL,
                    UnitPrice decimal(18,2) NOT NULL,
                    DiscountAmount decimal(18,2) NOT NULL DEFAULT 0
                );
                """);
            return;
        }

        await ExecuteAsync("""
            IF OBJECT_ID(N'dbo.PromocionesClientes', N'U') IS NULL
                CREATE TABLE dbo.PromocionesClientes (PromotionId int NOT NULL, CustomerId int NOT NULL, CONSTRAINT PK_PromocionesClientes PRIMARY KEY (PromotionId, CustomerId));
            IF OBJECT_ID(N'dbo.Combos', N'U') IS NULL
                CREATE TABLE dbo.Combos (ComboId int IDENTITY(1,1) PRIMARY KEY, Name nvarchar(140) NOT NULL, Description nvarchar(400) NULL, SpecialPrice decimal(18,2) NOT NULL, ImageUrl nvarchar(500) NULL, IsActive bit NOT NULL DEFAULT 1, CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
            IF OBJECT_ID(N'dbo.ComboProductos', N'U') IS NULL
                CREATE TABLE dbo.ComboProductos (ComboId int NOT NULL, ProductId int NOT NULL, Quantity decimal(18,2) NOT NULL, CONSTRAINT PK_ComboProductos PRIMARY KEY (ComboId, ProductId));
            IF OBJECT_ID(N'dbo.VentaCombos', N'U') IS NULL
                CREATE TABLE dbo.VentaCombos (SaleComboId int IDENTITY(1,1) PRIMARY KEY, SaleId int NOT NULL, ComboId int NOT NULL, Quantity decimal(18,2) NOT NULL, UnitPrice decimal(18,2) NOT NULL, DiscountAmount decimal(18,2) NOT NULL DEFAULT 0);
            """);
    }

    public async Task<IReadOnlyList<ComboData>> CombosAsync(bool activeOnly = false)
    {
        await EnsureCommerceSchemaAsync();
        var comboSql = UseMySql
            ? "SELECT ComboId, Name, Description, SpecialPrice, ImageUrl, IsActive FROM Combos WHERE (@ActiveOnly = 0 OR IsActive = 1) ORDER BY IsActive DESC, Name;"
            : "SELECT ComboId, Name, Description, SpecialPrice, ImageUrl, IsActive FROM dbo.Combos WHERE (@ActiveOnly = 0 OR IsActive = 1) ORDER BY IsActive DESC, Name;";
        var combos = await QueryAsync(comboSql, reader => new ComboRow(
            reader.GetInt32("ComboId"), reader.GetString("Name"), reader.GetNullableString("Description") ?? "",
            reader.GetDecimal("SpecialPrice"), reader.GetNullableString("ImageUrl") ?? "/img/products/producto-sin-imagen.svg", reader.GetBoolean("IsActive")),
            new SqlParameter("@ActiveOnly", activeOnly));
        var result = new List<ComboData>();
        foreach (var combo in combos)
        {
            var itemSql = UseMySql
                ? """
                  SELECT cp.ProductId, cp.Quantity, p.Code, p.Name, p.UnitPrice,
                         COALESCE((SELECT ImageUrl FROM ImagenesProducto WHERE ProductId = p.ProductId ORDER BY IsPrimary DESC, SortOrder LIMIT 1), '/img/products/producto-sin-imagen.svg') AS ImageUrl
                  FROM ComboProductos cp INNER JOIN Productos p ON p.ProductId = cp.ProductId
                  WHERE cp.ComboId = @ComboId ORDER BY p.Name;
                  """
                : """
                  SELECT cp.ProductId, cp.Quantity, p.Code, p.Name, p.UnitPrice,
                         COALESCE((SELECT TOP 1 ImageUrl FROM dbo.ImagenesProducto WHERE ProductId = p.ProductId ORDER BY IsPrimary DESC, SortOrder), '/img/products/producto-sin-imagen.svg') AS ImageUrl
                  FROM dbo.ComboProductos cp INNER JOIN dbo.Productos p ON p.ProductId = cp.ProductId
                  WHERE cp.ComboId = @ComboId ORDER BY p.Name;
                  """;
            var items = await QueryAsync(itemSql, reader => new ComboProductData(
                reader.GetInt32("ProductId"), reader.GetDecimal("Quantity"), reader.GetString("Code"), reader.GetString("Name"), reader.GetDecimal("UnitPrice"), reader.GetString("ImageUrl")), new SqlParameter("@ComboId", combo.Id));
            var regularPrice = items.Sum(item => item.UnitPrice * item.Quantity);
            result.Add(new ComboData(combo.Id, combo.Name, combo.Description, combo.SpecialPrice, regularPrice, Math.Max(0, regularPrice - combo.SpecialPrice), combo.ImageUrl, combo.Active, items));
        }
        return result;
    }

    public async Task<int> SaveComboAsync(ComboInput input, string? userEmail = null)
    {
        await EnsureCommerceSchemaAsync();
        var name = input.Name?.Trim() ?? "";
        var items = (input.Items ?? []).Where(item => item.ProductId > 0 && item.Quantity > 0).GroupBy(item => item.ProductId).Select(group => new ComboItemInput(group.Key, group.Sum(item => item.Quantity))).ToArray();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Debe indicar el nombre del combo.");
        if (input.SpecialPrice <= 0) throw new InvalidOperationException("El precio especial debe ser mayor a cero.");
        if (items.Length == 0) throw new InvalidOperationException("Seleccione al menos un producto para el combo.");

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var item in items)
            {
                var validSql = UseMySql
                    ? "SELECT COUNT(1) FROM Productos p INNER JOIN TiposProducto t ON t.ProductTypeId=p.ProductTypeId WHERE p.ProductId=@ProductId AND p.IsActive=1 AND t.Name='Producto terminado';"
                    : "SELECT COUNT(1) FROM dbo.Productos p INNER JOIN dbo.TiposProducto t ON t.ProductTypeId=p.ProductTypeId WHERE p.ProductId=@ProductId AND p.IsActive=1 AND t.Name=N'Producto terminado';";
                if (Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, validSql, new SqlParameter("@ProductId", item.ProductId))) == 0)
                    throw new InvalidOperationException("Uno de los productos seleccionados no estÃ¡ disponible para venta.");
            }

            int comboId;
            if (input.Id is > 0)
            {
                var updateSql = UseMySql
                    ? "UPDATE Combos SET Name=@Name, Description=@Description, SpecialPrice=@SpecialPrice, ImageUrl=@ImageUrl, IsActive=@IsActive WHERE ComboId=@Id;"
                    : "UPDATE dbo.Combos SET Name=@Name, Description=@Description, SpecialPrice=@SpecialPrice, ImageUrl=@ImageUrl, IsActive=@IsActive WHERE ComboId=@Id;";
                await ExecuteInTransactionAsync(connection, transaction, updateSql, new SqlParameter("@Name", name), new SqlParameter("@Description", (object?)input.Description?.Trim() ?? DBNull.Value), new SqlParameter("@SpecialPrice", input.SpecialPrice), new SqlParameter("@ImageUrl", (object?)input.ImageUrl?.Trim() ?? DBNull.Value), new SqlParameter("@IsActive", input.IsActive), new SqlParameter("@Id", input.Id.Value));
                comboId = input.Id.Value;
            }
            else
            {
                var insertSql = UseMySql
                    ? "INSERT INTO Combos (Name,Description,SpecialPrice,ImageUrl,IsActive) VALUES (@Name,@Description,@SpecialPrice,@ImageUrl,@IsActive); SELECT LAST_INSERT_ID();"
                    : "INSERT INTO dbo.Combos (Name,Description,SpecialPrice,ImageUrl,IsActive) OUTPUT INSERTED.ComboId VALUES (@Name,@Description,@SpecialPrice,@ImageUrl,@IsActive);";
                comboId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, insertSql, new SqlParameter("@Name", name), new SqlParameter("@Description", (object?)input.Description?.Trim() ?? DBNull.Value), new SqlParameter("@SpecialPrice", input.SpecialPrice), new SqlParameter("@ImageUrl", (object?)input.ImageUrl?.Trim() ?? DBNull.Value), new SqlParameter("@IsActive", input.IsActive)));
            }
            var comboProductsTable = UseMySql ? "ComboProductos" : "dbo.ComboProductos";
            await ExecuteInTransactionAsync(connection, transaction, $"DELETE FROM {comboProductsTable} WHERE ComboId=@ComboId;", new SqlParameter("@ComboId", comboId));
            foreach (var item in items)
                await ExecuteInTransactionAsync(connection, transaction, $"INSERT INTO {comboProductsTable} (ComboId,ProductId,Quantity) VALUES (@ComboId,@ProductId,@Quantity);", new SqlParameter("@ComboId", comboId), new SqlParameter("@ProductId", item.ProductId), new SqlParameter("@Quantity", item.Quantity));
            await transaction.CommitAsync();
            await AddAuditLogAsync("CONFIGURAR_COMBO", $"Combo '{name}' configurado con {items.Length} productos", userEmail);
            return comboId;
        }
        catch { await transaction.RollbackAsync(); throw; }
    }

    public async Task ToggleComboAsync(int id, string? userEmail = null)
    {
        await EnsureCommerceSchemaAsync();
        var sql = UseMySql ? "UPDATE Combos SET IsActive=CASE WHEN IsActive=1 THEN 0 ELSE 1 END WHERE ComboId=@Id;" : "UPDATE dbo.Combos SET IsActive=CASE WHEN IsActive=1 THEN 0 ELSE 1 END WHERE ComboId=@Id;";
        await ExecuteAsync(sql, new SqlParameter("@Id", id));
        await AddAuditLogAsync("CONFIGURAR_COMBO", $"Combo #{id} cambiÃ³ de estado", userEmail);
    }

    public async Task DeleteComboAsync(int id, string? userEmail = null)
    {
        await EnsureCommerceSchemaAsync();
        var salesTable = UseMySql ? "VentaCombos" : "dbo.VentaCombos";
        if (Convert.ToInt32(await ScalarAsync($"SELECT COUNT(1) FROM {salesTable} WHERE ComboId=@Id;", new SqlParameter("@Id", id))) > 0)
            throw new InvalidOperationException("El combo ya tiene ventas asociadas y no se puede eliminar; puede desactivarlo.");
        var productTable = UseMySql ? "ComboProductos" : "dbo.ComboProductos";
        var comboTable = UseMySql ? "Combos" : "dbo.Combos";
        await ExecuteAsync($"DELETE FROM {productTable} WHERE ComboId=@Id; DELETE FROM {comboTable} WHERE ComboId=@Id;", new SqlParameter("@Id", id));
        await AddAuditLogAsync("ELIMINAR_COMBO", $"Combo #{id} eliminado", userEmail);
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

    public async Task<int> SaveUserAsync(UserInput input)
    {
        if (UseMySql)
            return await SaveUserMySqlAsync(input);

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @RoleId int = (SELECT RoleId FROM dbo.Roles WHERE RoleName = @RoleName);

            IF @RoleId IS NULL
            BEGIN
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (@RoleName, N'Rol operativo del sistema', 1);

                SET @RoleId = CONVERT(int, SCOPE_IDENTITY());
            END;

            IF EXISTS (
                SELECT 1
                FROM dbo.Usuarios
                WHERE LOWER(Email) = LOWER(@Email)
                  AND (@UserId IS NULL OR UserId <> @UserId)
            )
                THROW 50004, 'Ya existe un usuario con ese correo.', 1;

            DECLARE @SavedUserId int;

            IF @UserId IS NULL
            BEGIN
                INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
                VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, @AddressLine, 1, SYSUTCDATETIME());

                SET @SavedUserId = CONVERT(int, SCOPE_IDENTITY());
            END
            ELSE
            BEGIN
                UPDATE dbo.Usuarios
                SET RoleId = @RoleId,
                    FirstName = @FirstName,
                    LastName = @LastName,
                    Email = @Email,
                    Phone = @Phone,
                    AddressLine = @AddressLine,
                    PasswordHash = CASE WHEN NULLIF(@PasswordHash, N'') IS NULL THEN PasswordHash ELSE @PasswordHash END
                WHERE UserId = @UserId;

                SET @SavedUserId = @UserId;
            END;

            IF @RoleName = N'Cliente'
            BEGIN
                DECLARE @CustomerId int = (SELECT CustomerId FROM dbo.Clientes WHERE UserId = @SavedUserId);

                IF @CustomerId IS NULL
                    SET @CustomerId = (SELECT CustomerId FROM dbo.Clientes WHERE LOWER(Email) = LOWER(@Email));

                IF @CustomerId IS NULL
                BEGIN
                    INSERT INTO dbo.Clientes (UserId, FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
                    VALUES (@SavedUserId, CONCAT(@FirstName, N' ', @LastName), @Email, @Phone, 0, 0, SYSUTCDATETIME());

                    SET @CustomerId = CONVERT(int, SCOPE_IDENTITY());
                END
                ELSE
                BEGIN
                    UPDATE dbo.Clientes
                    SET UserId = @SavedUserId,
                        FullName = CONCAT(@FirstName, N' ', @LastName),
                        Email = @Email,
                        Phone = @Phone
                    WHERE CustomerId = @CustomerId;
                END;

                IF NULLIF(@AddressLine, N'') IS NOT NULL
                BEGIN
                    IF EXISTS (SELECT 1 FROM dbo.DireccionesCliente WHERE CustomerId = @CustomerId AND IsDefault = 1)
                    BEGIN
                        UPDATE dbo.DireccionesCliente
                        SET AddressLine = @AddressLine
                        WHERE CustomerId = @CustomerId AND IsDefault = 1;
                    END
                    ELSE
                    BEGIN
                        INSERT INTO dbo.DireccionesCliente (CustomerId, Label, AddressLine, Latitude, Longitude, IsDefault)
                        VALUES (@CustomerId, N'Principal', @AddressLine, 9.932500, -84.079600, 1);
                    END;
                END;
            END;

            COMMIT TRAN;

            SELECT @SavedUserId;
            """;

        return Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@UserId", (object?)input.Id ?? DBNull.Value),
            new SqlParameter("@RoleName", input.Role.Trim()),
            new SqlParameter("@FirstName", input.FirstName.Trim()),
            new SqlParameter("@LastName", input.LastName.Trim()),
            new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
            new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
            new SqlParameter("@AddressLine", (object?)input.Address?.Trim() ?? DBNull.Value),
            new SqlParameter("@PasswordHash", string.IsNullOrWhiteSpace(input.Password) ? "" : HashPassword(input.Password))));
    }

    public async Task ToggleUserAsync(int id)
    {
        const string sql = """
            UPDATE dbo.Usuarios
            SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END
            WHERE UserId = @UserId;
            """;

        await ExecuteAsync(sql, new SqlParameter("@UserId", id));
    }

    public async Task<AuthUser?> AuthenticateAsync(string email, string password)
    {
        var sql = UseMySql
            ? """
            SELECT u.Email, u.FirstName, u.LastName, u.PasswordHash, r.RoleName
            FROM Usuarios u
            INNER JOIN Roles r ON r.RoleId = u.RoleId
            WHERE LOWER(u.Email) = LOWER(@Email)
              AND u.IsActive = 1
            LIMIT 1;
            """
            : """
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
        if (UseMySql)
        {
            await RegisterCustomerMySqlAsync(input);
            return;
        }

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

    private async Task<int> SaveUserMySqlAsync(UserInput input)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var roleName = input.Role.Trim();
            var roleId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO Roles (RoleName, Description, IsSystemRole)
                SELECT @RoleName, 'Rol operativo del sistema', 1
                WHERE NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = @RoleName);

                SELECT RoleId FROM Roles WHERE RoleName = @RoleName;
                """,
                new SqlParameter("@RoleName", roleName)));

            var duplicate = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                SELECT COUNT(1)
                FROM Usuarios
                WHERE LOWER(Email) = LOWER(@Email)
                  AND (@UserId IS NULL OR UserId <> @UserId);
                """,
                new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                new SqlParameter("@UserId", (object?)input.Id ?? DBNull.Value))) > 0;

            if (duplicate)
                throw new InvalidOperationException("Ya existe un usuario con ese correo.");

            int savedUserId;
            var passwordHash = string.IsNullOrWhiteSpace(input.Password) ? "" : HashPassword(input.Password);
            if (input.Id is > 0)
            {
                await ExecuteInTransactionAsync(connection, transaction, """
                    UPDATE Usuarios
                    SET RoleId = @RoleId,
                        FirstName = @FirstName,
                        LastName = @LastName,
                        Email = @Email,
                        Phone = @Phone,
                        AddressLine = @AddressLine,
                        PasswordHash = CASE WHEN @PasswordHash = '' THEN PasswordHash ELSE @PasswordHash END
                    WHERE UserId = @UserId;
                    """,
                    new SqlParameter("@UserId", input.Id.Value),
                    new SqlParameter("@RoleId", roleId),
                    new SqlParameter("@FirstName", input.FirstName.Trim()),
                    new SqlParameter("@LastName", input.LastName.Trim()),
                    new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                    new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
                    new SqlParameter("@AddressLine", (object?)input.Address?.Trim() ?? DBNull.Value),
                    new SqlParameter("@PasswordHash", passwordHash));
                savedUserId = input.Id.Value;
            }
            else
            {
                savedUserId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    INSERT INTO Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
                    VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, @AddressLine, 1, UTC_TIMESTAMP());
                    SELECT LAST_INSERT_ID();
                    """,
                    new SqlParameter("@RoleId", roleId),
                    new SqlParameter("@FirstName", input.FirstName.Trim()),
                    new SqlParameter("@LastName", input.LastName.Trim()),
                    new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                    new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
                    new SqlParameter("@AddressLine", (object?)input.Address?.Trim() ?? DBNull.Value),
                    new SqlParameter("@PasswordHash", string.IsNullOrWhiteSpace(passwordHash) ? HashPassword("12345678") : passwordHash)));
            }

            if (string.Equals(roleName, "Cliente", StringComparison.OrdinalIgnoreCase))
            {
                var customerIdValue = await ScalarInTransactionAsync(connection, transaction, """
                    SELECT CustomerId FROM Clientes WHERE UserId = @UserId
                    UNION
                    SELECT CustomerId FROM Clientes WHERE LOWER(Email) = LOWER(@Email)
                    LIMIT 1;
                    """,
                    new SqlParameter("@UserId", savedUserId),
                    new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()));

                int customerId;
                if (customerIdValue is null || customerIdValue == DBNull.Value)
                {
                    customerId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                        INSERT INTO Clientes (UserId, FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
                        VALUES (@UserId, CONCAT(@FirstName, ' ', @LastName), @Email, @Phone, 0, 0, UTC_TIMESTAMP());
                        SELECT LAST_INSERT_ID();
                        """,
                        new SqlParameter("@UserId", savedUserId),
                        new SqlParameter("@FirstName", input.FirstName.Trim()),
                        new SqlParameter("@LastName", input.LastName.Trim()),
                        new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                        new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value)));
                }
                else
                {
                    customerId = Convert.ToInt32(customerIdValue);
                    await ExecuteInTransactionAsync(connection, transaction, """
                        UPDATE Clientes
                        SET UserId = @UserId,
                            FullName = CONCAT(@FirstName, ' ', @LastName),
                            Email = @Email,
                            Phone = @Phone
                        WHERE CustomerId = @CustomerId;
                        """,
                        new SqlParameter("@UserId", savedUserId),
                        new SqlParameter("@FirstName", input.FirstName.Trim()),
                        new SqlParameter("@LastName", input.LastName.Trim()),
                        new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                        new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
                        new SqlParameter("@CustomerId", customerId));
                }

                if (!string.IsNullOrWhiteSpace(input.Address))
                {
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO DireccionesCliente (CustomerId, Label, AddressLine, Latitude, Longitude, IsDefault)
                        VALUES (@CustomerId, 'Principal', @AddressLine, 9.932500, -84.079600, 1)
                        ON DUPLICATE KEY UPDATE AddressLine = VALUES(AddressLine);
                        """,
                        new SqlParameter("@CustomerId", customerId),
                        new SqlParameter("@AddressLine", input.Address.Trim()));
                }
            }

            await transaction.CommitAsync();
            return savedUserId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task RegisterCustomerMySqlAsync(RegisterCustomerInput input)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var roleId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                "SELECT RoleId FROM Roles WHERE RoleName = 'Cliente' LIMIT 1;"));
            var exists = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                "SELECT COUNT(1) FROM Usuarios WHERE LOWER(Email) = LOWER(@Email);",
                new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()))) > 0;
            if (exists)
                throw new InvalidOperationException("Ya existe un usuario con ese correo.");

            var userId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
                VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, @AddressLine, 1, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """,
                new SqlParameter("@RoleId", roleId),
                new SqlParameter("@FirstName", input.FirstName.Trim()),
                new SqlParameter("@LastName", input.LastName.Trim()),
                new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
                new SqlParameter("@PasswordHash", HashPassword(input.Password)),
                new SqlParameter("@AddressLine", (object?)input.AddressLine?.Trim() ?? DBNull.Value)));

            var customerId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO Clientes (UserId, FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
                VALUES (@UserId, CONCAT(@FirstName, ' ', @LastName), @Email, @Phone, 0, 0, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """,
                new SqlParameter("@UserId", userId),
                new SqlParameter("@FirstName", input.FirstName.Trim()),
                new SqlParameter("@LastName", input.LastName.Trim()),
                new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
                new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value)));

            if (!string.IsNullOrWhiteSpace(input.AddressLine))
            {
                await ExecuteInTransactionAsync(connection, transaction, """
                    INSERT INTO DireccionesCliente (CustomerId, Label, AddressLine, Latitude, Longitude, IsDefault)
                    VALUES (@CustomerId, 'Principal', @AddressLine, 9.932500, -84.079600, 1);
                    """,
                    new SqlParameter("@CustomerId", customerId),
                    new SqlParameter("@AddressLine", input.AddressLine.Trim()));
            }

            await ExecuteInTransactionAsync(connection, transaction, """
                INSERT INTO BitacoraAuditoria (UserId, LogType, Detail, CreatedAt)
                VALUES (@UserId, 'REGISTRO_USUARIO', 'Registro de cliente desde formulario web', UTC_TIMESTAMP());
                """,
                new SqlParameter("@UserId", userId));

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<object>> RolesAsync()
    {
        if (UseMySql)
        {
            await ExecuteAsync("""
                INSERT INTO Roles (RoleName, Description, IsSystemRole)
                SELECT 'Cajero', 'GestiÃ³n de caja, ventas y pedidos de mostrador', 1
                WHERE NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = 'Cajero');
                INSERT INTO Roles (RoleName, Description, IsSystemRole)
                SELECT 'Repostero', 'ProducciÃ³n, recetas e inventario operativo', 1
                WHERE NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = 'Repostero');
                INSERT INTO Roles (RoleName, Description, IsSystemRole)
                SELECT 'Supervisor', 'Seguimiento operativo, reportes y control de tienda', 1
                WHERE NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = 'Supervisor');
                INSERT INTO Roles (RoleName, Description, IsSystemRole)
                SELECT 'EncargadoRecetas', 'Validacion de recetas, materiales y disponibilidad de produccion', 1
                WHERE NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = 'EncargadoRecetas');
                """);

            const string mySql = """
                SELECT RoleId, RoleName, Description, IsSystemRole
                FROM Roles
                ORDER BY RoleName;
                """;

            return await QueryAsync(mySql, reader =>
            {
                var roleName = reader.GetString("RoleName");
                return new
                {
                    id = reader.GetInt32("RoleId"),
                    name = roleName,
                    description = NormalizeUiCopy(reader.GetString("Description")),
                    system = reader.GetBoolean("IsSystemRole"),
                    permissions = PermissionsForRole(roleName)
                };
            });
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'Cajero')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'Cajero', N'GestiÃ³n de caja, ventas y pedidos de mostrador', 1);

            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'Repostero')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'Repostero', N'ProducciÃ³n, recetas e inventario operativo', 1);

            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'Supervisor')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'Supervisor', N'Seguimiento operativo, reportes y control de tienda', 1);

            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'EncargadoRecetas')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'EncargadoRecetas', N'Validacion de recetas, materiales y disponibilidad de produccion', 1);

            SELECT RoleId, RoleName, Description, IsSystemRole
            FROM dbo.Roles
            ORDER BY RoleName;
            """;

        return await QueryAsync(sql, reader =>
        {
            var roleName = reader.GetString("RoleName");
            return new
            {
                id = reader.GetInt32("RoleId"),
                name = roleName,
                description = NormalizeUiCopy(reader.GetString("Description")),
                system = reader.GetBoolean("IsSystemRole"),
                permissions = PermissionsForRole(roleName)
            };
        });
    }

    private static string[] PermissionsForRole(string roleName)
    {
        var normalized = (roleName ?? "")
            .Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Aggregate(new StringBuilder(), (builder, ch) => builder.Append(char.ToLowerInvariant(ch)))
            .ToString();

        if (normalized.Contains("admin"))
            return new[]
            {
                "Dashboard", "Pedidos", "ProducciÃ³n", "Inventario", "Punto de venta",
                "Reportes", "BitÃ¡cora", "ConfiguraciÃ³n", "Usuarios", "Roles",
                "Contabilidad", "Marketing", "CatÃ¡logo", "Perfil"
            };

        if (normalized.Contains("staff"))
            return new[]
            {
                "Dashboard", "Pedidos", "ProducciÃ³n", "Inventario", "Punto de venta",
                "BitÃ¡cora", "ConfiguraciÃ³n", "CatÃ¡logo", "Perfil"
            };

        if (normalized.Contains("super"))
            return new[]
            {
                "Dashboard", "Pedidos", "ProducciÃ³n", "Inventario", "Punto de venta",
                "Reportes", "BitÃ¡cora", "Contabilidad", "Marketing", "Perfil"
            };

        if (normalized.Contains("caj"))
            return new[] { "Dashboard", "Pedidos", "Punto de venta", "CatÃ¡logo", "Perfil" };

        if (normalized.Contains("repost"))
            return new[] { "Dashboard", "ProducciÃ³n", "Inventario", "Pedidos", "Perfil" };

        if (normalized.Contains("encargadorecetas"))
            return new[] { "Dashboard", "Recetas", "ProducciÃ³n", "Inventario", "Pedidos", "Perfil" };

        if (normalized.Contains("cliente"))
            return new[] { "CatÃ¡logo", "Pedido rÃ¡pido", "Mis pedidos", "Seguimiento", "Perfil" };

        return roleName switch
        {
            "Admin" => new[]
            {
                "Dashboard", "Pedidos", "ProducciÃ³n", "Inventario", "Punto de venta",
                "Reportes", "BitÃ¡cora", "ConfiguraciÃ³n", "Usuarios", "Roles",
                "Contabilidad", "Marketing", "CatÃ¡logo", "Perfil"
            },
            "Staff" => new[]
            {
                "Dashboard", "Pedidos", "ProducciÃ³n", "Inventario", "Punto de venta",
                "BitÃ¡cora", "ConfiguraciÃ³n", "CatÃ¡logo", "Perfil"
            },
            "Supervisor" => new[]
            {
                "Dashboard", "Pedidos", "ProducciÃ³n", "Inventario", "Punto de venta",
                "Reportes", "BitÃ¡cora", "Contabilidad", "Marketing", "Perfil"
            },
            "Cajero" => new[]
            {
                "Dashboard", "Pedidos", "Punto de venta", "CatÃ¡logo", "Perfil"
            },
            "Repostero" => new[]
            {
                "Dashboard", "ProducciÃ³n", "Inventario", "Pedidos", "Perfil"
            },
            "EncargadoRecetas" => new[]
            {
                "Dashboard", "Recetas", "ProducciÃ³n", "Inventario", "Pedidos", "Perfil"
            },
            "Cliente" => new[]
            {
                "CatÃ¡logo", "Pedido rÃ¡pido", "Mis pedidos", "Seguimiento", "Perfil"
            },
            _ => new[] { "Perfil" }
        };
    }

    private static string NormalizeUiCopy(string value) => (value ?? "")
        .Replace("Gestion de", "GestiÃ³n de", StringComparison.Ordinal)
        .Replace("Produccion", "ProducciÃ³n", StringComparison.Ordinal)
        .Replace("Catalogo", "CatÃ¡logo", StringComparison.Ordinal)
        .Replace("Configuracion", "ConfiguraciÃ³n", StringComparison.Ordinal)
        .Replace("Bitacora", "BitÃ¡cora", StringComparison.Ordinal)
        .Replace("Pedido rapido", "Pedido rÃ¡pido", StringComparison.Ordinal);

    public async Task<IReadOnlyList<object>> PaymentMethodsAsync()
    {
        const string sql = """
            SELECT PaymentMethodId, Name, CommissionRate, IsActive
            FROM dbo.MetodosPago
            ORDER BY Name;
            """;

        var methods = await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("PaymentMethodId"),
            name = reader.GetString("Name"),
            commissionRate = reader.GetDecimal("CommissionRate"),
            active = reader.GetBoolean("IsActive"),
            account = reader.GetString("Name")
        });

        return methods
            .Where(method => method.name.Equals("Efectivo", StringComparison.OrdinalIgnoreCase)
                          || method.name.Equals("SINPE", StringComparison.OrdinalIgnoreCase)
                          || method.name.Equals("PayPal", StringComparison.OrdinalIgnoreCase))
            .Cast<object>()
            .ToList();
    }

    public async Task<int> SavePaymentMethodAsync(PaymentMethodInput input, string? userEmail = null)
    {
        var name = (input.Name ?? "").Trim();
        var account = string.IsNullOrWhiteSpace(input.Account) ? name : input.Account.Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Debe indicar el nombre de la forma de pago.");

        if (input.CommissionRate < 0)
            throw new InvalidOperationException("La comision debe ser mayor o igual a 0.");

        if (UseMySql)
        {
            var duplicateSql = input.Id is null
                ? "SELECT COUNT(1) FROM MetodosPago WHERE LOWER(Name) = LOWER(@Name);"
                : "SELECT COUNT(1) FROM MetodosPago WHERE LOWER(Name) = LOWER(@Name) AND PaymentMethodId <> @Id;";
            var duplicate = Convert.ToInt32(await ScalarAsync(duplicateSql,
                new SqlParameter("@Name", name),
                new SqlParameter("@Id", (object?)input.Id ?? DBNull.Value))) > 0;
            if (duplicate)
                throw new InvalidOperationException("Ya existe una forma de pago con ese nombre.");

            int mysqlId;
            if (input.Id is > 0)
            {
                await ExecuteAsync("""
                    UPDATE MetodosPago
                    SET Name = @Name,
                        CommissionRate = @CommissionRate,
                        IsActive = @IsActive
                    WHERE PaymentMethodId = @Id;
                    """,
                    new SqlParameter("@Id", input.Id.Value),
                    new SqlParameter("@Name", name),
                    new SqlParameter("@CommissionRate", input.CommissionRate),
                    new SqlParameter("@IsActive", input.IsActive));
                mysqlId = input.Id.Value;
            }
            else
            {
                mysqlId = Convert.ToInt32(await ScalarAsync("""
                    INSERT INTO MetodosPago (Name, CommissionRate, IsActive)
                    VALUES (@Name, @CommissionRate, @IsActive);
                    SELECT LAST_INSERT_ID();
                    """,
                    new SqlParameter("@Name", name),
                    new SqlParameter("@CommissionRate", input.CommissionRate),
                    new SqlParameter("@IsActive", input.IsActive)));
            }

            await ExecuteAsync("""
                INSERT INTO ConfiguracionesAplicacion (SettingKey, SettingValue)
                VALUES (CONCAT('paymentMethodAccount:', @PaymentMethodId), @Account)
                ON DUPLICATE KEY UPDATE SettingValue = VALUES(SettingValue);
                """,
                new SqlParameter("@PaymentMethodId", mysqlId),
                new SqlParameter("@Account", account));

            await AddAuditLogAsync("CONFIGURACION_POS", $"Forma de pago '{name}' configurada", userEmail);
            return mysqlId;
        }

        const string sql = """
            DECLARE @PaymentMethodId int;

            IF @Id IS NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM dbo.MetodosPago WHERE LOWER(Name) = LOWER(@Name))
                    THROW 50100, 'Ya existe una forma de pago con ese nombre.', 1;

                INSERT INTO dbo.MetodosPago (Name, CommissionRate, IsActive)
                VALUES (@Name, @CommissionRate, @IsActive);
                SET @PaymentMethodId = SCOPE_IDENTITY();
            END
            ELSE
            BEGIN
                IF EXISTS (SELECT 1 FROM dbo.MetodosPago WHERE LOWER(Name) = LOWER(@Name) AND PaymentMethodId <> @Id)
                    THROW 50101, 'Ya existe una forma de pago con ese nombre.', 1;

                UPDATE dbo.MetodosPago
                SET Name = @Name,
                    CommissionRate = @CommissionRate,
                    IsActive = @IsActive
                WHERE PaymentMethodId = @Id;

                SET @PaymentMethodId = @Id;
            END;

            MERGE dbo.ConfiguracionesAplicacion AS target
            USING (SELECT CONCAT(N'paymentMethodAccount:', @PaymentMethodId) AS SettingKey) AS source
            ON target.SettingKey = source.SettingKey
            WHEN MATCHED THEN UPDATE SET SettingValue = @Account
            WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (source.SettingKey, @Account);

            SELECT @PaymentMethodId;
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@Id", (object?)input.Id ?? DBNull.Value),
            new SqlParameter("@Name", name),
            new SqlParameter("@CommissionRate", input.CommissionRate),
            new SqlParameter("@IsActive", input.IsActive),
            new SqlParameter("@Account", account)));

        await AddAuditLogAsync("CONFIGURACION_POS", $"Forma de pago '{name}' configurada", userEmail);
        return id;
    }

    public async Task TogglePaymentMethodAsync(int id, string? userEmail = null)
    {
        const string sql = """
            UPDATE dbo.MetodosPago
            SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END
            WHERE PaymentMethodId = @Id;
            """;

        await ExecuteAsync(sql, new SqlParameter("@Id", id));
        await AddAuditLogAsync("CONFIGURACION_POS", $"Forma de pago ID {id} cambio de estado", userEmail);
    }

    public async Task<object> PosConfigAsync()
    {
        var methods = await PaymentMethodsAsync();
        const string sql = "SELECT SettingKey, SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey IN (N'iva', N'frequentCustomerDiscount', N'originName', N'originAddress', N'originLatitude', N'originLongitude');";
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
            activePromotionDiscount = await ActivePromotionDiscountAsync(),
            originName = settingText("originName", "BakeSmart Patri"),
            originAddress = settingText("originAddress", "San Jose, Costa Rica"),
            originLatitude = setting("originLatitude", 9.9142m),
            originLongitude = setting("originLongitude", -84.0734m),
            paymentMethods = methods
        };
    }

    private async Task<decimal> ActivePromotionDiscountAsync()
    {
        const string sql = """
            SELECT COALESCE(MAX(DiscountRate), 0)
            FROM dbo.Promociones
            WHERE IsActive = 1
              AND CAST(SYSUTCDATETIME() AS date) BETWEEN StartDate AND EndDate;
            """;

        return Convert.ToDecimal(await ScalarAsync(sql) ?? 0m);
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

    public async Task AddAuditLogAsync(string logType, string detail, string? userEmail = null)
    {
        if (UseMySql)
        {
            const string mySql = """
                INSERT INTO BitacoraAuditoria (UserId, LogType, Detail, CreatedAt)
                VALUES (
                    (SELECT UserId FROM Usuarios WHERE LOWER(Email) = LOWER(@UserEmail) LIMIT 1),
                    @LogType,
                    @Detail,
                    UTC_TIMESTAMP()
                );
                """;

            await ExecuteAsync(mySql,
                new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
                new SqlParameter("@LogType", logType),
                new SqlParameter("@Detail", detail));
            return;
        }

        const string sql = """
            DECLARE @UserId int;
            IF @UserEmail IS NOT NULL
                SELECT @UserId = UserId FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@UserEmail);

            INSERT INTO dbo.BitacoraAuditoria (UserId, LogType, Detail, CreatedAt)
            VALUES (@UserId, @LogType, @Detail, SYSUTCDATETIME());
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
            new SqlParameter("@LogType", logType),
            new SqlParameter("@Detail", detail));
    }

    public async Task<IReadOnlyList<object>> AuditLogsAsync()
    {
        if (UseMySql)
        {
            const string mySql = """
                SELECT
                    a.AuditLogId,
                    a.LogType,
                    a.Detail,
                    a.CreatedAt,
                    COALESCE(NULLIF(TRIM(CONCAT(COALESCE(u.FirstName, ''), ' ', COALESCE(u.LastName, ''))), ''), 'Sistema') AS UserName,
                    COALESCE(u.Email, 'sistema@bakesmart.local') AS UserEmail
                FROM BitacoraAuditoria a
                LEFT JOIN Usuarios u ON u.UserId = a.UserId
                ORDER BY a.CreatedAt DESC, a.AuditLogId DESC
                LIMIT 250;
                """;

            return await QueryAsync(mySql, reader => new
            {
                id = reader.GetInt32("AuditLogId"),
                type = reader.GetString("LogType"),
                detail = reader.GetString("Detail"),
                createdAt = reader.GetDateTime("CreatedAt").ToString("o"),
                userName = reader.GetString("UserName"),
                userEmail = reader.GetString("UserEmail")
            });
        }

        const string sql = """
            SELECT TOP 250
                a.AuditLogId,
                a.LogType,
                a.Detail,
                a.CreatedAt,
                COALESCE(NULLIF(CONCAT(u.FirstName, N' ', u.LastName), N' '), N'Sistema') AS UserName,
                COALESCE(u.Email, N'sistema@bakesmart.local') AS UserEmail
            FROM dbo.BitacoraAuditoria a
            LEFT JOIN dbo.Usuarios u ON u.UserId = a.UserId
            ORDER BY a.CreatedAt DESC, a.AuditLogId DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("AuditLogId"),
            type = reader.GetString("LogType"),
            detail = reader.GetString("Detail"),
            createdAt = reader.GetDateTime("CreatedAt").ToString("o"),
            userName = reader.GetString("UserName"),
            userEmail = reader.GetString("UserEmail")
        });
    }

    public async Task<bool> MarkCustomerFrequentAsync(int customerId, string? userEmail = null)
    {
        if (UseMySql)
        {
            var exists = Convert.ToInt32(await ScalarAsync(
                "SELECT COUNT(1) FROM Clientes WHERE CustomerId = @CustomerId;",
                new SqlParameter("@CustomerId", customerId))) > 0;
            if (!exists)
                throw new InvalidOperationException("El cliente no existe.");

            await ExecuteAsync("""
                UPDATE Clientes
                SET IsFrequent = CASE WHEN IsFrequent = 1 THEN 0 ELSE 1 END
                WHERE CustomerId = @CustomerId;
                """,
                new SqlParameter("@CustomerId", customerId));

            var mysqlIsFrequent = Convert.ToBoolean(await ScalarAsync(
                "SELECT IsFrequent FROM Clientes WHERE CustomerId = @CustomerId;",
                new SqlParameter("@CustomerId", customerId)));
            await AddAuditLogAsync("CREAR_CLIENTE_FRECUENTE", $"Cliente ID {customerId} cambio marca frecuente", userEmail);
            return mysqlIsFrequent;
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.Clientes WHERE CustomerId = @CustomerId)
                THROW 50110, 'El cliente no existe.', 1;

            UPDATE dbo.Clientes
            SET IsFrequent = CASE WHEN IsFrequent = 1 THEN 0 ELSE 1 END
            WHERE CustomerId = @CustomerId;
            """;

        await ExecuteAsync(sql, new SqlParameter("@CustomerId", customerId));
        var isFrequent = Convert.ToBoolean(await ScalarAsync(
            "SELECT IsFrequent FROM dbo.Clientes WHERE CustomerId = @CustomerId;",
            new SqlParameter("@CustomerId", customerId)));
        await AddAuditLogAsync("CREAR_CLIENTE_FRECUENTE", $"Cliente ID {customerId} cambio marca frecuente", userEmail);
        return isFrequent;
    }

    public async Task<int> SavePromotionAsync(PromotionInput input, string? userEmail = null)
    {
        await EnsureCommerceSchemaAsync();
        var name = (input.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Debe indicar el nombre del descuento.");
        if (input.Discount <= 0)
            throw new InvalidOperationException("El descuento debe ser mayor a 0.");
        if (input.StartDate.Date > input.EndDate.Date)
            throw new InvalidOperationException("La fecha final debe ser posterior a la fecha inicial.");

        var discount = NormalizePromotionDiscount(input.Discount);

        if (UseMySql)
        {
            var duplicateSql = input.Id is null
                ? "SELECT COUNT(1) FROM Promociones WHERE LOWER(Name) = LOWER(@Name);"
                : "SELECT COUNT(1) FROM Promociones WHERE LOWER(Name) = LOWER(@Name) AND PromotionId <> @Id;";
            var duplicate = Convert.ToInt32(await ScalarAsync(duplicateSql,
                new SqlParameter("@Name", name),
                new SqlParameter("@Id", (object?)input.Id ?? DBNull.Value))) > 0;
            if (duplicate)
                throw new InvalidOperationException("Ya existe un descuento con ese nombre.");

            int mysqlPromotionId;
            if (input.Id is > 0)
            {
                await ExecuteAsync("""
                    UPDATE Promociones
                    SET Name = @Name,
                        StartDate = @StartDate,
                        EndDate = @EndDate,
                        DiscountRate = @DiscountRate,
                        IsActive = @IsActive
                    WHERE PromotionId = @Id;
                    """,
                    new SqlParameter("@Id", input.Id.Value),
                    new SqlParameter("@Name", name),
                    new SqlParameter("@StartDate", input.StartDate.Date),
                    new SqlParameter("@EndDate", input.EndDate.Date),
                    new SqlParameter("@DiscountRate", discount),
                    new SqlParameter("@IsActive", input.IsActive));
                mysqlPromotionId = input.Id.Value;
            }
            else
            {
                mysqlPromotionId = Convert.ToInt32(await ScalarAsync("""
                    INSERT INTO Promociones (Name, StartDate, EndDate, DiscountRate, IsActive)
                    VALUES (@Name, @StartDate, @EndDate, @DiscountRate, @IsActive);
                    SELECT LAST_INSERT_ID();
                    """,
                    new SqlParameter("@Name", name),
                    new SqlParameter("@StartDate", input.StartDate.Date),
                    new SqlParameter("@EndDate", input.EndDate.Date),
                    new SqlParameter("@DiscountRate", discount),
                    new SqlParameter("@IsActive", input.IsActive)));
            }

            await SavePromotionAssignmentsAsync(mysqlPromotionId, input.ProductIds, input.CustomerIds);
            await AddAuditLogAsync("CONFIGURAR_DESCUENTO", $"Descuento '{name}' configurado", userEmail);
            return mysqlPromotionId;
        }

        const string sql = """
            DECLARE @PromotionId int;

            IF @Id IS NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM dbo.Promociones WHERE LOWER(Name) = LOWER(@Name))
                    THROW 50120, 'Ya existe un descuento con ese nombre.', 1;

                INSERT INTO dbo.Promociones (Name, StartDate, EndDate, DiscountRate, IsActive)
                VALUES (@Name, @StartDate, @EndDate, @DiscountRate, @IsActive);
                SET @PromotionId = SCOPE_IDENTITY();
            END
            ELSE
            BEGIN
                IF EXISTS (SELECT 1 FROM dbo.Promociones WHERE LOWER(Name) = LOWER(@Name) AND PromotionId <> @Id)
                    THROW 50121, 'Ya existe un descuento con ese nombre.', 1;

                UPDATE dbo.Promociones
                SET Name = @Name,
                    StartDate = @StartDate,
                    EndDate = @EndDate,
                    DiscountRate = @DiscountRate,
                    IsActive = @IsActive
                WHERE PromotionId = @Id;
                SET @PromotionId = @Id;
            END;

            SELECT @PromotionId;
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@Id", (object?)input.Id ?? DBNull.Value),
            new SqlParameter("@Name", name),
            new SqlParameter("@StartDate", input.StartDate.Date),
            new SqlParameter("@EndDate", input.EndDate.Date),
            new SqlParameter("@DiscountRate", discount),
            new SqlParameter("@IsActive", input.IsActive)));

        await SavePromotionAssignmentsAsync(id, input.ProductIds, input.CustomerIds);
        await AddAuditLogAsync("CONFIGURAR_DESCUENTO", $"Descuento '{name}' configurado", userEmail);
        return id;
    }

    private async Task SavePromotionAssignmentsAsync(int promotionId, IReadOnlyList<int>? productIds, IReadOnlyList<int>? customerIds)
    {
        var products = (productIds ?? []).Where(id => id > 0).Distinct().ToArray();
        var customers = (customerIds ?? []).Where(id => id > 0).Distinct().ToArray();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var productTable = UseMySql ? "ProductosPromocion" : "dbo.ProductosPromocion";
            var customerTable = UseMySql ? "PromocionesClientes" : "dbo.PromocionesClientes";
            await ExecuteInTransactionAsync(connection, transaction, $"DELETE FROM {productTable} WHERE PromotionId = @PromotionId;", new SqlParameter("@PromotionId", promotionId));
            await ExecuteInTransactionAsync(connection, transaction, $"DELETE FROM {customerTable} WHERE PromotionId = @PromotionId;", new SqlParameter("@PromotionId", promotionId));
            foreach (var productId in products)
                await ExecuteInTransactionAsync(connection, transaction, $"INSERT INTO {productTable} (PromotionId, ProductId) VALUES (@PromotionId, @ProductId);", new SqlParameter("@PromotionId", promotionId), new SqlParameter("@ProductId", productId));
            foreach (var customerId in customers)
                await ExecuteInTransactionAsync(connection, transaction, $"INSERT INTO {customerTable} (PromotionId, CustomerId) VALUES (@PromotionId, @CustomerId);", new SqlParameter("@PromotionId", promotionId), new SqlParameter("@CustomerId", customerId));
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> OrderBelongsToAsync(int orderId, string? customerEmail)
    {
        if (orderId <= 0 || string.IsNullOrWhiteSpace(customerEmail)) return false;
        var sql = UseMySql
            ? "SELECT COUNT(1) FROM Pedidos o INNER JOIN Clientes c ON c.CustomerId = o.CustomerId WHERE o.OrderId = @OrderId AND LOWER(c.Email) = LOWER(@Email);"
            : "SELECT COUNT(1) FROM dbo.Pedidos o INNER JOIN dbo.Clientes c ON c.CustomerId = o.CustomerId WHERE o.OrderId = @OrderId AND LOWER(c.Email) = LOWER(@Email);";
        return Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@OrderId", orderId),
            new SqlParameter("@Email", customerEmail.Trim()))) > 0;
    }

    internal static decimal NormalizePromotionDiscount(decimal value)
    {
        var normalized = value > 1 ? value / 100m : value;
        if (normalized <= 0)
            throw new InvalidOperationException("El descuento debe ser mayor a 0%.");
        if (normalized > 1)
            throw new InvalidOperationException("El descuento no puede superar el 100%.");
        return normalized;
    }

    public async Task TogglePromotionAsync(int id, string? userEmail = null)
    {
        if (id <= 0)
            throw new InvalidOperationException("La promociÃ³n no es vÃ¡lida.");

        if (UseMySql)
        {
            var exists = Convert.ToInt32(await ScalarAsync(
                "SELECT COUNT(1) FROM Promociones WHERE PromotionId = @Id;",
                new SqlParameter("@Id", id))) > 0;
            if (!exists)
                throw new InvalidOperationException("La promociÃ³n no existe.");

            await ExecuteAsync("""
                UPDATE Promociones
                SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END
                WHERE PromotionId = @Id;
                """, new SqlParameter("@Id", id));

            await AddAuditLogAsync("CONFIGURAR_DESCUENTO", $"PromociÃ³n ID {id} cambiÃ³ de estado", userEmail);
            return;
        }

        const string sql = """
            UPDATE dbo.Promociones
            SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END
            WHERE PromotionId = @Id;
            """;

        await ExecuteAsync(sql, new SqlParameter("@Id", id));
        await AddAuditLogAsync("CONFIGURAR_DESCUENTO", $"PromociÃ³n ID {id} cambiÃ³ de estado", userEmail);
    }

    public async Task<int> SendMarketingCampaignAsync(MarketingCampaignInput input, string? userEmail = null)
    {
        if (input.CustomerIds is null || input.CustomerIds.Count == 0)
            throw new InvalidOperationException("Debe seleccionar al menos un destinatario.");
        if (string.IsNullOrWhiteSpace(input.Message))
            throw new InvalidOperationException("Debe redactar el mensaje de la comunicaciÃ³n.");

        var subject = string.IsNullOrWhiteSpace(input.Subject) ? "PromociÃ³n BakeSmart" : input.Subject.Trim();
        var message = input.Message.Trim();
        var customerIds = input.CustomerIds.Where(id => id > 0).Distinct().ToArray();
        if (customerIds.Length == 0)
            throw new InvalidOperationException("Debe seleccionar al menos un destinatario vÃ¡lido.");
        if (subject.Length > 160)
            throw new InvalidOperationException("El asunto no puede superar 160 caracteres.");

        if (UseMySql)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS ComunicacionesMarketing
                (
                    CommunicationId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    Subject varchar(160) NOT NULL,
                    Message longtext NOT NULL,
                    RecipientCount int NOT NULL,
                    CreatedAt datetime NOT NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                CREATE TABLE IF NOT EXISTS ComunicacionesMarketingDestinatarios
                (
                    CommunicationRecipientId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    CommunicationId int NOT NULL,
                    CustomerId int NOT NULL,
                    CreatedAt datetime NOT NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                """);

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var mysqlCampaignId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    INSERT INTO ComunicacionesMarketing (Subject, Message, RecipientCount, CreatedAt)
                    VALUES (@Subject, @Message, @RecipientCount, UTC_TIMESTAMP());
                    SELECT LAST_INSERT_ID();
                    """,
                    new SqlParameter("@Subject", subject),
                    new SqlParameter("@Message", message),
                    new SqlParameter("@RecipientCount", customerIds.Length)));

                foreach (var customerId in customerIds)
                {
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO ComunicacionesMarketingDestinatarios (CommunicationId, CustomerId, CreatedAt)
                        SELECT @CommunicationId, @CustomerId, UTC_TIMESTAMP()
                        WHERE EXISTS (SELECT 1 FROM Clientes WHERE CustomerId = @CustomerId);
                        """,
                        new SqlParameter("@CommunicationId", mysqlCampaignId),
                        new SqlParameter("@CustomerId", customerId));
                }

                await transaction.CommitAsync();
                await AddAuditLogAsync("COMUNICACION_MARKETING", $"CampaÃ±a #{mysqlCampaignId} registrada para {customerIds.Length} clientes", userEmail);
                return mysqlCampaignId;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        const string sql = """
            IF OBJECT_ID(N'dbo.ComunicacionesMarketing', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ComunicacionesMarketing
                (
                    CommunicationId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    Subject nvarchar(160) NOT NULL,
                    Message nvarchar(max) NOT NULL,
                    RecipientCount int NOT NULL,
                    CreatedAt datetime2 NOT NULL
                );
            END;

            IF OBJECT_ID(N'dbo.ComunicacionesMarketingDestinatarios', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ComunicacionesMarketingDestinatarios
                (
                    CommunicationRecipientId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    CommunicationId int NOT NULL,
                    CustomerId int NOT NULL,
                    CreatedAt datetime2 NOT NULL
                );
            END;

            INSERT INTO dbo.ComunicacionesMarketing (Subject, Message, RecipientCount, CreatedAt)
            VALUES (@Subject, @Message, @RecipientCount, SYSUTCDATETIME());
            DECLARE @CommunicationId int = SCOPE_IDENTITY();

            INSERT INTO dbo.ComunicacionesMarketingDestinatarios (CommunicationId, CustomerId, CreatedAt)
            SELECT @CommunicationId, value, SYSUTCDATETIME()
            FROM OPENJSON(@RecipientsJson)
            WHERE EXISTS (SELECT 1 FROM dbo.Clientes WHERE CustomerId = value);

            SELECT @CommunicationId;
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@Subject", subject),
            new SqlParameter("@Message", message),
            new SqlParameter("@RecipientCount", customerIds.Length),
            new SqlParameter("@RecipientsJson", System.Text.Json.JsonSerializer.Serialize(customerIds))));

        await AddAuditLogAsync("COMUNICACION_MARKETING", $"CampaÃ±a #{id} registrada para {customerIds.Length} clientes", userEmail);
        return id;
    }

    public async Task<IReadOnlyList<MarketingRecipient>> MarketingRecipientsAsync(IEnumerable<int> customerIds)
    {
        var ids = customerIds.Where(id => id > 0).Distinct().ToArray();
        var table = UseMySql ? "Clientes" : "dbo.Clientes";
        var filter = ids.Length == 0
            ? string.Empty
            : $"AND CustomerId IN ({string.Join(",", ids.Select((_, index) => $"@Id{index}"))})";
        var parameters = ids.Select((id, index) => new SqlParameter($"@Id{index}", id)).ToArray();
        var rows = await QueryAsync($"""
            SELECT CustomerId, FullName, Email
            FROM {table}
            WHERE Email IS NOT NULL
              AND TRIM(Email) <> ''
              {filter};
            """, reader => new MarketingRecipient(
                reader.GetInt32("CustomerId"),
                reader.GetString("FullName"),
                reader.GetString("Email")), parameters);

        return rows
            .Where(recipient =>
                System.Net.Mail.MailAddress.TryCreate(recipient.Email, out var address)
                && address.Host.Contains('.', StringComparison.Ordinal)
                && !address.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task SubscribeNewsletterAsync(string email)
    {
        email = email.Trim().ToLowerInvariant();
        if (UseMySql)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS SuscriptoresNovedades
                (
                    SubscriberId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    Email varchar(254) NOT NULL,
                    IsActive bit NOT NULL DEFAULT 1,
                    CreatedAt datetime NOT NULL,
                    UNIQUE KEY UX_SuscriptoresNovedades_Email (Email)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                INSERT INTO SuscriptoresNovedades (Email, IsActive, CreatedAt)
                VALUES (@Email, 1, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE IsActive = 1;
                """, new SqlParameter("@Email", email));
            return;
        }

        await ExecuteAsync("""
            IF OBJECT_ID(N'dbo.SuscriptoresNovedades', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SuscriptoresNovedades
                (
                    SubscriberId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    Email nvarchar(254) NOT NULL UNIQUE,
                    IsActive bit NOT NULL CONSTRAINT DF_SuscriptoresNovedades_IsActive DEFAULT 1,
                    CreatedAt datetime2 NOT NULL
                );
            END;

            IF EXISTS (SELECT 1 FROM dbo.SuscriptoresNovedades WHERE LOWER(Email) = LOWER(@Email))
                UPDATE dbo.SuscriptoresNovedades SET IsActive = 1 WHERE LOWER(Email) = LOWER(@Email);
            ELSE
                INSERT INTO dbo.SuscriptoresNovedades (Email, IsActive, CreatedAt) VALUES (@Email, 1, SYSUTCDATETIME());
            """, new SqlParameter("@Email", email));
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

    public async Task<string> SendOrderToProductionAsync(int orderId, string? userEmail = null)
    {
        (string OrderStatus, string PaymentStatus)? state;
        try { state = await OrderWorkflowStateAsync(orderId); }
        catch (Exception ex) { throw new InvalidOperationException($"No se pudo consultar el pedido: {ex.GetBaseException().Message}", ex); }
        if (state is null) throw new InvalidOperationException("El pedido no existe.");
        if (!string.Equals(state.Value.PaymentStatus, "Pagado", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Debe completar el pago antes de enviar el pedido a Produccion.");

        var normalized = RemoveDiacritics(state.Value.OrderStatus).ToUpperInvariant();
        if (normalized == "PENDIENTE PRODUCCION") return "Pendiente produccion";
        if (normalized != "CONFIRMADO")
            throw new InvalidOperationException($"El pedido no puede enviarse a Produccion desde el estado '{state.Value.OrderStatus}'.");

        var readiness = await ProductionMaterialReadinessAsync(orderId);
        if (readiness.MissingRecipes.Count > 0)
            throw new InvalidOperationException($"No se puede enviar a Produccion: falta validar la receta de {string.Join(", ", readiness.MissingRecipes)}.");

        try { await EnsureOrderStatusAsync("Pendiente produccion"); }
        catch (Exception ex) { throw new InvalidOperationException($"No se pudo preparar el estado de Produccion: {ex.GetBaseException().Message}", ex); }
        try { await UpdateOrderStatusAsync(orderId, "Pendiente produccion", userEmail); }
        catch (Exception ex) { throw new InvalidOperationException($"No se pudo actualizar el pedido: {ex.GetBaseException().Message}", ex); }
        try { await AddAuditLogAsync("ENVIAR_A_PRODUCCION", $"Pedido #{orderId} enviado a la cola de Produccion", userEmail); } catch { }
        return "Pendiente produccion";
    }

    public async Task<string> AdvanceProductionOrderAsync(int orderId, string? userEmail = null)
    {
        var state = await OrderWorkflowStateAsync(orderId);
        if (state is null) throw new InvalidOperationException("El pedido no existe.");

        var normalized = RemoveDiacritics(state.Value.OrderStatus).ToUpperInvariant();
        var next = normalized switch
        {
            "PENDIENTE PRODUCCION" => "En produccion",
            "EN PRODUCCION" => "Listo",
            "LISTO" => throw new InvalidOperationException("El pedido ya esta listo para entrega."),
            _ => throw new InvalidOperationException("Este pedido no pertenece a la cola de Produccion.")
        };

        if (normalized == "PENDIENTE PRODUCCION")
            await ReserveProductionMaterialsAsync(orderId, userEmail);

        await EnsureOrderStatusAsync(next);
        await UpdateOrderStatusAsync(orderId, next, userEmail);
        await AddAuditLogAsync("AVANCE_PRODUCCION", $"Pedido #{orderId} avanzado a {next}", userEmail);
        return next;
    }

    public async Task<string> AdvanceOrderDeliveryAsync(int orderId, string? userEmail = null)
    {
        var state = await OrderWorkflowStateAsync(orderId);
        if (state is null) throw new InvalidOperationException("El pedido no existe.");

        var normalized = RemoveDiacritics(state.Value.OrderStatus).ToUpperInvariant();
        var next = normalized switch
        {
            "LISTO" => "En camino",
            "EN CAMINO" => "Entregado",
            "ENTREGADO" => throw new InvalidOperationException("El pedido ya fue entregado."),
            _ => throw new InvalidOperationException("Produccion debe marcar el pedido como listo antes de enviarlo a entrega.")
        };

        await EnsureOrderStatusAsync(next);
        await UpdateOrderStatusAsync(orderId, next, userEmail);
        await AddAuditLogAsync("AVANCE_ENTREGA", $"Pedido #{orderId} avanzado a {next}", userEmail);
        return next;
    }

    private async Task<(string OrderStatus, string PaymentStatus)?> OrderWorkflowStateAsync(int orderId)
    {
        const string sql = """
            SELECT os.Name AS OrderStatus, ps.Name AS PaymentStatus
            FROM dbo.Pedidos o
            INNER JOIN dbo.EstadosPedido os ON os.OrderStatusId = o.OrderStatusId
            INNER JOIN dbo.EstadosPago ps ON ps.PaymentStatusId = o.PaymentStatusId
            WHERE o.OrderId = @OrderId;
            """;
        var rows = await QueryAsync(sql, reader => (
            OrderStatus: reader.GetString("OrderStatus"),
            PaymentStatus: reader.GetString("PaymentStatus")),
            new SqlParameter("@OrderId", orderId));
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task EnsureOrderStatusAsync(string status)
    {
        if (UseMySql)
        {
            await ExecuteAsync("""
                INSERT INTO EstadosPedido (Name, SortOrder)
                SELECT @Status, COALESCE(MAX(SortOrder), 0) + 1
                FROM EstadosPedido
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM EstadosPedido existing
                    WHERE LOWER(existing.Name) = LOWER(@Status)
                );
                """, new SqlParameter("@Status", status));
            return;
        }

        await ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM dbo.EstadosPedido WHERE Name = @Status)
                INSERT INTO dbo.EstadosPedido (Name) VALUES (@Status);
            """, new SqlParameter("@Status", status));
    }

    public async Task UpdateOrderStatusAsync(int orderId, string status, string? userEmail = null)
    {
        if (UseMySql)
        {
            await ExecuteAsync("""
                UPDATE Pedidos o
                INNER JOIN EstadosPedido os ON LOWER(os.Name) = LOWER(@Status)
                SET o.OrderStatusId = os.OrderStatusId
                WHERE o.OrderId = @OrderId;
                """,
                new SqlParameter("@OrderId", orderId),
                new SqlParameter("@Status", status));

            // Algunas instalaciones antiguas aun no tienen la tabla de seguimiento.
            // El registro del evento es complementario y no debe revertir el cambio
            // operativo del pedido ni responder 500 despues de actualizarlo.
            try
            {
                await ExecuteAsync("""
                    INSERT INTO EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
                    SELECT @OrderId, os.OrderStatusId, CONCAT('Estado actualizado a ', os.Name), UTC_TIMESTAMP()
                    FROM EstadosPedido os
                    WHERE LOWER(os.Name) = LOWER(@Status)
                      AND EXISTS (SELECT 1 FROM Pedidos WHERE OrderId = @OrderId);
                    """,
                    new SqlParameter("@OrderId", orderId),
                    new SqlParameter("@Status", status));
            }
            catch { }

            var normalizedMySql = RemoveDiacritics(status).ToUpperInvariant();
            try { await AddAuditLogAsync(normalizedMySql.Contains("ENTREGADO") ? "ENTREGA_PEDIDO" : "ACTUALIZAR_ESTADO_PEDIDO", $"Pedido #{orderId} actualizado a {status}", userEmail); } catch { }
            return;
        }

        const string sql = """
            UPDATE o
            SET OrderStatusId = os.OrderStatusId
            FROM dbo.Pedidos o
            INNER JOIN dbo.EstadosPedido os ON os.Name COLLATE Latin1_General_CI_AI = @Status COLLATE Latin1_General_CI_AI
            WHERE o.OrderId = @OrderId;

            INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
            SELECT @OrderId, os.OrderStatusId, CONCAT(N'Estado actualizado a ', os.Name), SYSUTCDATETIME()
            FROM dbo.EstadosPedido os
            WHERE os.Name COLLATE Latin1_General_CI_AI = @Status COLLATE Latin1_General_CI_AI;
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@OrderId", orderId),
            new SqlParameter("@Status", status));

        var normalized = RemoveDiacritics(status).ToUpperInvariant();
        await AddAuditLogAsync(normalized.Contains("ENTREGADO") ? "ENTREGA_PEDIDO" : "ACTUALIZAR_ESTADO_PEDIDO", $"Pedido #{orderId} actualizado a {status}", userEmail);
    }

    public async Task MarkOrderPaidAsync(int orderId, string method, string? userEmail = null)
    {
        if (UseMySql)
        {
            // La confirmacion financiera es la fuente de verdad y debe persistir
            // aunque falle posteriormente la venta o el asiento contable. PayPal
            // puede reintentar el retorno y el webhook, por lo que este bloque es
            // deliberadamente pequeno e idempotente.
            var paymentMethodId = 0;
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await ExecuteInTransactionAsync(connection, transaction, """
                    INSERT INTO MetodosPago (Name, CommissionRate, IsActive)
                    SELECT @Method, 0, 1
                    WHERE NOT EXISTS (
                        SELECT 1 FROM MetodosPago WHERE LOWER(Name) = LOWER(@Method)
                    );
                    """, new SqlParameter("@Method", method));
                paymentMethodId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT PaymentMethodId
                    FROM MetodosPago
                    WHERE LOWER(Name) = LOWER(@Method)
                    LIMIT 1;
                    """, new SqlParameter("@Method", method)) ?? 0);
                if (paymentMethodId <= 0)
                    throw new InvalidOperationException("No hay metodos de pago activos.");

                var paidStatusId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT PaymentStatusId FROM EstadosPago WHERE Name = 'Pagado' LIMIT 1;") ?? 0);
                var confirmedStatusId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT OrderStatusId FROM EstadosPedido WHERE Name = 'Confirmado' LIMIT 1;") ?? 0);
                if (paidStatusId <= 0)
                    throw new InvalidOperationException("No existe el estado de pago Pagado.");

                var wasPaid = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT COUNT(1)
                    FROM Pedidos o
                    INNER JOIN EstadosPago ps ON ps.PaymentStatusId = o.PaymentStatusId
                    WHERE o.OrderId = @OrderId AND LOWER(ps.Name) = 'pagado';
                    """, new SqlParameter("@OrderId", orderId)) ?? 0) > 0;

                var wasPendingPayment = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT COUNT(1)
                    FROM Pedidos o
                    INNER JOIN EstadosPedido os ON os.OrderStatusId = o.OrderStatusId
                    WHERE o.OrderId = @OrderId AND LOWER(os.Name) = 'pendiente pago';
                    """, new SqlParameter("@OrderId", orderId)) ?? 0) > 0;

                var updatedRows = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    UPDATE Pedidos o
                    INNER JOIN EstadosPedido currentStatus ON currentStatus.OrderStatusId = o.OrderStatusId
                    SET o.PaymentStatusId = @PaidStatusId,
                        o.PaymentMethodId = @PaymentMethodId,
                        o.OrderStatusId = CASE
                            WHEN currentStatus.Name = 'Pendiente pago' AND @ConfirmedStatusId > 0 THEN @ConfirmedStatusId
                            ELSE o.OrderStatusId
                        END
                    WHERE o.OrderId = @OrderId;
                    SELECT ROW_COUNT();
                    """,
                    new SqlParameter("@OrderId", orderId),
                    new SqlParameter("@PaymentMethodId", paymentMethodId),
                    new SqlParameter("@PaidStatusId", paidStatusId),
                    new SqlParameter("@ConfirmedStatusId", confirmedStatusId)));
                if (updatedRows == 0)
                    throw new InvalidOperationException("El pedido no existe.");

                if (!wasPaid && wasPendingPayment && confirmedStatusId > 0)
                {
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
                        VALUES (@OrderId, @ConfirmedStatusId, 'Pago confirmado; pedido enviado a produccion', UTC_TIMESTAMP());
                        """,
                        new SqlParameter("@OrderId", orderId),
                        new SqlParameter("@ConfirmedStatusId", confirmedStatusId));
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            // La conciliacion operativa es reintentable y nunca puede deshacer
            // un pago ya confirmado por la pasarela.
            try
            {
                var cashAccountId = await EnsureAccountAsync("1-02", "Banco / SINPE / Tarjeta", "ACTIVO");
                var incomeAccountId = await EnsureAccountAsync("4-01", "Ingresos por ventas", "INGRESO");
                await using var reconcileConnection = CreateConnection();
                await reconcileConnection.OpenAsync();
                await using var reconcileTransaction = await reconcileConnection.BeginTransactionAsync();
                try
                {
                    var saleId = Convert.ToInt32(await ScalarInTransactionAsync(reconcileConnection, reconcileTransaction,
                        "SELECT SaleId FROM Ventas WHERE OrderId = @OrderId LIMIT 1;",
                        new SqlParameter("@OrderId", orderId)) ?? 0);
                    if (saleId == 0)
                    {
                        saleId = Convert.ToInt32(await ScalarInTransactionAsync(reconcileConnection, reconcileTransaction, """
                            INSERT INTO Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
                            SELECT OrderId, @PaymentMethodId, Subtotal, Tax, Total, UTC_TIMESTAMP()
                            FROM Pedidos WHERE OrderId = @OrderId;
                            SELECT LAST_INSERT_ID();
                            """, new SqlParameter("@OrderId", orderId), new SqlParameter("@PaymentMethodId", paymentMethodId)));
                    }
                    else
                    {
                        await ExecuteInTransactionAsync(reconcileConnection, reconcileTransaction,
                            "UPDATE Ventas SET PaymentMethodId = @PaymentMethodId WHERE SaleId = @SaleId;",
                            new SqlParameter("@PaymentMethodId", paymentMethodId), new SqlParameter("@SaleId", saleId));
                    }

                    var total = Convert.ToDecimal(await ScalarInTransactionAsync(reconcileConnection, reconcileTransaction,
                        "SELECT Total FROM Ventas WHERE SaleId = @SaleId;", new SqlParameter("@SaleId", saleId)) ?? 0m);
                    var entryId = Convert.ToInt32(await ScalarInTransactionAsync(reconcileConnection, reconcileTransaction, """
                        SELECT AccountingEntryId FROM AsientosContables
                        WHERE EntryType = 'VENTA' AND ReferenceTable = 'Ventas' AND ReferenceId = @SaleId
                        ORDER BY AccountingEntryId LIMIT 1;
                        """, new SqlParameter("@SaleId", saleId)) ?? 0);
                    if (total > 0 && entryId == 0)
                    {
                        entryId = Convert.ToInt32(await ScalarInTransactionAsync(reconcileConnection, reconcileTransaction, """
                            INSERT INTO AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                            VALUES ('VENTA', 'Ventas', @SaleId, CONCAT('Pago web pedido #', @OrderId), UTC_TIMESTAMP());
                            SELECT LAST_INSERT_ID();
                            """, new SqlParameter("@SaleId", saleId), new SqlParameter("@OrderId", orderId)));
                        await ExecuteInTransactionAsync(reconcileConnection, reconcileTransaction, """
                            INSERT INTO LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                            VALUES (@EntryId, @CashAccountId, @Total, 0), (@EntryId, @IncomeAccountId, 0, @Total);
                            """, new SqlParameter("@EntryId", entryId), new SqlParameter("@CashAccountId", cashAccountId),
                            new SqlParameter("@IncomeAccountId", incomeAccountId), new SqlParameter("@Total", total));
                    }
                    await reconcileTransaction.CommitAsync();
                }
                catch
                {
                    await reconcileTransaction.RollbackAsync();
                    throw;
                }
            }
            catch
            {
                // El cierre diario puede reconstruir esta informacion. No se
                // devuelve un falso fallo al cliente despues de cobrarle.
            }

            try { await AddAuditLogAsync("PAGO_PEDIDO", $"Pago confirmado para pedido #{orderId} con {method}", userEmail); } catch { }
            return;
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @PaymentMethodId int = (SELECT PaymentMethodId FROM dbo.MetodosPago WHERE Name = @Method);
            IF @PaymentMethodId IS NULL
                SELECT @PaymentMethodId = PaymentMethodId FROM dbo.MetodosPago WHERE Name = N'Tarjeta';
            IF @PaymentMethodId IS NULL
                SELECT TOP 1 @PaymentMethodId = PaymentMethodId FROM dbo.MetodosPago WHERE IsActive = 1 ORDER BY PaymentMethodId;

            DECLARE @PaidStatusId int = (SELECT PaymentStatusId FROM dbo.EstadosPago WHERE Name = N'Pagado');
            DECLARE @ConfirmedStatusId int = (SELECT OrderStatusId FROM dbo.EstadosPedido WHERE Name = N'Confirmado');
            DECLARE @Updated int = 0;

            UPDATE o
            SET PaymentStatusId = @PaidStatusId,
                PaymentMethodId = @PaymentMethodId,
                OrderStatusId = CASE
                    WHEN currentStatus.Name = N'Pendiente pago' THEN COALESCE(@ConfirmedStatusId, o.OrderStatusId)
                    ELSE o.OrderStatusId
                END
            FROM dbo.Pedidos o
            INNER JOIN dbo.EstadosPedido currentStatus ON currentStatus.OrderStatusId = o.OrderStatusId
            WHERE o.OrderId = @OrderId;

            SET @Updated = @@ROWCOUNT;

            INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
            SELECT @OrderId, @ConfirmedStatusId, N'Pago confirmado; pedido enviado a produccion', SYSUTCDATETIME()
            WHERE @Updated > 0 AND @ConfirmedStatusId IS NOT NULL;

            IF @Updated = 0
                THROW 50061, 'El pedido no existe.', 1;

            DECLARE @SaleId int = (SELECT TOP 1 SaleId FROM dbo.Ventas WHERE OrderId = @OrderId);
            IF @SaleId IS NULL
            BEGIN
                DECLARE @Subtotal decimal(18,2), @Tax decimal(18,2), @Total decimal(18,2);
                SELECT @Subtotal = Subtotal, @Tax = Tax, @Total = Total
                FROM dbo.Pedidos
                WHERE OrderId = @OrderId;

                INSERT INTO dbo.Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
                VALUES (@OrderId, @PaymentMethodId, @Subtotal, @Tax, @Total, SYSUTCDATETIME());
                SET @SaleId = SCOPE_IDENTITY();

                DECLARE @CashAccountId int;
                DECLARE @IncomeAccountId int;

                SELECT @CashAccountId = AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'1-02';
                IF @CashAccountId IS NULL
                BEGIN
                    INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                    VALUES (N'1-02', N'Banco / SINPE / Tarjeta', N'ACTIVO');
                    SET @CashAccountId = SCOPE_IDENTITY();
                END;

                SELECT @IncomeAccountId = AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'4-01';
                IF @IncomeAccountId IS NULL
                BEGIN
                    INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                    VALUES (N'4-01', N'Ingresos por ventas', N'INGRESO');
                    SET @IncomeAccountId = SCOPE_IDENTITY();
                END;

                IF @Total > 0
                BEGIN
                    INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                    VALUES (N'VENTA', N'Ventas', @SaleId, CONCAT(N'Pago web pedido #', @OrderId), SYSUTCDATETIME());
                    DECLARE @EntryId int = SCOPE_IDENTITY();

                    INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                    VALUES (@EntryId, @CashAccountId, @Total, 0), (@EntryId, @IncomeAccountId, 0, @Total);
                END;
            END;

            COMMIT TRAN;
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@OrderId", orderId),
            new SqlParameter("@Method", method));

        await AddAuditLogAsync("PAGO_PEDIDO", $"Pago confirmado para pedido #{orderId} con {method}", userEmail);
    }

    public async Task DeleteOrderAsync(int orderId, string? userEmail = null)
    {
        if (UseMySql)
        {
            var inventoryLocationId = await EnsureInventoryLocationAsync();

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var exists = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT COUNT(1) FROM Pedidos WHERE OrderId = @OrderId;",
                    new SqlParameter("@OrderId", orderId))) > 0;
                if (!exists)
                    throw new InvalidOperationException("El pedido no existe.");

                await ExecuteInTransactionAsync(connection, transaction, """
                    INSERT INTO ExistenciasInventario (ProductId, InventoryLocationId, Quantity, UpdatedAt)
                    SELECT ProductId, @InventoryLocationId, SUM(Quantity), UTC_TIMESTAMP()
                    FROM DetallePedido
                    WHERE OrderId = @OrderId
                    GROUP BY ProductId
                    ON DUPLICATE KEY UPDATE
                        Quantity = ExistenciasInventario.Quantity + VALUES(Quantity),
                        UpdatedAt = UTC_TIMESTAMP();

                    INSERT INTO MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt)
                    SELECT ProductId, @InventoryLocationId, 'ENTRADA', SUM(Quantity), CONCAT('Reversion por eliminacion pedido #', @OrderId), UTC_TIMESTAMP()
                    FROM DetallePedido
                    WHERE OrderId = @OrderId
                    GROUP BY ProductId;

                    DELETE csp
                    FROM PagosSesionCaja csp
                    INNER JOIN Ventas v ON v.SaleId = csp.SaleId
                    WHERE v.OrderId = @OrderId;

                    DELETE FROM Ventas WHERE OrderId = @OrderId;
                    DELETE FROM EventosSeguimientoPedido WHERE OrderId = @OrderId;
                    DELETE FROM DetallePedido WHERE OrderId = @OrderId;
                    DELETE FROM Pedidos WHERE OrderId = @OrderId;
                    """,
                    new SqlParameter("@OrderId", orderId),
                    new SqlParameter("@InventoryLocationId", inventoryLocationId));

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            await AddAuditLogAsync("ELIMINAR_PEDIDO", $"Pedido #{orderId} eliminado y stock restaurado", userEmail);
            return;
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            IF NOT EXISTS (SELECT 1 FROM dbo.Pedidos WHERE OrderId = @OrderId)
                THROW 50060, 'El pedido no existe.', 1;

            DECLARE @InventoryLocationId int;

            IF NOT EXISTS (SELECT 1 FROM dbo.UbicacionesInventario WHERE Name = N'Bodega principal')
                INSERT INTO dbo.UbicacionesInventario (Name, Description)
                VALUES (N'Bodega principal', N'Ubicacion principal de BakeSmart Patri');

            SELECT @InventoryLocationId = InventoryLocationId
            FROM dbo.UbicacionesInventario
            WHERE Name = N'Bodega principal';

            ;WITH Items AS (
                SELECT ProductId, SUM(Quantity) AS Quantity
                FROM dbo.DetallePedido
                WHERE OrderId = @OrderId
                GROUP BY ProductId
            )
            MERGE dbo.ExistenciasInventario AS target
            USING Items AS source
            ON target.ProductId = source.ProductId AND target.InventoryLocationId = @InventoryLocationId
            WHEN MATCHED THEN
                UPDATE SET Quantity = target.Quantity + source.Quantity, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ProductId, InventoryLocationId, Quantity)
                VALUES (source.ProductId, @InventoryLocationId, source.Quantity);

            INSERT INTO dbo.MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt)
            SELECT ProductId, @InventoryLocationId, N'ENTRADA', SUM(Quantity), CONCAT(N'Reversion por eliminacion pedido #', @OrderId), SYSUTCDATETIME()
            FROM dbo.DetallePedido
            WHERE OrderId = @OrderId
            GROUP BY ProductId;

            DELETE csp
            FROM dbo.PagosSesionCaja csp
            INNER JOIN dbo.Ventas v ON v.SaleId = csp.SaleId
            WHERE v.OrderId = @OrderId;

            DELETE FROM dbo.Ventas WHERE OrderId = @OrderId;
            DELETE FROM dbo.EventosSeguimientoPedido WHERE OrderId = @OrderId;
            DELETE FROM dbo.DetallePedido WHERE OrderId = @OrderId;
            DELETE FROM dbo.Pedidos WHERE OrderId = @OrderId;

            COMMIT TRAN;
            """;

        await ExecuteAsync(sql, new SqlParameter("@OrderId", orderId));
        await AddAuditLogAsync("ELIMINAR_PEDIDO", $"Pedido #{orderId} eliminado y stock restaurado", userEmail);
    }

    public async Task<object> AccountingOverviewAsync()
    {
        var sql = UseMySql
            ? """
            SELECT
                e.AccountingEntryId,
                e.EntryType,
                e.ReferenceTable,
                e.ReferenceId,
                COALESCE(
                    g.Description,
                    pp.Concept,
                    CONCAT('Venta POS pedido #', v.OrderId),
                    e.Note,
                    CONCAT(e.ReferenceTable, ' #', e.ReferenceId)
                ) AS EntryDetail,
                COALESCE(GROUP_CONCAT(CONCAT(a.AccountCode, ' - ', a.AccountName) ORDER BY l.Debit DESC, a.AccountCode SEPARATOR ' | '), 'Sin cuenta') AS AccountName,
                COALESCE(SUM(l.Debit), 0) AS Debit,
                COALESCE(SUM(l.Credit), 0) AS Credit,
                COALESCE(entryTotals.DebitTotal, 0) AS EntryDebitTotal,
                COALESCE(entryTotals.CreditTotal, 0) AS EntryCreditTotal,
                e.CreatedAt
            FROM AsientosContables e
            LEFT JOIN LineasAsientoContable l ON l.AccountingEntryId = e.AccountingEntryId
            LEFT JOIN CatalogoCuentas a ON a.AccountId = l.AccountId
            LEFT JOIN Gastos g ON e.ReferenceTable = 'Gastos' AND g.ExpenseId = e.ReferenceId
            LEFT JOIN PagosProveedor pp ON e.ReferenceTable = 'PagosProveedor' AND pp.SupplierPaymentId = e.ReferenceId
            LEFT JOIN Ventas v ON e.ReferenceTable = 'Ventas' AND v.SaleId = e.ReferenceId
            LEFT JOIN (
                SELECT AccountingEntryId, SUM(Debit) AS DebitTotal, SUM(Credit) AS CreditTotal
                FROM LineasAsientoContable
                GROUP BY AccountingEntryId
            ) entryTotals ON entryTotals.AccountingEntryId = e.AccountingEntryId
            GROUP BY e.AccountingEntryId, e.EntryType, e.ReferenceTable, e.ReferenceId, e.Note,
                     g.Description, pp.Concept, v.OrderId,
                     entryTotals.DebitTotal, entryTotals.CreditTotal, e.CreatedAt
            ORDER BY e.CreatedAt DESC, e.AccountingEntryId DESC
            LIMIT 150;
            """
            : """
            SELECT TOP 150
                e.AccountingEntryId,
                e.EntryType,
                e.ReferenceTable,
                e.ReferenceId,
                COALESCE(
                    g.Description,
                    pp.Concept,
                    CONCAT(N'Venta POS pedido #', v.OrderId),
                    e.Note,
                    CONCAT(e.ReferenceTable, N' #', e.ReferenceId)
                ) AS EntryDetail,
                COALESCE(STRING_AGG(a.AccountCode + N' - ' + a.AccountName, N' | ') WITHIN GROUP (ORDER BY l.Debit DESC, a.AccountCode), N'Sin cuenta') AS AccountName,
                COALESCE(SUM(l.Debit), 0) AS Debit,
                COALESCE(SUM(l.Credit), 0) AS Credit,
                COALESCE(entryTotals.DebitTotal, 0) AS EntryDebitTotal,
                COALESCE(entryTotals.CreditTotal, 0) AS EntryCreditTotal,
                e.CreatedAt
            FROM dbo.AsientosContables e
            LEFT JOIN dbo.LineasAsientoContable l ON l.AccountingEntryId = e.AccountingEntryId
            LEFT JOIN dbo.CatalogoCuentas a ON a.AccountId = l.AccountId
            LEFT JOIN dbo.Gastos g ON e.ReferenceTable = N'Gastos' AND g.ExpenseId = e.ReferenceId
            LEFT JOIN dbo.PagosProveedor pp ON e.ReferenceTable = N'PagosProveedor' AND pp.SupplierPaymentId = e.ReferenceId
            LEFT JOIN dbo.Ventas v ON e.ReferenceTable = N'Ventas' AND v.SaleId = e.ReferenceId
            LEFT JOIN (
                SELECT AccountingEntryId, SUM(Debit) AS DebitTotal, SUM(Credit) AS CreditTotal
                FROM dbo.LineasAsientoContable
                GROUP BY AccountingEntryId
            ) entryTotals ON entryTotals.AccountingEntryId = e.AccountingEntryId
            GROUP BY e.AccountingEntryId, e.EntryType, e.ReferenceTable, e.ReferenceId, e.Note,
                     g.Description, pp.Concept, v.OrderId,
                     entryTotals.DebitTotal, entryTotals.CreditTotal, e.CreatedAt
            ORDER BY e.CreatedAt DESC, e.AccountingEntryId DESC;
            """;

        var entries = await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("AccountingEntryId"),
            type = reader.GetString("EntryType"),
            referenceTable = reader.GetString("ReferenceTable"),
            referenceId = reader.GetInt32("ReferenceId"),
            detail = reader.GetString("EntryDetail"),
            account = reader.GetString("AccountName"),
            debit = reader.GetDecimal("Debit"),
            credit = reader.GetDecimal("Credit"),
            balanced = Math.Abs(reader.GetDecimal("EntryDebitTotal") - reader.GetDecimal("EntryCreditTotal")) <= 0.01m,
            createdAt = reader.GetDateTime("CreatedAt").ToString("o")
        });

        var expenseCountTask = ScalarAsync(UseMySql ? "SELECT COUNT(1) FROM Gastos" : "SELECT COUNT(1) FROM dbo.Gastos");
        var supplierPaymentCountTask = ScalarAsync(UseMySql ? "SELECT COUNT(1) FROM PagosProveedor" : "SELECT COUNT(1) FROM dbo.PagosProveedor");
        await Task.WhenAll(expenseCountTask, supplierPaymentCountTask);
        var expenseCount = Convert.ToInt32(await expenseCountTask);
        var supplierPaymentCount = Convert.ToInt32(await supplierPaymentCountTask);

        return new { entries, expensesCount = expenseCount, supplierPaymentsCount = supplierPaymentCount };
    }

    public async Task<int> RegisterExpenseAsync(AccountingExpenseInput input, string? userEmail = null)
    {
        if (string.IsNullOrWhiteSpace(input.Description))
            throw new InvalidOperationException("Debe indicar la descripciÃ³n del gasto.");
        if (input.Amount <= 0)
            throw new InvalidOperationException("El monto del gasto debe ser mayor a 0.");

        var accountTask = EnsureAccountAsync(input.Account, "Gastos operativos", "GASTO");
        var cashAccountTask = PaymentAssetAccountAsync(input.Method);
        var categoryTask = EnsureExpenseCategoryAsync("Operativo");
        var methodTask = EnsurePaymentMethodAsync(string.IsNullOrWhiteSpace(input.Method) ? "Transferencia" : input.Method.Trim());
        await Task.WhenAll(accountTask, cashAccountTask, categoryTask, methodTask);
        var accountId = await accountTask;
        var cashAccountId = await cashAccountTask;
        var categoryId = await categoryTask;
        var methodId = await methodTask;

        if (UseMySql)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var mysqlExpenseId = 0;
            try
            {
                mysqlExpenseId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO Gastos (ExpenseCategoryId, PaymentMethodId, Description, Amount, CreatedAt)
                VALUES (@CategoryId, @PaymentMethodId, @Description, @Amount, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """,
                new SqlParameter("@CategoryId", categoryId),
                new SqlParameter("@PaymentMethodId", methodId),
                new SqlParameter("@Description", input.Description.Trim()),
                new SqlParameter("@Amount", input.Amount)));
                var entryId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                VALUES ('GASTO', 'Gastos', @ExpenseId, @Description, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """, new SqlParameter("@ExpenseId", mysqlExpenseId), new SqlParameter("@Description", input.Description.Trim())));
                await ExecuteInTransactionAsync(connection, transaction, """
                INSERT INTO LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                VALUES (@EntryId, @AccountId, @Amount, 0), (@EntryId, @CashAccountId, 0, @Amount);
                """,
                new SqlParameter("@EntryId", entryId),
                new SqlParameter("@Amount", input.Amount),
                new SqlParameter("@AccountId", accountId),
                new SqlParameter("@CashAccountId", cashAccountId));
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            // La auditoría es secundaria: nunca debe convertir un gasto ya confirmado
            // en un error ni intentar revertir una transacción que ya hizo commit.
            try { await AddAuditLogAsync("CONTABILIDAD_GASTO", $"Gasto #{mysqlExpenseId} registrado por {input.Amount:N2}", userEmail); }
            catch { }
            return mysqlExpenseId;
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            INSERT INTO dbo.Gastos (ExpenseCategoryId, PaymentMethodId, Description, Amount, CreatedAt)
            VALUES (@CategoryId, @PaymentMethodId, @Description, @Amount, SYSUTCDATETIME());
            DECLARE @ExpenseId int = SCOPE_IDENTITY();

            INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
            VALUES (N'GASTO', N'Gastos', @ExpenseId, @Description, SYSUTCDATETIME());
            DECLARE @EntryId int = SCOPE_IDENTITY();

            INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
            VALUES (@EntryId, @AccountId, @Amount, 0), (@EntryId, @CashAccountId, 0, @Amount);

            COMMIT TRAN;
            SELECT @ExpenseId;
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@CategoryId", categoryId),
            new SqlParameter("@PaymentMethodId", methodId),
            new SqlParameter("@Description", input.Description.Trim()),
            new SqlParameter("@Amount", input.Amount),
            new SqlParameter("@AccountId", accountId),
            new SqlParameter("@CashAccountId", cashAccountId)));

        await AddAuditLogAsync("CONTABILIDAD_GASTO", $"Gasto #{id} registrado por {input.Amount:N2}", userEmail);
        return id;
    }

    public async Task<int> RegisterSupplierPaymentAsync(SupplierPaymentInput input, string? userEmail = null)
    {
        if (string.IsNullOrWhiteSpace(input.Supplier))
            throw new InvalidOperationException("Debe indicar el proveedor.");
        if (input.Amount <= 0)
            throw new InvalidOperationException("El monto del pago debe ser mayor a 0.");
        if (string.IsNullOrWhiteSpace(input.Method))
            throw new InvalidOperationException("MÃ©todo de pago no vÃ¡lido.");

        var accountTask = EnsureAccountAsync(input.Account, "Cuentas por pagar", "PASIVO");
        var cashAccountTask = PaymentAssetAccountAsync(input.Method);
        var supplierTask = EnsureSupplierAsync(input.Supplier);
        var methodTask = EnsurePaymentMethodAsync(input.Method);
        await Task.WhenAll(accountTask, cashAccountTask, supplierTask, methodTask);
        var accountId = await accountTask;
        var cashAccountId = await cashAccountTask;
        var supplierId = await supplierTask;
        var methodId = await methodTask;

        if (UseMySql)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var mysqlSupplierPaymentId = 0;
            try
            {
                mysqlSupplierPaymentId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO PagosProveedor (SupplierId, PaymentMethodId, Concept, Amount, DueDate, PaidAt, CreatedAt)
                VALUES (@SupplierId, @PaymentMethodId, @Concept, @Amount, DATE(UTC_TIMESTAMP()), DATE(UTC_TIMESTAMP()), UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """, new SqlParameter("@SupplierId", supplierId), new SqlParameter("@PaymentMethodId", methodId),
                new SqlParameter("@Concept", $"Pago a {input.Supplier.Trim()}"), new SqlParameter("@Amount", input.Amount)));
                var entryId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                INSERT INTO AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                VALUES ('PAGO_PROVEEDOR', 'PagosProveedor', @PaymentId, @Concept, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """, new SqlParameter("@PaymentId", mysqlSupplierPaymentId), new SqlParameter("@Concept", $"Pago a {input.Supplier.Trim()}")));
                await ExecuteInTransactionAsync(connection, transaction, """
                INSERT INTO LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                VALUES (@EntryId, @AccountId, @Amount, 0), (@EntryId, @CashAccountId, 0, @Amount);
                """,
                new SqlParameter("@EntryId", entryId),
                new SqlParameter("@Amount", input.Amount),
                new SqlParameter("@AccountId", accountId),
                new SqlParameter("@CashAccountId", cashAccountId));
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            try { await AddAuditLogAsync("CONTABILIDAD_PAGO_PROVEEDOR", $"Pago proveedor #{mysqlSupplierPaymentId} registrado por {input.Amount:N2}", userEmail); }
            catch { }
            return mysqlSupplierPaymentId;
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            INSERT INTO dbo.PagosProveedor (SupplierId, PaymentMethodId, Concept, Amount, DueDate, PaidAt, CreatedAt)
            VALUES (@SupplierId, @PaymentMethodId, @Concept, @Amount, CAST(SYSUTCDATETIME() AS date), CAST(SYSUTCDATETIME() AS date), SYSUTCDATETIME());
            DECLARE @PaymentId int = SCOPE_IDENTITY();

            INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
            VALUES (N'PAGO_PROVEEDOR', N'PagosProveedor', @PaymentId, @Concept, SYSUTCDATETIME());
            DECLARE @EntryId int = SCOPE_IDENTITY();

            INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
            VALUES (@EntryId, @AccountId, @Amount, 0), (@EntryId, @CashAccountId, 0, @Amount);

            COMMIT TRAN;
            SELECT @PaymentId;
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@SupplierId", supplierId),
            new SqlParameter("@PaymentMethodId", methodId),
            new SqlParameter("@Concept", $"Pago a {input.Supplier.Trim()}"),
            new SqlParameter("@Amount", input.Amount),
            new SqlParameter("@AccountId", accountId),
            new SqlParameter("@CashAccountId", cashAccountId)));

        await AddAuditLogAsync("CONTABILIDAD_PAGO_PROVEEDOR", $"Pago proveedor #{id} registrado por {input.Amount:N2}", userEmail);
        return id;
    }

    public async Task<object> ReconcilePosAsync(string? userEmail = null)
    {
        if (UseMySql)
        {
            var cashAccountTask = EnsureAccountAsync("1-02", "Banco / SINPE / Tarjeta", "ACTIVO");
            var incomeAccountTask = EnsureAccountAsync("4-01", "Ingresos por ventas", "INGRESO");
            await Task.WhenAll(cashAccountTask, incomeAccountTask);
            var cashAccountId = await cashAccountTask;
            var incomeAccountId = await incomeAccountTask;
            // Recupera ventas que no llegaron a crearse después de que la
            // pasarela confirmó el pago. Esto hace que el webhook y la
            // conciliación sean complementarios y reintentables.
            var recoveredSales = Convert.ToInt32(await ScalarAsync("""
                INSERT INTO Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
                SELECT o.OrderId, o.PaymentMethodId, o.Subtotal, o.Tax, o.Total, UTC_TIMESTAMP()
                FROM Pedidos o
                INNER JOIN EstadosPago ep ON ep.PaymentStatusId = o.PaymentStatusId
                WHERE LOWER(ep.Name) = 'pagado'
                  AND o.PaymentMethodId IS NOT NULL
                  AND o.Total > 0
                  AND NOT EXISTS (SELECT 1 FROM Ventas v WHERE v.OrderId = o.OrderId);
                SELECT ROW_COUNT();
                """));
            var rows = await QueryAsync("""
                SELECT
                    v.SaleId,
                    v.Total,
                    e.AccountingEntryId,
                    COALESCE(entryLines.LineCount, 0) AS LineCount,
                    COALESCE(entryLines.DebitTotal, 0) AS DebitTotal,
                    COALESCE(entryLines.CreditTotal, 0) AS CreditTotal
                FROM Ventas v
                LEFT JOIN (
                    SELECT ReferenceId, MIN(AccountingEntryId) AS AccountingEntryId
                    FROM AsientosContables
                    WHERE ReferenceTable = 'Ventas'
                    GROUP BY ReferenceId
                ) e ON e.ReferenceId = v.SaleId
                LEFT JOIN (
                    SELECT AccountingEntryId, COUNT(1) AS LineCount, SUM(Debit) AS DebitTotal, SUM(Credit) AS CreditTotal
                    FROM LineasAsientoContable
                    GROUP BY AccountingEntryId
                ) entryLines ON entryLines.AccountingEntryId = e.AccountingEntryId
                WHERE v.Total > 0
                  AND (
                    e.AccountingEntryId IS NULL
                    OR COALESCE(entryLines.LineCount, 0) < 2
                    OR ABS(COALESCE(entryLines.DebitTotal, 0) - v.Total) > 0.01
                    OR ABS(COALESCE(entryLines.CreditTotal, 0) - v.Total) > 0.01
                  );
                """, reader => new
                {
                    saleId = reader.GetInt32("SaleId"),
                    total = reader.GetDecimal("Total"),
                    entryId = reader.GetNullableInt32("AccountingEntryId")
                });

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                foreach (var pendingRow in rows)
                {
                    var entryId = pendingRow.entryId ?? Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                        INSERT INTO AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                        VALUES ('VENTA', 'Ventas', @SaleId, CONCAT('Asiento generado por conciliaciÃ³n POS venta #', @SaleId), UTC_TIMESTAMP());
                        SELECT LAST_INSERT_ID();
                        """, new SqlParameter("@SaleId", pendingRow.saleId)));

                    await ExecuteInTransactionAsync(connection, transaction,
                        "DELETE FROM LineasAsientoContable WHERE AccountingEntryId = @EntryId;",
                        new SqlParameter("@EntryId", entryId));
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                        VALUES (@EntryId, @CashAccountId, @Total, 0), (@EntryId, @IncomeAccountId, 0, @Total);
                        """,
                        new SqlParameter("@EntryId", entryId),
                        new SqlParameter("@CashAccountId", cashAccountId),
                        new SqlParameter("@IncomeAccountId", incomeAccountId),
                        new SqlParameter("@Total", pendingRow.total));
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            var reviewedTask = ScalarAsync("SELECT COUNT(1) FROM Ventas;");
            var issuesTask = ScalarAsync("SELECT COUNT(1) FROM Ventas WHERE Total < 0;");
            await Task.WhenAll(reviewedTask, issuesTask);
            var reviewed = Convert.ToInt32(await reviewedTask);
            var issues = Convert.ToInt32(await issuesTask);
            await AddAuditLogAsync("CONCILIACION_POS", $"Conciliación: {reviewed} ventas revisadas, {recoveredSales} ventas recuperadas, {rows.Count} asientos reparados, {issues} diferencias", userEmail);
            return new { status = issues == 0 ? "Correcto" : "Con diferencias", reviewed, issues, generated = rows.Count, recovered = recoveredSales };
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @CashAccountId int;
            DECLARE @IncomeAccountId int;

            SELECT @CashAccountId = AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'1-02';
            IF @CashAccountId IS NULL
            BEGIN
                INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                VALUES (N'1-02', N'Banco / SINPE / Tarjeta', N'ACTIVO');
                SET @CashAccountId = SCOPE_IDENTITY();
            END;

            SELECT @IncomeAccountId = AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'4-01';
            IF @IncomeAccountId IS NULL
            BEGIN
                INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                VALUES (N'4-01', N'Ingresos por ventas', N'INGRESO');
                SET @IncomeAccountId = SCOPE_IDENTITY();
            END;

            DECLARE @Pending TABLE
            (
                SaleId int NOT NULL,
                Total decimal(18,2) NOT NULL,
                AccountingEntryId int NULL
            );

            INSERT INTO @Pending (SaleId, Total, AccountingEntryId)
            SELECT v.SaleId, v.Total, e.AccountingEntryId
            FROM dbo.Ventas v
            OUTER APPLY
            (
                SELECT TOP 1 entry.AccountingEntryId
                FROM dbo.AsientosContables entry
                WHERE entry.ReferenceTable = N'Ventas' AND entry.ReferenceId = v.SaleId
                ORDER BY entry.AccountingEntryId
            ) e
            OUTER APPLY
            (
                SELECT
                    COUNT(1) AS LineCount,
                    COALESCE(SUM(line.Debit), 0) AS DebitTotal,
                    COALESCE(SUM(line.Credit), 0) AS CreditTotal
                FROM dbo.LineasAsientoContable line
                WHERE line.AccountingEntryId = e.AccountingEntryId
            ) totals
            WHERE v.Total > 0
              AND (
                    e.AccountingEntryId IS NULL
                    OR totals.LineCount < 2
                    OR ABS(totals.DebitTotal - v.Total) > 0.01
                    OR ABS(totals.CreditTotal - v.Total) > 0.01
                  );

            DECLARE @SaleId int;
            DECLARE @Total decimal(18,2);
            DECLARE @EntryId int;
            DECLARE @Generated int = 0;

            DECLARE pending_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT SaleId, Total, AccountingEntryId FROM @Pending;

            OPEN pending_cursor;
            FETCH NEXT FROM pending_cursor INTO @SaleId, @Total, @EntryId;

            WHILE @@FETCH_STATUS = 0
            BEGIN
                IF @EntryId IS NULL
                BEGIN
                    INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                    VALUES (N'VENTA', N'Ventas', @SaleId, CONCAT(N'Asiento generado por conciliaciÃ³n POS venta #', @SaleId), SYSUTCDATETIME());

                    SET @EntryId = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    DELETE FROM dbo.LineasAsientoContable
                    WHERE AccountingEntryId = @EntryId;
                END;

                INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                VALUES (@EntryId, @CashAccountId, @Total, 0), (@EntryId, @IncomeAccountId, 0, @Total);

                SET @Generated += 1;

                FETCH NEXT FROM pending_cursor INTO @SaleId, @Total, @EntryId;
            END;

            CLOSE pending_cursor;
            DEALLOCATE pending_cursor;

            COMMIT TRAN;

            SELECT
                COUNT(1) AS Reviewed,
                COALESCE(SUM(CASE WHEN v.Total < 0 THEN 1 ELSE 0 END), 0) AS Issues,
                @Generated AS Generated
            FROM dbo.Ventas v
            OUTER APPLY
            (
                SELECT TOP 1 entry.AccountingEntryId
                FROM dbo.AsientosContables entry
                WHERE entry.ReferenceTable = N'Ventas' AND entry.ReferenceId = v.SaleId
                ORDER BY entry.AccountingEntryId
            ) e;
            """;

        var row = (await QueryAsync(sql, reader => new
        {
            reviewed = reader.GetInt32("Reviewed"),
            issues = reader.GetInt32("Issues"),
            generated = reader.GetInt32("Generated")
        })).FirstOrDefault() ?? new { reviewed = 0, issues = 0, generated = 0 };

        await AddAuditLogAsync("CONCILIACION_POS", $"ConciliaciÃ³n POS: {row.reviewed} ventas revisadas, {row.generated} asientos reparados, {row.issues} diferencias", userEmail);
        return new { status = row.issues == 0 ? "Correcto" : "Con diferencias", row.reviewed, row.issues, row.generated };
    }

    public async Task<object> DailyAccountingCloseAsync(string? userEmail = null)
        => await AccountingCloseAsync("DIARIO", userEmail);

    public async Task<object> AccountingCloseAsync(string closeType, string? userEmail = null)
    {
        var normalizedType = RemoveDiacritics(string.IsNullOrWhiteSpace(closeType) ? "DIARIO" : closeType.Trim()).ToUpperInvariant();
        if (normalizedType is not ("DIARIO" or "SEMANAL" or "MENSUAL"))
            throw new InvalidOperationException("Tipo de cierre no vÃ¡lido.");

        // Un cierre nunca debe congelar cifras antes de recuperar ventas o
        // asientos pendientes provenientes de pagos confirmados.
        await ReconcilePosAsync(userEmail);

        if (UseMySql)
        {
            var today = DateTime.UtcNow.Date;
            var start = normalizedType switch
            {
                "SEMANAL" => today.AddDays(-6),
                "MENSUAL" => new DateTime(today.Year, today.Month, 1),
                _ => today
            };

            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS CierresContables
                (
                    AccountingCloseId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    CloseType varchar(24) NOT NULL,
                    PeriodStart date NOT NULL,
                    PeriodEnd date NOT NULL,
                    TotalSales decimal(18,2) NOT NULL,
                    TotalExpenses decimal(18,2) NOT NULL,
                    TotalSupplierPayments decimal(18,2) NOT NULL,
                    CreatedAt datetime NOT NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                """);

            var mysqlCloseId = Convert.ToInt32(await ScalarAsync("""
                INSERT INTO CierresContables (CloseType, PeriodStart, PeriodEnd, TotalSales, TotalExpenses, TotalSupplierPayments, CreatedAt)
                SELECT
                    @CloseType,
                    @Start,
                    @Today,
                    COALESCE((SELECT SUM(Total) FROM Ventas WHERE DATE(CreatedAt) BETWEEN @Start AND @Today), 0),
                    COALESCE((SELECT SUM(Amount) FROM Gastos WHERE DATE(CreatedAt) BETWEEN @Start AND @Today), 0),
                    COALESCE((SELECT SUM(Amount) FROM PagosProveedor WHERE DATE(CreatedAt) BETWEEN @Start AND @Today), 0),
                    UTC_TIMESTAMP();
                SELECT LAST_INSERT_ID();
                """,
                new SqlParameter("@CloseType", normalizedType),
                new SqlParameter("@Start", start),
                new SqlParameter("@Today", today)));

            await AddAuditLogAsync("CIERRE_CONTABLE", $"Cierre contable {normalizedType.ToLowerInvariant()} #{mysqlCloseId} generado", userEmail);
            return new { closeId = mysqlCloseId, type = normalizedType, count = 1 };
        }

        const string sql = """
            IF OBJECT_ID(N'dbo.CierresContables', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CierresContables
                (
                    AccountingCloseId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    CloseType nvarchar(24) NOT NULL,
                    PeriodStart date NOT NULL,
                    PeriodEnd date NOT NULL,
                    TotalSales decimal(18,2) NOT NULL,
                    TotalExpenses decimal(18,2) NOT NULL,
                    TotalSupplierPayments decimal(18,2) NOT NULL,
                    CreatedAt datetime2 NOT NULL
                );
            END;

            DECLARE @Today date = CAST(SYSUTCDATETIME() AS date);
            DECLARE @Start date = CASE
                WHEN @CloseType = N'SEMANAL' THEN DATEADD(day, -6, @Today)
                WHEN @CloseType = N'MENSUAL' THEN DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1)
                ELSE @Today
            END;
            DECLARE @Sales decimal(18,2) = COALESCE((SELECT SUM(Total) FROM dbo.Ventas WHERE CAST(CreatedAt AS date) BETWEEN @Start AND @Today), 0);
            DECLARE @Expenses decimal(18,2) = COALESCE((SELECT SUM(Amount) FROM dbo.Gastos WHERE CAST(CreatedAt AS date) BETWEEN @Start AND @Today), 0);
            DECLARE @SupplierPayments decimal(18,2) = COALESCE((SELECT SUM(Amount) FROM dbo.PagosProveedor WHERE CAST(CreatedAt AS date) BETWEEN @Start AND @Today), 0);

            INSERT INTO dbo.CierresContables (CloseType, PeriodStart, PeriodEnd, TotalSales, TotalExpenses, TotalSupplierPayments, CreatedAt)
            VALUES (@CloseType, @Start, @Today, @Sales, @Expenses, @SupplierPayments, SYSUTCDATETIME());

            SELECT CONVERT(int, SCOPE_IDENTITY());
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@CloseType", normalizedType)));
        await AddAuditLogAsync("CIERRE_CONTABLE", $"Cierre contable {normalizedType.ToLowerInvariant()} #{id} generado", userEmail);
        return new { closeId = id, type = normalizedType, count = 1 };
    }

    public async Task<int> RegisterCreditNoteAsync(CreditNoteInput input, string? userEmail = null)
    {
        if (input.SaleId <= 0)
            throw new InvalidOperationException("Debe indicar una venta valida.");
        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new InvalidOperationException("Debe indicar el motivo de la nota de credito.");

        if (UseMySql)
        {
            var inventoryLocationId = await EnsureInventoryLocationAsync();
            var incomeAccountId = await EnsureAccountAsync("4-01", "Ingresos por ventas", "INGRESO");
            var refundAccountId = await EnsureAccountAsync("1-02", "Banco / SINPE / PayPal", "ACTIVO");
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS NotasCreditoPOS
                (
                    CreditNoteId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    SaleId int NOT NULL,
                    Reason varchar(300) NOT NULL,
                    Amount decimal(18,2) NOT NULL,
                    CreatedAt datetime NOT NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                """);

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var creditNoteId = 0;
            try
            {
                var saleId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT COALESCE(
                        (SELECT SaleId FROM Ventas WHERE SaleId = @SaleId LIMIT 1),
                        (SELECT SaleId FROM Ventas WHERE OrderId = @SaleId LIMIT 1)
                    );
                    """, new SqlParameter("@SaleId", input.SaleId)) ?? 0);
                if (saleId <= 0)
                    throw new InvalidOperationException("La venta o pedido no existe.");

                var alreadyReversed = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT COUNT(1) FROM NotasCreditoPOS WHERE SaleId = @SaleId;",
                    new SqlParameter("@SaleId", saleId)) ?? 0) > 0;
                if (alreadyReversed)
                    throw new InvalidOperationException("La venta ya tiene una nota de crédito registrada.");

                var amount = Convert.ToDecimal(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT Total FROM Ventas WHERE SaleId = @SaleId;",
                    new SqlParameter("@SaleId", saleId)) ?? 0m);
                var orderId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT OrderId FROM Ventas WHERE SaleId = @SaleId;",
                    new SqlParameter("@SaleId", saleId)) ?? 0);
                var cancelledStatusId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT OrderStatusId FROM EstadosPedido WHERE Name = 'Cancelado' LIMIT 1;") ?? 0);

                creditNoteId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    INSERT INTO NotasCreditoPOS (SaleId, Reason, Amount, CreatedAt)
                    VALUES (@SaleId, @Reason, @Amount, UTC_TIMESTAMP());
                    SELECT LAST_INSERT_ID();
                    """,
                    new SqlParameter("@SaleId", saleId),
                    new SqlParameter("@Reason", input.Reason.Trim()),
                    new SqlParameter("@Amount", amount)));

                await ExecuteInTransactionAsync(connection, transaction, """
                    UPDATE PagosSesionCaja SET Amount = 0 WHERE SaleId = @SaleId;
                    UPDATE Ventas SET Subtotal = 0, Tax = 0, Total = 0 WHERE SaleId = @SaleId;
                    """, new SqlParameter("@SaleId", saleId));

                if (orderId > 0 && cancelledStatusId > 0)
                {
                    await ExecuteInTransactionAsync(connection, transaction, """
                        UPDATE Pedidos SET OrderStatusId = @CancelledStatusId WHERE OrderId = @OrderId;
                        INSERT INTO EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
                        VALUES (@OrderId, @CancelledStatusId, CONCAT('Venta reversada por nota de credito: ', @Reason), UTC_TIMESTAMP());
                        """,
                        new SqlParameter("@OrderId", orderId),
                        new SqlParameter("@CancelledStatusId", cancelledStatusId),
                        new SqlParameter("@Reason", input.Reason.Trim()));

                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO ExistenciasInventario (ProductId, InventoryLocationId, Quantity, UpdatedAt)
                        SELECT ProductId, @InventoryLocationId, SUM(Quantity), UTC_TIMESTAMP()
                        FROM DetallePedido
                        WHERE OrderId = @OrderId
                        GROUP BY ProductId
                        ON DUPLICATE KEY UPDATE
                            Quantity = ExistenciasInventario.Quantity + VALUES(Quantity),
                            UpdatedAt = UTC_TIMESTAMP();

                        INSERT INTO MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt)
                        SELECT ProductId, @InventoryLocationId, 'ENTRADA', SUM(Quantity), CONCAT('Reversion nota credito venta #', @SaleId), UTC_TIMESTAMP()
                        FROM DetallePedido
                        WHERE OrderId = @OrderId
                        GROUP BY ProductId;
                        """,
                        new SqlParameter("@OrderId", orderId),
                        new SqlParameter("@SaleId", saleId),
                        new SqlParameter("@InventoryLocationId", inventoryLocationId));
                }

                var entryId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    INSERT INTO AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                    VALUES ('NOTA_CREDITO', 'NotasCreditoPOS', @CreditNoteId, @Reason, UTC_TIMESTAMP());
                    SELECT LAST_INSERT_ID();
                    """,
                    new SqlParameter("@CreditNoteId", creditNoteId),
                    new SqlParameter("@Reason", input.Reason.Trim())));

                await ExecuteInTransactionAsync(connection, transaction, """
                    INSERT INTO LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                    VALUES (@EntryId, @IncomeAccountId, @Amount, 0), (@EntryId, @RefundAccountId, 0, @Amount);
                    """,
                    new SqlParameter("@EntryId", entryId),
                    new SqlParameter("@IncomeAccountId", incomeAccountId),
                    new SqlParameter("@RefundAccountId", refundAccountId),
                    new SqlParameter("@Amount", amount));

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            try { await AddAuditLogAsync("NOTA_CREDITO_POS", $"Nota de credito #{creditNoteId} registrada para venta o pedido #{input.SaleId}", userEmail); }
            catch { }
            return creditNoteId;
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @ResolvedSaleId int = @SaleId;
            IF NOT EXISTS (SELECT 1 FROM dbo.Ventas WHERE SaleId = @ResolvedSaleId)
                SELECT @ResolvedSaleId = SaleId FROM dbo.Ventas WHERE OrderId = @SaleId;

            IF @ResolvedSaleId IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.Ventas WHERE SaleId = @ResolvedSaleId)
                THROW 50150, 'La venta o pedido no existe.', 1;

            IF OBJECT_ID(N'dbo.NotasCreditoPOS', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.NotasCreditoPOS
                (
                    CreditNoteId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    SaleId int NOT NULL,
                    Reason nvarchar(300) NOT NULL,
                    Amount decimal(18,2) NOT NULL,
                    CreatedAt datetime2 NOT NULL
                );
            END;

            DECLARE @Amount decimal(18,2) = (SELECT Total FROM dbo.Ventas WHERE SaleId = @ResolvedSaleId);
            DECLARE @OrderId int = (SELECT OrderId FROM dbo.Ventas WHERE SaleId = @ResolvedSaleId);
            DECLARE @CancelledStatusId int = (SELECT OrderStatusId FROM dbo.EstadosPedido WHERE Name = N'Cancelado');

            INSERT INTO dbo.NotasCreditoPOS (SaleId, Reason, Amount, CreatedAt)
            VALUES (@ResolvedSaleId, @Reason, @Amount, SYSUTCDATETIME());
            DECLARE @CreditNoteId int = SCOPE_IDENTITY();

            UPDATE dbo.PagosSesionCaja SET Amount = 0 WHERE SaleId = @ResolvedSaleId;
            UPDATE dbo.Ventas SET Subtotal = 0, Tax = 0, Total = 0 WHERE SaleId = @ResolvedSaleId;
            IF @CancelledStatusId IS NOT NULL
            BEGIN
                UPDATE dbo.Pedidos SET OrderStatusId = @CancelledStatusId WHERE OrderId = @OrderId;

                INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
                VALUES (@OrderId, @CancelledStatusId, CONCAT(N'Venta reversada por nota de credito: ', @Reason), SYSUTCDATETIME());
            END;

            DECLARE @InventoryLocationId int;
            IF NOT EXISTS (SELECT 1 FROM dbo.UbicacionesInventario WHERE Name = N'Bodega principal')
                INSERT INTO dbo.UbicacionesInventario (Name, Description)
                VALUES (N'Bodega principal', N'Ubicacion principal de BakeSmart Patri');

            SELECT @InventoryLocationId = InventoryLocationId
            FROM dbo.UbicacionesInventario
            WHERE Name = N'Bodega principal';

            ;WITH Items AS (
                SELECT ProductId, SUM(Quantity) AS Quantity
                FROM dbo.DetallePedido
                WHERE OrderId = @OrderId
                GROUP BY ProductId
            )
            MERGE dbo.ExistenciasInventario AS target
            USING Items AS source
            ON target.ProductId = source.ProductId AND target.InventoryLocationId = @InventoryLocationId
            WHEN MATCHED THEN
                UPDATE SET Quantity = target.Quantity + source.Quantity, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ProductId, InventoryLocationId, Quantity)
                VALUES (source.ProductId, @InventoryLocationId, source.Quantity);

            INSERT INTO dbo.MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt)
            SELECT ProductId, @InventoryLocationId, N'ENTRADA', SUM(Quantity), CONCAT(N'Reversion nota credito venta #', @ResolvedSaleId), SYSUTCDATETIME()
            FROM dbo.DetallePedido
            WHERE OrderId = @OrderId
            GROUP BY ProductId;

            DECLARE @AccountId int = (SELECT TOP 1 AccountId FROM dbo.CatalogoCuentas ORDER BY AccountId);
            INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
            VALUES (N'NOTA_CREDITO', N'NotasCreditoPOS', @CreditNoteId, @Reason, SYSUTCDATETIME());
            DECLARE @EntryId int = SCOPE_IDENTITY();

            IF @AccountId IS NOT NULL
                INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                VALUES (@EntryId, @AccountId, @Amount, 0), (@EntryId, @AccountId, 0, @Amount);

            COMMIT TRAN;
            SELECT @CreditNoteId;
            """;

        var id = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@SaleId", input.SaleId),
            new SqlParameter("@Reason", input.Reason.Trim())));

        await AddAuditLogAsync("NOTA_CREDITO_POS", $"Nota de credito #{id} registrada para venta o pedido #{input.SaleId}", userEmail);
        return id;
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
        var activeUsers = rows.Count(row => row.GetType().GetProperty("active")?.GetValue(row) is true);
        return new { rows, activeUsers };
    }

    public async Task SavePayPalCheckoutAsync(string providerOrderId, IEnumerable<int> orderIds, string customerEmail, decimal amount, string currency)
    {
        if (!UseMySql || string.IsNullOrWhiteSpace(providerOrderId)) return;
        var ids = string.Join(',', orderIds.Where(id => id > 0).Distinct());
        if (string.IsNullOrWhiteSpace(ids)) return;
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS ReferenciasPagoPayPal
            (
                ProviderOrderId varchar(64) NOT NULL PRIMARY KEY,
                OrderIds varchar(500) NOT NULL,
                CustomerEmail varchar(254) NOT NULL,
                ExpectedAmount decimal(18,2) NOT NULL,
                Currency varchar(8) NOT NULL,
                CreatedAt datetime NOT NULL,
                ConfirmedAt datetime NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            INSERT INTO ReferenciasPagoPayPal
                (ProviderOrderId, OrderIds, CustomerEmail, ExpectedAmount, Currency, CreatedAt)
            VALUES (@ProviderOrderId, @OrderIds, @CustomerEmail, @ExpectedAmount, @Currency, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                OrderIds = VALUES(OrderIds), CustomerEmail = VALUES(CustomerEmail),
                ExpectedAmount = VALUES(ExpectedAmount), Currency = VALUES(Currency);
            """,
            new SqlParameter("@ProviderOrderId", providerOrderId.Trim()),
            new SqlParameter("@OrderIds", ids),
            new SqlParameter("@CustomerEmail", customerEmail.Trim()),
            new SqlParameter("@ExpectedAmount", amount),
            new SqlParameter("@Currency", currency.Trim().ToUpperInvariant()));
    }

    public async Task<(string OrderIds, string CustomerEmail, decimal ExpectedAmount, string Currency)?> GetPayPalCheckoutAsync(string providerOrderId)
    {
        if (!UseMySql || string.IsNullOrWhiteSpace(providerOrderId)) return null;
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS ReferenciasPagoPayPal
            (
                ProviderOrderId varchar(64) NOT NULL PRIMARY KEY,
                OrderIds varchar(500) NOT NULL,
                CustomerEmail varchar(254) NOT NULL,
                ExpectedAmount decimal(18,2) NOT NULL,
                Currency varchar(8) NOT NULL,
                CreatedAt datetime NOT NULL,
                ConfirmedAt datetime NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """);
        var rows = await QueryAsync("""
            SELECT OrderIds, CustomerEmail, ExpectedAmount, Currency
            FROM ReferenciasPagoPayPal WHERE ProviderOrderId = @ProviderOrderId LIMIT 1;
            """, reader => (
                reader.GetString("OrderIds"),
                reader.GetString("CustomerEmail"),
                reader.GetDecimal("ExpectedAmount"),
                reader.GetString("Currency")),
            new SqlParameter("@ProviderOrderId", providerOrderId.Trim()));
        return rows.Select(row => ((string OrderIds, string CustomerEmail, decimal ExpectedAmount, string Currency)?)row).FirstOrDefault();
    }

    public async Task MarkPayPalCheckoutConfirmedAsync(string providerOrderId)
    {
        if (!UseMySql || string.IsNullOrWhiteSpace(providerOrderId)) return;
        await ExecuteAsync("UPDATE ReferenciasPagoPayPal SET ConfirmedAt = COALESCE(ConfirmedAt, UTC_TIMESTAMP()) WHERE ProviderOrderId = @ProviderOrderId;",
            new SqlParameter("@ProviderOrderId", providerOrderId.Trim()));
    }

    public async Task UpdateCustomerOrderAsync(int orderId, DateTime? deliveryDate, string? notes, string? userEmail = null)
    {
        var sql = UseMySql
            ? "UPDATE Pedidos SET DeliveryDate = COALESCE(@DeliveryDate, DeliveryDate), Notes = COALESCE(NULLIF(@Notes, ''), Notes) WHERE OrderId = @OrderId;"
            : "UPDATE dbo.Pedidos SET DeliveryDate = COALESCE(@DeliveryDate, DeliveryDate), Notes = COALESCE(NULLIF(@Notes, N''), Notes) WHERE OrderId = @OrderId;";
        await ExecuteAsync(sql, new SqlParameter("@OrderId", orderId), new SqlParameter("@DeliveryDate", (object?)deliveryDate ?? DBNull.Value), new SqlParameter("@Notes", (object?)notes?.Trim() ?? DBNull.Value));
        await AddAuditLogAsync("EDITAR_PEDIDO_CLIENTE", $"Pedido #{orderId} actualizado por el cliente", userEmail);
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

    private static CatalogProductViewModel MapCatalogProduct(DbDataReader reader) =>
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

    private async Task<int> EnsureProductTypeAsync(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Producto terminado" : name.Trim();
        if (UseMySql)
        {
            const string mysql = """
                INSERT INTO TiposProducto (Name)
                SELECT @Name
                WHERE NOT EXISTS (SELECT 1 FROM TiposProducto WHERE Name = @Name);

                SELECT ProductTypeId FROM TiposProducto WHERE Name = @Name;
                """;

            return Convert.ToInt32(await ScalarAsync(mysql, new SqlParameter("@Name", clean)));
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.TiposProducto WHERE Name = @Name)
                INSERT INTO dbo.TiposProducto (Name) VALUES (@Name);

            SELECT ProductTypeId FROM dbo.TiposProducto WHERE Name = @Name;
            """;

        return Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Name", clean)));
    }

    private async Task<int> EnsureUnitMeasureAsync(string code)
    {
        var clean = string.IsNullOrWhiteSpace(code) ? "unidad" : code.Trim();
        if (UseMySql)
        {
            const string mysql = """
                INSERT INTO UnidadesMedida (Code, Name, AllowsDecimal)
                SELECT @Code, @Code, 1
                WHERE NOT EXISTS (SELECT 1 FROM UnidadesMedida WHERE Code = @Code);

                SELECT UnitMeasureId FROM UnidadesMedida WHERE Code = @Code;
                """;

            return Convert.ToInt32(await ScalarAsync(mysql, new SqlParameter("@Code", clean)));
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.UnidadesMedida WHERE Code = @Code)
                INSERT INTO dbo.UnidadesMedida (Code, Name, AllowsDecimal) VALUES (@Code, @Code, 1);

            SELECT UnitMeasureId FROM dbo.UnidadesMedida WHERE Code = @Code;
            """;

        return Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Code", clean)));
    }

    private async Task<int> EnsureProductCategoryAsync(string category, string? subcategory)
    {
        var parentName = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        var childName = string.IsNullOrWhiteSpace(subcategory) ? parentName : subcategory.Trim();

        if (UseMySql)
        {
            const string parentSql = """
                INSERT INTO CategoriasProducto (ParentCategoryId, Name)
                SELECT NULL, @ParentName
                WHERE NOT EXISTS (
                    SELECT 1 FROM CategoriasProducto
                    WHERE ParentCategoryId IS NULL AND Name = @ParentName
                );

                SELECT ProductCategoryId
                FROM CategoriasProducto
                WHERE ParentCategoryId IS NULL AND Name = @ParentName;
                """;

            var parentId = Convert.ToInt32(await ScalarAsync(parentSql, new SqlParameter("@ParentName", parentName)));
            if (string.Equals(childName, parentName, StringComparison.OrdinalIgnoreCase))
                return parentId;

            const string childSql = """
                INSERT INTO CategoriasProducto (ParentCategoryId, Name)
                SELECT @ParentId, @ChildName
                WHERE NOT EXISTS (
                    SELECT 1 FROM CategoriasProducto
                    WHERE ParentCategoryId = @ParentId AND Name = @ChildName
                );

                SELECT ProductCategoryId
                FROM CategoriasProducto
                WHERE ParentCategoryId = @ParentId AND Name = @ChildName;
                """;

            return Convert.ToInt32(await ScalarAsync(childSql,
                new SqlParameter("@ParentId", parentId),
                new SqlParameter("@ChildName", childName)));
        }

        const string sql = """
            DECLARE @ParentId int;

            SELECT @ParentId = ProductCategoryId
            FROM dbo.CategoriasProducto
            WHERE ParentCategoryId IS NULL AND Name = @ParentName;

            IF @ParentId IS NULL
            BEGIN
                INSERT INTO dbo.CategoriasProducto (ParentCategoryId, Name)
                VALUES (NULL, @ParentName);

                SET @ParentId = SCOPE_IDENTITY();
            END;

            IF @ChildName = @ParentName
            BEGIN
                SELECT @ParentId;
            END
            ELSE
            BEGIN
                DECLARE @ChildId int;

                SELECT @ChildId = ProductCategoryId
                FROM dbo.CategoriasProducto
                WHERE ParentCategoryId = @ParentId AND Name = @ChildName;

                IF @ChildId IS NULL
                BEGIN
                    INSERT INTO dbo.CategoriasProducto (ParentCategoryId, Name)
                    VALUES (@ParentId, @ChildName);

                    SET @ChildId = SCOPE_IDENTITY();
                END;

                SELECT @ChildId;
            END;
            """;

        return Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@ParentName", parentName),
            new SqlParameter("@ChildName", childName)));
    }

    private async Task<int> EnsureInventoryLocationAsync()
    {
        if (UseMySql)
        {
            const string mysql = """
                INSERT INTO UbicacionesInventario (Name, Description)
                SELECT 'Bodega principal', 'Ubicacion principal de BakeSmart Patri'
                WHERE NOT EXISTS (SELECT 1 FROM UbicacionesInventario WHERE Name = 'Bodega principal');

                SELECT InventoryLocationId
                FROM UbicacionesInventario
                WHERE Name = 'Bodega principal';
                """;

            return Convert.ToInt32(await ScalarAsync(mysql));
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.UbicacionesInventario WHERE Name = N'Bodega principal')
                INSERT INTO dbo.UbicacionesInventario (Name, Description)
                VALUES (N'Bodega principal', N'Ubicacion principal de BakeSmart Patri');

            SELECT InventoryLocationId
            FROM dbo.UbicacionesInventario
            WHERE Name = N'Bodega principal';
            """;

        return Convert.ToInt32(await ScalarAsync(sql));
    }

    private static async Task SetInventoryBalanceAsync(DbConnection connection, DbTransaction transaction, int productId, int locationId, decimal quantity)
    {
        if (connection is MySqlConnection)
        {
            const string mysql = """
                INSERT INTO ExistenciasInventario (ProductId, InventoryLocationId, Quantity, UpdatedAt)
                VALUES (@ProductId, @LocationId, @Quantity, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE
                    Quantity = VALUES(Quantity),
                    UpdatedAt = UTC_TIMESTAMP();
                """;

            await ExecuteInTransactionAsync(connection, transaction, mysql,
                new SqlParameter("@ProductId", productId),
                new SqlParameter("@LocationId", locationId),
                new SqlParameter("@Quantity", quantity));
            return;
        }

        const string sql = """
            MERGE dbo.ExistenciasInventario AS target
            USING (SELECT @ProductId AS ProductId, @LocationId AS InventoryLocationId) AS source
            ON target.ProductId = source.ProductId AND target.InventoryLocationId = source.InventoryLocationId
            WHEN MATCHED THEN
                UPDATE SET Quantity = @Quantity, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ProductId, InventoryLocationId, Quantity)
                VALUES (@ProductId, @LocationId, @Quantity);
            """;

        await ExecuteInTransactionAsync(connection, transaction, sql,
            new SqlParameter("@ProductId", productId),
            new SqlParameter("@LocationId", locationId),
            new SqlParameter("@Quantity", quantity));
    }

    private static async Task AddInventoryMovementAsync(DbConnection connection, DbTransaction transaction, int productId, int locationId, string type, decimal quantity, string? note)
    {
        var sql = connection is MySqlConnection
            ? """
              INSERT INTO MovimientosInventario
                  (ProductId, InventoryLocationId, MovementType, Quantity, ResponsibleUserId, Note, CreatedAt)
              VALUES
                  (@ProductId, @LocationId, @Type, @Quantity, NULL, @Note, UTC_TIMESTAMP());
              """
            : """
            INSERT INTO dbo.MovimientosInventario
                (ProductId, InventoryLocationId, MovementType, Quantity, ResponsibleUserId, Note, CreatedAt)
            VALUES
                (@ProductId, @LocationId, @Type, @Quantity, NULL, @Note, SYSUTCDATETIME());
            """;

        await ExecuteInTransactionAsync(connection, transaction, sql,
            new SqlParameter("@ProductId", productId),
            new SqlParameter("@LocationId", locationId),
            new SqlParameter("@Type", type),
            new SqlParameter("@Quantity", quantity),
            new SqlParameter("@Note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim()));
    }

    private async Task<int> EnsureAccountAsync(string? codeOrName, string fallbackName, string accountType)
    {
        var clean = string.IsNullOrWhiteSpace(codeOrName) ? fallbackName : codeOrName.Trim();
        var looksLikeCode = clean.Any(char.IsDigit) && clean.Contains('-') && clean.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.');
        var accountCode = looksLikeCode
            ? clean
            : new string(clean.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        if (accountCode.Length > 32) accountCode = accountCode[..32];
        var accountName = looksLikeCode ? fallbackName : clean;
        if (UseMySql)
        {
            return Convert.ToInt32(await ScalarAsync("""
                INSERT INTO CatalogoCuentas (AccountCode, AccountName, AccountType)
                SELECT @AccountCode, @AccountName, @AccountType
                WHERE NOT EXISTS (
                    SELECT 1 FROM CatalogoCuentas WHERE LOWER(AccountCode) = LOWER(@Lookup) OR LOWER(AccountName) = LOWER(@Lookup)
                );

                SELECT AccountId
                FROM CatalogoCuentas
                WHERE LOWER(AccountCode) = LOWER(@Lookup) OR LOWER(AccountName) = LOWER(@Lookup)
                ORDER BY AccountId
                LIMIT 1;
                """,
                new SqlParameter("@Lookup", clean),
                new SqlParameter("@AccountCode", accountCode),
                new SqlParameter("@AccountName", accountName),
                new SqlParameter("@AccountType", accountType)) ?? 0);
        }

        const string sql = """
            DECLARE @AccountId int;
            SELECT @AccountId = AccountId
            FROM dbo.CatalogoCuentas
            WHERE LOWER(AccountCode) = LOWER(@Lookup) OR LOWER(AccountName) = LOWER(@Lookup);

            IF @AccountId IS NULL
            BEGIN
                INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                VALUES (@AccountCode, @AccountName, @AccountType);
                SET @AccountId = SCOPE_IDENTITY();
            END;

            SELECT @AccountId;
            """;

        return Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@Lookup", clean),
            new SqlParameter("@AccountCode", accountCode),
            new SqlParameter("@AccountName", accountName),
            new SqlParameter("@AccountType", accountType)));
    }

    private async Task<int> PaymentAssetAccountAsync(string? method)
    {
        var normalized = RemoveDiacritics(method ?? string.Empty).Trim().ToUpperInvariant();
        return normalized == "EFECTIVO"
            ? await EnsureAccountAsync("1-01", "Caja general", "ACTIVO")
            : await EnsureAccountAsync("1-02", "Bancos y medios electronicos", "ACTIVO");
    }

    private async Task<int> EnsureExpenseCategoryAsync(string name)
    {
        if (UseMySql)
        {
            return Convert.ToInt32(await ScalarAsync("""
                INSERT INTO CategoriasGasto (Name)
                SELECT @Name
                WHERE NOT EXISTS (SELECT 1 FROM CategoriasGasto WHERE LOWER(Name) = LOWER(@Name));

                SELECT ExpenseCategoryId FROM CategoriasGasto WHERE LOWER(Name) = LOWER(@Name) LIMIT 1;
                """, new SqlParameter("@Name", name.Trim())) ?? 0);
        }

        const string sql = """
            DECLARE @CategoryId int;
            SELECT @CategoryId = ExpenseCategoryId FROM dbo.CategoriasGasto WHERE LOWER(Name) = LOWER(@Name);
            IF @CategoryId IS NULL
            BEGIN
                INSERT INTO dbo.CategoriasGasto (Name) VALUES (@Name);
                SET @CategoryId = SCOPE_IDENTITY();
            END;
            SELECT @CategoryId;
            """;

        return Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Name", name.Trim())));
    }

    private async Task<int> EnsureSupplierAsync(string name)
    {
        if (UseMySql)
        {
            return Convert.ToInt32(await ScalarAsync("""
                INSERT INTO Proveedores (Name, Phone, Email)
                SELECT @Name, NULL, NULL
                WHERE NOT EXISTS (SELECT 1 FROM Proveedores WHERE LOWER(Name) = LOWER(@Name));

                SELECT SupplierId FROM Proveedores WHERE LOWER(Name) = LOWER(@Name) LIMIT 1;
                """, new SqlParameter("@Name", name.Trim())) ?? 0);
        }

        const string sql = """
            DECLARE @SupplierId int;
            SELECT @SupplierId = SupplierId FROM dbo.Proveedores WHERE LOWER(Name) = LOWER(@Name);
            IF @SupplierId IS NULL
            BEGIN
                INSERT INTO dbo.Proveedores (Name, Phone, Email) VALUES (@Name, NULL, NULL);
                SET @SupplierId = SCOPE_IDENTITY();
            END;
            SELECT @SupplierId;
            """;

        return Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Name", name.Trim())));
    }

    private async Task<int> EnsurePaymentMethodAsync(string name)
    {
        if (UseMySql)
        {
            return Convert.ToInt32(await ScalarAsync("""
                INSERT INTO MetodosPago (Name, CommissionRate, IsActive)
                SELECT @Name, 0, 1
                WHERE NOT EXISTS (SELECT 1 FROM MetodosPago WHERE LOWER(Name) = LOWER(@Name));

                SELECT PaymentMethodId FROM MetodosPago WHERE LOWER(Name) = LOWER(@Name) LIMIT 1;
                """, new SqlParameter("@Name", name.Trim())) ?? 0);
        }

        const string sql = """
            DECLARE @PaymentMethodId int;
            SELECT @PaymentMethodId = PaymentMethodId FROM dbo.MetodosPago WHERE LOWER(Name) = LOWER(@Name);
            IF @PaymentMethodId IS NULL
            BEGIN
                INSERT INTO dbo.MetodosPago (Name, CommissionRate, IsActive) VALUES (@Name, 0, 1);
                SET @PaymentMethodId = SCOPE_IDENTITY();
            END;
            SELECT @PaymentMethodId;
            """;

        return Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@Name", name.Trim())));
    }

    private async Task ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await WithTransientRetryAsync(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();

            await using var command = CreateCommand(connection, sql, parameters);
            await command.ExecuteNonQueryAsync();
        });
    }

    private async Task<object?> ScalarAsync(string sql, params SqlParameter[] parameters)
    {
        return await WithTransientRetryAsync<object?>(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();

            await using var command = CreateCommand(connection, sql, parameters);

            return await command.ExecuteScalarAsync();
        });
    }

    private static async Task ExecuteInTransactionAsync(DbConnection connection, DbTransaction transaction, string sql, params SqlParameter[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters, transaction);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarInTransactionAsync(DbConnection connection, DbTransaction transaction, string sql, params SqlParameter[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters, transaction);

        return await command.ExecuteScalarAsync();
    }

    private static SqlParameter[] DateParameters(DateTime? start, DateTime? end) =>
    [
        new SqlParameter("@Start", (object?)start?.Date ?? DBNull.Value),
        new SqlParameter("@End", (object?)end?.Date ?? DBNull.Value)
    ];

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> map, params SqlParameter[] parameters)
    {
        return await WithTransientRetryAsync<IReadOnlyList<T>>(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();

            await using var command = CreateCommand(connection, sql, parameters);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

            var rows = new List<T>();
            while (await reader.ReadAsync())
            {
                rows.Add(map(reader));
            }

            return rows;
        });
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, SqlParameter[]? parameters = null, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = connection is MySqlConnection ? ToMySqlSql(sql) : sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        if (transaction is not null)
            command.Transaction = transaction;

        if (parameters is { Length: > 0 })
        {
            foreach (var parameter in parameters)
            {
                var dbParameter = command.CreateParameter();
                dbParameter.ParameterName = parameter.ParameterName;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                command.Parameters.Add(dbParameter);
            }
        }

        return command;
    }

    private static string ToMySqlSql(string sql) =>
        NormalizeTopForMySql(sql)
            .Replace("dbo.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("SYSUTCDATETIME()", "UTC_TIMESTAMP()", StringComparison.OrdinalIgnoreCase)
            .Replace("GETDATE()", "UTC_TIMESTAMP()", StringComparison.OrdinalIgnoreCase)
            .Replace("N'", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("CAST(0 AS bit)", "0", StringComparison.OrdinalIgnoreCase)
            .Replace("CAST(1 AS bit)", "1", StringComparison.OrdinalIgnoreCase)
            .Replace("CONVERT(int, SCOPE_IDENTITY())", "LAST_INSERT_ID()", StringComparison.OrdinalIgnoreCase)
            .Replace("SCOPE_IDENTITY()", "LAST_INSERT_ID()", StringComparison.OrdinalIgnoreCase)
            .Replace("COUNT_BIG(*)", "COUNT(*)", StringComparison.OrdinalIgnoreCase)
            .Replace("CAST(CreatedAt AS date)", "DATE(CreatedAt)", StringComparison.OrdinalIgnoreCase)
            .Replace("CAST(UTC_TIMESTAMP() AS date)", "DATE(UTC_TIMESTAMP())", StringComparison.OrdinalIgnoreCase)
            .Replace("CAST(SYSUTCDATETIME() AS date)", "DATE(UTC_TIMESTAMP())", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTopForMySql(string sql)
    {
        var normalized = Regex.Replace(
            sql,
            @"SELECT\s+TOP\s+(?<count>\d+)\s+(?<body>.*?)(?<end>;\s*)",
            match =>
            {
                var body = match.Groups["body"].Value.TrimEnd();
                if (Regex.IsMatch(body, @"\bLIMIT\s+\d+\s*$", RegexOptions.IgnoreCase))
                    return $"SELECT {body}{match.Groups["end"].Value}";

                return $"SELECT {body} LIMIT {match.Groups["count"].Value}{match.Groups["end"].Value}";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return normalized;
    }

    private static async Task WithTransientRetryAsync(Func<Task> operation)
    {
        await WithTransientRetryAsync(async () =>
        {
            await operation();
            return true;
        });
    }

    private static async Task<T> WithTransientRetryAsync<T>(Func<Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < MaxTransientAttempts && IsTransientSqlFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }

    private static bool IsTransientSqlFailure(Exception ex)
    {
        if (ex is TimeoutException)
            return true;

        if (ex is not SqlException sqlException)
            return false;

        return sqlException.Errors.Cast<SqlError>().Any(error => error.Number is
            -2 or 20 or 64 or 233 or 10053 or 10054 or 10060 or 10928 or 10929 or 40143 or 40197 or 40501 or 4060 or 40613 or 49918 or 49919 or 49920);
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback = false)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return bool.TryParse(value.Trim().Trim('\uFEFF'), out var parsed) ? parsed : fallback;
    }

    public async Task<ProfileData?> GetProfileAsync(string email)
    {
        var sql = UseMySql
            ? """
            SELECT
                u.FirstName,
                u.LastName,
                u.Email,
                u.Phone,
                u.AddressLine,
                r.RoleName,
                ca.CustomerAddressId,
                ca.Label AS AddressLabel,
                COALESCE(ca.AddressLine, u.AddressLine) AS DefaultAddressLine,
                ca.Latitude,
                ca.Longitude,
                COALESCE(c.IsFrequent, 0) AS IsFrequent
            FROM Usuarios u
            INNER JOIN Roles r ON r.RoleId = u.RoleId
            LEFT JOIN Clientes c ON c.UserId = u.UserId
            LEFT JOIN (
                SELECT d.CustomerId, d.CustomerAddressId, d.Label, d.AddressLine, d.Latitude, d.Longitude
                FROM DireccionesCliente d
                INNER JOIN (
                    SELECT CustomerId, MAX(CustomerAddressId) AS CustomerAddressId
                    FROM DireccionesCliente
                    WHERE IsDefault = 1
                    GROUP BY CustomerId
                ) pick ON pick.CustomerId = d.CustomerId AND pick.CustomerAddressId = d.CustomerAddressId
            ) ca ON ca.CustomerId = c.CustomerId
            WHERE LOWER(u.Email) = LOWER(@Email);
            """
            : """
            SELECT
                u.FirstName,
                u.LastName,
                u.Email,
                u.Phone,
                u.AddressLine,
                r.RoleName,
                ca.CustomerAddressId,
                ca.Label AS AddressLabel,
                COALESCE(ca.AddressLine, u.AddressLine) AS DefaultAddressLine,
                ca.Latitude,
                ca.Longitude,
                CAST(COALESCE(c.IsFrequent, 0) AS bit) AS IsFrequent
            FROM dbo.Usuarios u
            INNER JOIN dbo.Roles r ON r.RoleId = u.RoleId
            LEFT JOIN dbo.Clientes c ON c.UserId = u.UserId
            OUTER APPLY (
                SELECT TOP 1 CustomerAddressId, Label, AddressLine, Latitude, Longitude
                FROM dbo.DireccionesCliente
                WHERE CustomerId = c.CustomerId AND IsDefault = 1
                ORDER BY CustomerAddressId DESC
            ) ca
            WHERE LOWER(u.Email) = LOWER(@Email);
            """;

        var rows = await QueryAsync(sql, reader => new ProfileData(
            reader.GetString("FirstName"),
            reader.GetString("LastName"),
            reader.GetString("Email"),
            reader.GetNullableString("Phone") ?? "",
            reader.GetNullableString("DefaultAddressLine") ?? reader.GetNullableString("AddressLine") ?? "",
            reader.GetString("RoleName"),
            reader.IsDBNull(reader.GetOrdinal("CustomerAddressId")) ? null : reader.GetInt32("CustomerAddressId"),
            reader.GetNullableString("AddressLabel") ?? "Principal",
            reader.GetNullableDecimal("Latitude"),
            reader.GetNullableDecimal("Longitude"),
            reader.GetBoolean("IsFrequent")
        ), new SqlParameter("@Email", email));

        return rows.FirstOrDefault();
    }

    public async Task<string?> CreatePasswordResetTokenAsync(string email)
    {
        email = email.Trim().ToLowerInvariant();
        var userTable = UseMySql ? "Usuarios" : "dbo.Usuarios";
        var exists = Convert.ToInt32(await ScalarAsync($"SELECT COUNT(1) FROM {userTable} WHERE LOWER(Email) = LOWER(@Email) AND IsActive = 1", new SqlParameter("@Email", email)));
        if (exists == 0)
        {
            var anyUser = Convert.ToInt32(await ScalarAsync(
                $"SELECT COUNT(1) FROM {userTable} WHERE LOWER(Email) = LOWER(@Email)",
                new SqlParameter("@Email", email)));
            if (anyUser > 0)
                return null;

            var customerTable = UseMySql ? "Clientes" : "dbo.Clientes";
            var customerSql = UseMySql
                ? $"SELECT FullName, Phone FROM {customerTable} WHERE LOWER(Email) = LOWER(@Email) ORDER BY CustomerId LIMIT 1;"
                : $"SELECT TOP 1 FullName, Phone FROM {customerTable} WHERE LOWER(Email) = LOWER(@Email) ORDER BY CustomerId;";
            var customer = (await QueryAsync(customerSql, reader => new
            {
                FullName = reader.GetString("FullName"),
                Phone = reader.GetNullableString("Phone")
            }, new SqlParameter("@Email", email))).FirstOrDefault();

            if (customer is not null)
            {
                var nameParts = customer.FullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var firstName = nameParts.ElementAtOrDefault(0) ?? "Cliente";
                var lastName = nameParts.ElementAtOrDefault(1) ?? "ReposterÃ­a Patri";
                var temporaryPasswordHash = HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
                var roleTable = UseMySql ? "Roles" : "dbo.Roles";
                var roleId = Convert.ToInt32(await ScalarAsync(
                    $"SELECT RoleId FROM {roleTable} WHERE RoleName = 'Cliente'",
                    Array.Empty<SqlParameter>()));

                var userId = UseMySql
                    ? Convert.ToInt32(await ScalarAsync("""
                        INSERT INTO Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
                        VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, NULL, 1, UTC_TIMESTAMP());
                        SELECT LAST_INSERT_ID();
                        """, new SqlParameter("@RoleId", roleId), new SqlParameter("@FirstName", firstName),
                        new SqlParameter("@LastName", lastName), new SqlParameter("@Email", email),
                        new SqlParameter("@Phone", (object?)customer.Phone ?? DBNull.Value), new SqlParameter("@PasswordHash", temporaryPasswordHash)))
                    : Convert.ToInt32(await ScalarAsync("""
                        INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
                        OUTPUT INSERTED.UserId
                        VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, NULL, 1, SYSUTCDATETIME());
                        """, new SqlParameter("@RoleId", roleId), new SqlParameter("@FirstName", firstName),
                        new SqlParameter("@LastName", lastName), new SqlParameter("@Email", email),
                        new SqlParameter("@Phone", (object?)customer.Phone ?? DBNull.Value), new SqlParameter("@PasswordHash", temporaryPasswordHash)));

                await ExecuteAsync($"UPDATE {customerTable} SET UserId = @UserId WHERE LOWER(Email) = LOWER(@Email) AND UserId IS NULL;",
                    new SqlParameter("@UserId", userId), new SqlParameter("@Email", email));
                exists = 1;
            }
        }
        if (exists == 0) return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (UseMySql)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS TokensRestablecimiento (ResetTokenId int NOT NULL AUTO_INCREMENT PRIMARY KEY, Email varchar(254) NOT NULL, TokenHash char(64) NOT NULL, ExpiresAt datetime NOT NULL, UsedAt datetime NULL, CreatedAt datetime NOT NULL, UNIQUE KEY UX_TokensRestablecimiento_TokenHash (TokenHash)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                UPDATE TokensRestablecimiento SET UsedAt = UTC_TIMESTAMP() WHERE LOWER(Email) = LOWER(@Email) AND UsedAt IS NULL;
                INSERT INTO TokensRestablecimiento (Email, TokenHash, ExpiresAt, UsedAt, CreatedAt) VALUES (@Email, @TokenHash, DATE_ADD(UTC_TIMESTAMP(), INTERVAL 30 MINUTE), NULL, UTC_TIMESTAMP());
                """, new SqlParameter("@Email", email), new SqlParameter("@TokenHash", tokenHash));
        }
        else
        {
            await ExecuteAsync("""
                IF OBJECT_ID(N'dbo.TokensRestablecimiento', N'U') IS NULL CREATE TABLE dbo.TokensRestablecimiento (ResetTokenId int IDENTITY(1,1) NOT NULL PRIMARY KEY, Email nvarchar(254) NOT NULL, TokenHash char(64) NOT NULL UNIQUE, ExpiresAt datetime2 NOT NULL, UsedAt datetime2 NULL, CreatedAt datetime2 NOT NULL);
                UPDATE dbo.TokensRestablecimiento SET UsedAt = SYSUTCDATETIME() WHERE LOWER(Email) = LOWER(@Email) AND UsedAt IS NULL;
                INSERT INTO dbo.TokensRestablecimiento (Email, TokenHash, ExpiresAt, UsedAt, CreatedAt) VALUES (@Email, @TokenHash, DATEADD(minute, 30, SYSUTCDATETIME()), NULL, SYSUTCDATETIME());
                """, new SqlParameter("@Email", email), new SqlParameter("@TokenHash", tokenHash));
        }
        await AddAuditLogAsync("SOLICITAR_RECUPERACION", $"Se solicitÃ³ recuperar la contraseÃ±a de {email}");
        return token;
    }

    public async Task<bool> ResetPasswordWithTokenAsync(string token, string newPassword)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var tokenTable = UseMySql ? "TokensRestablecimiento" : "dbo.TokensRestablecimiento";
        var nowSql = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        var email = (await QueryAsync($"SELECT Email FROM {tokenTable} WHERE TokenHash = @TokenHash AND UsedAt IS NULL AND ExpiresAt > {nowSql};", reader => reader.GetString("Email"), new SqlParameter("@TokenHash", tokenHash))).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(email)) return false;
        var userTable = UseMySql ? "Usuarios" : "dbo.Usuarios";
        await ExecuteAsync($"UPDATE {userTable} SET PasswordHash = @PasswordHash WHERE LOWER(Email) = LOWER(@Email) AND IsActive = 1;", new SqlParameter("@PasswordHash", HashPassword(newPassword)), new SqlParameter("@Email", email));
        await ExecuteAsync($"UPDATE {tokenTable} SET UsedAt = {nowSql} WHERE TokenHash = @TokenHash;", new SqlParameter("@TokenHash", tokenHash));
        await AddAuditLogAsync("RECUPERAR_CONTRASENA", $"ContraseÃ±a restablecida para {email}");
        return true;
    }

    public async Task<bool> ChangePasswordAsync(string email, string currentPassword, string newPassword)
    {
        if (await AuthenticateAsync(email, currentPassword) is null) return false;
        var userTable = UseMySql ? "Usuarios" : "dbo.Usuarios";
        await ExecuteAsync($"UPDATE {userTable} SET PasswordHash = @PasswordHash WHERE LOWER(Email) = LOWER(@Email) AND IsActive = 1;",
            new SqlParameter("@PasswordHash", HashPassword(newPassword)), new SqlParameter("@Email", email));
        await AddAuditLogAsync("CAMBIAR_CONTRASENA", $"ContraseÃ±a actualizada desde el perfil para {email}", email);
        return true;
    }

    public async Task<bool> RequestPasswordResetAsync(string email)
    {
        const string checkSql = "SELECT COUNT(1) FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@Email) AND IsActive = 1";
        var exists = Convert.ToInt32(await ScalarAsync(checkSql, new SqlParameter("@Email", email)));
        if (exists == 0)
            return false;

        // En un entorno real, aquÃ­ se enviarÃ­a un email con un token.
        // Por ahora, generamos una contraseÃ±a temporal y la registramos en bitÃ¡cora.
        var tempPassword = $"Temp{Guid.NewGuid().ToString("N")[..8]}!";
        var hash = HashPassword(tempPassword);

        const string sql = """
            UPDATE dbo.Usuarios
            SET PasswordHash = @PasswordHash
            WHERE LOWER(Email) = LOWER(@Email) AND IsActive = 1;
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@Email", email),
            new SqlParameter("@PasswordHash", hash));

        // TODO: En produccion, enviar la temporal por email en lugar de guardarla en bitacora
        await AddAuditLogAsync("RECUPERAR_CONTRASENA", $"Contrasena restablecida para {email}");
        return true;
    }

    public async Task<decimal> GetIvaRateAsync()
    {
        const string sql = "SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'iva'";
        var value = await ScalarAsync(sql);
        if (value is not null && decimal.TryParse(value.ToString(), out var rate))
            return rate;
        return 0.13m;
    }

    public async Task UpdateProfileAsync(string email, ProfileInput input)
    {
        if (UseMySql)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await ExecuteInTransactionAsync(connection, transaction, """
                    UPDATE Usuarios
                    SET FirstName = @FirstName,
                        LastName = @LastName,
                        Phone = @Phone,
                        AddressLine = @AddressLine,
                        PasswordHash = CASE WHEN NULLIF(@PasswordHash, '') IS NULL THEN PasswordHash ELSE @PasswordHash END
                    WHERE LOWER(Email) = LOWER(@Email);
                    """,
                    new SqlParameter("@Email", email),
                    new SqlParameter("@FirstName", input.FirstName.Trim()),
                    new SqlParameter("@LastName", input.LastName.Trim()),
                    new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
                    new SqlParameter("@AddressLine", (object?)input.Address?.Trim() ?? DBNull.Value),
                    new SqlParameter("@PasswordHash", string.IsNullOrWhiteSpace(input.NewPassword) ? "" : HashPassword(input.NewPassword)));

                var customerId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT c.CustomerId
                    FROM Clientes c
                    INNER JOIN Usuarios u ON u.UserId = c.UserId
                    WHERE LOWER(u.Email) = LOWER(@Email)
                    LIMIT 1;
                    """, new SqlParameter("@Email", email)) ?? 0);

                if (customerId > 0 && !string.IsNullOrWhiteSpace(input.Address))
                {
                    var addressId = input.CustomerAddressId is > 0
                        ? Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, """
                            SELECT CustomerAddressId
                            FROM DireccionesCliente
                            WHERE CustomerAddressId = @CustomerAddressId AND CustomerId = @CustomerId
                            LIMIT 1;
                            """,
                            new SqlParameter("@CustomerAddressId", input.CustomerAddressId.Value),
                            new SqlParameter("@CustomerId", customerId)) ?? 0)
                        : 0;

                    await ExecuteInTransactionAsync(connection, transaction,
                        "UPDATE DireccionesCliente SET IsDefault = 0 WHERE CustomerId = @CustomerId;",
                        new SqlParameter("@CustomerId", customerId));

                    if (addressId > 0)
                    {
                        await ExecuteInTransactionAsync(connection, transaction, """
                            UPDATE DireccionesCliente
                            SET Label = @AddressLabel,
                                AddressLine = @AddressLine,
                                Latitude = @Latitude,
                                Longitude = @Longitude,
                                IsDefault = 1
                            WHERE CustomerAddressId = @CustomerAddressId AND CustomerId = @CustomerId;
                            """,
                            new SqlParameter("@CustomerId", customerId),
                            new SqlParameter("@CustomerAddressId", addressId),
                            new SqlParameter("@AddressLabel", (object?)input.AddressLabel?.Trim() ?? "Principal"),
                            new SqlParameter("@AddressLine", input.Address.Trim()),
                            new SqlParameter("@Latitude", (object?)input.Latitude ?? DBNull.Value),
                            new SqlParameter("@Longitude", (object?)input.Longitude ?? DBNull.Value));
                    }
                    else
                    {
                        await ExecuteInTransactionAsync(connection, transaction, """
                            INSERT INTO DireccionesCliente (CustomerId, Label, AddressLine, Latitude, Longitude, IsDefault)
                            VALUES (@CustomerId, @AddressLabel, @AddressLine, @Latitude, @Longitude, 1);
                            """,
                            new SqlParameter("@CustomerId", customerId),
                            new SqlParameter("@AddressLabel", (object?)input.AddressLabel?.Trim() ?? "Principal"),
                            new SqlParameter("@AddressLine", input.Address.Trim()),
                            new SqlParameter("@Latitude", (object?)input.Latitude ?? DBNull.Value),
                            new SqlParameter("@Longitude", (object?)input.Longitude ?? DBNull.Value));
                    }
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            return;
        }

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            UPDATE dbo.Usuarios
            SET FirstName   = @FirstName,
                LastName    = @LastName,
                Phone       = @Phone,
                AddressLine = @AddressLine,
                PasswordHash = CASE WHEN NULLIF(@PasswordHash, N'') IS NULL THEN PasswordHash ELSE @PasswordHash END
            WHERE LOWER(Email) = LOWER(@Email);

            DECLARE @CustomerId int;
            SELECT @CustomerId = CustomerId FROM dbo.Clientes WHERE UserId = (SELECT UserId FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@Email));

            IF @CustomerId IS NOT NULL AND NULLIF(@AddressLine, N'') IS NOT NULL
            BEGIN
                IF @CustomerAddressId IS NOT NULL AND EXISTS (
                    SELECT 1 FROM dbo.DireccionesCliente WHERE CustomerAddressId = @CustomerAddressId AND CustomerId = @CustomerId
                )
                BEGIN
                    UPDATE dbo.DireccionesCliente
                    SET Label = @AddressLabel,
                        AddressLine = @AddressLine,
                        Latitude = @Latitude,
                        Longitude = @Longitude,
                        IsDefault = 1,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE CustomerAddressId = @CustomerAddressId;

                    UPDATE dbo.DireccionesCliente
                    SET IsDefault = 0, UpdatedAt = SYSUTCDATETIME()
                    WHERE CustomerId = @CustomerId AND CustomerAddressId <> @CustomerAddressId;
                END
                ELSE
                BEGIN
                    UPDATE dbo.DireccionesCliente
                    SET IsDefault = 0, UpdatedAt = SYSUTCDATETIME()
                    WHERE CustomerId = @CustomerId;

                    INSERT INTO dbo.DireccionesCliente (CustomerId, Label, AddressLine, Latitude, Longitude, IsDefault, Status, CreatedAt)
                    VALUES (@CustomerId, @AddressLabel, @AddressLine, @Latitude, @Longitude, 1, N'Activa', SYSUTCDATETIME());
                END
            END

            COMMIT TRAN;
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@Email", email),
            new SqlParameter("@FirstName", input.FirstName.Trim()),
            new SqlParameter("@LastName", input.LastName.Trim()),
            new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
            new SqlParameter("@AddressLine", (object?)input.Address?.Trim() ?? DBNull.Value),
            new SqlParameter("@PasswordHash", string.IsNullOrWhiteSpace(input.NewPassword) ? "" : HashPassword(input.NewPassword)),
            new SqlParameter("@CustomerAddressId", (object?)input.CustomerAddressId ?? DBNull.Value),
            new SqlParameter("@AddressLabel", (object?)input.AddressLabel?.Trim() ?? "Principal"),
            new SqlParameter("@Latitude", (object?)input.Latitude ?? DBNull.Value),
            new SqlParameter("@Longitude", (object?)input.Longitude ?? DBNull.Value));
    }

    public async Task<CustomerAddressData?> GetDefaultAddressByEmailAsync(string email)
    {
        var sql = UseMySql
            ? """
            SELECT
                ca.CustomerAddressId,
                ca.Label,
                ca.AddressLine,
                ca.Latitude,
                ca.Longitude,
                ca.IsDefault
            FROM DireccionesCliente ca
            INNER JOIN Clientes c ON c.CustomerId = ca.CustomerId
            INNER JOIN Usuarios u ON u.UserId = c.UserId
            WHERE LOWER(u.Email) = LOWER(@Email) AND ca.IsDefault = 1
            ORDER BY ca.CustomerAddressId DESC
            LIMIT 1;
            """
            : """
            SELECT TOP 1
                ca.CustomerAddressId,
                ca.Label,
                ca.AddressLine,
                ca.Latitude,
                ca.Longitude,
                ca.IsDefault
            FROM dbo.DireccionesCliente ca
            INNER JOIN dbo.Clientes c ON c.CustomerId = ca.CustomerId
            INNER JOIN dbo.Usuarios u ON u.UserId = c.UserId
            WHERE LOWER(u.Email) = LOWER(@Email) AND ca.IsDefault = 1
            ORDER BY ca.CustomerAddressId DESC;
            """;

        var rows = await QueryAsync(sql, MapCustomerAddress, new SqlParameter("@Email", email));
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<CustomerAddressData>> GetAddressesByEmailAsync(string email)
    {
        var sql = UseMySql
            ? """
            SELECT
                ca.CustomerAddressId,
                ca.Label,
                ca.AddressLine,
                ca.Latitude,
                ca.Longitude,
                ca.IsDefault
            FROM DireccionesCliente ca
            INNER JOIN Clientes c ON c.CustomerId = ca.CustomerId
            INNER JOIN Usuarios u ON u.UserId = c.UserId
            WHERE LOWER(u.Email) = LOWER(@Email)
            ORDER BY ca.IsDefault DESC, ca.CustomerAddressId DESC;
            """
            : """
            SELECT
                ca.CustomerAddressId,
                ca.Label,
                ca.AddressLine,
                ca.Latitude,
                ca.Longitude,
                ca.IsDefault
            FROM dbo.DireccionesCliente ca
            INNER JOIN dbo.Clientes c ON c.CustomerId = ca.CustomerId
            INNER JOIN dbo.Usuarios u ON u.UserId = c.UserId
            WHERE LOWER(u.Email) = LOWER(@Email) AND ca.Status = N'Activa'
            ORDER BY ca.IsDefault DESC, ca.CustomerAddressId DESC;
            """;

        return await QueryAsync(sql, MapCustomerAddress, new SqlParameter("@Email", email));
    }

    private static CustomerAddressData MapCustomerAddress(DbDataReader reader) => new(
        reader.GetInt32("CustomerAddressId"),
        reader.GetString("Label"),
        reader.GetString("AddressLine"),
        reader.GetNullableDecimal("Latitude"),
        reader.GetNullableDecimal("Longitude"),
        reader.GetBoolean("IsDefault")
    );

    public static bool HasValidCoordinates(decimal? latitude, decimal? longitude) =>
        latitude is >= -90 and <= 90 &&
        longitude is >= -180 and <= 180 &&
        !(latitude == 0 && longitude == 0);

    private static bool TryParseCoordinate(string? value, out decimal coordinate)
    {
        coordinate = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace(',', '.');
        return decimal.TryParse(
            normalized,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out coordinate);
    }

    public async Task<int> CreateOrderAsync(CreateOrderInput input, string? userEmail = null)
    {
        if (UseMySql)
            return await CreateOrderMySqlAsync(input, userEmail);

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @CustomerId int;
            SELECT @CustomerId = CustomerId FROM dbo.Clientes WHERE LOWER(Email) = LOWER(@Email);

            IF @CustomerId IS NULL
            BEGIN
                INSERT INTO dbo.Clientes (FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
                VALUES (@CustomerName, @Email, @Phone, 0, 0, SYSUTCDATETIME());
                SET @CustomerId = SCOPE_IDENTITY();
            END;

            DECLARE @OriginLat decimal(10,6) = TRY_CAST((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'originLatitude') AS decimal(10,6));
            DECLARE @OriginLng decimal(10,6) = TRY_CAST((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'originLongitude') AS decimal(10,6));
            DECLARE @OriginName nvarchar(160) = COALESCE((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'originName'), N'BakeSmart Patri');
            IF @OriginLat IS NULL SET @OriginLat = 9.9142;
            IF @OriginLng IS NULL SET @OriginLng = -84.0734;

            DECLARE @DestLat decimal(10,6) = @DestinationLatitude;
            DECLARE @DestLng decimal(10,6) = @DestinationLongitude;
            DECLARE @DestLabel nvarchar(160) = COALESCE(NULLIF(@Address, N''), N'Sin direccion');
            DECLARE @ResolvedAddressId int = @CustomerAddressId;

            IF @DeliveryMethod = N'retiro'
            BEGIN
                SET @DestLat = @OriginLat;
                SET @DestLng = @OriginLng;
                SET @DestLabel = @OriginName;
                SET @ResolvedAddressId = NULL;
            END
            ELSE IF @ResolvedAddressId IS NOT NULL AND EXISTS (
                SELECT 1 FROM dbo.DireccionesCliente WHERE CustomerAddressId = @ResolvedAddressId AND CustomerId = @CustomerId
            )
            BEGIN
                SELECT
                    @DestLat = COALESCE(@DestLat, Latitude),
                    @DestLng = COALESCE(@DestLng, Longitude),
                    @DestLabel = COALESCE(NULLIF(@Address, N''), AddressLine)
                FROM dbo.DireccionesCliente
                WHERE CustomerAddressId = @ResolvedAddressId;
            END

            IF @DeliveryMethod <> N'retiro' AND (@DestLat IS NULL OR @DestLng IS NULL)
                THROW 50020, 'Debe indicar una ubicacion de entrega valida en el mapa.', 1;

            DECLARE @InventoryLocationId int;
            DECLARE @AvailableStock decimal(18,2);

            SELECT TOP 1
                @InventoryLocationId = ib.InventoryLocationId,
                @AvailableStock = ib.Quantity
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN dbo.ExistenciasInventario ib ON ib.ProductId = p.ProductId
            WHERE p.ProductId = @ProductId
              AND p.IsActive = 1
              AND pt.Name = N'Producto terminado'
            ORDER BY ib.Quantity DESC;

            IF @InventoryLocationId IS NULL
                THROW 50030, 'El producto seleccionado no esta disponible para venta.', 1;

            IF @AvailableStock < @Quantity
                THROW 50031, 'No hay stock suficiente para completar el pedido.', 1;

            DECLARE @WebChannelId int = (SELECT OrderChannelId FROM dbo.CanalesPedido WHERE Name = N'Web');
            DECLARE @PendingStatusId int = (SELECT OrderStatusId FROM dbo.EstadosPedido WHERE Name = N'Pendiente pago');
            DECLARE @PendingPaymentId int = (SELECT PaymentStatusId FROM dbo.EstadosPago WHERE Name = N'Pendiente');
            DECLARE @CashMethodId int = (SELECT PaymentMethodId FROM dbo.MetodosPago WHERE Name = @PaymentMethod);
            IF @CashMethodId IS NULL SELECT @CashMethodId = PaymentMethodId FROM dbo.MetodosPago WHERE Name = N'Pendiente';

            DECLARE @FrequentDiscountRate decimal(18,4) = TRY_CAST((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'frequentCustomerDiscount') AS decimal(18,4));
            
            DECLARE @TaxRate decimal(18,4) = TRY_CAST((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'iva') AS decimal(18,4));
            IF @FrequentDiscountRate IS NULL SET @FrequentDiscountRate = 0;
            IF @TaxRate IS NULL SET @TaxRate = 0.13;

            DECLARE @EffectiveDiscount decimal(18,2) = 0;
            IF EXISTS (SELECT 1 FROM dbo.Clientes WHERE CustomerId = @CustomerId AND IsFrequent = 1)
                SET @EffectiveDiscount = ROUND(@Subtotal * @FrequentDiscountRate, 2);

            DECLARE @DiscountedSubtotal decimal(18,2) = @Subtotal - @EffectiveDiscount;
            DECLARE @EffectiveTax decimal(18,2) = ROUND(@DiscountedSubtotal * @TaxRate, 2);
            DECLARE @EffectiveTotal decimal(18,2) = @DiscountedSubtotal + @EffectiveTax;

            INSERT INTO dbo.Pedidos
                (CustomerId, CustomerAddressId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId,
                 Notes, Subtotal, Discount, Tax, Total, DeliveryDate,
                 CurrentLatitude, CurrentLongitude,
                 DestinationLatitude, DestinationLongitude, DestinationLabel, DestinationCountry,
                 RouteMode, OriginLabel)
            VALUES
                (@CustomerId, @ResolvedAddressId, @WebChannelId, @PendingStatusId, @PendingPaymentId, @CashMethodId,
                 @Notes, @Subtotal, @EffectiveDiscount, @EffectiveTax, @EffectiveTotal, @DeliveryDate,
                 @OriginLat, @OriginLng,
                 @DestLat, @DestLng, @DestLabel, N'Costa Rica',
                 CASE WHEN @DeliveryMethod = N'retiro' THEN N'pickup' ELSE N'ground' END, @OriginName);

            DECLARE @OrderId int = SCOPE_IDENTITY();

            INSERT INTO dbo.DetallePedido (OrderId, ProductId, Quantity, UnitPrice)
            VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice);

            UPDATE dbo.ExistenciasInventario
            SET Quantity = Quantity - @Quantity,
                UpdatedAt = SYSUTCDATETIME()
            WHERE ProductId = @ProductId
              AND InventoryLocationId = @InventoryLocationId;

            INSERT INTO dbo.MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt)
            VALUES (@ProductId, @InventoryLocationId, N'SALIDA', @Quantity, CONCAT(N'Pedido web #', @OrderId), SYSUTCDATETIME());

            INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
            VALUES (@OrderId, @PendingStatusId, N'Pedido creado desde formulario web', SYSUTCDATETIME());

            COMMIT TRAN;
            SELECT @OrderId;
            """;

        var notes = input.Notes?.Trim();
        if (!string.IsNullOrWhiteSpace(input.DeliveryReference))
        {
            var deliveryReference = $"Referencia de entrega: {input.DeliveryReference.Trim()}";
            notes = string.IsNullOrWhiteSpace(notes) ? deliveryReference : $"{notes}\n{deliveryReference}";
        }

        var orderId = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@CustomerName", input.CustomerName.Trim()),
            new SqlParameter("@Email", input.Email.Trim().ToLowerInvariant()),
            new SqlParameter("@Phone", (object?)input.Phone?.Trim() ?? DBNull.Value),
            new SqlParameter("@ProductId", input.ProductId),
            new SqlParameter("@Quantity", input.Quantity),
            new SqlParameter("@UnitPrice", input.UnitPrice),
            new SqlParameter("@Subtotal", input.Subtotal),
            new SqlParameter("@Tax", input.Tax),
            new SqlParameter("@Total", input.Total),
            new SqlParameter("@DeliveryDate", input.DeliveryDate),
            new SqlParameter("@Address", (object?)input.Address?.Trim() ?? DBNull.Value),
            new SqlParameter("@Notes", (object?)notes ?? DBNull.Value),
            new SqlParameter("@PaymentMethod", (object?)input.PaymentMethod?.Trim() ?? "Pendiente"),
            new SqlParameter("@DestinationLatitude", (object?)input.DestinationLatitude ?? DBNull.Value),
            new SqlParameter("@DestinationLongitude", (object?)input.DestinationLongitude ?? DBNull.Value),
            new SqlParameter("@CustomerAddressId", (object?)input.CustomerAddressId ?? DBNull.Value),
            new SqlParameter("@DeliveryMethod", (object?)input.DeliveryMethod?.Trim() ?? "domicilio")));

        await AddAuditLogAsync("CREAR_PEDIDO", $"Pedido #{orderId} creado para {input.CustomerName}", userEmail);
        return orderId;
    }

    public async Task<int> OpenCashSessionAsync(decimal openingAmount, string? userEmail = null)
    {
        if (UseMySql)
        {
            const string userSql = "SELECT UserId FROM Usuarios WHERE LOWER(Email) = LOWER(@UserEmail) LIMIT 1;";
            var userId = Convert.ToInt32(await ScalarAsync(userSql, new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value)) ?? 0);
            if (userId <= 0)
                throw new InvalidOperationException("No se encontro el usuario para abrir caja.");

            const string activeSql = """
                SELECT COUNT(1)
                FROM SesionesCaja
                WHERE Status = 'Abierta' AND OpenedByUserId = @UserId;
                """;
            var activeSessionsForUser = Convert.ToInt32(await ScalarAsync(activeSql, new SqlParameter("@UserId", userId)));
            if (activeSessionsForUser > 0)
                throw new InvalidOperationException("Ya tiene una caja abierta. Debe cerrarla antes de abrir otra.");

            const string insertSql = """
                INSERT INTO SesionesCaja (OpenedByUserId, OpeningAmount, Status, OpenedAt)
                VALUES (@UserId, @Amount, 'Abierta', UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """;

            var newSessionId = Convert.ToInt32(await ScalarAsync(insertSql,
                new SqlParameter("@UserId", userId),
                new SqlParameter("@Amount", openingAmount)));

            await AddAuditLogAsync("APERTURA_CAJA", $"Sesion de caja #{newSessionId} abierta con {openingAmount:N0}", userEmail);
            return newSessionId;
        }

        // Verificar que no haya sesiÃ³n activa
        const string checkSql = """
            DECLARE @UserId int;
            IF @UserEmail IS NOT NULL
                SELECT @UserId = UserId FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@UserEmail);

            SELECT COUNT(1)
            FROM dbo.SesionesCaja
            WHERE Status = N'Abierta'
              AND (
                    (@UserEmail IS NULL AND OpenedByUserId IS NULL)
                    OR OpenedByUserId = @UserId
                  );
            """;
        var activeSessions = Convert.ToInt32(await ScalarAsync(checkSql,
            new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value)));
        if (activeSessions > 0)
            throw new InvalidOperationException("Ya tiene una caja abierta. Debe cerrarla antes de abrir otra.");

        const string sql = """
            DECLARE @UserId int;
            IF @UserEmail IS NOT NULL
                SELECT @UserId = UserId FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@UserEmail);

            INSERT INTO dbo.SesionesCaja (OpenedByUserId, OpeningAmount, Status, OpenedAt)
            VALUES (@UserId, @Amount, N'Abierta', SYSUTCDATETIME());

            SELECT CONVERT(int, SCOPE_IDENTITY());
            """;

        var sessionId = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
            new SqlParameter("@Amount", openingAmount)));

        await AddAuditLogAsync("APERTURA_CAJA", $"SesiÃ³n de caja #{sessionId} abierta con â‚¡{openingAmount:N0}", userEmail);
        return sessionId;
    }

    public async Task<bool> HasOpenCashSessionAsync(string? userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return false;
        var sql = UseMySql
            ? """
              SELECT COUNT(1)
              FROM SesionesCaja cs
              INNER JOIN Usuarios u ON u.UserId = cs.OpenedByUserId
              WHERE cs.Status = 'Abierta' AND LOWER(u.Email) = LOWER(@UserEmail);
              """
            : """
              SELECT COUNT(1)
              FROM dbo.SesionesCaja cs
              INNER JOIN dbo.Usuarios u ON u.UserId = cs.OpenedByUserId
              WHERE cs.Status = N'Abierta' AND LOWER(u.Email) = LOWER(@UserEmail);
              """;
        return Convert.ToInt32(await ScalarAsync(sql, new SqlParameter("@UserEmail", userEmail.Trim()))) > 0;
    }

    public async Task CloseCashSessionAsync(int sessionId, decimal closingAmount, string? userEmail = null)
    {
        if (UseMySql)
        {
            const string closeSql = """
                UPDATE SesionesCaja cs
                INNER JOIN Usuarios u ON u.UserId = cs.OpenedByUserId
                SET cs.ClosingAmount = @ClosingAmount,
                    cs.Status = 'Cerrada',
                    cs.ClosedAt = UTC_TIMESTAMP()
                WHERE cs.CashSessionId = @SessionId
                  AND cs.Status = 'Abierta'
                  AND LOWER(u.Email) = LOWER(@UserEmail);
                SELECT ROW_COUNT();
                """;

            var updatedRows = Convert.ToInt32(await ScalarAsync(closeSql,
                new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
                new SqlParameter("@SessionId", sessionId),
                new SqlParameter("@ClosingAmount", closingAmount)));

            if (updatedRows == 0)
                throw new InvalidOperationException("No se encontro una caja abierta para cerrar.");

            await AddAuditLogAsync("CIERRE_CAJA", $"Sesion de caja #{sessionId} cerrada con {closingAmount:N0}", userEmail);
            return;
        }

        const string sql = """
            DECLARE @Updated int = 0;
            DECLARE @UserId int;

            IF @UserEmail IS NOT NULL
                SELECT @UserId = UserId FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@UserEmail);

            UPDATE dbo.SesionesCaja
            SET ClosingAmount = @ClosingAmount,
                Status = N'Cerrada',
                ClosedAt = SYSUTCDATETIME()
            WHERE CashSessionId = @SessionId
              AND Status = N'Abierta'
              AND (
                    (@UserEmail IS NULL AND OpenedByUserId IS NULL)
                    OR OpenedByUserId = @UserId
                  );

            SET @Updated = @@ROWCOUNT;
            SELECT @Updated;
            """;

        var updated = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
            new SqlParameter("@SessionId", sessionId),
            new SqlParameter("@ClosingAmount", closingAmount)));

        if (updated == 0)
            throw new InvalidOperationException("No se encontro una caja abierta para cerrar.");

        await AddAuditLogAsync("CIERRE_CAJA", $"SesiÃ³n de caja #{sessionId} cerrada con â‚¡{closingAmount:N0}", userEmail);
    }

    public async Task<IReadOnlyList<object>> CashSessionsAsync(string? userEmail = null, bool includeAll = false)
    {
        const string sql = """
            SELECT cs.CashSessionId, cs.OpenedAt, cs.ClosedAt, cs.OpeningAmount, cs.ClosingAmount, cs.Status,
                   COALESCE(CONCAT(u.FirstName, N' ', u.LastName), N'Sistema') AS UserName,
                   COALESCE(u.Email, N'') AS UserEmail,
                   COALESCE(SUM(csp.Amount), 0) AS TotalSales,
                   COALESCE(SUM(CASE WHEN LOWER(pm.Name) LIKE '%efectivo%' THEN csp.Amount ELSE 0 END), 0) AS CashSales,
                   COALESCE(SUM(CASE WHEN LOWER(pm.Name) LIKE '%tarjeta%' THEN csp.Amount ELSE 0 END), 0) AS CardSales,
                   COALESCE(SUM(CASE WHEN LOWER(pm.Name) LIKE '%sinpe%' THEN csp.Amount ELSE 0 END), 0) AS SinpeSales,
                   COALESCE(SUM(CASE WHEN LOWER(pm.Name) LIKE '%transfer%' THEN csp.Amount ELSE 0 END), 0) AS TransferSales
            FROM dbo.SesionesCaja cs
            LEFT JOIN dbo.Usuarios u ON u.UserId = cs.OpenedByUserId
            LEFT JOIN dbo.PagosSesionCaja csp ON csp.CashSessionId = cs.CashSessionId
            LEFT JOIN dbo.Ventas v ON v.SaleId = csp.SaleId
            LEFT JOIN dbo.MetodosPago pm ON pm.PaymentMethodId = v.PaymentMethodId
            WHERE @IncludeAll = 1
               OR @UserEmail IS NULL
               OR LOWER(u.Email) = LOWER(@UserEmail)
            GROUP BY cs.CashSessionId, cs.OpenedAt, cs.ClosedAt, cs.OpeningAmount, cs.ClosingAmount, cs.Status, u.FirstName, u.LastName, u.Email
            ORDER BY cs.OpenedAt DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("CashSessionId"),
            openedAt = DateTime.SpecifyKind(reader.GetDateTime("OpenedAt"), DateTimeKind.Utc).ToString("O"),
            closedAt = reader.IsDBNull(reader.GetOrdinal("ClosedAt")) ? null : DateTime.SpecifyKind(reader.GetDateTime("ClosedAt"), DateTimeKind.Utc).ToString("O"),
            openingAmount = reader.GetDecimal("OpeningAmount"),
            closingAmount = reader.IsDBNull(reader.GetOrdinal("ClosingAmount")) ? (decimal?)null : reader.GetDecimal("ClosingAmount"),
            totalSales = reader.GetDecimal("TotalSales"),
            cashSales = reader.GetDecimal("CashSales"),
            cardSales = reader.GetDecimal("CardSales"),
            sinpeSales = reader.GetDecimal("SinpeSales"),
            transferSales = reader.GetDecimal("TransferSales"),
            expectedCash = reader.GetDecimal("OpeningAmount") + reader.GetDecimal("CashSales"),
            userName = reader.GetString("UserName"),
            userEmail = reader.GetString("UserEmail"),
            status = reader.GetString("Status")
        },
        new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
        new SqlParameter("@IncludeAll", includeAll));
    }

    public async Task<IReadOnlyList<object>> RecentPosSalesAsync()
    {
        await EnsureCommerceSchemaAsync();
        if (UseMySql)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS NotasCreditoPOS
                (
                    CreditNoteId int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    SaleId int NOT NULL,
                    Reason varchar(300) NOT NULL,
                    Amount decimal(18,2) NOT NULL,
                    CreatedAt datetime NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                """);
        }

        var sql = UseMySql
            ? """
            SELECT
                v.SaleId,
                v.OrderId,
                v.CreatedAt,
                v.Total,
                pm.Name AS PaymentMethod,
                c.FullName AS CustomerName,
                COALESCE(o.Notes, '') AS Notes,
                COALESCE(cs.CashSessionId, 0) AS CashSessionId,
                COALESCE((SELECT GROUP_CONCAT(CONCAT(cb.Name, ' x', FORMAT(vc.Quantity, 0)) SEPARATOR ' Â· ')
                          FROM VentaCombos vc INNER JOIN Combos cb ON cb.ComboId = vc.ComboId
                          WHERE vc.SaleId = v.SaleId), '') AS ComboSummary,
                CASE WHEN cn.CreditNoteId IS NULL THEN 0 ELSE 1 END AS HasCreditNote
            FROM Ventas v
            INNER JOIN Pedidos o ON o.OrderId = v.OrderId
            INNER JOIN Clientes c ON c.CustomerId = o.CustomerId
            INNER JOIN MetodosPago pm ON pm.PaymentMethodId = v.PaymentMethodId
            LEFT JOIN PagosSesionCaja csp ON csp.SaleId = v.SaleId
            LEFT JOIN SesionesCaja cs ON cs.CashSessionId = csp.CashSessionId
            LEFT JOIN (
                SELECT SaleId, MAX(CreditNoteId) AS CreditNoteId
                FROM NotasCreditoPOS
                GROUP BY SaleId
            ) cn ON cn.SaleId = v.SaleId
            ORDER BY v.CreatedAt DESC, v.SaleId DESC
            LIMIT 25;
            """
            : """
            IF OBJECT_ID(N'dbo.NotasCreditoPOS', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.NotasCreditoPOS
                (
                    CreditNoteId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    SaleId int NOT NULL,
                    Reason nvarchar(300) NOT NULL,
                    Amount decimal(18,2) NOT NULL,
                    CreatedAt datetime2 NOT NULL
                );
            END;

            SELECT TOP 25
                v.SaleId,
                v.OrderId,
                v.CreatedAt,
                v.Total,
                pm.Name AS PaymentMethod,
                c.FullName AS CustomerName,
                COALESCE(o.Notes, N'') AS Notes,
                COALESCE(cs.CashSessionId, 0) AS CashSessionId,
                COALESCE((SELECT STRING_AGG(CONCAT(cb.Name, N' x', CONVERT(int, vc.Quantity)), N' Â· ')
                          FROM dbo.VentaCombos vc INNER JOIN dbo.Combos cb ON cb.ComboId = vc.ComboId
                          WHERE vc.SaleId = v.SaleId), N'') AS ComboSummary,
                CASE WHEN cn.CreditNoteId IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS HasCreditNote
            FROM dbo.Ventas v
            INNER JOIN dbo.Pedidos o ON o.OrderId = v.OrderId
            INNER JOIN dbo.Clientes c ON c.CustomerId = o.CustomerId
            INNER JOIN dbo.MetodosPago pm ON pm.PaymentMethodId = v.PaymentMethodId
            LEFT JOIN dbo.PagosSesionCaja csp ON csp.SaleId = v.SaleId
            LEFT JOIN dbo.SesionesCaja cs ON cs.CashSessionId = csp.CashSessionId
            OUTER APPLY (
                SELECT TOP 1 CreditNoteId
                FROM dbo.NotasCreditoPOS n
                WHERE n.SaleId = v.SaleId
                ORDER BY n.CreditNoteId DESC
            ) cn
            ORDER BY v.CreatedAt DESC, v.SaleId DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            saleId = reader.GetInt32("SaleId"),
            orderId = reader.GetInt32("OrderId"),
            cashSessionId = reader.GetInt32("CashSessionId"),
            createdAt = reader.GetDateTime("CreatedAt").ToString("o"),
            customerName = reader.GetString("CustomerName"),
            paymentMethod = reader.GetString("PaymentMethod"),
            notes = reader.GetNullableString("Notes"),
            comboSummary = reader.GetString("ComboSummary"),
            total = reader.GetDecimal("Total"),
            hasCreditNote = reader.GetBoolean("HasCreditNote")
        });
    }

    public async Task<int> RegisterSaleAsync(SaleInput input, string? userEmail = null)
    {
        await EnsureCommerceSchemaAsync();
        if (UseMySql)
            return await RegisterSaleMySqlAsync(input, userEmail);

        // Serializar items a JSON para pasarlos como parÃ¡metro
        var itemsJson = System.Text.Json.JsonSerializer.Serialize(input.Items.Select(i => new
        {
            productId = i.ProductId,
            quantity = i.Quantity,
            unitPrice = i.UnitPrice
        }));

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

            DECLARE @SaleItems TABLE
            (
                ProductId int NOT NULL,
                Quantity decimal(18,2) NOT NULL,
                UnitPrice decimal(18,2) NOT NULL,
                InventoryLocationId int NULL,
                AvailableStock decimal(18,2) NULL
            );

            INSERT INTO @SaleItems (ProductId, Quantity, UnitPrice)
            SELECT ProductId, Quantity, UnitPrice
            FROM OPENJSON(@ItemsJson)
            WITH (
                ProductId int N'$.productId',
                Quantity decimal(18,2) N'$.quantity',
                UnitPrice decimal(18,2) N'$.unitPrice'
            );

            IF EXISTS (
                SELECT 1
                FROM @SaleItems si
                LEFT JOIN dbo.Productos p ON p.ProductId = si.ProductId
                LEFT JOIN dbo.TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
                WHERE p.ProductId IS NULL
                   OR p.IsActive = 0
                   OR pt.Name <> N'Producto terminado'
                   OR si.Quantity <= 0
            )
                THROW 50040, 'El carrito contiene productos no disponibles para venta.', 1;

            UPDATE si
            SET InventoryLocationId = stock.InventoryLocationId,
                AvailableStock = stock.Quantity
            FROM @SaleItems si
            OUTER APPLY (
                SELECT TOP 1 ib.InventoryLocationId, ib.Quantity
                FROM dbo.ExistenciasInventario ib
                WHERE ib.ProductId = si.ProductId
                ORDER BY ib.Quantity DESC
            ) stock;

            IF EXISTS (
                SELECT 1
                FROM @SaleItems
                WHERE InventoryLocationId IS NULL OR AvailableStock < Quantity
            )
                THROW 50041, 'No hay stock suficiente para completar la venta.', 1;

            -- Obtener o crear cliente
            DECLARE @CustomerId int;
            IF NULLIF(@CustomerEmail, N'') IS NOT NULL
                SELECT @CustomerId = CustomerId FROM dbo.Clientes WHERE LOWER(Email) = LOWER(@CustomerEmail);

            IF @CustomerId IS NULL AND NULLIF(@CustomerName, N'') IS NOT NULL
            BEGIN
                INSERT INTO dbo.Clientes (FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
                VALUES (@CustomerName, COALESCE(NULLIF(@CustomerEmail, N''), N'mostrador@local'), NULLIF(@CustomerPhone, N''), 0, 0, SYSUTCDATETIME());
                SET @CustomerId = SCOPE_IDENTITY();
            END;

            IF @CustomerId IS NULL
                SELECT TOP 1 @CustomerId = CustomerId FROM dbo.Clientes ORDER BY CustomerId;

            IF @CustomerId IS NULL
                THROW 50010, 'No se pudo identificar el cliente para la venta.', 1;

            -- Estados por defecto
            DECLARE @PosChannelId int = (SELECT OrderChannelId FROM dbo.CanalesPedido WHERE Name = N'POS');
            DECLARE @DeliveredStatusId int = (SELECT OrderStatusId FROM dbo.EstadosPedido WHERE Name = N'Entregado');
            DECLARE @PaidStatusId int = (SELECT PaymentStatusId FROM dbo.EstadosPago WHERE Name = N'Pagado');
            DECLARE @PaymentMethodId int = (SELECT PaymentMethodId FROM dbo.MetodosPago WHERE Name = @PaymentMethodName);
            IF @PaymentMethodId IS NULL SELECT TOP 1 @PaymentMethodId = PaymentMethodId FROM dbo.MetodosPago WHERE Name = N'Efectivo';

            DECLARE @CurrentUserId int;
            IF @UserEmail IS NOT NULL
                SELECT @CurrentUserId = UserId FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@UserEmail);

            DECLARE @ActiveSessionId int = (
                SELECT TOP 1 CashSessionId
                FROM dbo.SesionesCaja
                WHERE Status = N'Abierta'
                  AND (
                        (@UserEmail IS NULL AND OpenedByUserId IS NULL)
                        OR OpenedByUserId = @CurrentUserId
                      )
                ORDER BY CashSessionId DESC
            );
            IF @ActiveSessionId IS NULL
                THROW 50042, 'Debe abrir caja antes de confirmar ventas.', 1;

            DECLARE @FrequentDiscountRate decimal(18,4) = TRY_CAST((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'frequentCustomerDiscount') AS decimal(18,4));
            DECLARE @PromotionDiscountRate decimal(18,4) = COALESCE((
                SELECT MAX(DiscountRate)
                FROM dbo.Promociones
                WHERE PromotionId = @PromotionId
                  AND IsActive = 1
                  AND CAST(SYSUTCDATETIME() AS date) BETWEEN StartDate AND EndDate
                  AND (NOT EXISTS (SELECT 1 FROM dbo.PromocionesClientes pc WHERE pc.PromotionId = @PromotionId)
                       OR EXISTS (SELECT 1 FROM dbo.PromocionesClientes pc WHERE pc.PromotionId = @PromotionId AND pc.CustomerId = @CustomerId))
            ), 0);
            DECLARE @TaxRate decimal(18,4) = TRY_CAST((SELECT SettingValue FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'iva') AS decimal(18,4));
            IF @FrequentDiscountRate IS NULL SET @FrequentDiscountRate = 0;
            IF @TaxRate IS NULL SET @TaxRate = 0.13;

            DECLARE @EffectiveDiscount decimal(18,2) = COALESCE(@Discount, 0);
            IF EXISTS (SELECT 1 FROM dbo.Clientes WHERE CustomerId = @CustomerId AND IsFrequent = 1)
            BEGIN
                DECLARE @FrequentDiscount decimal(18,2) = ROUND(@Subtotal * @FrequentDiscountRate, 2);
                IF @FrequentDiscount > @EffectiveDiscount SET @EffectiveDiscount = @FrequentDiscount;
            END;
            DECLARE @PromotionBase decimal(18,2) = @Subtotal;
            IF EXISTS (SELECT 1 FROM dbo.ProductosPromocion WHERE PromotionId = @PromotionId)
                SELECT @PromotionBase = COALESCE(SUM(si.Quantity * si.UnitPrice), 0)
                FROM @SaleItems si
                INNER JOIN dbo.ProductosPromocion pp ON pp.ProductId = si.ProductId AND pp.PromotionId = @PromotionId;
            DECLARE @PromotionDiscount decimal(18,2) = ROUND(@PromotionBase * @PromotionDiscountRate, 2);
            IF @PromotionDiscount > @EffectiveDiscount SET @EffectiveDiscount = @PromotionDiscount;

            DECLARE @DiscountedSubtotal decimal(18,2) = @Subtotal - @EffectiveDiscount;
            IF @DiscountedSubtotal < 0 SET @DiscountedSubtotal = 0;
            DECLARE @EffectiveTax decimal(18,2) = ROUND(@DiscountedSubtotal * @TaxRate, 2);
            DECLARE @EffectiveTotal decimal(18,2) = @DiscountedSubtotal + @EffectiveTax;

            -- Crear pedido (venta directa POS)
            INSERT INTO dbo.Pedidos
                (CustomerId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId,
                 Subtotal, Discount, Tax, Total, Notes, DeliveryDate,
                 CurrentLatitude, CurrentLongitude,
                 DestinationLatitude, DestinationLongitude, DestinationLabel, DestinationCountry,
                 RouteMode, OriginLabel, TrackingStep)
            VALUES
                (@CustomerId, @PosChannelId, @DeliveredStatusId, @PaidStatusId, @PaymentMethodId,
                 @Subtotal, @EffectiveDiscount, @EffectiveTax, @EffectiveTotal, NULLIF(@Notes, N''), CAST(SYSUTCDATETIME() AS date),
                 9.9142, -84.0734,
                 9.9142, -84.0734, N'Tienda BakeSmart', N'Costa Rica',
                 N'pickup', N'BakeSmart Patri', 6);

            DECLARE @OrderId int = SCOPE_IDENTITY();

            -- Registrar productos del pedido desde JSON
            INSERT INTO dbo.DetallePedido (OrderId, ProductId, Quantity, UnitPrice)
            SELECT @OrderId, ProductId, Quantity, UnitPrice
            FROM @SaleItems;

            UPDATE ib
            SET Quantity = ib.Quantity - si.Quantity,
                UpdatedAt = SYSUTCDATETIME()
            FROM dbo.ExistenciasInventario ib
            INNER JOIN @SaleItems si
                ON si.ProductId = ib.ProductId
               AND si.InventoryLocationId = ib.InventoryLocationId;

            INSERT INTO dbo.MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt)
            SELECT ProductId, InventoryLocationId, N'SALIDA', Quantity, CONCAT(N'Venta POS #', @OrderId), SYSUTCDATETIME()
            FROM @SaleItems;

            -- Crear venta
            INSERT INTO dbo.Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
            VALUES (@OrderId, @PaymentMethodId, @Subtotal, @EffectiveTax, @EffectiveTotal, SYSUTCDATETIME());

            DECLARE @SaleId int = SCOPE_IDENTITY();

            -- Asociar a sesiÃ³n de caja activa
            INSERT INTO dbo.PagosSesionCaja (CashSessionId, SaleId, Amount)
            VALUES (@ActiveSessionId, @SaleId, @EffectiveTotal);

            DECLARE @CashAccountId int;
            DECLARE @IncomeAccountId int;

            SELECT @CashAccountId = AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'1-02';
            IF @CashAccountId IS NULL
            BEGIN
                INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                VALUES (N'1-02', N'Banco / SINPE / Tarjeta', N'ACTIVO');
                SET @CashAccountId = SCOPE_IDENTITY();
            END;

            SELECT @IncomeAccountId = AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'4-01';
            IF @IncomeAccountId IS NULL
            BEGIN
                INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
                VALUES (N'4-01', N'Ingresos por ventas', N'INGRESO');
                SET @IncomeAccountId = SCOPE_IDENTITY();
            END;

            IF @EffectiveTotal > 0
            BEGIN
                INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
                VALUES (N'VENTA', N'Ventas', @SaleId, CONCAT(N'Venta POS pedido #', @OrderId), SYSUTCDATETIME());
                DECLARE @EntryId int = SCOPE_IDENTITY();

                INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
                VALUES (@EntryId, @CashAccountId, @EffectiveTotal, 0), (@EntryId, @IncomeAccountId, 0, @EffectiveTotal);
            END;

            COMMIT TRAN;
            SELECT @OrderId;
            """;

        var orderId = Convert.ToInt32(await ScalarAsync(sql,
            new SqlParameter("@CustomerName", (object?)input.CustomerName?.Trim() ?? DBNull.Value),
            new SqlParameter("@CustomerEmail", (object?)input.CustomerEmail?.Trim() ?? DBNull.Value),
            new SqlParameter("@CustomerPhone", (object?)input.CustomerPhone?.Trim() ?? DBNull.Value),
            new SqlParameter("@PaymentMethodName", (object?)input.PaymentMethod?.Trim() ?? "Efectivo"),
            new SqlParameter("@Subtotal", input.Subtotal),
            new SqlParameter("@Discount", input.Discount),
            new SqlParameter("@PromotionId", (object?)input.PromotionId ?? DBNull.Value),
            new SqlParameter("@Tax", input.Tax),
            new SqlParameter("@Total", input.Total),
            new SqlParameter("@Notes", (object?)input.Notes?.Trim() ?? DBNull.Value),
            new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value),
            new SqlParameter("@ItemsJson", itemsJson)));

        await AddAuditLogAsync("VENTA_POS", $"Venta POS #{orderId} por â‚¡{input.Total:N0}", userEmail);
        return orderId;
    }

    private async Task<int> CreateOrderMySqlAsync(CreateOrderInput input, string? userEmail)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var customerId = await EnsureCustomerForOrderMySqlAsync(connection, transaction, input.CustomerName, input.Email, input.Phone);
            var config = await LoadOperationalConfigMySqlAsync(connection, transaction);
            var deliveryMethod = string.IsNullOrWhiteSpace(input.DeliveryMethod) ? "domicilio" : input.DeliveryMethod.Trim().ToLowerInvariant();
            var destinationLat = input.DestinationLatitude;
            var destinationLng = input.DestinationLongitude;
            var destinationLabel = string.IsNullOrWhiteSpace(input.Address) ? "Sin direccion" : input.Address.Trim();
            var addressId = input.CustomerAddressId;

            if (addressId is > 0)
            {
                const string addressSql = """
                    SELECT CustomerAddressId, AddressLine, Latitude, Longitude
                    FROM DireccionesCliente
                    WHERE CustomerAddressId = @AddressId AND CustomerId = @CustomerId
                    LIMIT 1;
                    """;
                await using var addressCommand = CreateCommand(connection, addressSql,
                    new[] { new SqlParameter("@AddressId", addressId.Value), new SqlParameter("@CustomerId", customerId) }, transaction);
                await using var addressReader = await addressCommand.ExecuteReaderAsync();
                if (await addressReader.ReadAsync())
                {
                    destinationLat ??= addressReader.GetNullableDecimal("Latitude");
                    destinationLng ??= addressReader.GetNullableDecimal("Longitude");
                    destinationLabel = addressReader.GetNullableString("AddressLine") ?? destinationLabel;
                }
            }

            if (deliveryMethod != "retiro" && !HasValidCoordinates(destinationLat, destinationLng))
                throw new InvalidOperationException("Debe indicar una ubicacion de entrega valida en el mapa.");

            if (deliveryMethod == "retiro")
            {
                destinationLat = config.OriginLatitude;
                destinationLng = config.OriginLongitude;
                destinationLabel = config.OriginName;
            }

            var stock = await ResolveProductStockMySqlAsync(connection, transaction, input.ProductId, input.Quantity);
            var subtotal = Math.Round(stock.UnitPrice * input.Quantity, 2, MidpointRounding.AwayFromZero);
            var paymentMethodId = await ResolvePaymentMethodMySqlAsync(connection, transaction, input.PaymentMethod, fallbackToCash: false);
            var channelId = await ResolveLookupIdMySqlAsync(connection, transaction, "CanalesPedido", "OrderChannelId", "Name", "Web");
            var statusId = await ResolveLookupIdMySqlAsync(connection, transaction, "EstadosPedido", "OrderStatusId", "Name", "Pendiente pago");
            var paymentStatusId = await ResolveLookupIdMySqlAsync(connection, transaction, "EstadosPago", "PaymentStatusId", "Name", "Pendiente");
            var isFrequent = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                "SELECT IsFrequent FROM Clientes WHERE CustomerId = @CustomerId;",
                new SqlParameter("@CustomerId", customerId)) ?? 0) == 1;
            var discount = isFrequent ? Math.Round(subtotal * config.FrequentDiscountRate, 2, MidpointRounding.AwayFromZero) : 0m;
            var tax = Math.Round((subtotal - discount) * config.IvaRate, 2, MidpointRounding.AwayFromZero);
            var total = Math.Round(subtotal - discount + tax, 2, MidpointRounding.AwayFromZero);

            const string orderSql = """
                INSERT INTO Pedidos
                    (CustomerId, CustomerAddressId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId,
                     Notes, Subtotal, Discount, Tax, Total, DeliveryDate,
                     CurrentLatitude, CurrentLongitude, DestinationLatitude, DestinationLongitude, DestinationLabel,
                     DestinationCountry, RouteMode, TrackingStep, OriginLabel, CreatedAt)
                VALUES
                    (@CustomerId, @CustomerAddressId, @ChannelId, @StatusId, @PaymentStatusId, @PaymentMethodId,
                     @Notes, @Subtotal, @Discount, @Tax, @Total, @DeliveryDate,
                     @OriginLat, @OriginLng, @DestLat, @DestLng, @DestLabel,
                     'Costa Rica', @RouteMode, 0, @OriginName, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """;

            var orderId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, orderSql,
                new SqlParameter("@CustomerId", customerId),
                new SqlParameter("@CustomerAddressId", (object?)addressId ?? DBNull.Value),
                new SqlParameter("@ChannelId", channelId),
                new SqlParameter("@StatusId", statusId),
                new SqlParameter("@PaymentStatusId", paymentStatusId),
                new SqlParameter("@PaymentMethodId", paymentMethodId),
                new SqlParameter("@Notes", (object?)BuildOrderNotes(input.Notes, input.DeliveryReference) ?? DBNull.Value),
                new SqlParameter("@Subtotal", subtotal),
                new SqlParameter("@Discount", discount),
                new SqlParameter("@Tax", tax),
                new SqlParameter("@Total", total),
                new SqlParameter("@DeliveryDate", input.DeliveryDate.Date),
                new SqlParameter("@OriginLat", config.OriginLatitude),
                new SqlParameter("@OriginLng", config.OriginLongitude),
                new SqlParameter("@DestLat", destinationLat!.Value),
                new SqlParameter("@DestLng", destinationLng!.Value),
                new SqlParameter("@DestLabel", destinationLabel),
                new SqlParameter("@RouteMode", deliveryMethod == "retiro" ? "pickup" : "ground"),
                new SqlParameter("@OriginName", config.OriginName)));

            await ExecuteInTransactionAsync(connection, transaction,
                "INSERT INTO DetallePedido (OrderId, ProductId, Quantity, UnitPrice) VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice);",
                new SqlParameter("@OrderId", orderId),
                new SqlParameter("@ProductId", input.ProductId),
                new SqlParameter("@Quantity", input.Quantity),
                new SqlParameter("@UnitPrice", stock.UnitPrice));
            await DeductStockMySqlAsync(connection, transaction, input.ProductId, stock.InventoryLocationId, input.Quantity, $"Pedido web #{orderId}");
            await ExecuteInTransactionAsync(connection, transaction,
                "INSERT INTO EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt) VALUES (@OrderId, @StatusId, 'Pedido creado desde formulario web', UTC_TIMESTAMP());",
                new SqlParameter("@OrderId", orderId),
                new SqlParameter("@StatusId", statusId));

            await transaction.CommitAsync();
            await AddAuditLogAsync("CREAR_PEDIDO", $"Pedido #{orderId} creado para {input.CustomerName}", userEmail);
            return orderId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<int> RegisterSaleMySqlAsync(SaleInput input, string? userEmail)
    {
        if (input.Items.Count == 0 && (input.Combos?.Count ?? 0) == 0)
            throw new InvalidOperationException("El carrito esta vacio.");

        var activeCombos = (await CombosAsync(activeOnly: true)).ToDictionary(combo => combo.Id);
        var comboSelections = (input.Combos ?? []).Where(combo => combo.ComboId > 0 && combo.Quantity > 0).ToArray();
        foreach (var selection in comboSelections)
            if (!activeCombos.ContainsKey(selection.ComboId)) throw new InvalidOperationException("Uno de los combos ya no estÃ¡ disponible.");

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var expandedItems = new List<SaleItemInput>();
            decimal saleSubtotal = 0;
            foreach (var item in input.Items.Where(item => item.ProductId > 0 && item.Quantity > 0))
            {
                var price = Convert.ToDecimal(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT p.UnitPrice FROM Productos p INNER JOIN TiposProducto t ON t.ProductTypeId=p.ProductTypeId
                    WHERE p.ProductId=@ProductId AND p.IsActive=1 AND t.Name='Producto terminado' LIMIT 1;
                    """, new SqlParameter("@ProductId", item.ProductId)) ?? throw new InvalidOperationException("Uno de los productos ya no estÃ¡ disponible."));
                expandedItems.Add(new SaleItemInput(item.ProductId, item.Quantity, price));
                saleSubtotal += price * item.Quantity;
            }
            foreach (var selection in comboSelections)
            {
                var combo = activeCombos[selection.ComboId];
                saleSubtotal += combo.SpecialPrice * selection.Quantity;
                foreach (var component in combo.Items)
                {
                    var lineRegular = component.UnitPrice * component.Quantity;
                    var allocatedLine = combo.RegularPrice > 0 ? combo.SpecialPrice * lineRegular / combo.RegularPrice : 0;
                    var allocatedUnit = component.Quantity > 0 ? allocatedLine / component.Quantity : 0;
                    expandedItems.Add(new SaleItemInput(component.ProductId, component.Quantity * selection.Quantity, Math.Round(allocatedUnit, 4)));
                }
            }
            if (expandedItems.Count == 0) throw new InvalidOperationException("El carrito no contiene productos vÃ¡lidos.");
            var email = string.IsNullOrWhiteSpace(input.CustomerEmail)
                ? $"mostrador-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}@local"
                : input.CustomerEmail.Trim().ToLowerInvariant();
            var customerId = await EnsureCustomerForOrderMySqlAsync(connection, transaction,
                string.IsNullOrWhiteSpace(input.CustomerName) ? "Cliente mostrador" : input.CustomerName!,
                email,
                input.CustomerPhone);
            var config = await LoadOperationalConfigMySqlAsync(connection, transaction);
            var isFrequent = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                "SELECT IsFrequent FROM Clientes WHERE CustomerId = @CustomerId;",
                new SqlParameter("@CustomerId", customerId)) ?? 0) == 1;
            var frequentDiscount = isFrequent ? Math.Round(saleSubtotal * config.FrequentDiscountRate, 2) : 0m;
            var manualDiscount = Math.Clamp(input.Discount, 0m, saleSubtotal);
            var promotionDiscountRate = input.PromotionId is > 0
                ? Convert.ToDecimal(await ScalarInTransactionAsync(connection, transaction, """
                    SELECT COALESCE(MAX(p.DiscountRate), 0)
                    FROM Promociones p
                    WHERE p.PromotionId = @PromotionId
                      AND p.IsActive = 1
                      AND DATE(UTC_TIMESTAMP()) BETWEEN p.StartDate AND p.EndDate
                      AND (NOT EXISTS (SELECT 1 FROM PromocionesClientes pc WHERE pc.PromotionId = p.PromotionId)
                           OR EXISTS (SELECT 1 FROM PromocionesClientes pc WHERE pc.PromotionId = p.PromotionId AND pc.CustomerId = @CustomerId));
                    """, new SqlParameter("@PromotionId", input.PromotionId.Value), new SqlParameter("@CustomerId", customerId)) ?? 0m)
                : 0m;
            var promotionProductsCsv = input.PromotionId is > 0
                ? Convert.ToString(await ScalarInTransactionAsync(connection, transaction,
                    "SELECT GROUP_CONCAT(ProductId) FROM ProductosPromocion WHERE PromotionId = @PromotionId;",
                    new SqlParameter("@PromotionId", input.PromotionId.Value)))
                : null;
            var promotionProductIds = ParseIdList(promotionProductsCsv).ToHashSet();
            var promotionBase = promotionProductIds.Count == 0
                ? saleSubtotal
                : expandedItems.Where(item => promotionProductIds.Contains(item.ProductId)).Sum(item => item.UnitPrice * item.Quantity);
            var promotionDiscount = Math.Round(promotionBase * promotionDiscountRate, 2);
            var effectiveDiscount = Math.Max(Math.Max(frequentDiscount, manualDiscount), promotionDiscount);
            var taxable = Math.Max(0m, saleSubtotal - effectiveDiscount);
            var tax = Math.Round(taxable * config.IvaRate, 2);
            var total = taxable + tax;
            var paymentMethodId = await ResolvePaymentMethodMySqlAsync(connection, transaction, input.PaymentMethod);
            var channelId = await ResolveLookupIdMySqlAsync(connection, transaction, "CanalesPedido", "OrderChannelId", "Name", "POS");
            var statusId = await ResolveLookupIdMySqlAsync(connection, transaction, "EstadosPedido", "OrderStatusId", "Name", "Entregado");
            var paymentStatusId = await ResolveLookupIdMySqlAsync(connection, transaction, "EstadosPago", "PaymentStatusId", "Name", "Pagado");
            var cashSessionId = await ResolveOpenCashSessionMySqlAsync(connection, transaction, userEmail);

            const string orderSql = """
                INSERT INTO Pedidos
                    (CustomerId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId,
                     Subtotal, Discount, Tax, Total, Notes, DeliveryDate,
                     CurrentLatitude, CurrentLongitude, DestinationLatitude, DestinationLongitude, DestinationLabel,
                     DestinationCountry, RouteMode, OriginLabel, TrackingStep, CreatedAt)
                VALUES
                    (@CustomerId, @ChannelId, @StatusId, @PaymentStatusId, @PaymentMethodId,
                     @Subtotal, @Discount, @Tax, @Total, @Notes, DATE(UTC_TIMESTAMP()),
                     @OriginLat, @OriginLng, @OriginLat, @OriginLng, 'Tienda BakeSmart',
                     'Costa Rica', 'pickup', @OriginName, 5, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """;

            var orderId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, orderSql,
                new SqlParameter("@CustomerId", customerId),
                new SqlParameter("@ChannelId", channelId),
                new SqlParameter("@StatusId", statusId),
                new SqlParameter("@PaymentStatusId", paymentStatusId),
                new SqlParameter("@PaymentMethodId", paymentMethodId),
                new SqlParameter("@Subtotal", saleSubtotal),
                new SqlParameter("@Discount", effectiveDiscount),
                new SqlParameter("@Tax", tax),
                new SqlParameter("@Total", total),
                new SqlParameter("@Notes", (object?)input.Notes?.Trim() ?? DBNull.Value),
                new SqlParameter("@OriginLat", config.OriginLatitude),
                new SqlParameter("@OriginLng", config.OriginLongitude),
                new SqlParameter("@OriginName", config.OriginName)));

            foreach (var item in expandedItems)
            {
                var stock = await ResolveProductStockMySqlAsync(connection, transaction, item.ProductId, item.Quantity);
                await ExecuteInTransactionAsync(connection, transaction,
                    "INSERT INTO DetallePedido (OrderId, ProductId, Quantity, UnitPrice) VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice);",
                    new SqlParameter("@OrderId", orderId),
                    new SqlParameter("@ProductId", item.ProductId),
                    new SqlParameter("@Quantity", item.Quantity),
                    new SqlParameter("@UnitPrice", item.UnitPrice));
                await DeductStockMySqlAsync(connection, transaction, item.ProductId, stock.InventoryLocationId, item.Quantity, $"Venta POS #{orderId}");
            }

            const string saleSql = """
                INSERT INTO Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
                VALUES (@OrderId, @PaymentMethodId, @Subtotal, @Tax, @Total, UTC_TIMESTAMP());
                SELECT LAST_INSERT_ID();
                """;
            var saleId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, saleSql,
                new SqlParameter("@OrderId", orderId),
                new SqlParameter("@PaymentMethodId", paymentMethodId),
                new SqlParameter("@Subtotal", saleSubtotal),
                new SqlParameter("@Tax", tax),
                new SqlParameter("@Total", total)));
            await ExecuteInTransactionAsync(connection, transaction,
                "INSERT INTO PagosSesionCaja (CashSessionId, SaleId, Amount) VALUES (@CashSessionId, @SaleId, @Amount);",
                new SqlParameter("@CashSessionId", cashSessionId),
                new SqlParameter("@SaleId", saleId),
                new SqlParameter("@Amount", total));
            foreach (var selection in comboSelections)
            {
                var combo = activeCombos[selection.ComboId];
                await ExecuteInTransactionAsync(connection, transaction, """
                    INSERT INTO VentaCombos (SaleId, ComboId, Quantity, UnitPrice, DiscountAmount)
                    VALUES (@SaleId, @ComboId, @Quantity, @UnitPrice, @DiscountAmount);
                    """, new SqlParameter("@SaleId", saleId), new SqlParameter("@ComboId", combo.Id), new SqlParameter("@Quantity", selection.Quantity), new SqlParameter("@UnitPrice", combo.SpecialPrice), new SqlParameter("@DiscountAmount", combo.Discount * selection.Quantity));
            }
            await ExecuteInTransactionAsync(connection, transaction,
                "UPDATE Clientes SET TotalSpent = TotalSpent + @Total WHERE CustomerId = @CustomerId;",
                new SqlParameter("@Total", total),
                new SqlParameter("@CustomerId", customerId));

            await transaction.CommitAsync();
            await AddAuditLogAsync("VENTA_POS", $"Venta POS #{orderId} por {total:N0}", userEmail);
            return orderId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static string? BuildOrderNotes(string? notes, string? deliveryReference)
    {
        var clean = notes?.Trim();
        if (string.IsNullOrWhiteSpace(deliveryReference))
            return string.IsNullOrWhiteSpace(clean) ? null : clean;

        var reference = $"Referencia de entrega: {deliveryReference.Trim()}";
        return string.IsNullOrWhiteSpace(clean) ? reference : $"{clean}\n{reference}";
    }

    private async Task<int> EnsureCustomerForOrderMySqlAsync(DbConnection connection, DbTransaction transaction, string name, string email, string? phone)
    {
        var customerId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
            "SELECT CustomerId FROM Clientes WHERE LOWER(Email) = LOWER(@Email) LIMIT 1;",
            new SqlParameter("@Email", email)) ?? 0);
        if (customerId > 0)
            return customerId;

        const string insertSql = """
            INSERT INTO Clientes (FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
            VALUES (@Name, @Email, @Phone, 0, 0, UTC_TIMESTAMP());
            SELECT LAST_INSERT_ID();
            """;
        return Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, insertSql,
            new SqlParameter("@Name", name.Trim()),
            new SqlParameter("@Email", email.Trim().ToLowerInvariant()),
            new SqlParameter("@Phone", (object?)phone?.Trim() ?? DBNull.Value)));
    }

    private async Task<int> ResolvePaymentMethodMySqlAsync(DbConnection connection, DbTransaction transaction, string? method, bool fallbackToCash = true)
    {
        var requested = string.IsNullOrWhiteSpace(method) ? "Efectivo" : method.Trim();
        var id = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
            "SELECT PaymentMethodId FROM MetodosPago WHERE Name = @Name AND IsActive = 1 LIMIT 1;",
            new SqlParameter("@Name", requested)) ?? 0);
        if (id > 0)
            return id;

        if (!fallbackToCash)
            throw new InvalidOperationException($"La forma de pago '{requested}' no está habilitada. Intente de nuevo con una opción disponible.");

        return Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
            "SELECT PaymentMethodId FROM MetodosPago WHERE Name = 'Efectivo' LIMIT 1;") ?? 0);
    }

    private static async Task<int> ResolveLookupIdMySqlAsync(DbConnection connection, DbTransaction transaction, string table, string idColumn, string nameColumn, string name)
    {
        var sql = $"SELECT {idColumn} FROM {table} WHERE {nameColumn} = @Name LIMIT 1;";
        return Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, sql, new SqlParameter("@Name", name)) ?? 0);
    }

    private async Task<int> ResolveOpenCashSessionMySqlAsync(DbConnection connection, DbTransaction transaction, string? userEmail)
    {
        const string sql = """
            SELECT cs.CashSessionId
            FROM SesionesCaja cs
            INNER JOIN Usuarios u ON u.UserId = cs.OpenedByUserId
            WHERE cs.Status = 'Abierta' AND LOWER(u.Email) = LOWER(@UserEmail)
            ORDER BY cs.OpenedAt DESC
            LIMIT 1;
            """;
        var id = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, sql,
            new SqlParameter("@UserEmail", (object?)userEmail ?? DBNull.Value)) ?? 0);
        if (id <= 0)
            throw new InvalidOperationException("Debe abrir caja antes de confirmar la venta.");
        return id;
    }

    private static async Task<(int InventoryLocationId, decimal Quantity, decimal UnitPrice)> ResolveProductStockMySqlAsync(DbConnection connection, DbTransaction transaction, int productId, decimal quantity)
    {
        const string sql = """
            SELECT ib.InventoryLocationId, ib.Quantity, p.UnitPrice
            FROM Productos p
            INNER JOIN TiposProducto pt ON pt.ProductTypeId = p.ProductTypeId
            INNER JOIN ExistenciasInventario ib ON ib.ProductId = p.ProductId
            WHERE p.ProductId = @ProductId
              AND p.IsActive = 1
              AND pt.Name = 'Producto terminado'
            ORDER BY ib.Quantity DESC
            LIMIT 1;
            """;
        await using var command = CreateCommand(connection, sql, new[] { new SqlParameter("@ProductId", productId) }, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("El producto seleccionado no esta disponible para venta.");

        var locationId = reader.GetInt32("InventoryLocationId");
        var available = reader.GetDecimal("Quantity");
        if (available < quantity)
            throw new InvalidOperationException("No hay stock suficiente para completar la venta.");

        return (locationId, available, reader.GetDecimal("UnitPrice"));
    }

    private static async Task DeductStockMySqlAsync(DbConnection connection, DbTransaction transaction, int productId, int locationId, decimal quantity, string note)
    {
        await ExecuteInTransactionAsync(connection, transaction,
            "UPDATE ExistenciasInventario SET Quantity = Quantity - @Quantity, UpdatedAt = UTC_TIMESTAMP() WHERE ProductId = @ProductId AND InventoryLocationId = @LocationId;",
            new SqlParameter("@ProductId", productId),
            new SqlParameter("@LocationId", locationId),
            new SqlParameter("@Quantity", quantity));
        await ExecuteInTransactionAsync(connection, transaction,
            "INSERT INTO MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, Note, CreatedAt) VALUES (@ProductId, @LocationId, 'SALIDA', @Quantity, @Note, UTC_TIMESTAMP());",
            new SqlParameter("@ProductId", productId),
            new SqlParameter("@LocationId", locationId),
            new SqlParameter("@Quantity", quantity),
            new SqlParameter("@Note", note));
    }

    private static async Task<(decimal IvaRate, decimal FrequentDiscountRate, decimal OriginLatitude, decimal OriginLongitude, string OriginName)> LoadOperationalConfigMySqlAsync(DbConnection connection, DbTransaction transaction)
    {
        const string sql = """
            SELECT SettingKey, SettingValue
            FROM ConfiguracionesAplicacion
            WHERE SettingKey IN ('iva', 'frequentCustomerDiscount', 'originLatitude', 'originLongitude', 'originName');
            """;
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, sql, null, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            settings[reader.GetString("SettingKey")] = reader.GetString("SettingValue");

        decimal setting(string key, decimal fallback) =>
            settings.TryGetValue(key, out var value) && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

        return (
            setting("iva", 0.13m),
            setting("frequentCustomerDiscount", 0.05m),
            setting("originLatitude", 9.9142m),
            setting("originLongitude", -84.0734m),
            settings.TryGetValue("originName", out var name) ? name : "BakeSmart Patri");
    }

    public async Task<object> GetSettingsAsync()
    {
        const string sql = "SELECT SettingKey, SettingValue FROM dbo.ConfiguracionesAplicacion";
        return await QueryAsync(sql, reader => new
        {
            key = reader.GetString("SettingKey"),
            value = reader.GetString("SettingValue")
        });
    }

    public async Task<IReadOnlyDictionary<string, string>> SettingsDictionaryAsync()
    {
        const string sql = "SELECT SettingKey, SettingValue FROM dbo.ConfiguracionesAplicacion";
        var rows = await QueryAsync(sql, reader => new
        {
            key = reader.GetString("SettingKey"),
            value = reader.GetString("SettingValue")
        });

        return rows
            .GroupBy(row => row.key)
            .ToDictionary(group => group.Key, group => group.Last().value);
    }

    public async Task SaveSettingsAsync(Dictionary<string, string> settings)
    {
        if (settings.TryGetValue("originLatitude", out var originLatText) ||
            settings.TryGetValue("originLongitude", out var originLngText))
        {
            var originLatProvided = settings.TryGetValue("originLatitude", out originLatText);
            var originLngProvided = settings.TryGetValue("originLongitude", out originLngText);

            if (originLatProvided || originLngProvided)
            {
                if (!TryParseCoordinate(originLatText, out var originLat) ||
                    !TryParseCoordinate(originLngText, out var originLng) ||
                    !HasValidCoordinates(originLat, originLng))
                {
                    throw new InvalidOperationException("La ubicacion del negocio debe tener coordenadas validas.");
                }

                settings["originLatitude"] = originLat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                settings["originLongitude"] = originLng.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        foreach (var kvp in settings)
        {
            if (UseMySql)
            {
                await ExecuteAsync("""
                    INSERT INTO ConfiguracionesAplicacion (SettingKey, SettingValue)
                    VALUES (@Key, @Value)
                    ON DUPLICATE KEY UPDATE SettingValue = VALUES(SettingValue);
                    """,
                    new SqlParameter("@Key", kvp.Key.Trim()),
                    new SqlParameter("@Value", kvp.Value.Trim()));
                continue;
            }

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

            await ExecuteAsync(sql,
                new SqlParameter("@Key", kvp.Key.Trim()),
                new SqlParameter("@Value", kvp.Value.Trim()));
        }
    }

    public sealed record AuthUser(string Email, string Role, string DisplayName);
    public sealed record RegisterCustomerInput(string FirstName, string LastName, string Email, string? Phone, string? AddressLine, string Password);
    public sealed record UserInput(int? Id, string FirstName, string LastName, string Email, string? Phone, string? Address, string Role, string? Password);
    public sealed record ProfileInput(string FirstName, string LastName, string? Phone, string? Address, string? NewPassword, int? CustomerAddressId = null, string? AddressLabel = null, decimal? Latitude = null, decimal? Longitude = null);
    public sealed record ProfileData(string FirstName, string LastName, string Email, string Phone, string Address, string Role, int? CustomerAddressId, string AddressLabel, decimal? Latitude, decimal? Longitude, bool IsFrequent);
    public sealed record CustomerAddressData(int Id, string Label, string AddressLine, decimal? Latitude, decimal? Longitude, bool IsDefault);
    public sealed record InventoryProductInput(int? Id, string Code, string Description, string Type, string Unit, string Category, string? Subcategory, decimal Price, decimal Stock, decimal MinStock, string? ImageUrl = null);
    public sealed record InventoryMovementInput(int ProductId, string Type, decimal Quantity, string? Note);
    public sealed record PaymentMethodInput(int? Id, string Name, decimal CommissionRate, bool IsActive, string? Account);
    public sealed record PromotionInput(int? Id, string Name, DateTime StartDate, DateTime EndDate, decimal Discount, bool IsActive = true, IReadOnlyList<int>? ProductIds = null, IReadOnlyList<int>? CustomerIds = null);
    public sealed record ComboInput(int? Id, string Name, string? Description, decimal SpecialPrice, string? ImageUrl, bool IsActive, IReadOnlyList<ComboItemInput>? Items);
    public sealed record ComboItemInput(int ProductId, decimal Quantity);
    public sealed record ComboProductData(int ProductId, decimal Quantity, string Code, string Name, decimal UnitPrice, string ImageUrl);
    public sealed record ComboData(int Id, string Name, string Description, decimal SpecialPrice, decimal RegularPrice, decimal Discount, string ImageUrl, bool Active, IReadOnlyList<ComboProductData> Items);
    public sealed record MarketingCampaignInput(string? Subject, string Message, IReadOnlyList<int> CustomerIds);
    public sealed record MarketingRecipient(int CustomerId, string FullName, string Email);
    public sealed record AccountingExpenseInput(string Description, decimal Amount, string? Account, string? Method = null);
    public sealed record SupplierPaymentInput(string Supplier, decimal Amount, string? Account, string Method);
    public sealed record CreditNoteInput(int SaleId, string Reason);
    public sealed record CreateOrderInput(string CustomerName, string Email, string? Phone, int ProductId, decimal Quantity, decimal UnitPrice, decimal Subtotal, decimal Tax, decimal Total, DateTime DeliveryDate, string? Address, string? Notes, string? PaymentMethod, decimal? DestinationLatitude = null, decimal? DestinationLongitude = null, string? DeliveryReference = null, int? CustomerAddressId = null, string? DeliveryMethod = "domicilio");
    public sealed record SaleInput(string? CustomerName, string? CustomerEmail, string? CustomerPhone, string? PaymentMethod, decimal Subtotal, decimal Discount, decimal Tax, decimal Total, string? Notes, IReadOnlyList<SaleItemInput> Items, int? PromotionId = null, IReadOnlyList<ComboSaleInput>? Combos = null);
    public sealed record SaleItemInput(int ProductId, decimal Quantity, decimal UnitPrice);
    public sealed record ComboSaleInput(int ComboId, decimal Quantity);

    private sealed record DashboardRow(int OrdersToday, int InProduction, decimal SalesToday, int LowStock);
    private sealed record ComboRow(int Id, string Name, string Description, decimal SpecialPrice, string ImageUrl, bool Active);
}

internal static class SqlReaderExtensions
{
    public static int GetInt32(this DbDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));
    public static string GetString(this DbDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    public static bool GetBoolean(this DbDataReader reader, string name) => reader.GetBoolean(reader.GetOrdinal(name));
    public static decimal GetDecimal(this DbDataReader reader, string name) => reader.GetDecimal(reader.GetOrdinal(name));
    public static DateTime GetDateTime(this DbDataReader reader, string name) => reader.GetDateTime(reader.GetOrdinal(name));

    public static int? GetNullableInt32(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static string? GetNullableString(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static DateTime? GetNullableDateTime(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    public static decimal? GetNullableDecimal(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }
}
