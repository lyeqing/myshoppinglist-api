using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Workers;

public sealed class PackDiscoveryWorker(IServiceScopeFactory scopes, IOptions<ProductImportOptions> options,
    ILogger<PackDiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            PackDiscoveryClaim? claim = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<PackDiscoveryService>();
                claim = await jobs.ClaimAsync(stoppingToken);
                if (claim is not null)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    // A claim lasts three minutes; abandon reads before its fence expires.
                    deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    try { await scope.ServiceProvider.GetRequiredService<PackDiscoveryProcessor>().RunAsync(claim, deadline.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    { await jobs.FinishAsync(claim, "Failed", 0, "search_timeout", stoppingToken, TimeSpan.FromMinutes(1)); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Package discovery failed ({ExceptionType})", error.GetType().Name);
                if (claim is not null)
                {
                    try
                    {
                        await using var recovery = scopes.CreateAsyncScope();
                        await recovery.ServiceProvider.GetRequiredService<PackDiscoveryService>().FinishAsync(claim, "Failed", 0,
                            "discovery_check_failed", stoppingToken, TimeSpan.FromMinutes(1));
                    }
                    catch (Exception recoveryError) when (recoveryError is not OperationCanceledException)
                    { logger.LogWarning("Discovery recovery deferred ({ExceptionType})", recoveryError.GetType().Name); }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(claim is null ? Math.Max(1, options.Value.PollSeconds) : 1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
