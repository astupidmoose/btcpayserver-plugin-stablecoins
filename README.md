# Stablecoins for BTCPay Server

A BTCPay Server plugin that lets customers pay a normal BTC invoice from other chains.

Each source chain is its own BTCPay payment method, so the WooCommerce BTCPay plugin ("Separate payment gateways") lists them as separate options ("Ethereum", "Base", "Litecoin"...). Whatever the customer pays with, the invoice is priced in BTC and native BTC lands at a fresh address from the store's own BTC wallet. The plugin never holds anyone's coins or keys.

| Route | Chains | How the customer pays |
|---|---|---|
| **LI.FI** (tokens) | Ethereum, Base, Arbitrum, Optimism, Polygon, BNB Chain, Avalanche, Solana, Tron | Connects their wallet in the [LI.FI](https://li.fi) panel, picks any token, signs once. A bridge delivers BTC. |
| **THORChain** (native coins) | Litecoin, Dogecoin, Zcash (transparent), Cosmos Hub (ATOM), XRP | Sends an exact amount of the coin to a [THORChain](https://thorchain.org) vault, with no memo. THORChain's memoless deposits recognise the payment by its amount and swap it to BTC for the invoice. |

Bitcoin Cash is deliberately not offered. Monero is not offered by THORChain's memoless API yet.

## What it adds

| Piece | Where |
|---|---|
| Chain catalog: payment method ids (`LIFI_ETHEREUM`, `THOR_LITECOIN`...), LI.FI chain ids, THORChain assets | `Swap/SwapSourceChain.cs` |
| Payment method: priced in BTC, reserves an address from the store's BTC wallet | `Swap/SwapToBtcPaymentMethodHandler.cs` |
| Listener: credits BTC delivered to those addresses, tracks confirmations, reads THORChain's `OUT:` memo | `Swap/SwapToBtcListener.cs` |
| Checkout: BTCPay's own checkout with the LI.FI panel or the THORChain deposit in place of the QR | `Swap/SwapToBtcCheckout.cs`, `Views/Stablecoins/SwapCheckout.cshtml` |
| THORNode client and the solver that finds the input covering the BTC due | `Thorchain/` |
| Checkout endpoints: THORChain deposit quote, customer transaction reporting | `Controllers/UISwapCheckoutController.cs` |
| Memoless deposits: quote, register with THORChain's memoless API, read the vault address | `Thorchain/ThorDepositService.cs`, `Thorchain/MemolessClient.cs` |
| Wallet payment panel (LI.FI SDK), one bundle per wallet family (EVM, Solana, Tron) | `client/` → `Resources/js/swappanel-*.js` |
| Store settings page (Wallets → Pay from other chains) | `Controllers/UISwapStoreController.cs`, `Views/Stablecoins/SwapSettings.cshtml` |
| Swap payments on the invoice page, with explorer links | `Views/Stablecoins/ViewSwapToBtcPaymentData.cshtml` |

Payment method ids use underscores, not dashes: the WooCommerce plugin sends the id back with every `-` turned into `_`, and BTCPay only maps that back for its built-in coins.

## How a payment works

1. Picking a chain creates a BTCPay invoice with only that method. The handler prices it in BTC and reserves the next address from the store's BTC on-chain wallet, exactly like a plain Bitcoin invoice, and labels the address with the invoice in the wallet.
2. The checkout is BTCPay's own (store header, amount in BTC, timer, View Details).
   - **LI.FI:** the header shows the amount in the invoice's currency. The customer connects a wallet, picks one of the tokens they hold on that chain, and sees one locked amount ("Pay 0.004124 ETH"): an exact-output LI.FI quote for the BTC due, including every swap and bridge fee. BTC and the swap route are never shown.
   - **THORChain:** the checkout is BTCPay's own on-chain checkout, exactly like a Litecoin invoice: amount in the coin, QR code, address, Pay in wallet, and View Details with the coin's effective exchange rate. In the background the plugin quotes THORChain from the BTC still due (THORChain only quotes from an input, so it starts from pool prices and scales until the expected BTC covers it), registers the swap memo with THORChain's hosted memoless API, and shows an amount ending in the registration's reference number. Every couple of minutes it re-reads the vault address (vaults rotate), notices when the deposit was made, and re-registers if prices moved against the amount. When every payment method on an invoice is one of these, other plugins' extra checkout tabs (such as Trocador's) are hidden.
3. BTC arrives at the invoice address. BTCPay's NBXplorer connection already watches the store's wallet; the listener records the payment under the chain's payment method. BTCPay's own Bitcoin listener only credits `BTC-CHAIN` addresses, so nothing is counted twice. On every block the listener also rescans the wallet's coins, which catches anything that arrived while BTCPay was down.
4. Confirmations follow the store's speed policy, as for on-chain BTC. Partial payments, overpayments, expiry and paid-late work as BTCPay normally does, and the checkout re-quotes whatever is still due.

## When something goes wrong

- **Too little BTC arrives:** the invoice is partially paid and the checkout quotes the rest. If it expires that way, refund the partial amount with BTCPay's Refund button.
- **A LI.FI route fails:** the bridge returns the funds to the customer's wallet on the source chain.
- **A THORChain deposit of the wrong amount, past the price tolerance, or below the minimum:** THORChain refunds the coins to the sending address, minus network fees (an exchange withdrawal is refunded to the exchange).
- **Late BTC:** recorded as paid late, like late on-chain BTC.

For support, the invoice log records each THORChain payment the customer was shown (amount, vault, memoless registration, memo, expiry) and each LI.FI transaction the customer signs in the panel. Each is linked to LI.FI's explorer (scan.li.fi) or a THORChain explorer (runescan.io). The invoice's payment table also shows the THORChain inbound transaction read from the BTC payment's `OUT:` memo.

## Settings (per store)

- Which chains are enabled.
- LI.FI: integrator name (from [portal.li.fi](https://portal.li.fi)), optional API key, optional WalletConnect project id for mobile EVM wallets, integrator fee (default 0).
- THORChain: THORNode URL (default `https://gateway.liquify.com/chain/thorchain_api`), price tolerance (default 300 bps), affiliate THORName and fee (default 0).
- Amount buffer: extra BTC asked from the customer (default 0). Leave it at 0 and set the store's payment tolerance to about 1% to absorb small shortfalls yourself.

Also set a longer invoice expiry (for example 60 minutes): swaps to BTC usually take several minutes, and THORChain's outbound delay grows with the amount.

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

- **Not yet run end to end.** Built and unit-tested (THORNode is faked in tests); LI.FI and THORNode were not reachable from the build environment.
- **Memoless deposits depend on THORChain's hosted API** (api.thorchain.org/memoless, no key, about 100 requests per 15 minutes per IP). It pays the registration from its own wallet and may add a small affiliate fee to the memo; the plugin checks the memo still pays the invoice's address.
- **The customer can edit the amount in the LI.FI panel.** Anything short leaves the invoice partially paid.
- **Chain icons** are simple placeholders.
- **Refunds** use BTCPay's own refund flow (BTC). Address screening is not built.

## Roadmap

- Monero, when THORChain's memoless API lists it.
- Settle into USDC instead of BTC.
- Publish on the Plugin Builder.
