using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Workers;

public sealed class PriceRefreshWorker(IServiceScopeFactory scopes, IOptions<PriceRefreshOptions> options,
    IOptions<ProductImportOptions> imports, ILogger<PriceRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The import switch is the application's master background-processing switch.
        if (!options.Value.Enabled || !imports.Value.Enabled) return;
        logger.LogInformation("Price refresh worker started; scanning every {Minutes} minutes", options.Value.ScanMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var count = await scope.ServiceProvider.GetRequiredService<PriceRefreshService>().ScanAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Queued {Count} stale retailer products for refresh", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Price refresh scan failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(options.Value.ScanMinutes), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
