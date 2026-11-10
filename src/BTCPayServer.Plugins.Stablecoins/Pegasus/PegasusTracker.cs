#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Pegasus;

/// <summary>Resumes the saved Pegasus Swap orders at startup (until that succeeds), and follows every open one in the background.</summary>
public class PegasusTracker(PegasusSwapService swaps, StoreRepository storeRepository, ILogger<PegasusTracker> logger) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var restored = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Until every saved order is picked up (the database may not answer at startup), try again each round.
            if (!restored)
            {
                try
                {
                    restored = await swaps.Restore(storeRepository, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not resume Pegasus Swap orders; trying again shortly");
                }
            }
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
