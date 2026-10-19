using Microsoft.Data.SqlClient;

namespace BakeSmartPatri.Data;

public sealed partial class SqlStore
{
    private static readonly SemaphoreSlim TemporaryQaSchemaLock = new(1, 1);
    private static volatile bool _temporaryQaSchemaReady;

    private async Task EnsureTemporaryQaSchemaAsync()
    {
        if (_temporaryQaSchemaReady) return;

        await TemporaryQaSchemaLock.WaitAsync();
        try
        {
            if (_temporaryQaSchemaReady) return;

            if (UseMySql)
                await ExecuteAsync("CREATE TABLE IF NOT EXISTS TemporaryQaArtifacts (ArtifactId int NOT NULL AUTO_INCREMENT PRIMARY KEY, EntityType varchar(40) NOT NULL, EntityId int NOT NULL, CreatedBy varchar(254) NOT NULL, DeleteAfter datetime NOT NULL, UNIQUE KEY UX_TemporaryQaArtifact(EntityType,EntityId)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
            else
                await ExecuteAsync("IF OBJECT_ID(N'dbo.TemporaryQaArtifacts',N'U') IS NULL CREATE TABLE dbo.TemporaryQaArtifacts(ArtifactId int IDENTITY PRIMARY KEY,EntityType nvarchar(40) NOT NULL,EntityId int NOT NULL,CreatedBy nvarchar(254) NOT NULL,DeleteAfter datetime2 NOT NULL,CONSTRAINT UX_TemporaryQaArtifact UNIQUE(EntityType,EntityId));");

            _temporaryQaSchemaReady = true;
        }
        finally
        {
            TemporaryQaSchemaLock.Release();
        }
    }

    public async Task ScheduleTemporaryQaArtifactAsync(string entityType, int entityId, string createdBy)
    {
        await EnsureTemporaryQaSchemaAsync();
        var table = UseMySql ? "TemporaryQaArtifacts" : "dbo.TemporaryQaArtifacts";
        var sql = UseMySql
            ? $"INSERT INTO {table}(EntityType,EntityId,CreatedBy,DeleteAfter) VALUES(@Type,@Id,@By,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)) ON DUPLICATE KEY UPDATE DeleteAfter=VALUES(DeleteAfter),CreatedBy=VALUES(CreatedBy);"
            : $"IF EXISTS(SELECT 1 FROM {table} WHERE EntityType=@Type AND EntityId=@Id) UPDATE {table} SET DeleteAfter=DATEADD(minute,10,SYSUTCDATETIME()),CreatedBy=@By WHERE EntityType=@Type AND EntityId=@Id; ELSE INSERT INTO {table}(EntityType,EntityId,CreatedBy,DeleteAfter) VALUES(@Type,@Id,@By,DATEADD(minute,10,SYSUTCDATETIME()));";
        await ExecuteAsync(sql, new SqlParameter("@Type", entityType), new SqlParameter("@Id", entityId), new SqlParameter("@By", createdBy));
    }

    public async Task CleanupExpiredQaArtifactsAsync()
    {
        await EnsureTemporaryQaSchemaAsync();
        var table = UseMySql ? "TemporaryQaArtifacts" : "dbo.TemporaryQaArtifacts";
        var recoverUntrackedProducts = UseMySql
            ? $"""
              INSERT INTO {table}(EntityType,EntityId,CreatedBy,DeleteAfter)
              SELECT 'PRODUCT',p.ProductId,'system:legacy-qa-recovery',UTC_TIMESTAMP()
              FROM Productos p
              LEFT JOIN {table} tracked
                ON tracked.EntityType='PRODUCT' AND tracked.EntityId=p.ProductId
              WHERE p.IsActive=1
                AND p.Code LIKE 'PT-QA%'
                AND p.CreatedAt<=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
                AND tracked.ArtifactId IS NULL
              ON DUPLICATE KEY UPDATE DeleteAfter=VALUES(DeleteAfter);
              """
            : $"""
              INSERT INTO {table}(EntityType,EntityId,CreatedBy,DeleteAfter)
              SELECT N'PRODUCT',p.ProductId,N'system:legacy-qa-recovery',SYSUTCDATETIME()
              FROM dbo.Productos p
              WHERE p.IsActive=1
                AND p.Code LIKE N'PT-QA%'
                AND p.CreatedAt<=DATEADD(minute,-10,SYSUTCDATETIME())
                AND NOT EXISTS(
                    SELECT 1 FROM {table} tracked
                    WHERE tracked.EntityType=N'PRODUCT' AND tracked.EntityId=p.ProductId
                );
              """;
        await ExecuteAsync(recoverUntrackedProducts);

        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        var expired = await QueryAsync($"SELECT ArtifactId,EntityType,EntityId FROM {table} WHERE DeleteAfter<={now};", r => new { ArtifactId=r.GetInt32("ArtifactId"), Type=r.GetString("EntityType"), Id=r.GetInt32("EntityId") });
        var failures = new List<Exception>();
        foreach (var item in expired)
        {
            try
            {
                var prefix = UseMySql ? "" : "dbo.";
                var sql = item.Type switch
                {
                    "MARKETING" => $"DELETE FROM {prefix}ComunicacionesMarketingDestinatarios WHERE CommunicationId=@Id; DELETE FROM {prefix}ComunicacionesMarketing WHERE CommunicationId=@Id;",
                    "EXPENSE" => $"DELETE FROM {prefix}LineasAsientoContable WHERE AccountingEntryId IN (SELECT AccountingEntryId FROM {prefix}AsientosContables WHERE ReferenceTable='Gastos' AND ReferenceId=@Id); DELETE FROM {prefix}AsientosContables WHERE ReferenceTable='Gastos' AND ReferenceId=@Id; DELETE FROM {prefix}Gastos WHERE ExpenseId=@Id;",
                    "SUPPLIER_PAYMENT" => $"DELETE FROM {prefix}LineasAsientoContable WHERE AccountingEntryId IN (SELECT AccountingEntryId FROM {prefix}AsientosContables WHERE ReferenceTable='PagosProveedor' AND ReferenceId=@Id); DELETE FROM {prefix}AsientosContables WHERE ReferenceTable='PagosProveedor' AND ReferenceId=@Id; DELETE FROM {prefix}PagosProveedor WHERE SupplierPaymentId=@Id;",
                    "PRODUCT" => $"UPDATE {prefix}Productos SET IsActive=0 WHERE ProductId=@Id; DELETE FROM {prefix}MovimientosInventario WHERE ProductId=@Id; DELETE FROM {prefix}ExistenciasInventario WHERE ProductId=@Id; DELETE FROM {prefix}LotesInventario WHERE ProductId=@Id; DELETE FROM {prefix}ImagenesProducto WHERE ProductId=@Id; DELETE FROM {prefix}ProductosPromocion WHERE ProductId=@Id; DELETE FROM {prefix}ComboProductos WHERE ProductId=@Id; DELETE FROM {prefix}IngredientesReceta WHERE IngredientProductId=@Id OR RecipeId IN (SELECT RecipeId FROM {prefix}RecetasProducto WHERE ProductId=@Id); DELETE FROM {prefix}RecetasProducto WHERE ProductId=@Id; DELETE FROM {prefix}Productos WHERE ProductId=@Id AND NOT EXISTS(SELECT 1 FROM {prefix}DetallePedido WHERE ProductId=@Id);",
                    "RECIPE" => $"DELETE FROM {prefix}IngredientesReceta WHERE RecipeId IN (SELECT RecipeId FROM {prefix}RecetasProducto WHERE ProductId=@Id); DELETE FROM {prefix}RecetasProducto WHERE ProductId=@Id;",
                    "COMBO" => $"DELETE FROM {prefix}ComboProductos WHERE ComboId=@Id; DELETE FROM {prefix}Combos WHERE ComboId=@Id;",
                    "PROMOTION" => $"DELETE FROM {prefix}ProductosPromocion WHERE PromotionId=@Id; DELETE FROM {prefix}PromocionesClientes WHERE PromotionId=@Id; DELETE FROM {prefix}Promociones WHERE PromotionId=@Id;",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(sql)) await ExecuteAsync(sql, new SqlParameter("@Id", item.Id));
                await ExecuteAsync($"DELETE FROM {table} WHERE ArtifactId=@ArtifactId;", new SqlParameter("@ArtifactId", item.ArtifactId));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException($"No se pudo limpiar el artefacto QA {item.Type} #{item.Id}.", ex));
            }
        }

        if (failures.Count > 0)
            throw new AggregateException(failures);
    }

    public async Task<bool> IsTemporaryQaArtifactAsync(string entityType, int entityId)
    {
        await EnsureTemporaryQaSchemaAsync();
        var table = UseMySql ? "TemporaryQaArtifacts" : "dbo.TemporaryQaArtifacts";
        try
        {
            var count = await ScalarAsync($"SELECT COUNT(1) FROM {table} WHERE EntityType=@Type AND EntityId=@Id;", new SqlParameter("@Type", entityType), new SqlParameter("@Id", entityId));
            return Convert.ToInt32(count ?? 0) > 0;
        }
        catch { return false; }
    }
}
