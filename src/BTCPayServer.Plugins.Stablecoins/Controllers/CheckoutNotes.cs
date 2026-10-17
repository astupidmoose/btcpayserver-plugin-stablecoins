#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.Stablecoins.Controllers;

/// <summary>
/// Bounds what the anonymous checkout endpoints can write: a few notes per invoice and a few requests per caller.
/// Entries expire, and the cache has a size limit, so memory stays bounded whatever callers do.
/// </summary>
public class CheckoutNotes : IDisposable
{
    public const int MaxNotesPerInvoice = 20;
    public const int MaxRequestsPerCaller = 60;
    public static readonly TimeSpan NotesFor = TimeSpan.FromHours(6);
    public static readonly TimeSpan CallerWindow = TimeSpan.FromMinutes(10);

    readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 50_000 });

    /// <summary>True the first time a key is noted for an invoice, while the invoice is under its limit.</summary>
    public bool TryAdd(string invoiceId, string key)
    {
        var keys = _cache.GetOrCreate("notes:" + invoiceId, e =>
        {
            e.SlidingExpiration = NotesFor;
            e.Size = 1;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        });
        if (keys is null)
            return false;
        lock (keys)
            return keys.Count < MaxNotesPerInvoice && keys.Add(key);
    }

    /// <summary>A fixed window per remote address.</summary>
    public bool AllowCaller(string? address)
    {
        var counter = _cache.GetOrCreate("caller:" + (address ?? "unknown"), e =>
        {
            e.AbsoluteExpirationRelativeToNow = CallerWindow;
            e.Size = 1;
            return new int[1];
        });
        if (counter is null)
            return false;
        lock (counter)
            return ++counter[0] <= MaxRequestsPerCaller;
    }

    public void Dispose() => _cache.Dispose();
}
