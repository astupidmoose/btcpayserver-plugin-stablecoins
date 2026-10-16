# Stablecoins for BTCPay Server

A BTCPay Server plugin that adds stablecoins as native payment methods, paid straight into a wallet the merchant controls. It starts with USDC.

It has two parts:

- **Pay from other chains (LI.FI → BTC).** One payment method per chain (Ethereum, Base, Arbitrum, Optimism, Polygon, BNB Chain, Avalanche, Solana). The invoice is a normal BTC invoice paying a fresh address from the store's own BTC wallet; the customer pays with any token on the chosen chain through the [LI.FI](https://li.fi) widget, and a bridge delivers native BTC to that address. See [Pay from other chains](#pay-from-other-chains-lifi--btc).
- **Direct USDC on Solana (Phase 1).** Customers pay USDC from any Solana Pay wallet (Phantom, Solflare, Backpack and others), BTCPay matches the payment to the invoice through a Solana Pay reference key, and the rest (invoice states, webhooks, the WooCommerce connector) is BTCPay's own.

## What it adds

| Piece | Where |
|---|---|
| `USDC (Solana)` payment method (`USDC-SOLANA`), 6 decimals | `Payments/SolanaUsdcPaymentMethodHandler.cs` |
| Solana Pay transfer request in the checkout QR and wallet button | `Payments/SolanaUsdcCheckout.cs`, `Solana/SolanaPay.cs` |
| Chain listener: finds, verifies and confirms payments | `Services/SolanaUsdcListener.cs`, `Solana/UsdcTransferParser.cs` |
| Minimal Solana JSON-RPC client | `Solana/SolanaRpcClient.cs` |
| Store settings page (Wallets → USDC (Solana)) | `Controllers/UIUsdcStoreController.cs`, `Views/Stablecoins/` |
| USDC payments table on the invoice page, linked to Solana Explorer | `Views/Stablecoins/ViewSolanaUsdcPaymentData.cshtml` |
| USDC currency data and a default rate rule | `StablecoinsPlugin.cs` |

## How a payment works

1. When an invoice is created (or the customer picks USDC, with lazy activation), the handler sets the amount due in USDC from the store's rates, and creates a fresh random 32-byte **reference key** for this prompt.
2. Checkout shows `solana:<merchant wallet>?amount=<due>&spl-token=<USDC mint>&reference=<key>&label=<store>&message=Invoice <id>` as the QR and "pay in wallet" link. The wallet adds the reference as a read-only account on the transfer.
3. The listener polls every 10 seconds. For each invoice BTCPay is still monitoring, it calls `getSignaturesForAddress(reference)` and reads each new transaction with `getTransaction` (jsonParsed).
4. A transaction counts only if it succeeded, contains the reference, and **increased the USDC balance owned by the merchant wallet**. The amount is the token balance delta, not what the instructions claim, so it works for plain transfers, `transferChecked`, and transfers routed through other programs (the Phase 2 swap). Anything else is logged and ignored.
5. The payment is recorded with the transaction signature as its id. BTCPay keys payments by (id, payment method), so one transaction can never be credited to two invoices, even if it carries both references.
6. Payment status follows Solana commitment: `confirmed` is Processing, `finalized` (about 13 seconds later) is Settled. A store on the **high speed** policy settles at `confirmed`.

BTCPay then computes the invoice state from the payments exactly as it does for Bitcoin: paid, partially paid (the QR updates to the remaining amount, same reference), overpaid, expired, and paid late. The WooCommerce connector sees the usual BTCPay webhooks.

## Pay from other chains (LI.FI → BTC)

| Piece | Where |
|---|---|
| Chain catalog: payment method ids `LIFI-ETHEREUM`, `LIFI-BASE`, ... and LI.FI chain ids | `Swap/SwapSourceChain.cs` |
| Payment method: priced in BTC, reserves an address from the store's BTC wallet | `Swap/SwapToBtcPaymentMethodHandler.cs` |
| Listener: credits BTC delivered to those addresses, tracks confirmations | `Swap/SwapToBtcListener.cs` |
| Checkout: BTCPay's own checkout with the LI.FI panel in place of the QR | `Swap/SwapToBtcCheckout.cs`, `Views/Stablecoins/SwapCheckout.cshtml` |
| LI.FI widget bundle (React, EVM and Solana wallets) | `client/` → `Resources/js/swappanel.js` |
| Store settings page (Wallets → Pay from other chains) | `Controllers/UISwapStoreController.cs`, `Views/Stablecoins/SwapSettings.cshtml` |

