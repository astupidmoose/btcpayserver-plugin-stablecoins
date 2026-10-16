# Cross-Chain Payments for BTCPay Server

A BTCPay Server plugin that lets customers pay a normal BTC invoice from other chains.

Each source chain is its own BTCPay payment method, so any checkout that lists BTCPay's payment methods (BTCPay's own, or a store integration) can offer them as separate options ("Ethereum", "Base"...). Whatever the customer pays with, the invoice is priced in BTC and native BTC lands at a fresh address from the store's own BTC wallet. The plugin never holds anyone's coins or keys.

| Route | Chains | How the customer pays |
|---|---|---|
| **LI.FI** (tokens from a wallet) | Ethereum, Base, Arbitrum, Optimism, Polygon, BNB Chain, Avalanche, Solana, Tron | Connects their wallet in the [LI.FI](https://li.fi) panel, picks any token, signs once. A bridge delivers BTC. |
| **NEAR Intents** (USDC, USDT from an exchange) | On each token checkout's own chain, where NEAR Intents takes the coin: USDC on Ethereum, Base, Arbitrum, Polygon, Solana, BNB Smart Chain, Avalanche, Optimism; USDT on Ethereum, Tron, BNB Smart Chain, Solana, Polygon, Arbitrum, Avalanche | Not a payment method of its own: "Send USDC from an exchange" buttons under the LI.FI panel. The customer sends an exact amount on that chain to a one-time deposit address from the [1Click API](https://github.com/defuse-protocol/one-click-sdk-typescript) (an exact-output quote for the BTC due, to the invoice's address). NEAR Intents holds the deposit until its solvers swap it; anything it cannot swap, and any excess, goes to the store's refund address for that network. Needs a 1Click API key. |

## What it adds

| Piece | Where |
|---|---|
| Chain catalog: payment method ids (`LIFI_ETHEREUM`...), LI.FI chain ids, the exchange options | `Swap/SwapSourceChain.cs` |
| Payment method: priced in BTC, reserves an address from the store's BTC wallet | `Swap/SwapToBtcPaymentMethodHandler.cs` |
| Listener: credits BTC delivered to those addresses, tracks confirmations | `Swap/SwapToBtcListener.cs` |
| Checkout: BTCPay's own checkout with the LI.FI panel, or the exchange deposit address | `Swap/SwapToBtcCheckout.cs`, `Views/Stablecoins/SwapCheckout.cshtml` |
| NEAR Intents deposits: quote, follow the deposit | `Intents/` |
| Checkout endpoints: exchange option, customer transaction reporting | `Controllers/UISwapCheckoutController.cs` |
| Wallet payment panel (LI.FI SDK), one bundle per wallet family (EVM, Solana, Tron) | `client/` → `Resources/js/swappanel-*.js` |
| Store settings page (Wallets → Other Chains), with an on/off switch for the whole feature | `Controllers/UISwapStoreController.cs`, `Views/Stablecoins/SwapSettings.cshtml` |
| Swap payments on the invoice page | `Views/Stablecoins/ViewSwapToBtcPaymentData.cshtml` |

Payment method ids use underscores, not dashes: some store integrations send the id back with every `-` turned into `_`, and BTCPay only maps that back for its built-in coins.

## How a payment works

1. Picking a chain creates a BTCPay invoice with only that method. The handler prices it in BTC and reserves the next address from the store's BTC on-chain wallet, exactly like a plain Bitcoin invoice, and labels the address with the invoice in the wallet.
2. The checkout is BTCPay's own (store header, amount in the invoice's currency, timer, View Details).
   - **Wallet:** the customer connects a wallet, picks one of the tokens they hold on that chain, and sees one locked amount ("Pay 0.004124 ETH"): an exact-output LI.FI quote for the BTC due, including every swap and bridge fee. BTC and the swap route are never shown.
   - **From an exchange:** the checkout looks like a normal coin invoice: amount in USDC or USDT, QR code, address. NEAR Intents' temporary minimums are respected: below a minimum it recently refused, the buttons are hidden for that chain.
