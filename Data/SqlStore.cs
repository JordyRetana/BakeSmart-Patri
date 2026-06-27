using Microsoft.Data.SqlClient;
using System.Data;
using System.Data.Common;
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

    public async Task<int> SaveInventoryProductAsync(InventoryProductInput input, string? userEmail = null)
    {
        // Validar duplicado de código
        var existingCode = input.Id is null
            ? await CodeExistsAsync(input.Code.Trim())
            : await CodeExistsExcludingAsync(input.Code.Trim(), input.Id.Value);

        if (existingCode)
            throw new InvalidOperationException($"Ya existe un producto con el código '{input.Code.Trim()}'.");

        var typeId = await EnsureProductTypeAsync(input.Type);
        var unitId = await EnsureUnitMeasureAsync(input.Unit);
        var categoryId = await EnsureProductCategoryAsync(input.Category, input.Subcategory);
        var locationId = await EnsureInventoryLocationAsync();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            int productId;
            if (input.Id is > 0)
            {
                const string updateProduct = """
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
                const string insertProduct = """
                    INSERT INTO dbo.Productos
                        (ProductTypeId, ProductCategoryId, UnitMeasureId, Code, Name, Description, UnitPrice, UnitCost, MinStock, IsActive)
                    OUTPUT INSERTED.ProductId
                    VALUES
                        (@ProductTypeId, @ProductCategoryId, @UnitMeasureId, @Code, @Name, @Description, @UnitPrice, @UnitCost, @MinStock, 1);
                    """;

                productId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, insertProduct,
                    new SqlParameter("@ProductTypeId", typeId),
                    new SqlParameter("@ProductCategoryId", categoryId),
                    new SqlParameter("@UnitMeasureId", unitId),
                    new SqlParameter("@Code", input.Code.Trim()),
                    new SqlParameter("@Name", input.Description.Trim()),
                    new SqlParameter("@Description", input.Description.Trim()),
                    new SqlParameter("@UnitPrice", input.Price),
                    new SqlParameter("@UnitCost", input.Price),
                    new SqlParameter("@MinStock", input.MinStock)));
            }

            await SetInventoryBalanceAsync(connection, transaction, productId, locationId, input.Stock);
            await AddInventoryMovementAsync(connection, transaction, productId, locationId, "AJUSTE", Math.Max(input.Stock, 0.01m), "Registro/actualizacion de producto");

            await transaction.CommitAsync();

            var action = input.Id is > 0 ? "actualizado" : "creado";
            await AddAuditLogAsync($"INVENTARIO_PRODUCTO_{action.ToUpperInvariant()}", $"Producto '{input.Code}' {action}: {input.Description}", userEmail);

            return productId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
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

    public async Task<int> SaveUserAsync(UserInput input)
    {
        const string sql = """
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

            IF @UserId IS NULL
            BEGIN
                INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
                VALUES (@RoleId, @FirstName, @LastName, @Email, @Phone, @PasswordHash, @AddressLine, 1, SYSUTCDATETIME());

                SELECT CONVERT(int, SCOPE_IDENTITY());
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

                SELECT @UserId;
            END;
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
            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'Cajero')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'Cajero', N'Gestion de caja, ventas y pedidos de mostrador', 1);

            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'Repostero')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'Repostero', N'Produccion, recetas e inventario operativo', 1);

            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = N'Supervisor')
                INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
                VALUES (N'Supervisor', N'Seguimiento operativo, reportes y control de tienda', 1);

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
            originName = settingText("originName", "BakeSmart Patri"),
            originAddress = settingText("originAddress", "San Jose, Costa Rica"),
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

    public async Task AddAuditLogAsync(string logType, string detail, string? userEmail = null)
    {
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

    private async Task<int> EnsureProductTypeAsync(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Producto terminado" : name.Trim();
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

    private static async Task SetInventoryBalanceAsync(SqlConnection connection, DbTransaction transaction, int productId, int locationId, decimal quantity)
    {
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

    private static async Task AddInventoryMovementAsync(SqlConnection connection, DbTransaction transaction, int productId, int locationId, string type, decimal quantity, string? note)
    {
        const string sql = """
            INSERT INTO dbo.MovimientosInventario
                (ProductId, InventoryLocationId, MovementType, Quantity, ResponsibleUserId, Note)
            VALUES
                (@ProductId, @LocationId, @Type, @Quantity, NULL, @Note);
            """;

        await ExecuteInTransactionAsync(connection, transaction, sql,
            new SqlParameter("@ProductId", productId),
            new SqlParameter("@LocationId", locationId),
            new SqlParameter("@Type", type),
            new SqlParameter("@Quantity", quantity),
            new SqlParameter("@Note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim()));
    }

    private async Task ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        if (parameters.Length > 0)
            command.Parameters.AddRange(parameters);

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteInTransactionAsync(SqlConnection connection, DbTransaction transaction, string sql, params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
        if (parameters.Length > 0)
            command.Parameters.AddRange(parameters);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarInTransactionAsync(SqlConnection connection, DbTransaction transaction, string sql, params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
        if (parameters.Length > 0)
            command.Parameters.AddRange(parameters);

        return await command.ExecuteScalarAsync();
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

    public async Task<ProfileData?> GetProfileAsync(string email)
    {
        const string sql = """
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
                ca.Longitude
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
            reader.GetNullableDecimal("Longitude")
        ), new SqlParameter("@Email", email));

        return rows.FirstOrDefault();
    }

    public async Task<bool> RequestPasswordResetAsync(string email)
    {
        const string checkSql = "SELECT COUNT(1) FROM dbo.Usuarios WHERE LOWER(Email) = LOWER(@Email) AND IsActive = 1";
        var exists = Convert.ToInt32(await ScalarAsync(checkSql, new SqlParameter("@Email", email)));
        if (exists == 0)
            return false;

        // En un entorno real, aquí se enviaría un email con un token.
        // Por ahora, generamos una contraseña temporal y la registramos en bitácora.
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
        const string sql = """
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
        const string sql = """
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

    private static CustomerAddressData MapCustomerAddress(SqlDataReader reader) => new(
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

            DECLARE @WebChannelId int = (SELECT OrderChannelId FROM dbo.CanalesPedido WHERE Name = N'Web');
            DECLARE @PendingStatusId int = (SELECT OrderStatusId FROM dbo.EstadosPedido WHERE Name = N'Pendiente pago');
            DECLARE @PendingPaymentId int = (SELECT PaymentStatusId FROM dbo.EstadosPago WHERE Name = N'Pendiente');
            DECLARE @CashMethodId int = (SELECT PaymentMethodId FROM dbo.MetodosPago WHERE Name = @PaymentMethod);
            IF @CashMethodId IS NULL SELECT @CashMethodId = PaymentMethodId FROM dbo.MetodosPago WHERE Name = N'Pendiente';

            INSERT INTO dbo.Pedidos
                (CustomerId, CustomerAddressId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId,
                 Notes, Subtotal, Discount, Tax, Total, DeliveryDate,
                 CurrentLatitude, CurrentLongitude,
                 DestinationLatitude, DestinationLongitude, DestinationLabel, DestinationCountry,
                 RouteMode, OriginLabel, DeliveryReference)
            VALUES
                (@CustomerId, @ResolvedAddressId, @WebChannelId, @PendingStatusId, @PendingPaymentId, @CashMethodId,
                 @Notes, @Subtotal, 0, @Tax, @Total, @DeliveryDate,
                 @OriginLat, @OriginLng,
                 @DestLat, @DestLng, @DestLabel, N'Costa Rica',
                 CASE WHEN @DeliveryMethod = N'retiro' THEN N'pickup' ELSE N'ground' END, @OriginName, @DeliveryReference);

            DECLARE @OrderId int = SCOPE_IDENTITY();

            INSERT INTO dbo.DetallePedido (OrderId, ProductId, Quantity, UnitPrice)
            VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice);

            INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Detail, CreatedAt)
            VALUES (@OrderId, @PendingStatusId, N'Pedido creado desde formulario web', SYSUTCDATETIME());

            COMMIT TRAN;
            SELECT @OrderId;
            """;

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
            new SqlParameter("@Notes", (object?)input.Notes?.Trim() ?? DBNull.Value),
            new SqlParameter("@PaymentMethod", (object?)input.PaymentMethod?.Trim() ?? "Pendiente"),
            new SqlParameter("@DestinationLatitude", (object?)input.DestinationLatitude ?? DBNull.Value),
            new SqlParameter("@DestinationLongitude", (object?)input.DestinationLongitude ?? DBNull.Value),
            new SqlParameter("@DeliveryReference", (object?)input.DeliveryReference?.Trim() ?? DBNull.Value),
            new SqlParameter("@CustomerAddressId", (object?)input.CustomerAddressId ?? DBNull.Value),
            new SqlParameter("@DeliveryMethod", (object?)input.DeliveryMethod?.Trim() ?? "domicilio")));

        await AddAuditLogAsync("CREAR_PEDIDO", $"Pedido #{orderId} creado para {input.CustomerName}", userEmail);
        return orderId;
    }

    public async Task<int> OpenCashSessionAsync(decimal openingAmount, string? userEmail = null)
    {
        // Verificar que no haya sesión activa
        const string checkSql = "SELECT COUNT(1) FROM dbo.SesionesCaja WHERE Status = N'Abierta'";
        var activeSessions = Convert.ToInt32(await ScalarAsync(checkSql));
        if (activeSessions > 0)
            throw new InvalidOperationException("Ya existe una caja abierta. Debe cerrarla antes de abrir una nueva.");

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

        await AddAuditLogAsync("APERTURA_CAJA", $"Sesion de caja #{sessionId} abierta con ₡{openingAmount:N0}", userEmail);
        return sessionId;
    }

    public async Task CloseCashSessionAsync(int sessionId, decimal closingAmount, string? userEmail = null)
    {
        const string sql = """
            UPDATE dbo.SesionesCaja
            SET ClosingAmount = @ClosingAmount,
                Status = N'Cerrada',
                ClosedAt = SYSUTCDATETIME()
            WHERE CashSessionId = @SessionId AND Status = N'Abierta';
            """;

        await ExecuteAsync(sql,
            new SqlParameter("@SessionId", sessionId),
            new SqlParameter("@ClosingAmount", closingAmount));

        await AddAuditLogAsync("CIERRE_CAJA", $"Sesion de caja #{sessionId} cerrada con ₡{closingAmount:N0}", userEmail);
    }

    public async Task<IReadOnlyList<object>> CashSessionsAsync()
    {
        const string sql = """
            SELECT cs.CashSessionId, cs.OpenedAt, cs.ClosedAt, cs.OpeningAmount, cs.ClosingAmount, cs.Status,
                   COALESCE(CONCAT(u.FirstName, N' ', u.LastName), N'Sistema') AS UserName,
                   COALESCE(SUM(csp.Amount), 0) AS TotalSales
            FROM dbo.SesionesCaja cs
            LEFT JOIN dbo.Usuarios u ON u.UserId = cs.OpenedByUserId
            LEFT JOIN dbo.PagosSesionCaja csp ON csp.CashSessionId = cs.CashSessionId
            GROUP BY cs.CashSessionId, cs.OpenedAt, cs.ClosedAt, cs.OpeningAmount, cs.ClosingAmount, cs.Status, u.FirstName, u.LastName
            ORDER BY cs.OpenedAt DESC;
            """;

        return await QueryAsync(sql, reader => new
        {
            id = reader.GetInt32("CashSessionId"),
            openedAt = reader.GetDateTime("OpenedAt").ToString("o"),
            closedAt = reader.IsDBNull(reader.GetOrdinal("ClosedAt")) ? null : reader.GetDateTime("ClosedAt").ToString("o"),
            openingAmount = reader.GetDecimal("OpeningAmount"),
            closingAmount = reader.IsDBNull(reader.GetOrdinal("ClosingAmount")) ? (decimal?)null : reader.GetDecimal("ClosingAmount"),
            totalSales = reader.GetDecimal("TotalSales"),
            userName = reader.GetString("UserName"),
            status = reader.GetString("Status")
        });
    }

    public async Task<int> RegisterSaleAsync(SaleInput input, string? userEmail = null)
    {
        // Serializar items a JSON para pasarlos como parámetro
        var itemsJson = System.Text.Json.JsonSerializer.Serialize(input.Items.Select(i => new
        {
            productId = i.ProductId,
            quantity = i.Quantity,
            unitPrice = i.UnitPrice
        }));

        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;

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

            -- Crear pedido (venta directa POS)
            INSERT INTO dbo.Pedidos
                (CustomerId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId,
                 Subtotal, Discount, Tax, Total, Notes, DeliveryDate,
                 CurrentLatitude, CurrentLongitude,
                 DestinationLatitude, DestinationLongitude, DestinationLabel, DestinationCountry,
                 RouteMode, OriginLabel, TrackingStep)
            VALUES
                (@CustomerId, @PosChannelId, @DeliveredStatusId, @PaidStatusId, @PaymentMethodId,
                 @Subtotal, NULLIF(@Discount, 0), @Tax, @Total, NULLIF(@Notes, N''), CAST(SYSUTCDATETIME() AS date),
                 9.9142, -84.0734,
                 9.9142, -84.0734, N'Tienda BakeSmart', N'Costa Rica',
                 N'pickup', N'BakeSmart Patri', 6);

            DECLARE @OrderId int = SCOPE_IDENTITY();

            -- Registrar productos del pedido desde JSON
            INSERT INTO dbo.DetallePedido (OrderId, ProductId, Quantity, UnitPrice)
            SELECT @OrderId, ProductId, Quantity, UnitPrice
            FROM OPENJSON(@ItemsJson)
            WITH (
                ProductId int N'$.productId',
                Quantity decimal(18,2) N'$.quantity',
                UnitPrice decimal(18,2) N'$.unitPrice'
            );

            -- Crear venta
            INSERT INTO dbo.Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
            VALUES (@OrderId, @PaymentMethodId, @Subtotal, @Tax, @Total, SYSUTCDATETIME());

            DECLARE @SaleId int = SCOPE_IDENTITY();

            -- Asociar a sesión de caja activa
            DECLARE @ActiveSessionId int = (SELECT TOP 1 CashSessionId FROM dbo.SesionesCaja WHERE Status = N'Abierta' ORDER BY CashSessionId DESC);
            IF @ActiveSessionId IS NOT NULL
                INSERT INTO dbo.PagosSesionCaja (CashSessionId, SaleId, Amount)
                VALUES (@ActiveSessionId, @SaleId, @Total);

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
            new SqlParameter("@Tax", input.Tax),
            new SqlParameter("@Total", input.Total),
            new SqlParameter("@Notes", (object?)input.Notes?.Trim() ?? DBNull.Value),
            new SqlParameter("@ItemsJson", itemsJson)));

        await AddAuditLogAsync("VENTA_POS", $"Venta POS #{orderId} por ₡{input.Total:N0}", userEmail);
        return orderId;
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
    public sealed record ProfileData(string FirstName, string LastName, string Email, string Phone, string Address, string Role, int? CustomerAddressId, string AddressLabel, decimal? Latitude, decimal? Longitude);
    public sealed record CustomerAddressData(int Id, string Label, string AddressLine, decimal? Latitude, decimal? Longitude, bool IsDefault);
    public sealed record InventoryProductInput(int? Id, string Code, string Description, string Type, string Unit, string Category, string? Subcategory, decimal Price, decimal Stock, decimal MinStock);
    public sealed record InventoryMovementInput(int ProductId, string Type, decimal Quantity, string? Note);
    public sealed record CreateOrderInput(string CustomerName, string Email, string? Phone, int ProductId, decimal Quantity, decimal UnitPrice, decimal Subtotal, decimal Tax, decimal Total, DateTime DeliveryDate, string? Address, string? Notes, string? PaymentMethod, decimal? DestinationLatitude = null, decimal? DestinationLongitude = null, string? DeliveryReference = null, int? CustomerAddressId = null, string? DeliveryMethod = "domicilio");
    public sealed record SaleInput(string? CustomerName, string? CustomerEmail, string? CustomerPhone, string? PaymentMethod, decimal Subtotal, decimal Discount, decimal Tax, decimal Total, string? Notes, IReadOnlyList<SaleItemInput> Items);
    public sealed record SaleItemInput(int ProductId, decimal Quantity, decimal UnitPrice);

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