How a payment works:

1. WooCommerce (BTCPay plugin with "Separate payment gateways") lists each enabled chain as its own gateway, because it lists one gateway per store payment method. Rename them in WooCommerce, for example `LIFI_ETHEREUM` to "Ethereum".
2. Picking one creates a BTCPay invoice with only that method. The handler prices it in BTC and reserves the next address from the store's BTC on-chain wallet, like a plain Bitcoin invoice, and labels the address with the invoice in the wallet.
3. The checkout is BTCPay's own (store header, amount in BTC, timer, View Details). In place of the QR, the LI.FI widget is locked to the chosen source chain, with BTC to the invoice address as the fixed destination and the amount due (plus a small buffer) as an exact-output quote. The customer picks a token, connects their wallet and signs.
4. LI.FI routes through a bridge that pays native BTC (THORChain, Chainflip, Garden...). BTCPay's NBXplorer connection already watches the store's wallet; the listener picks up the transaction to the invoice's address and records it under the chain's payment method. BTCPay's own Bitcoin listener only credits `BTC-CHAIN` addresses, so the two never double count.
5. Confirmations follow the store's speed policy exactly as for on-chain BTC. On every block the listener also rescans the wallet's coins, which catches anything that arrived while BTCPay was down.

Settings: LI.FI integrator name (register it at [portal.li.fi](https://portal.li.fi) to collect fees), optional API key, optional integrator fee, the amount buffer (default 0.5%), and an optional WalletConnect project id for mobile EVM wallets.

Things to set on the store: bridging to BTC usually takes several minutes and can take longer, so give invoices a longer expiry (for example 60 minutes) and consider a small payment tolerance.

To rebuild the widget bundle: `cd client && npm ci && npm run build`.

## Design choices, and why

- **Payment method shape.** Follows the BTCPay 2.x pattern used by the Monero plugin (handler, checkout model extension, payment link extension, hosted listener, `PaymentService.AddPayment`) but without registering a `BTCPayNetwork`, like the Stripe plugin. USDC is a token on someone else's chain, so it has no node, no wallet and no derivation scheme in BTCPay. This also leaves room for `USDC-BASE` in Phase 3 without a currency-code clash.
- **Matching by Solana Pay reference**, as the project plan chose. The merchant address is shared by every invoice, so only the reference is registered in BTCPay's address-to-invoice table (`TrackedDestinations`); registering the address would make BTCPay disable the method for every invoice after the first.
- **Non-custodial.** The store config holds only a public address. The plugin never signs anything.
- **No database tables.** Everything lives in BTCPay's own invoice and payment blobs, so there are no migrations to maintain.
- **Rates.** USDC is not in BTCPay's currency table, so the plugin adds it and a default rule, `USDC_X = USDC_BTC * BTC_X` with `USDC_BTC = kraken(USDC_USD) / kraken(BTC_USD)`. A store can override it on its Rates page (for example `USDC_CAD = kraken(USDC_USD) * <your USD_CAD source>`).
- **Fees.** The payment method fee is zero; the customer's wallet pays the Solana network fee (a fraction of a cent).

## Configuration

Server-wide, as environment variables (BTCPay strips the `BTCPAY_` prefix):

| Variable | Default |
|---|---|
| `BTCPAY_SOLANA_CLUSTER` | `mainnet-beta` when BTCPay runs on mainnet, `devnet` otherwise |
| `BTCPAY_SOLANA_RPC_URI` | `https://api.mainnet-beta.solana.com` or `https://api.devnet.solana.com` |
| `BTCPAY_SOLANA_POLL_SECONDS` | `10` |

The public RPC endpoints are rate-limited and not meant for production. Use a provider endpoint (Helius, QuickNode, Triton or similar) for `BTCPAY_SOLANA_RPC_URI`. Each poll costs one call per open USDC invoice, plus one call per new transaction.

Per store: **Wallets → USDC (Solana)**, enter the Solana wallet address and enable it. The wallet must already hold a USDC token account (receive any amount of USDC once); Solana Pay wallets will not create it for you, and the settings page warns if it is missing.

USDC mints: mainnet `EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v`, devnet `4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU` (devnet USDC from https://faucet.circle.com).

## Build, test, package

Requires the .NET 10 SDK and the BTCPay submodule (pinned to v2.4.5):

```bash
git submodule update --init --recursive
dotnet build src/BTCPayServer.Plugins.Stablecoins/BTCPayServer.Plugins.Stablecoins.csproj
dotnet test --project tests/BTCPayServer.Plugins.Stablecoins.Tests/BTCPayServer.Plugins.Stablecoins.Tests.csproj
```

To run it inside a local BTCPay dev instance, run `./plugin-register.sh` and start BTCPay from `submodules/btcpayserver` (see the [plugin template](https://github.com/btcpayserver/btcpayserver-plugin-template)).

To make an installable package the same way the [Plugin Builder](https://plugin-builder.btcpayserver.org/) does:

```bash
dotnet publish src/BTCPayServer.Plugins.Stablecoins/BTCPayServer.Plugins.Stablecoins.csproj -c Release -o out/publish
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- out/publish BTCPayServer.Plugins.Stablecoins out/pack
```

Upload the resulting `.btcpay` file under Server Settings → Plugins → Upload. On the Plugin Builder, point a build at this repository with plugin directory `src/BTCPayServer.Plugins.Stablecoins` (it requires exactly one `.csproj` there).

## Phase 1 test plan (BTCPay test store)

1. Run BTCPay on testnet or regtest (or set `BTCPAY_SOLANA_CLUSTER=devnet`), install the plugin, and configure a devnet wallet that holds devnet USDC.
2. Create invoices from a WooCommerce test store through the existing BTCPay connector and pay with Phantom or Solflare on devnet. Check each case:
   - exact payment: Processing, then Settled after finalization; WooCommerce order goes to processing
   - two partial payments to the same invoice: partially paid, then paid
   - overpayment: paid, flagged overpaid
   - payment after expiry: paid late
   - high speed store policy: settles at `confirmed`
   - restart BTCPay mid-payment: the payment is still picked up (state lives on chain and in BTCPay)
3. Check the invoice page shows the signature, payer and commitment, with a working Explorer link.

## Known limits and open questions

- **Copy-paste payments are not matched.** A customer who copies the address and sends USDC by hand (no reference) pays the merchant, but the invoice will not see it. The checkout leads with the QR and wallet button, which carry the reference. Options: hide the raw address on checkout, or add a fallback that matches unreferenced transfers by exact amount. Not done yet.
- **Not yet run against a live BTCPay or Solana node.** The build environment used so far had no access to Solana RPC or a BTCPay test instance; the matching logic is unit-tested against recorded transaction shapes.
- **Polling, not websockets.** Simple and restart-safe; fine at small volume. `logsSubscribe`/`accountSubscribe` could cut latency later.
- **USDC rate.** The default treats USDC through BTC prices on Kraken. If the store wants USDC pegged at exactly 1 USD, set `USDC_USD = 1` in the store's rate rules.
- **Refunds** are Phase 4 (BTCPay pull payments). The payer address is already recorded on each payment for that.
- **Compliance screening** of the payer is Phase 4.
- **Pay from other chains is not yet run live.** Built and unit-tested; LI.FI and Solana hosts were not reachable from the build environment. First live test: a small Base or Arbitrum payment on the test store.
- **A swap that finishes after the invoice expired** is recorded and shows as paid late, like late BTC. Longer expiry avoids this.
- **The customer can edit the BTC amount in the LI.FI panel.** Anything short leaves the invoice partially paid, and the panel re-quotes the rest.
- **Chain icons** are simple placeholders.
- **Zcash and Monero** are not reachable through LI.FI. THORChain routes ZEC directly (and XMR may follow); a direct THORChain route would reuse the payment methods, checkout and listener and only replace the widget.

## Roadmap

1. **Phase 1 (this):** direct USDC on Solana.
2. Phase 2: "pay with another token" swap panel on Solana via Jupiter, settling to the same reference-tagged transfer, so the listener should need no change.
3. Phase 3: pay BTC invoices from other chains via LI.FI (this branch). Later: settle into USDC instead of BTC, and direct THORChain routes for ZEC/XMR.
4. Phase 4: address screening, refunds through pull payments, monitoring.
5. Phase 5: publish on the Plugin Builder.
