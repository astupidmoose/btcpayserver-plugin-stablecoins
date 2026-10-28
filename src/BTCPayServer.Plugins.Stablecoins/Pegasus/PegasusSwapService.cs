#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Pegasus;

/// <summary>What the Monero checkout shows: send exactly <see cref="Amount"/> XMR to <see cref="Address"/>.</summary>
public record PegasusPayment(
    string OrderNumber,
    string Address,
    string? Memo,
    decimal Amount,
    long TargetSats,
    int Status,
    DateTimeOffset Created,
    DateTimeOffset CheckedAt,
    decimal? ReceiveBtc = null)
{
    /// <summary>What the pending payment is recorded as: the order's BTC, or what was asked for.</summary>
    public decimal PendingBtc => ReceiveBtc is > 0m and var r ? r : TargetSats / 100_000_000m;
}

public enum PegasusState { Preparing, Ready, Received, Refunded, Failed }

public record PegasusSwapStatus(PegasusState State, PegasusPayment? Payment, string? Error);

/// <summary>A swap as kept on the invoice (prompt details), so tracking survives a restart.</summary>
public class PersistedPegasusSwap
{
    public string OrderNumber { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Memo { get; set; }
    public string Amount { get; set; } = "0";
    public long TargetSats { get; set; }
    /// <summary>The BTC the order pays, as the exchange stated it.</summary>
    public string? ReceiveBtc { get; set; }
    public int Status { get; set; } = PegasusStatus.New;
    // BTCPay's serializer writes dates as unix seconds; this reads them back.
    [Newtonsoft.Json.JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset Created { get; set; }
    public string[] LoggedTxs { get; set; } = [];
    /// <summary>Search terms not written yet (the write failed); retried until they are.</summary>
    public string[] UnsavedTerms { get; set; } = [];
    /// <summary>The pending payment was recorded (or found not needed).</summary>
    public bool PendingAdded { get; set; }
    /// <summary>The pending payment was retired (refund, overdue, or no payout in time).</summary>
    public bool PendingRetired { get; set; }
    /// <summary>Followed for its full time without paying out.</summary>
    public bool GaveUp { get; set; }
}

/// <summary>
/// Prepares Pegasus Swap fixed-rate swaps for Monero checkouts: XMR in, the BTC due out to this invoice's own address.
/// Shows the swap's one-time XMR deposit address and amount, and follows every swap made for an invoice in the background
/// until it is settled, whether or not anyone has the checkout open, and across restarts (<see cref="PegasusTracker"/>).
/// The BTC itself is credited by <see cref="SwapToBtcListener"/> when it reaches the address, like every other swap.
/// </summary>
public class PegasusSwapService(PegasusClient client, InvoiceRepository invoiceRepository, ILogger<PegasusSwapService> logger,
    SwapSecrets? secrets = null, IPendingPayments? pendingPayments = null)
{
    public const string DepositCoin = "xmr";
    public const string ReceiveCoin = "btc";
    /// <summary>XMR amounts are sent with at most this many decimals: exact for any wallet, and below a cent.</summary>
    public const int AmountDecimals = 6;
    /// <summary>Environment variable that points the client somewhere else (a local test double). Not a store setting.</summary>
    public const string UrlVariable = "BTCPAY_STABLECOINS_PEGASUS_URL";

    static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(20);
    static readonly TimeSpan RetryFailedAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan NetworksFor = TimeSpan.FromHours(1);
    /// <summary>An unsettled swap is followed this long: a late deposit or a slow exchange is still worth logging.</summary>
    static readonly TimeSpan FollowFor = TimeSpan.FromHours(24);
    static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);
    /// <summary>A minimum or maximum the exchange stated is trusted this long before asking again.</summary>
    static readonly TimeSpan LimitsFor = TimeSpan.FromMinutes(30);
    /// <summary>Swaps kept per invoice: one per amount due (a partial payment asks a new one). Bounds what callers can make it ask.</summary>
    const int MaxSwapsPerInvoice = 6;
    /// <summary>Orders created for one checkout before giving up on getting one that pays what is due.</summary>
    const int MaxCreateAttempts = 3;

    static readonly Newtonsoft.Json.JsonSerializer DetailsSerializer = BlobSerializer.CreateSerializer().Serializer;
    static readonly Regex MinimumPattern = new(@"minimum amount[^\d]*([\d.,]+)", RegexOptions.IgnoreCase);
    static readonly Regex MaximumPattern = new(@"maximum amount[^\d]*([\d.,]+)", RegexOptions.IgnoreCase);

    class Swap
    {
        public required PegasusPayment Payment;
        public readonly HashSet<string> LoggedTxs = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> UnsavedTerms = new(StringComparer.OrdinalIgnoreCase);
        public bool Final => Payment.Status is PegasusStatus.Success or PegasusStatus.Refund;
        /// <summary>Followed for its full time: its pending payment, if any, stops counting.</summary>
        public bool GaveUp;
        public bool PendingAdded;
        public bool PendingRetired;
        /// <summary>The exchange has the XMR and the order still counts: it should hold the invoice in Processing.</summary>
        public bool NeedsAdd => !PendingAdded && Received(Payment.Status) && !Abandoned;
        /// <summary>The order will not pay out: a pending payment recorded for it should stop counting.</summary>
        public bool NeedsRetire => PendingAdded && !PendingRetired && Abandoned;
        bool Abandoned => GaveUp || Payment.Status is PegasusStatus.Refund or PegasusStatus.Overdue;
        /// <summary>
        /// Nothing left to follow: no pending payment to record or retire, and the order is either past its follow time,
        /// retired, or finished without one. A paid-out order with a pending payment is kept until its follow time ends,
        /// in case the BTC never comes.
        /// </summary>
        public bool Done => !NeedsAdd && !NeedsRetire && (GaveUp || PendingRetired || Final && !PendingAdded);
    }

    class Entry
    {
        public required string InvoiceId;
        public required string StoreId;
        public required PaymentMethodId PaymentMethodId;
        public SwapToBtcPaymentMethodConfig Config = new();
        public readonly List<Swap> Swaps = new();
        public long Target;
        public string? Error;
        public DateTimeOffset ErrorAt;
        public string? LoggedError;
        public Task? Running;
        public DateTimeOffset Touched;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new();
    (string Deposit, string Receive, DateTimeOffset At)? _networks;
    /// <summary>
    /// The estimate asked at invoice creation, per store and BTC amount, reused when the checkout opens soon after so the
    /// address shows sooner. Only the XMR amount comes from it: every order made is still checked to pay what is due.
    /// </summary>
    readonly ConcurrentDictionary<(string Store, decimal Btc), (PegasusEstimate Estimate, DateTimeOffset At)> _estimates = new();
    static readonly TimeSpan EstimateFor = TimeSpan.FromMinutes(2);
    /// <summary>Per store: the smallest BTC amount refused as too small and the largest refused as too large, and when.</summary>
    readonly ConcurrentDictionary<string, (decimal? Below, decimal? Above, DateTimeOffset At)> _limits = new();

    /// <summary>Settable so tests do not wait.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Settable by tests; otherwise Pegasus Swap's own endpoint, or the environment variable's.</summary>
    public Uri BaseUrl { get; set; } = Intents.IntentsDepositService.SafeUrl(Environment.GetEnvironmentVariable(UrlVariable), PegasusClient.DefaultUrl);

    /// <summary>Used by tests to wait for background work.</summary>
    public Task Pending(string invoiceId) =>
        _entries.TryGetValue(invoiceId, out var e) && e.Running is { } t ? t : Task.CompletedTask;

    async Task<PegasusClient.Credentials?> Keys(string storeId, SwapToBtcPaymentMethodConfig config)
    {
        // Only the plugin's own store setting: a key in a payment method config would be readable through BTCPay's API.
        var keys = secrets is null ? null : await secrets.GetAsync(storeId);
        var publicKey = secrets is null ? config.PegasusPublicKey : keys?.PegasusPublicKey;
        var secret = secrets is null ? config.PegasusSecret : keys?.PegasusSecret;
        return string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(secret) ? null : new(publicKey.Trim(), secret.Trim());
    }

    /// <summary>
    /// Why Monero cannot be offered on a new invoice for <paramref name="btcDue"/>, or null when it can.
    /// Asks the exchange for an estimate, so an order below its minimum (or above its maximum) does not offer Monero at all.
    /// </summary>
    public async Task<string?> Unavailable(string storeId, SwapToBtcPaymentMethodConfig config, decimal btcDue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.RefundMonero))
            return "No XMR refund address is set";
        if (await Keys(storeId, config) is not { } keys)
            return "The Pegasus Swap API keys are not set";
        var target = TargetUnits(btcDue, config.BufferPercent) / 100_000_000m;
        var now = Now();
        if (_limits.TryGetValue(storeId, out var limits) && now - limits.At < LimitsFor)
        {
            if (limits.Below is { } below && target <= below)
                return "Below the exchange's minimum";
            if (limits.Above is { } above && target >= above)
                return "Above the exchange's maximum";
        }
        try
        {
            var (deposit, receive) = await Networks(keys, cancellationToken);
            var estimate = await client.Estimate(BaseUrl, keys, DepositCoin, deposit, ReceiveCoin, receive, target, "receive", cancellationToken);
            foreach (var (key, old) in _estimates.ToArray())
                if (now - old.At >= EstimateFor)
                    _estimates.TryRemove(key, out _);
            _estimates[(storeId, target)] = (estimate, now);
            return null;
        }
        catch (PegasusException ex) when (ex.Status == 400 && Limit(ex.Message) is { } limit)
        {
            _limits.AddOrUpdate(storeId,
                _ => limit == Bound.Minimum ? (target, null, now) : (null, target, now),
                (_, old) => now - old.At >= LimitsFor
                    ? limit == Bound.Minimum ? (target, null, now) : (null, target, now)
                    : limit == Bound.Minimum ? (Math.Max(old.Below ?? 0m, target), old.Above, now) : (old.Below, Math.Min(old.Above ?? target, target), now));
            return limit == Bound.Minimum ? "Below the exchange's minimum" : "Above the exchange's maximum";
        }
        catch (Exception ex)
        {
            // Includes a timeout: a slow exchange leaves Monero off this invoice instead of failing the invoice.
            logger.LogWarning(ex, "Could not get a Monero estimate for store {StoreId}", storeId);
            return "The exchange could not be reached: " + ex.Message;
        }
    }

