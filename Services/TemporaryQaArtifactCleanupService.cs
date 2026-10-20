using BakeSmartPatri.Data;

namespace BakeSmartPatri.Services;

public sealed class TemporaryQaArtifactCleanupService(IServiceScopeFactory scopeFactory, IWebHostEnvironment environment, ILogger<TemporaryQaArtifactCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SqlStore>().CleanupExpiredQaArtifactsAsync();
                CleanupQaImages(environment.WebRootPath);
            }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo limpiar un dato temporal de pruebas."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private static void CleanupQaImages(string webRootPath)
    {
        var folder = Path.Combine(webRootPath, "img", "uploads", "qa");
        if (!Directory.Exists(folder)) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (File.GetCreationTimeUtc(file) <= cutoff) File.Delete(file);
            }
            catch { }
        }
    }
}