3. BTC arrives at the invoice address. BTCPay's NBXplorer connection already watches the store's wallet; the listener records the payment under the chain's payment method. BTCPay's own Bitcoin listener only credits `BTC-CHAIN` addresses, so nothing is counted twice. On every block the listener also rescans the wallet's coins, which catches anything that arrived while BTCPay was down.
4. Confirmations follow the store's speed policy, as for on-chain BTC. Partial payments, overpayments, expiry and paid-late work as BTCPay normally does, and the checkout re-quotes whatever is still due.

## When something goes wrong

- **Too little BTC arrives:** the invoice is partially paid and the checkout quotes the rest. If it expires that way, refund the partial amount with BTCPay's Refund button.
- **A LI.FI route fails:** the bridge returns the funds to the customer's wallet on the source chain.
- **An exchange deposit NEAR Intents cannot swap** (too little, too late): it goes to the store's refund address on that network; refund the customer from there.
- **Late BTC:** recorded as paid late, like late on-chain BTC.

For support, the invoice log records:

- **Wallet payments:** the customer's wallet address, token and amount as soon as they press pay (before signing), then each transaction they sign, linked to scan.li.fi.
- **Exchange payments:** each NEAR Intents quote (deposit address, amount, deadline, refund address, quote id), its status changes, why a quote failed, the customer's deposit transaction and NEAR Intents' BTC payout transaction.

The wallet address, transaction hashes, deposit address and quote id are also added to the invoice's search terms, so pasting whatever the customer sends you (their wallet, a transaction hash, the address they paid) into the Invoices search finds the invoice.

## Settings (per store)

- An Enable switch for the whole feature. Off removes every chain from every checkout but keeps your choices.
- Which chains are enabled, and USDC/USDT from an exchange.
- LI.FI: integrator name (from [portal.li.fi](https://portal.li.fi)), optional API key, integrator fee (default 0).
- WalletConnect project ID (optional): lets customers connect a mobile EVM wallet by scanning a QR code; browser wallets work without it. To get one, sign up free at [Reown](https://cloud.reown.com) (formerly WalletConnect Cloud), create a project, copy its Project ID into this field, and add your BTCPay domain (for example `btcpay.example.com`) to the project's allowed domains.
- NEAR Intents: 1Click API key, refund addresses (EVM, Solana, Tron), price tolerance (default 50 bps).
- Amount buffer: extra BTC asked from the customer (default 0). Leave it at 0 and set the store's payment tolerance to about 1% to absorb small shortfalls yourself.

Also set a longer invoice expiry (for example 60 minutes): swaps to BTC usually take several minutes.

## Build, test, package

Requires the .NET 10 SDK and the BTCPay submodule (pinned to v2.4.5):

```bash
git submodule update --init --recursive
dotnet build src/BTCPayServer.Plugins.Stablecoins/BTCPayServer.Plugins.Stablecoins.csproj
dotnet test --project tests/BTCPayServer.Plugins.Stablecoins.Tests/BTCPayServer.Plugins.Stablecoins.Tests.csproj
```

The panel bundles are committed. To rebuild them: `cd client && npm ci && npm run build`.

To make an installable package the same way the [Plugin Builder](https://plugin-builder.btcpayserver.org/) does:

```bash
dotnet publish src/BTCPayServer.Plugins.Stablecoins/BTCPayServer.Plugins.Stablecoins.csproj -c Release -o out/publish
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- out/publish BTCPayServer.Plugins.Stablecoins out/pack
```

Upload the resulting `.btcpay` file under Server Settings → Plugins → Upload. On the Plugin Builder, point a build at this repository with plugin directory `src/BTCPayServer.Plugins.Stablecoins`.

## Known limits

- **Not yet run end to end on mainnet.** LI.FI and NEAR Intents are faked in the local tests.
- **NEAR Intents minimums** change over time ($1,000 on BNB Smart Chain and $100 on Tron on 2026-10-16), which rules out small orders on those chains while they last.
- **Refunds** use BTCPay's own refund flow (BTC). Address screening is not built.

## Roadmap

- Publish on the Plugin Builder.