    enum Bound { Minimum, Maximum }

    static Bound? Limit(string message) =>
        MinimumPattern.IsMatch(message) ? Bound.Minimum : MaximumPattern.IsMatch(message) ? Bound.Maximum : null;

    /// <summary>The BTC due plus the buffer, in satoshis, rounded up.</summary>
    public static long TargetUnits(decimal btcDue, decimal bufferPercent) => Intents.IntentsDepositService.TargetUnits(btcDue, bufferPercent);

    /// <summary>
    /// The swap to show on the Monero checkout: made in the background the first time, then followed.
    /// A new amount due (after a partial payment) asks a new swap; the earlier ones are still followed.
    /// </summary>
    public PegasusSwapStatus Get(SwapToBtcPaymentMethodConfig config, string storeId, PaymentMethodId pmi, string invoiceId,
        string btcAddress, decimal btcDue)
    {
        var target = TargetUnits(btcDue, config.BufferPercent);
        var now = Now();
        var entry = _entries.GetOrAdd(invoiceId, _ => new Entry { InvoiceId = invoiceId, StoreId = storeId, PaymentMethodId = pmi });
        PegasusSwapStatus result;
        lock (entry)
        {
            entry.Touched = now;
            entry.Config = config;
            // A deposit on its way stays on screen even if the amount due changed meanwhile. A finished order only for the
            // amount it was made for: after a short payout, the rest gets a new order.
            var shown = entry.Swaps.LastOrDefault(s => InProgress(s.Payment.Status))
                        ?? entry.Swaps.LastOrDefault(s => s.Payment.TargetSats == target && s.Payment.Status is PegasusStatus.New or PegasusStatus.Success or PegasusStatus.Refund);
            if (shown is not null)
            {
                if (!shown.Final && now - shown.Payment.CheckedAt > RecheckEvery && entry.Running is not { IsCompleted: false })
                    entry.Running = Task.Run(() => Recheck(entry, shown));
                return StatusOf(shown.Payment);
            }
            if (entry.Running is { IsCompleted: false })
                return new(PegasusState.Preparing, null, null);
            if (entry.Error is not null && entry.Target == target && now - entry.ErrorAt < RetryFailedAfter)
                return new(PegasusState.Failed, null, entry.Error);
            if (entry.Swaps.Count >= MaxSwapsPerInvoice)
                return new(PegasusState.Failed, null, "Paying with Monero is unavailable right now. Please choose another payment method.");
            entry.Target = target;
            entry.Error = null;
            entry.Running = Task.Run(() => Prepare(entry, btcAddress, target));
            result = new(PegasusState.Preparing, null, null);
        }
        Forget(now);
        return result;
    }

