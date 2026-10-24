#nullable enable
using System;
using System.Linq;
using System.Threading;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// One writer at a time per invoice for the swap prompt's details, which hold both the NEAR Intents
/// quotes and the source transactions customers reported: each writer reads, changes its part and writes back.
/// A fixed set of gates, picked by invoice id, so a gate is never replaced while a writer holds it.
/// </summary>
public static class PromptDetailsGate
{
    static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static SemaphoreSlim For(string invoiceId) =>
        Gates[(int)((uint)StringComparer.Ordinal.GetHashCode(invoiceId) % (uint)Gates.Length)];
}
