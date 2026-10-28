#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Pegasus;

/// <summary>Resumes the saved Pegasus Swap orders at startup, then follows every open one in the background.</summary>
public class PegasusTracker(PegasusSwapService swaps, StoreRepository storeRepository, ILogger<PegasusTracker> logger) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await swaps.Restore(storeRepository, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not resume Pegasus Swap orders");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await swaps.FollowAll(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not follow Pegasus Swap orders");
            }
            await Task.Delay(Every, stoppingToken);
        }
    }
}
