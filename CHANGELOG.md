# Changelog

## 0.13.0

- **NEAR Intents in the wallet checkout, as backup and when cheaper:** for the token the customer picks, the panel asks LI.FI and NEAR Intents what it must be to deliver the BTC due, and shows and pays the lower one. Same panel, same single signature: a NEAR Intents payment is a plain transfer from the wallet to a one-time deposit address made for this invoice. Makes tokens LI.FI cannot route payable (most of Solana and Tron), and is often cheaper (about 3% instead of about 5% for SOL on a ~$75 order in testing).
  - Refunds of a NEAR Intents wallet payment (too late, too little, any excess) go to the customer's own wallet, not the store's refund address.
  - The deposit is saved on the invoice before its address is used, and followed like the exchange deposits, across restarts. Pressing Pay again within five minutes reuses it.
  - A token is matched by chain, exact contract and decimals; anything ambiguous, deprecated or with a memo is not offered through NEAR Intents.
  - On for every store; "Also price wallet payments with NEAR Intents" switches it off.
- **Panel tests:** the offer choice and the Solana transfer encoding have their own tests (`npm test` in `client/`), run by CI.
- **Dependency:** `follow-redirects` pinned to 1.16.3 (GHSA-mrwp-f3q8-3g9p in 1.16.0-1.16.1, through tronweb's axios).

## 0.12.2

- **Earlier swaps become searchable:** once after the update, swaps checked before 0.12.1 that LI.FI reported failed or refunded become searchable by their transaction hash and the customer's wallet. Each is asked about again and follows the same rules as a new swap. Before, only swaps checked after the update were covered.
- **Wallets in either spelling:** invoice search is case-sensitive. A customer's EVM wallet is now searchable both in lower case and checksummed (mixed case, as wallets and the invoice log show it), for new swaps and, through the same one-time pass, earlier ones.

## 0.12.1

First release from `main` with every feature merged. Builds 0.12.0 and earlier were made from feature branches.

- **Monero (Pegasus Swap), new:** customers can pay with XMR. This is its own payment method, `PEGASUS_MONERO`. Each invoice gets a fixed-rate Pegasus Swap order that pays the BTC due to a fresh address from the store's own BTC wallet. No XMR wallet or spend key is needed on the server.
  - Unswapped XMR is refunded to the store's XMR refund address, which is checked as a mainnet Monero address.
  - While the swap runs, the invoice shows as Processing through a pending payment. The BTC that arrives replaces it once that BTC is confirmed. The pending payment stops counting if the order is refunded, goes overdue, or hasn't paid out within a day.
  - Orders, status changes and transaction ids go into the invoice log and the search terms.
  - Needs Pegasus Swap API keys.
- **Pay from another network (new):** the token checkout lets the customer switch to another network without leaving the page. A chain that can't pay offers the others.
- **Search for failed swaps (new):** a LI.FI swap reported as failed or refunded is now searchable by its transaction hash and paying wallet, unless LI.FI shows it was sent to another BTC address. It never counts as payment. Swaps checked before this version aren't backfilled.
- **Exact amounts from providers:** Pegasus Swap and NEAR Intents responses are parsed with numbers read as exact decimals. Newtonsoft's default read them as doubles, which can drop digits of an amount sent as a JSON number (XMR has 12 decimals).
- **Lighter test build:** the tests reference BTCPay and xUnit directly instead of BTCPay's whole test project.
- **Cleanup:** removed two unused services from the checkout endpoint.
- **Releases:** tagged releases are built, tested and published by GitHub Actions. CI runs the tests on every pull request.

## 0.10.2 and earlier

See the git history.
