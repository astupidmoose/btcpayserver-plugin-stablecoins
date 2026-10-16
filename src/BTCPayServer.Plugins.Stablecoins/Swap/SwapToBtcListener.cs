#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Detects the BTC that LI.FI's bridges deliver to swap invoices' addresses.
/// The addresses come from the store's own BTC wallet, so BTCPay's NBXplorer connection already
/// sees every transaction to them: this listens to the events BTCPay publishes for them, and
/// on every block also rescans the wallet's coins and refreshes confirmations, which catches
/// anything delivered while BTCPay was down.
/// </summary>
public class SwapToBtcListener(
    EventAggregator eventAggregator,
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    PaymentMethodHandlerDictionary handlers,
    BTCPayNetworkProvider networkProvider,
    BTCPayWalletProvider walletProvider,
    ILogger<SwapToBtcListener> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    BTCPayNetwork Network => networkProvider.BTC;
    PaymentMethodId BtcChain => PaymentTypes.CHAIN.GetPaymentMethodId(Network.CryptoCode);

    protected override void SubscribeToEvents()
    {
        Subscribe<NewOnChainTransactionEvent>();
        Subscribe<Events.NewBlockEvent>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        try
        {
            switch (evt)
            {
                case NewOnChainTransactionEvent { NewTransactionEvent: { DerivationStrategy: not null } tx } e when e.PaymentMethodId == BtcChain:
                    await OnTransaction(tx);
                    break;
                case Events.NewBlockEvent e when e.PaymentMethodId == BtcChain:
                    await Sweep(cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Swap-to-BTC listener failed on {Event}", evt);
        }
    }

    IEnumerable<SwapToBtcPaymentMethodHandler> SwapHandlers =>
        SwapSourceChain.PaymentMethods
            .Select(c => handlers.TryGetValue(c.PaymentMethodId, out var h) ? h as SwapToBtcPaymentMethodHandler : null)
            .OfType<SwapToBtcPaymentMethodHandler>();

    async Task OnTransaction(NewTransactionEvent tx)
    {
        var wallet = walletProvider.GetWallet(Network);
        foreach (var (output, outPoint) in Network.GetValidOutputs(tx))
        {
            if (!output.Value.IsCompatible(Network))
                continue;
            var key = Network.GetTrackedDestination(output.ScriptPubKey);
            foreach (var handler in SwapHandlers)
            {
                var invoice = await invoiceRepository.GetInvoiceFromAddress(handler.PaymentMethodId, key);
                if (invoice is null)
                    continue;
                await Record(wallet, invoice, handler, outPoint, output.Value.GetValue(Network),
                    tx.TransactionData.Transaction?.RBF ?? true, output.KeyPath?.ToString());
            }
        }
    }

    async Task Record(BTCPayWallet wallet, InvoiceEntity invoice, SwapToBtcPaymentMethodHandler handler,
        OutPoint outPoint, decimal amount, bool rbf, string? keyPath)
    {
        var pmi = handler.PaymentMethodId;
        var id = outPoint.ToString();
        if (invoice.GetPayments(false).Any(p => p.Id == id && p.PaymentMethodId == pmi))
        {
            await RefreshConfirmations(wallet, invoice, handler);
            return;
        }
        var tx = await wallet.GetTransactionAsync(outPoint.Hash);
        var data = new SwapToBtcPaymentData
        {
            Outpoint = id,
            RBF = rbf,
            KeyPath = keyPath,
            ConfirmationCount = Math.Max(0, tx?.Confirmations ?? 0)
        };
        var paymentData = new PaymentData
        {
            Id = id,
            Created = DateTimeOffset.UtcNow,
            Status = GetStatus(invoice.SpeedPolicy, data),
            Amount = amount,
            Currency = Network.CryptoCode
        }.Set(invoice, handler, data);

        var payment = await paymentService.AddPayment(paymentData, [outPoint.Hash.ToString()]);
        if (payment is null)
            return;
        logger.LogInformation("Invoice {InvoiceId} received {Amount} BTC from a {Chain} swap in {Outpoint}",
            invoice.Id, amount, handler.Chain.Name, id);
        wallet.InvalidateCache(handler.Network.NBXplorerNetwork.DerivationStrategyFactory.Parse(
            handler.ParsePaymentPromptDetails(invoice.GetPaymentPrompt(pmi)!.Details).AccountDerivation!));
        invoice = await invoiceRepository.GetInvoice(invoice.Id);
        EventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }

    /// <summary>
    /// Runs on every BTC block: records coins that arrived while no event was seen, and moves
    /// payments from Processing to Settled as they reach the store's confirmation policy.
    /// </summary>
    public async Task Sweep(CancellationToken cancellationToken)
    {
        var wallet = walletProvider.GetWallet(Network);
        var coinsByStrategy = new Dictionary<string, ReceivedCoin[]>();
        foreach (var handler in SwapHandlers)
        {
            var pmi = handler.PaymentMethodId;
            foreach (var invoice in await invoiceRepository.GetMonitoredInvoices(pmi, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var prompt = invoice.GetPaymentPrompt(pmi);
                    if (prompt is not { Activated: true, Details: not null })
                        continue;
                    var details = handler.ParsePaymentPromptDetails(prompt.Details);
                    if (details.AccountDerivation is { } derivation)
                    {
                        if (!coinsByStrategy.TryGetValue(derivation, out var coins))
                        {
                            var strategy = Network.NBXplorerNetwork.DerivationStrategyFactory.Parse(derivation);
                            coins = await wallet.GetUnspentCoins(strategy, cancellation: cancellationToken);
                            coinsByStrategy.Add(derivation, coins);
                        }
                        var known = invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi).Select(p => p.Id).ToHashSet();
                        foreach (var coin in coins)
                        {
                            if (known.Contains(coin.OutPoint.ToString()) || !coin.Value.IsCompatible(Network) ||
                                !invoice.Addresses.Contains((pmi, Network.GetTrackedDestination(coin.ScriptPubKey))))
                                continue;
                            await Record(wallet, invoice, handler, coin.OutPoint, coin.Value.GetValue(Network), true, coin.KeyPath?.ToString());
                        }
                    }
                    await RefreshConfirmations(wallet, await invoiceRepository.GetInvoice(invoice.Id) ?? invoice, handler);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Swap-to-BTC sweep failed for invoice {InvoiceId}", invoice.Id);
                }
            }
        }
    }

    async Task RefreshConfirmations(BTCPayWallet wallet, InvoiceEntity invoice, SwapToBtcPaymentMethodHandler handler)
    {
        var payments = invoice.GetPayments(false)
            .Where(p => p.PaymentMethodId == handler.PaymentMethodId && p.Status == PaymentStatus.Processing)
            .ToList();
        if (payments.Count == 0)
            return;
        var hashes = payments.Select(p => OutPoint.Parse(handler.ParsePaymentDetails(p.Details).Outpoint).Hash).Distinct().ToArray();
        var transactions = await wallet.GetTransactions(hashes, true);
        var changed = new List<PaymentEntity>();
        foreach (var payment in payments)
        {
            var data = handler.ParsePaymentDetails(payment.Details);
            if (!transactions.TryGetValue(OutPoint.Parse(data.Outpoint).Hash, out var tx))
                continue; // Unknown to NBXplorer right now; keep the current state.
            var confirmations = Math.Min(Math.Max(0, tx.Confirmations), Network.MaxTrackedConfirmation);
            var status = GetStatus(invoice.SpeedPolicy, new SwapToBtcPaymentData { ConfirmationCount = confirmations, RBF = data.RBF });
            if (confirmations == data.ConfirmationCount && status == payment.Status)
                continue;
            data.ConfirmationCount = confirmations;
            payment.Status = status;
            payment.Details = JToken.FromObject(data, handler.Serializer);
            changed.Add(payment);
        }
        if (changed.Count == 0)
            return;
        await paymentService.UpdatePayments(changed);
        EventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }

    /// <summary>Same confirmation rules as BTCPay's own on-chain payments.</summary>
    public static int ConfirmationRequired(SpeedPolicy speedPolicy, bool rbf) => speedPolicy switch
    {
        SpeedPolicy.HighSpeed => rbf ? 1 : 0,
        SpeedPolicy.MediumSpeed => 1,
        SpeedPolicy.LowMediumSpeed => 2,
        _ => 6
    };

    public static PaymentStatus GetStatus(SpeedPolicy speedPolicy, SwapToBtcPaymentData data) =>
        data.ConfirmationCount >= ConfirmationRequired(speedPolicy, data.RBF) ? PaymentStatus.Settled : PaymentStatus.Processing;
}