    /// <summary>The exchange has the customer's XMR and has not finished: confirming, swapping, paying out, or held up (error, pause, stuck).</summary>
    static bool InProgress(int status) => status is PegasusStatus.Confirmation or PegasusStatus.Exchanging or PegasusStatus.Sending
        or PegasusStatus.Error or PegasusStatus.Pause or PegasusStatus.Stuck;

    /// <summary>The exchange has the customer's XMR (or already paid out).</summary>
    static bool Received(int status) => InProgress(status) || status == PegasusStatus.Success;

    static PegasusSwapStatus StatusOf(PegasusPayment payment) => payment.Status switch
    {
        PegasusStatus.New => new(PegasusState.Ready, payment, null),
        PegasusStatus.Refund => new(PegasusState.Refunded, payment, null),
        _ => new(PegasusState.Received, payment, null)
    };

    async Task<(string Deposit, string Receive)> Networks(PegasusClient.Credentials keys, CancellationToken cancellationToken)
    {
        if (_networks is { } n && Now() - n.At < NetworksFor)
            return (n.Deposit, n.Receive);
        var xmr = await client.GetCoinNetworks(BaseUrl, keys, DepositCoin, cancellationToken);
        var btc = await client.GetCoinNetworks(BaseUrl, keys, ReceiveCoin, cancellationToken);
        var deposit = PickNetwork(xmr.Deposit, DepositCoin) ?? throw new PegasusException(null, "Pegasus Swap lists no network to deposit XMR on");
        var receive = PickNetwork(btc.Withdraw, ReceiveCoin) ?? throw new PegasusException(null, "Pegasus Swap lists no network to receive BTC on");
        _networks = (deposit, receive, Now());
        return (deposit, receive);
    }

