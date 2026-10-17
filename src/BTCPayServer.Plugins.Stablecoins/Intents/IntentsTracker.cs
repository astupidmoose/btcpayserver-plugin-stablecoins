#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Intents;

/// <summary>Resumes the saved NEAR Intents quotes at startup, then follows every open one in the background.</summary>
public class IntentsTracker(IntentsDepositService intents, StoreRepository storeRepository, ILogger<IntentsTracker> logger) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await intents.Restore(storeRepository, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not resume NEAR Intents deposits");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await intents.FollowAll(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not follow NEAR Intents deposits");
            }
            await Task.Delay(Every, stoppingToken);
        }
    }
}
