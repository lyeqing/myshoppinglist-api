using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Workers;

public sealed class ProductImportWorker(IServiceScopeFactory scopes, IOptions<ProductImportOptions> options,
    TimeProvider clock, ILogger<ProductImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            ProductImportClaim? claim = null;
            try
            {
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var jobs = scope.ServiceProvider.GetRequiredService<ProductImportJobService>();
                    await jobs.RecoverExpiredAsync(stoppingToken);
                    claim = await jobs.ClaimNextAsync(stoppingToken);
                }
                if (claim is not null) await RunClaimAsync(claim, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Log exception type only: provider exception messages can contain source URLs or response data.
                logger.LogError("Import worker iteration failed ({ExceptionType}); durable jobs remain recoverable", exception.GetType().Name);
            }
            if (claim is null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task RunClaimAsync(ProductImportClaim claim, CancellationToken stoppingToken)
    {
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(claim, processing, heartbeat.Token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ProductImportProcessor>().ProcessAsync(claim, processing.Token);
        }
        catch (OperationCanceledException) when (processing.IsCancellationRequested)
        {
            // Shutdown or loss of lease leaves the SQL record for recovery; never write through a stale claim.
        }
        catch (Exception exception)
        {
            logger.LogError("Import job {ProductImportJobId} failed unexpectedly ({ExceptionType})", claim.JobId, exception.GetType().Name);
            if (!processing.IsCancellationRequested)
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ProductImportJobService>()
                    .FailAsync(claim, "unexpected_processing_failure", true, processing.Token);
            }
        }
        finally
        {
            await heartbeat.CancelAsync();
            await renewal;
        }
    }

    private async Task RenewLeaseAsync(ProductImportClaim claim, CancellationTokenSource processing, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.RenewalSeconds), clock, token);
                await using var scope = scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<ProductImportJobService>().RenewAsync(claim, token))
                { await processing.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Lease renewal failed for import job {ProductImportJobId} ({ExceptionType})", claim.JobId, exception.GetType().Name);
            await processing.CancelAsync();
        }
    }
}