    /// <summary>The coin's own network (XMR for xmr), or the only one listed.</summary>
    public static string? PickNetwork(string[] networks, string coin) =>
        networks.FirstOrDefault(n => n.Equals(coin, StringComparison.OrdinalIgnoreCase)) ?? (networks.Length == 1 ? networks[0] : null);

    /// <summary>
    /// The XMR to send, from an estimate asked for the BTC amount: the side that is not the BTC itself
    /// (the docs do not say which field carries which side when the receive amount is given).
    /// </summary>
    public static decimal? DepositFromEstimate(PegasusEstimate estimate, decimal btc)
    {
        static bool Near(decimal? value, decimal btc) => value is { } v && btc > 0m && Math.Abs(v - btc) / btc < 0.01m;
        if (Near(estimate.Receive, btc) && estimate.Amount is > 0m && !Near(estimate.Amount, btc))
            return estimate.Amount;
        if (Near(estimate.Amount, btc) && estimate.Receive is > 0m && !Near(estimate.Receive, btc))
            return estimate.Receive;
        return null;
    }

    public static decimal RoundUp(decimal amount, int decimals)
    {
        var factor = 1m;
        for (var i = 0; i < decimals; i++) factor *= 10m;
        return decimal.Ceiling(amount * factor) / factor;
    }

