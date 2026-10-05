using Microsoft.Data.SqlClient;

namespace BakeSmartPatri.Data;

public sealed partial class SqlStore
{
    public async Task ScheduleTemporaryQaArtifactAsync(string entityType, int entityId, string createdBy)
    {
        var table = UseMySql ? "TemporaryQaArtifacts" : "dbo.TemporaryQaArtifacts";
        if (UseMySql)
            await ExecuteAsync("CREATE TABLE IF NOT EXISTS TemporaryQaArtifacts (ArtifactId int NOT NULL AUTO_INCREMENT PRIMARY KEY, EntityType varchar(40) NOT NULL, EntityId int NOT NULL, CreatedBy varchar(254) NOT NULL, DeleteAfter datetime NOT NULL, UNIQUE KEY UX_TemporaryQaArtifact(EntityType,EntityId)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
        else
            await ExecuteAsync("IF OBJECT_ID(N'dbo.TemporaryQaArtifacts',N'U') IS NULL CREATE TABLE dbo.TemporaryQaArtifacts(ArtifactId int IDENTITY PRIMARY KEY,EntityType nvarchar(40) NOT NULL,EntityId int NOT NULL,CreatedBy nvarchar(254) NOT NULL,DeleteAfter datetime2 NOT NULL,CONSTRAINT UX_TemporaryQaArtifact UNIQUE(EntityType,EntityId));");
        var sql = UseMySql
            ? $"INSERT INTO {table}(EntityType,EntityId,CreatedBy,DeleteAfter) VALUES(@Type,@Id,@By,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)) ON DUPLICATE KEY UPDATE DeleteAfter=VALUES(DeleteAfter),CreatedBy=VALUES(CreatedBy);"
            : $"IF EXISTS(SELECT 1 FROM {table} WHERE EntityType=@Type AND EntityId=@Id) UPDATE {table} SET DeleteAfter=DATEADD(minute,10,SYSUTCDATETIME()),CreatedBy=@By WHERE EntityType=@Type AND EntityId=@Id; ELSE INSERT INTO {table}(EntityType,EntityId,CreatedBy,DeleteAfter) VALUES(@Type,@Id,@By,DATEADD(minute,10,SYSUTCDATETIME()));";
        await ExecuteAsync(sql, new SqlParameter("@Type", entityType), new SqlParameter("@Id", entityId), new SqlParameter("@By", createdBy));
    }

    public async Task CleanupExpiredQaArtifactsAsync()
    {
        var table = UseMySql ? "TemporaryQaArtifacts" : "dbo.TemporaryQaArtifacts";
        try
        {
            var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
            var expired = await QueryAsync($"SELECT ArtifactId,EntityType,EntityId FROM {table} WHERE DeleteAfter<={now};", r => new { ArtifactId=r.GetInt32("ArtifactId"), Type=r.GetString("EntityType"), Id=r.GetInt32("EntityId") });
            foreach (var item in expired)
            {
                var prefix = UseMySql ? "" : "dbo.";
                var sql = item.Type switch
                {
                    "MARKETING" => $"DELETE FROM {prefix}ComunicacionesMarketingDestinatarios WHERE CommunicationId=@Id; DELETE FROM {prefix}ComunicacionesMarketing WHERE CommunicationId=@Id;",
                    "EXPENSE" => $"DELETE FROM {prefix}LineasAsientoContable WHERE AccountingEntryId IN (SELECT AccountingEntryId FROM {prefix}AsientosContables WHERE ReferenceTable='Gastos' AND ReferenceId=@Id); DELETE FROM {prefix}AsientosContables WHERE ReferenceTable='Gastos' AND ReferenceId=@Id; DELETE FROM {prefix}Gastos WHERE ExpenseId=@Id;",
                    "SUPPLIER_PAYMENT" => $"DELETE FROM {prefix}LineasAsientoContable WHERE AccountingEntryId IN (SELECT AccountingEntryId FROM {prefix}AsientosContables WHERE ReferenceTable='PagosProveedor' AND ReferenceId=@Id); DELETE FROM {prefix}AsientosContables WHERE ReferenceTable='PagosProveedor' AND ReferenceId=@Id; DELETE FROM {prefix}PagosProveedor WHERE SupplierPaymentId=@Id;",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(sql)) await ExecuteAsync(sql, new SqlParameter("@Id", item.Id));
                await ExecuteAsync($"DELETE FROM {table} WHERE ArtifactId=@ArtifactId;", new SqlParameter("@ArtifactId", item.ArtifactId));
            }
        }
        catch { /* Table may not exist before the first QA artifact is created. */ }
    }
}
