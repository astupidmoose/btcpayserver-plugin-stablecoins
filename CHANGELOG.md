# Changelog

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