    async Task Prepare(Entry entry, string btcAddress, long target)
    {
        var config = entry.Config;
        try
        {
            var keys = await Keys(entry.StoreId, config) ?? throw new PegasusException(null, "The Pegasus Swap API keys are not set");
            var refund = config.RefundMonero?.Trim() is { Length: > 0 } r ? r : throw new PegasusException(null, "No XMR refund address is set");
            var (depositNetwork, receiveNetwork) = await Networks(keys, CancellationToken.None);
            var btc = target / 100_000_000m;
            var estimate = _estimates.TryRemove((entry.StoreId, btc), out var cached) && Now() - cached.At < EstimateFor
                ? cached.Estimate
                : await client.Estimate(BaseUrl, keys, DepositCoin, depositNetwork, ReceiveCoin, receiveNetwork, btc, "receive", CancellationToken.None);
            var xmr = DepositFromEstimate(estimate, btc) ??
                      throw new PegasusException(null, $"Unexpected estimate for {btc} BTC: amount {estimate.Amount}, receive {estimate.Receive}");
            var deposit = RoundUp(xmr, AmountDecimals);
            var request = new PegasusCreateRequest
            {
                DepositCoin = DepositCoin,
                DepositAmount = deposit,
                DepositNetwork = depositNetwork,
                ReceiveAddress = btcAddress,
                ReceiveCoin = ReceiveCoin,
                ReceiveNetwork = receiveNetwork,
                RefundAddress = refund
            };
            var tx = await client.Create(BaseUrl, keys, request, CancellationToken.None);
            // The fixed rate can differ a little from the estimate: while the order pays less than is due, ask again for
            // proportionally more, a bounded number of times, and never show an order known to be short.
            for (var attempt = 1; tx.ReceiveAmount is > 0m and var paid && paid < btc; attempt++)
            {
                if (attempt >= MaxCreateAttempts)
                    throw new PegasusException(null, $"Pegasus Swap order {tx.OrderNumber} would pay {paid} BTC, less than the {btc} due, after {attempt} tries");
                await Log(entry.InvoiceId, $"Pegasus Swap order {tx.OrderNumber} would pay {paid} BTC, less than the {btc} due; asking again with more XMR.");
                request.DepositAmount = RoundUp(request.DepositAmount * btc / paid * 1.001m, AmountDecimals);
                tx = await client.Create(BaseUrl, keys, request, CancellationToken.None);
            }
            if (string.IsNullOrEmpty(tx.OrderNumber) || string.IsNullOrEmpty(tx.DepositAddress))
                throw new PegasusException(null, "The swap has no order number or deposit address");
            if (tx.ReceiveAddress is { Length: > 0 } to && to != btcAddress)
                throw new PegasusException(null, $"The swap pays {to}, not this invoice's address {btcAddress}");
            var amount = tx.DepositAmount is > 0m and var a ? RoundUp(a, AmountDecimals) : request.DepositAmount;
            var now = Now();
            var payment = new PegasusPayment(tx.OrderNumber, tx.DepositAddress, tx.DepositMemo, amount, target, tx.Status, now, now, tx.ReceiveAmount);
            var created = new Swap { Payment = payment };
            lock (entry)
                entry.Swaps.Add(created);
            await Log(entry.InvoiceId,
                $"Monero: send {amount} XMR to {payment.Address}" + (payment.Memo is null ? "" : $" with memo {payment.Memo}") +
                $". Pegasus Swap order {tx.OrderNumber} (fixed rate): " + (tx.ReceiveAmount is { } out_ ? $"{out_} BTC" : $"{btc} BTC asked") +
                $" to {btcAddress}. A swap that cannot complete is refunded in XMR to {refund}.");
            // Support can find the invoice by pasting the order number or the deposit address into the invoice search.
            await SearchTerms(entry.InvoiceId, created, payment.Address, tx.OrderNumber);
            await Save(entry);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prepare a Monero payment for {Address}", btcAddress);
            bool log;
            lock (entry)
            {
                entry.Error = ex is PegasusException { Status: 400 } pe && Limit(pe.Message) is not null
                    ? "This order is outside the amounts Monero can pay right now. Please choose another payment method."
                    : "Paying with Monero is unavailable right now. Please try again shortly or choose another payment method.";
                entry.ErrorAt = Now();
                // The reason goes in the invoice's log (for the store, not the customer), once per distinct reason.
                log = entry.LoggedError != ex.Message;
                entry.LoggedError = ex.Message;
            }
            if (log)
                await Log(entry.InvoiceId, $"Could not get a Monero deposit address from Pegasus Swap: {ex.Message}", InvoiceEventData.EventSeverity.Warning);
        }
    }

