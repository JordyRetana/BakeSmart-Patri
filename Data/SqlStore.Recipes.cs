using Microsoft.Data.SqlClient;
using System.Data.Common;

namespace BakeSmartPatri.Data;

public sealed partial class SqlStore
{
    private async Task EnsureRecipeSchemaAsync()
    {
        if (UseMySql)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS RecetasProducto (
                    RecipeId INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    ProductId INT NOT NULL UNIQUE,
                    Status VARCHAR(30) NOT NULL DEFAULT 'Pendiente',
                    YieldQuantity DECIMAL(18,4) NOT NULL DEFAULT 1,
                    YieldUnit VARCHAR(30) NOT NULL DEFAULT 'unidad',
                    WastePercent DECIMAL(8,2) NOT NULL DEFAULT 0,
                    Notes VARCHAR(1000) NULL,
                    ApprovedBy VARCHAR(180) NULL,
                    ApprovedAt DATETIME NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                CREATE TABLE IF NOT EXISTS IngredientesReceta (
                    RecipeIngredientId INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    RecipeId INT NOT NULL,
                    IngredientProductId INT NOT NULL,
                    Quantity DECIMAL(18,4) NOT NULL,
                    Unit VARCHAR(30) NOT NULL,
                    IsOptional BIT NOT NULL DEFAULT 0,
                    Notes VARCHAR(500) NULL,
                    UNIQUE KEY UX_IngredientesReceta (RecipeId, IngredientProductId)
                );
                CREATE TABLE IF NOT EXISTS ReservasMaterialProduccion (
                    ProductionReservationId INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    OrderId INT NOT NULL,
                    IngredientProductId INT NOT NULL,
                    RequiredQuantity DECIMAL(18,4) NOT NULL,
                    ReservedQuantity DECIMAL(18,4) NOT NULL,
                    Status VARCHAR(30) NOT NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UX_ReservaPedidoIngrediente (OrderId, IngredientProductId)
                );
                """);
            return;
        }

        await ExecuteAsync("""
            IF OBJECT_ID('dbo.RecetasProducto', 'U') IS NULL
            CREATE TABLE dbo.RecetasProducto (
                RecipeId int IDENTITY(1,1) PRIMARY KEY,
                ProductId int NOT NULL UNIQUE,
                Status nvarchar(30) NOT NULL DEFAULT N'Pendiente',
                YieldQuantity decimal(18,4) NOT NULL DEFAULT 1,
                YieldUnit nvarchar(30) NOT NULL DEFAULT N'unidad',
                WastePercent decimal(8,2) NOT NULL DEFAULT 0,
                Notes nvarchar(1000) NULL,
                ApprovedBy nvarchar(180) NULL,
                ApprovedAt datetime2 NULL,
                CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            IF OBJECT_ID('dbo.IngredientesReceta', 'U') IS NULL
            CREATE TABLE dbo.IngredientesReceta (
                RecipeIngredientId int IDENTITY(1,1) PRIMARY KEY,
                RecipeId int NOT NULL,
                IngredientProductId int NOT NULL,
                Quantity decimal(18,4) NOT NULL,
                Unit nvarchar(30) NOT NULL,
                IsOptional bit NOT NULL DEFAULT 0,
                Notes nvarchar(500) NULL,
                CONSTRAINT UX_IngredientesReceta UNIQUE (RecipeId, IngredientProductId)
            );
            IF OBJECT_ID('dbo.ReservasMaterialProduccion', 'U') IS NULL
            CREATE TABLE dbo.ReservasMaterialProduccion (
                ProductionReservationId int IDENTITY(1,1) PRIMARY KEY,
                OrderId int NOT NULL,
                IngredientProductId int NOT NULL,
                RequiredQuantity decimal(18,4) NOT NULL,
                ReservedQuantity decimal(18,4) NOT NULL,
                Status nvarchar(30) NOT NULL,
                CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UX_ReservaPedidoIngrediente UNIQUE (OrderId, IngredientProductId)
            );
            """);
    }

    public async Task<IReadOnlyList<object>> RecipesAsync()
    {
        await EnsureRecipeSchemaAsync();
        const string sql = """
            SELECT p.ProductId, p.Code, p.Name, t.Name AS ProductType,
                   COALESCE(r.Status, 'Sin receta') AS RecipeStatus,
                   COALESCE(r.YieldQuantity, 1) AS YieldQuantity,
                   COALESCE(r.YieldUnit, 'unidad') AS YieldUnit,
                   COUNT(i.RecipeIngredientId) AS IngredientCount
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto t ON t.ProductTypeId = p.ProductTypeId
            LEFT JOIN dbo.RecetasProducto r ON r.ProductId = p.ProductId
            LEFT JOIN dbo.IngredientesReceta i ON i.RecipeId = r.RecipeId
            WHERE p.IsActive = 1 AND LOWER(t.Name) = LOWER('Producto terminado')
            GROUP BY p.ProductId, p.Code, p.Name, t.Name, r.Status, r.YieldQuantity, r.YieldUnit
            ORDER BY p.Name;
            """;
        var rows = await QueryAsync(sql, r => new
        {
            productId = r.GetInt32("ProductId"), code = r.GetString("Code"), name = r.GetString("Name"),
            productType = r.GetString("ProductType"), status = r.GetString("RecipeStatus"),
            yieldQuantity = r.GetDecimal("YieldQuantity"), yieldUnit = r.GetString("YieldUnit"),
            ingredientCount = Convert.ToInt32(r["IngredientCount"])
        });
        return rows.Cast<object>().ToList();
    }

    public async Task<object> RecipeAsync(int productId)
    {
        await EnsureRecipeSchemaAsync();
        const string recipeSql = """
            SELECT p.ProductId, p.Code, p.Name, COALESCE(r.RecipeId, 0) RecipeId,
                   COALESCE(r.Status, 'Sin receta') Status, COALESCE(r.YieldQuantity, 1) YieldQuantity,
                   COALESCE(r.YieldUnit, 'unidad') YieldUnit, COALESCE(r.WastePercent, 0) WastePercent,
                   COALESCE(r.Notes, '') Notes
            FROM dbo.Productos p LEFT JOIN dbo.RecetasProducto r ON r.ProductId=p.ProductId
            WHERE p.ProductId=@ProductId;
            """;
        var recipes = await QueryAsync(recipeSql, r => new
        {
            productId = r.GetInt32("ProductId"), code = r.GetString("Code"), name = r.GetString("Name"),
            recipeId = r.GetInt32("RecipeId"), status = r.GetString("Status"),
            yieldQuantity = r.GetDecimal("YieldQuantity"), yieldUnit = r.GetString("YieldUnit"),
            wastePercent = r.GetDecimal("WastePercent"), notes = r.GetString("Notes")
        }, new SqlParameter("@ProductId", productId));
        if (recipes.Count == 0) throw new InvalidOperationException("El producto no existe.");

        const string ingredientSql = """
            SELECT i.IngredientProductId, p.Code, p.Name, i.Quantity, i.Unit, i.IsOptional,
                   COALESCE(i.Notes, '') Notes
            FROM dbo.IngredientesReceta i
            INNER JOIN dbo.RecetasProducto r ON r.RecipeId=i.RecipeId
            INNER JOIN dbo.Productos p ON p.ProductId=i.IngredientProductId
            WHERE r.ProductId=@ProductId ORDER BY p.Name;
            """;
        var ingredients = await QueryAsync(ingredientSql, r => new
        {
            productId = r.GetInt32("IngredientProductId"), code = r.GetString("Code"), name = r.GetString("Name"),
            quantity = r.GetDecimal("Quantity"), unit = r.GetString("Unit"), optional = r.GetBoolean("IsOptional"), notes = r.GetString("Notes")
        }, new SqlParameter("@ProductId", productId));
        return new { recipe = recipes[0], ingredients };
    }

    public async Task<IReadOnlyList<object>> RecipeIngredientOptionsAsync()
    {
        const string sql = """
            SELECT p.ProductId, p.Code, p.Name, t.Name ProductType, u.Name Unit,
                   COALESCE(SUM(e.Quantity), 0) Stock
            FROM dbo.Productos p
            INNER JOIN dbo.TiposProducto t ON t.ProductTypeId=p.ProductTypeId
            INNER JOIN dbo.UnidadesMedida u ON u.UnitMeasureId=p.UnitMeasureId
            LEFT JOIN dbo.ExistenciasInventario e ON e.ProductId=p.ProductId
            WHERE p.IsActive=1 AND LOWER(t.Name) <> LOWER('Producto terminado')
            GROUP BY p.ProductId,p.Code,p.Name,t.Name,u.Name ORDER BY p.Name;
            """;
        var rows = await QueryAsync(sql, r => new { productId=r.GetInt32("ProductId"), code=r.GetString("Code"), name=r.GetString("Name"), type=r.GetString("ProductType"), unit=r.GetString("Unit"), stock=r.GetDecimal("Stock") });
        return rows.Cast<object>().ToList();
    }

    public async Task SaveRecipeAsync(RecipeInput input, string? userEmail)
    {
        if (input.ProductId <= 0 || input.YieldQuantity <= 0 || input.Ingredients is null || input.Ingredients.Count == 0)
            throw new InvalidOperationException("La receta necesita un rendimiento y al menos un ingrediente.");
        if (input.Ingredients.Any(x => x.ProductId <= 0 || x.Quantity <= 0))
            throw new InvalidOperationException("Cada ingrediente debe tener una cantidad mayor que cero.");
        if (input.Ingredients.GroupBy(x => x.ProductId).Any(g => g.Count() > 1))
            throw new InvalidOperationException("No repita ingredientes; ajuste la cantidad en una sola fila.");

        await EnsureRecipeSchemaAsync();
        await using var connection = CreateConnection(); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var recipeId = Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,
                "SELECT RecipeId FROM dbo.RecetasProducto WHERE ProductId=@ProductId;", new SqlParameter("@ProductId", input.ProductId)) ?? 0);
            if (recipeId == 0)
            {
                var insert = UseMySql
                    ? "INSERT INTO RecetasProducto(ProductId,Status,YieldQuantity,YieldUnit,WastePercent,Notes,CreatedAt,UpdatedAt) VALUES(@ProductId,'En revision',@Yield,@Unit,@Waste,@Notes,UTC_TIMESTAMP(),UTC_TIMESTAMP());"
                    : "INSERT INTO dbo.RecetasProducto(ProductId,Status,YieldQuantity,YieldUnit,WastePercent,Notes,CreatedAt,UpdatedAt) OUTPUT INSERTED.RecipeId VALUES(@ProductId,N'En revision',@Yield,@Unit,@Waste,@Notes,SYSUTCDATETIME(),SYSUTCDATETIME());";
                if (UseMySql) { await ExecuteInTransactionAsync(connection, transaction, insert, RecipeParameters(input)); recipeId=Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction,"SELECT LAST_INSERT_ID();")); }
                else recipeId=Convert.ToInt32(await ScalarInTransactionAsync(connection, transaction, insert, RecipeParameters(input)));
            }
            else
            {
                await ExecuteInTransactionAsync(connection, transaction, "UPDATE dbo.RecetasProducto SET Status='En revision',YieldQuantity=@Yield,YieldUnit=@Unit,WastePercent=@Waste,Notes=@Notes,ApprovedBy=NULL,ApprovedAt=NULL,UpdatedAt=SYSUTCDATETIME() WHERE RecipeId=@RecipeId;",
                    new SqlParameter("@Yield",input.YieldQuantity),new SqlParameter("@Unit",input.YieldUnit.Trim()),new SqlParameter("@Waste",Math.Max(0,input.WastePercent)),new SqlParameter("@Notes",(object?)input.Notes??DBNull.Value),new SqlParameter("@RecipeId",recipeId));
                await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM dbo.IngredientesReceta WHERE RecipeId=@RecipeId;", new SqlParameter("@RecipeId",recipeId));
            }
            foreach(var item in input.Ingredients)
                await ExecuteInTransactionAsync(connection, transaction, "INSERT INTO dbo.IngredientesReceta(RecipeId,IngredientProductId,Quantity,Unit,IsOptional,Notes) VALUES(@RecipeId,@ProductId,@Quantity,@Unit,@Optional,@Notes);",
                    new SqlParameter("@RecipeId",recipeId),new SqlParameter("@ProductId",item.ProductId),new SqlParameter("@Quantity",item.Quantity),new SqlParameter("@Unit",item.Unit.Trim()),new SqlParameter("@Optional",item.Optional),new SqlParameter("@Notes",(object?)item.Notes??DBNull.Value));
            await transaction.CommitAsync();
        }
        catch { await transaction.RollbackAsync(); throw; }
        try { await AddAuditLogAsync("RECETA_EN_REVISION",$"Receta del producto {input.ProductId} enviada a revision",userEmail); } catch { }
    }

    private static SqlParameter[] RecipeParameters(RecipeInput input) => [new("@ProductId",input.ProductId),new("@Yield",input.YieldQuantity),new("@Unit",input.YieldUnit.Trim()),new("@Waste",Math.Max(0,input.WastePercent)),new("@Notes",(object?)input.Notes??DBNull.Value)];

    public async Task ReviewRecipeAsync(int productId, bool approved, string? userEmail)
    {
        await EnsureRecipeSchemaAsync();
        var changed=await ScalarAsync("SELECT COUNT(1) FROM dbo.RecetasProducto WHERE ProductId=@ProductId;",new SqlParameter("@ProductId",productId));
        if(Convert.ToInt32(changed??0)==0) throw new InvalidOperationException("El producto no tiene una receta para revisar.");
        await ExecuteAsync("UPDATE dbo.RecetasProducto SET Status=@Status,ApprovedBy=@User,ApprovedAt=CASE WHEN @Approved=1 THEN SYSUTCDATETIME() ELSE NULL END,UpdatedAt=SYSUTCDATETIME() WHERE ProductId=@ProductId;",
            new SqlParameter("@Status",approved?"Aprobada":"Pendiente"),new SqlParameter("@User",(object?)userEmail??"Sistema"),new SqlParameter("@Approved",approved),new SqlParameter("@ProductId",productId));
        try { await AddAuditLogAsync(approved?"RECETA_APROBADA":"RECETA_RECHAZADA",$"Receta del producto {productId}: {(approved?"aprobada":"requiere correcciones")}",userEmail); } catch { }
    }

    public async Task<ProductionReadiness> ProductionMaterialReadinessAsync(int orderId)
    {
        await EnsureRecipeSchemaAsync();
        const string missingSql="""
            SELECT DISTINCT p.Name FROM dbo.DetallePedido d
            INNER JOIN dbo.Productos p ON p.ProductId=d.ProductId
            INNER JOIN dbo.TiposProducto t ON t.ProductTypeId=p.ProductTypeId
            LEFT JOIN dbo.RecetasProducto r ON r.ProductId=p.ProductId AND LOWER(r.Status)=LOWER('Aprobada')
            WHERE d.OrderId=@OrderId AND LOWER(t.Name)=LOWER('Producto terminado') AND r.RecipeId IS NULL;
            """;
        var missing=await QueryAsync(missingSql,r=>r.GetString("Name"),new SqlParameter("@OrderId",orderId));
        const string reqSql="""
            SELECT i.IngredientProductId,p.Code,p.Name,i.Unit,
                   SUM((d.Quantity*i.Quantity/NULLIF(r.YieldQuantity,0))*(1+(r.WastePercent/100))) RequiredQuantity,
                   COALESCE((SELECT SUM(e.Quantity) FROM dbo.ExistenciasInventario e WHERE e.ProductId=i.IngredientProductId),0) Stock
            FROM dbo.DetallePedido d INNER JOIN dbo.RecetasProducto r ON r.ProductId=d.ProductId
            INNER JOIN dbo.IngredientesReceta i ON i.RecipeId=r.RecipeId
            INNER JOIN dbo.Productos p ON p.ProductId=i.IngredientProductId
            WHERE d.OrderId=@OrderId AND LOWER(r.Status)=LOWER('Aprobada') AND i.IsOptional=0
            GROUP BY i.IngredientProductId,p.Code,p.Name,i.Unit;
            """;
        var materials=await QueryAsync(reqSql,r=>new MaterialRequirement(r.GetInt32("IngredientProductId"),r.GetString("Code"),r.GetString("Name"),r.GetDecimal("RequiredQuantity"),r.GetDecimal("Stock"),r.GetString("Unit")),new SqlParameter("@OrderId",orderId));
        return new ProductionReadiness(orderId,missing,materials,missing.Count==0&&materials.All(x=>x.AvailableQuantity>=x.RequiredQuantity));
    }

    public async Task ReserveProductionMaterialsAsync(int orderId,string? userEmail)
    {
        var ready=await ProductionMaterialReadinessAsync(orderId);
        if(ready.MissingRecipes.Count>0) throw new InvalidOperationException($"Falta validar la receta de {string.Join(", ",ready.MissingRecipes)}.");
        var shortages=ready.Materials.Where(x=>x.AvailableQuantity<x.RequiredQuantity).Select(x=>$"{x.Name}: faltan {(x.RequiredQuantity-x.AvailableQuantity):0.##} {x.Unit}").ToList();
        if(shortages.Count>0) throw new InvalidOperationException("No hay materia prima suficiente. "+string.Join("; ",shortages));
        var locationId=await EnsureInventoryLocationAsync();
        await using var connection=CreateConnection(); await connection.OpenAsync(); await using var tx=await connection.BeginTransactionAsync();
        try
        {
            var already=Convert.ToInt32(await ScalarInTransactionAsync(connection,tx,"SELECT COUNT(1) FROM dbo.ReservasMaterialProduccion WHERE OrderId=@OrderId AND Status='Consumido';",new SqlParameter("@OrderId",orderId))??0);
            if(already>0){await tx.CommitAsync();return;}
            foreach(var item in ready.Materials)
            {
                var affectedSql="UPDATE dbo.ExistenciasInventario SET Quantity=Quantity-@Quantity,UpdatedAt=SYSUTCDATETIME() WHERE ProductId=@ProductId AND InventoryLocationId=@LocationId AND Quantity>=@Quantity;";
                await using var cmd=CreateCommand(connection,affectedSql,[new SqlParameter("@Quantity",item.RequiredQuantity),new SqlParameter("@ProductId",item.ProductId),new SqlParameter("@LocationId",locationId)],tx);
                if(await cmd.ExecuteNonQueryAsync()!=1) throw new InvalidOperationException($"El stock de {item.Name} cambió; vuelva a validar materiales.");
                await ExecuteInTransactionAsync(connection,tx,"INSERT INTO dbo.MovimientosInventario(ProductId,InventoryLocationId,MovementType,Quantity,Note,CreatedAt) VALUES(@ProductId,@LocationId,'SALIDA',@Quantity,@Note,SYSUTCDATETIME());",new SqlParameter("@ProductId",item.ProductId),new SqlParameter("@LocationId",locationId),new SqlParameter("@Quantity",item.RequiredQuantity),new SqlParameter("@Note",$"Consumo de producción pedido #{orderId}"));
                await ExecuteInTransactionAsync(connection,tx,"INSERT INTO dbo.ReservasMaterialProduccion(OrderId,IngredientProductId,RequiredQuantity,ReservedQuantity,Status,CreatedAt,UpdatedAt) VALUES(@OrderId,@ProductId,@Quantity,@Quantity,'Consumido',SYSUTCDATETIME(),SYSUTCDATETIME());",new SqlParameter("@OrderId",orderId),new SqlParameter("@ProductId",item.ProductId),new SqlParameter("@Quantity",item.RequiredQuantity));
            }
            await tx.CommitAsync();
        } catch {await tx.RollbackAsync();throw;}
        try{await AddAuditLogAsync("MATERIALES_PRODUCCION",$"Materiales consumidos para pedido #{orderId}",userEmail);}catch{}
    }

    public sealed record RecipeInput(int ProductId,decimal YieldQuantity,string YieldUnit,decimal WastePercent,string? Notes,IReadOnlyList<RecipeIngredientInput> Ingredients);
    public sealed record RecipeIngredientInput(int ProductId,decimal Quantity,string Unit,bool Optional=false,string? Notes=null);
    public sealed record MaterialRequirement(int ProductId,string Code,string Name,decimal RequiredQuantity,decimal AvailableQuantity,string Unit);
    public sealed record ProductionReadiness(int OrderId,IReadOnlyList<string> MissingRecipes,IReadOnlyList<MaterialRequirement> Materials,bool CanStart);
}
