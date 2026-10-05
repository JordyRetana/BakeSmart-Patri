using BakeSmartPatri.Data;

namespace BakeSmartPatri.Services;

public sealed class TemporaryQaArtifactCleanupService(IServiceScopeFactory scopeFactory, ILogger<TemporaryQaArtifactCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SqlStore>().CleanupExpiredQaArtifactsAsync();
            }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo limpiar un dato temporal de pruebas."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