    async Task Recheck(Entry entry, Swap swap)
    {
        var payment = swap.Payment;
        var updated = payment with { CheckedAt = Now() };
        var changed = false;
        try
        {
            if (await Keys(entry.StoreId, entry.Config) is { } keys)
            {
                var tx = await client.Get(BaseUrl, keys, payment.OrderNumber, CancellationToken.None);
                updated = updated with { Status = tx.Status };
                changed = updated.Status != payment.Status;
                if (changed)
                    await Log(entry.InvoiceId, $"Pegasus Swap order {payment.OrderNumber}: {PegasusStatus.Name(updated.Status)}",
                        updated.Status is PegasusStatus.Error or PegasusStatus.Stuck or PegasusStatus.Pause or PegasusStatus.Refund or PegasusStatus.Overdue
                            ? InvoiceEventData.EventSeverity.Warning
                            : InvoiceEventData.EventSeverity.Info);
                changed |= await LogTx(entry.InvoiceId, swap, tx.DepositTxId, "Customer's XMR deposit transaction");
                changed |= await LogTx(entry.InvoiceId, swap, tx.PayoutTxId, "Pegasus Swap's BTC payout transaction");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check Pegasus Swap order {OrderNumber}", payment.OrderNumber);
        }
        lock (entry)
            swap.Payment = updated;
        changed |= await SyncPending(entry, swap);
        if (changed)
            await Save(entry);
    }

    /// <summary>
    /// Keeps the invoice's pending payment in step with the order: recorded once the exchange has the XMR (the invoice
    /// shows as Processing until the BTC arrives), retired if the order is refunded, goes overdue or never pays out.
    /// A failed write is tried again on every round of <see cref="FollowAll"/> until it is done. True when something was done.
    /// </summary>
    async Task<bool> SyncPending(Entry entry, Swap swap)
    {
        if (pendingPayments is null)
            return false;
        bool add, retire;
        PegasusPayment payment;
        lock (entry)
        {
            add = swap.NeedsAdd;
            retire = swap.NeedsRetire;
            payment = swap.Payment;
        }
        if (add && await pendingPayments.Add(entry.InvoiceId, entry.PaymentMethodId, payment.OrderNumber, payment.PendingBtc, payment.Created))
        {
            lock (entry)
                swap.PendingAdded = true;
            return true;
        }
        if (retire && await pendingPayments.Retire(entry.InvoiceId, entry.PaymentMethodId, payment.OrderNumber,
                swap.GaveUp
                    ? $"Pegasus Swap order {payment.OrderNumber} has not paid out within {FollowFor.TotalHours:0} hours"
                    : $"Pegasus Swap order {payment.OrderNumber} is {PegasusStatus.Name(payment.Status)}"))
        {
            lock (entry)
                swap.PendingRetired = true;
            return true;
        }
        return false;
    }

    /// <summary>Follows every open swap, whether or not anyone is looking at the checkout. Called by <see cref="PegasusTracker"/>.</summary>
    public async Task FollowAll(CancellationToken cancellationToken)
    {
        var now = Now();
        foreach (var entry in _entries.Values.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            Swap[] open, unsaved, unsynced;
            var saved = false;
            lock (entry)
            {
                if (entry.Running is { IsCompleted: false })
                    continue;
                open = entry.Swaps.Where(s => !s.Final && now - s.Payment.Created < FollowFor && now - s.Payment.CheckedAt > RecheckEvery).ToArray();
                // A day on, an order that never paid out stops holding the invoice in Processing.
                foreach (var s in entry.Swaps.Where(s => !s.GaveUp && now - s.Payment.Created >= FollowFor))
                {
                    s.GaveUp = true;
                    saved = true;
                }
                unsaved = entry.Swaps.Where(s => { lock (s.UnsavedTerms) return s.UnsavedTerms.Count > 0; }).ToArray();
                unsynced = entry.Swaps.Where(s => !open.Contains(s) && (s.NeedsAdd || s.NeedsRetire)).ToArray();
            }
            foreach (var swap in unsaved)
                saved |= await SearchTerms(entry.InvoiceId, swap);
            foreach (var swap in unsynced)
                saved |= await SyncPending(entry, swap);
            if (saved)
                await Save(entry);
            foreach (var swap in open)
                await Recheck(entry, swap);
        }
        Forget(now);
    }

    /// <summary>
    /// Picks up the swaps saved on invoices BTCPay still monitors (after a restart or an update).
    /// False when some could not be listed: <see cref="PegasusTracker"/> calls it again until it succeeds.
    /// </summary>
    public async Task<bool> Restore(StoreRepository storeRepository, CancellationToken cancellationToken)
    {
        var complete = true;
        foreach (var chain in SwapSourceChain.PaymentMethods.Where(c => c.Provider == SwapProvider.Pegasus))
        {
            InvoiceEntity[] invoices;
            try
            {
                invoices = await invoiceRepository.GetMonitoredInvoices(chain.PaymentMethodId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not list invoices to resume Pegasus Swap orders for {Chain}; trying again shortly", chain.Name);
                complete = false;
                continue;
            }
            foreach (var invoice in invoices)
            {
                try
                {
                    if (invoice.GetPaymentPrompt(chain.PaymentMethodId)?.Details is not { } details)
                        continue;
                    var saved = details.ToObject<SwapToBtcPromptDetails>(DetailsSerializer)?.PegasusSwaps;
                    if (saved is not { Count: > 0 })
                        continue;
                    var config = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(invoice.StoreId, Controllers.UISwapStoreController.StoreSettingName)
                                 ?? new SwapToBtcPaymentMethodConfig();
                    Restore(invoice.Id, invoice.StoreId, chain.PaymentMethodId, config, saved);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not resume the Pegasus Swap orders of invoice {InvoiceId}; trying again shortly", invoice.Id);
                    complete = false;
                }
            }
        }
        return complete;
    }

    /// <summary>Adds saved swaps to the invoice's entry (tests call it directly).</summary>
    public void Restore(string invoiceId, string storeId, PaymentMethodId pmi, SwapToBtcPaymentMethodConfig config, IEnumerable<PersistedPegasusSwap> saved)
    {
        var entry = _entries.GetOrAdd(invoiceId, _ => new Entry { InvoiceId = invoiceId, StoreId = storeId, PaymentMethodId = pmi });
        lock (entry)
        {
            entry.Config = config;
            entry.Touched = Now();
            foreach (var s in saved)
            {
                if (entry.Swaps.Any(e => e.Payment.OrderNumber == s.OrderNumber))
                    continue;
                var swap = new Swap
                {
                    Payment = new PegasusPayment(s.OrderNumber, s.Address, s.Memo, decimal.Parse(s.Amount, CultureInfo.InvariantCulture), s.TargetSats,
                        s.Status, s.Created, DateTimeOffset.MinValue,
                        decimal.TryParse(s.ReceiveBtc, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : null)
                };
                swap.PendingAdded = s.PendingAdded;
                swap.PendingRetired = s.PendingRetired;
                swap.GaveUp = s.GaveUp;
                foreach (var tx in s.LoggedTxs)
                    swap.LoggedTxs.Add(tx);
                swap.UnsavedTerms.UnionWith(s.UnsavedTerms);
                entry.Swaps.Add(swap);
            }
        }
    }

    /// <summary>Writes the invoice's swaps into its prompt details.</summary>
    async Task Save(Entry entry)
    {
        var gate = PromptDetailsGate.For(entry.InvoiceId);
        await gate.WaitAsync();
        try
        {
            var invoice = await invoiceRepository.GetInvoice(entry.InvoiceId);
            if (invoice?.GetPaymentPrompt(entry.PaymentMethodId) is not { Details: { } details })
                return;
            var parsed = details.ToObject<SwapToBtcPromptDetails>(DetailsSerializer) ?? new SwapToBtcPromptDetails();
            lock (entry)
                parsed.PegasusSwaps = entry.Swaps.Select(s => new PersistedPegasusSwap
                {
                    OrderNumber = s.Payment.OrderNumber,
                    Address = s.Payment.Address,
                    Memo = s.Payment.Memo,
                    Amount = s.Payment.Amount.ToString(CultureInfo.InvariantCulture),
                    TargetSats = s.Payment.TargetSats,
                    ReceiveBtc = s.Payment.ReceiveBtc?.ToString(CultureInfo.InvariantCulture),
                    Status = s.Payment.Status,
                    Created = s.Payment.Created,
                    LoggedTxs = s.LoggedTxs.ToArray(),
                    UnsavedTerms = UnsavedOf(s),
                    PendingAdded = s.PendingAdded,
                    PendingRetired = s.PendingRetired,
                    GaveUp = s.GaveUp
                }).ToList();
            await invoiceRepository.UpdatePaymentDetails(entry.InvoiceId, entry.PaymentMethodId, Newtonsoft.Json.Linq.JObject.FromObject(parsed, DetailsSerializer));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save the Pegasus Swap orders of invoice {InvoiceId}", entry.InvoiceId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>True when the transaction was new.</summary>
    async Task<bool> LogTx(string invoiceId, Swap swap, string? txId, string what)
    {
        if (string.IsNullOrWhiteSpace(txId))
            return false;
        lock (swap.LoggedTxs)
        {
            if (!swap.LoggedTxs.Add(txId))
                return false;
        }
        await Log(invoiceId, $"{what}: {txId}");
        await SearchTerms(invoiceId, swap, txId);
        return true;
    }

    /// <summary>
    /// Makes the order findable from the invoice search. A failed write keeps the terms on the swap (saved with the invoice),
    /// and <see cref="FollowAll"/> tries them again, so the order number and transaction ids always end up searchable.
    /// </summary>
    async Task<bool> SearchTerms(string invoiceId, Swap swap, params string?[] terms)
    {
        var list = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        lock (swap.UnsavedTerms)
            list.AddRange(swap.UnsavedTerms.Where(t => !list.Contains(t, StringComparer.OrdinalIgnoreCase)));
        if (list.Count == 0)
            return true;
        try
        {
            await invoiceRepository.AddSearchTerms(invoiceId, list);
            lock (swap.UnsavedTerms)
                swap.UnsavedTerms.ExceptWith(list);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not add search terms to invoice {InvoiceId}; trying again later", invoiceId);
            lock (swap.UnsavedTerms)
                swap.UnsavedTerms.UnionWith(list);
            return false;
        }
    }

    static string[] UnsavedOf(Swap swap)
    {
        lock (swap.UnsavedTerms)
            return swap.UnsavedTerms.ToArray();
    }

    async Task Log(string invoiceId, string message, InvoiceEventData.EventSeverity severity = InvoiceEventData.EventSeverity.Info)
    {
        try
        {
            var logs = new InvoiceLogs();
            logs.Write(message, severity);
            await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not log to invoice {InvoiceId}", invoiceId);
        }
    }

    void Forget(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries.ToArray())
        {
            bool done;
            lock (entry)
                done = now - entry.Touched > ForgetAfter && entry.Swaps.All(s => s.Done && UnsavedOf(s).Length == 0);
            if (done)
                _entries.TryRemove(key, out _);
        }
    }
}
