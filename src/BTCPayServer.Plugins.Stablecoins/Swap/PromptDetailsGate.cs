#nullable enable
using System.Collections.Concurrent;
using System.Threading;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// One writer at a time per invoice for the swap prompt's details, which hold both the NEAR Intents
/// quotes and the source transactions customers reported: each writer reads, changes its part and writes back.
/// </summary>
public static class PromptDetailsGate
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public static SemaphoreSlim For(string invoiceId) => Gates.GetOrAdd(invoiceId, _ => new SemaphoreSlim(1, 1));

    public static void Forget(string invoiceId) => Gates.TryRemove(invoiceId, out _);
}
