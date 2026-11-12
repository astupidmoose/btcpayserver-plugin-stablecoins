// Shared core of the payment panel bundles (evm.jsx, solana.jsx, tron.jsx).
// The customer sees a plain token payment: connect a wallet, pick one of their
// tokens on this chain, and pay the exact amount shown. Behind it, LI.FI and
// NEAR Intents both quote what that token must be to deliver the BTC still due,
// and the lower one is shown and paid: a LI.FI route, or a plain transfer to a
// one-time NEAR Intents deposit address. Either way the BTC goes to the
// invoice's own address. BTC never appears in this panel.
import { createRoot } from 'react-dom/client';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createTheme, ThemeProvider } from '@mui/material/styles';
import {
  ChainType,
  convertQuoteToRoute,
  createClient,
  executeRoute,
  formatUnits,
  getChains,
  getQuote,
  getToken,
  getTokenBalances,
  getTokens,
} from '@lifi/sdk';
import { useEthereumContext, useSDKProviders, useTronContext } from '@lifi/widget-provider';
import { cheapest, withinShown } from './offers.js';
import { useAccount, useWalletMenu, WalletManagementProviders } from '@lifi/wallet-management';

export const BTC_CHAIN_ID = 20000000000001;
export const BTC_TOKEN = 'bitcoin';
export const SOLANA_CHAIN_ID = 1151111081099710;
export const TRON_CHAIN_ID = 728126428;

// A bridge may deliver slightly less than quoted if prices move before it runs.
// Accept a re-quote down to this share of the BTC requested; the store's payment
// tolerance covers the rest. Anything worse stops before the customer signs.
const MIN_ACCEPTED_OUTPUT = 0.99;
const QUOTE_REFRESH_MS = 45_000;
const MIN_BALANCE_USD = 0.5;
export { isNativeToken } from './offers.js';

async function postJson(url, body) {
  const r = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(data.error || `HTTP ${r.status}`);
  return data;
}

const WALLETCONNECT_ICON = 'data:image/svg+xml;utf8,' + encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40" rx="10" fill="#3B99FC"/>' +
  '<path d="M12.3 15.4c4.3-4.2 11.2-4.2 15.4 0l.5.5c.2.2.2.6 0 .8l-1.8 1.7c-.1.1-.3.1-.4 0l-.7-.7c-3-2.9-7.8-2.9-10.8 0l-.8.7c-.1.1-.3.1-.4 0l-1.8-1.7c-.2-.2-.2-.6 0-.8l.8-.5zm19.1 3.5 1.6 1.6c.2.2.2.6 0 .8l-7.2 7c-.2.2-.6.2-.8 0l-5.1-5c-.1-.1-.2-.1-.2 0l-5.1 5c-.2.2-.6.2-.8 0l-7.2-7c-.2-.2-.2-.6 0-.8l1.6-1.6c.2-.2.6-.2.8 0l5.1 5c.1.1.2.1.2 0l5.1-5c.2-.2.6-.2.8 0l5.1 5c.1.1.2.1.2 0l5.1-5c.2-.2.6-.2.8 0z" fill="#fff"/></svg>');

/**
 * Connects a phone wallet by QR code (WalletConnect) straight from the checkout, without the wallet list.
 * Solana has no WalletConnect connector in LI.FI's providers, so it has none.
 */
function useQrConnect(chain) {
  const ethereum = useEthereumContext();
  const tron = useTronContext();
  if (chain.id === SOLANA_CHAIN_ID) return null;
  if (chain.id === TRON_CHAIN_ID) return () => tron.connect('WalletConnect');
  return () => {
    // WalletConnect's QR dialog renders into this element; LI.FI's wallet menu normally adds it.
    if (!document.querySelector('w3m-modal')) document.body.appendChild(document.createElement('w3m-modal'));
    return ethereum.connect('walletConnect');
  };
}

/** BTC (decimal string) to satoshis (string). */
export function toSats(btc) {
  const [whole, frac = ''] = String(btc).split('.');
  return (BigInt(whole || '0') * 100000000n + BigInt((frac + '00000000').slice(0, 8))).toString();
}

/** Display amount: up to 6 significant decimals, never rounded below what is needed. */
export function formatAmount(units, decimals) {
  const value = formatUnits(BigInt(units), decimals);
  const [whole, frac = ''] = value.split('.');
  if (!frac) return whole;
  const keep = whole === '0' ? Math.max(6, frac.search(/[1-9]/) + 4) : 6;
  if (frac.length <= keep) return `${whole}.${frac}`.replace(/\.?0+$/, '');
  // Round up: the customer must never be shown less than the quote needs.
  const rounded = (BigInt(`${whole}${frac.slice(0, keep)}`) + 1n).toString().padStart(keep + 1, '0');
  const w = rounded.slice(0, rounded.length - keep) || '0';
  return `${w}.${rounded.slice(-keep)}`.replace(/\.?0+$/, '');
}

// LI.FI prices in USD; amounts are shown in the store's default currency instead.
// usdRate is that currency per US dollar (the BTC price in it over LI.FI's BTC price in USD).
function money(usdValue, usdRate, currency) {
  const value = Number(usdValue) * usdRate;
  if (!currency || !Number.isFinite(value) || !(usdRate > 0)) return null;
  let digits = 2;
  try { digits = new Intl.NumberFormat('en', { style: 'currency', currency }).resolvedOptions().maximumFractionDigits; } catch { /* unknown code */ }
  return `${value.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${currency}`;
}

function feesUsd(quote) {
  const costs = [...(quote.estimate?.gasCosts || []), ...(quote.estimate?.feeCosts || []).filter((f) => !f.included)];
  return costs.reduce((sum, c) => sum + (Number(c.amountUSD) || 0), 0);
}

function errorText(e) {
  const message = (e && (e.cause?.shortMessage || e.shortMessage || e.message)) || String(e);
  if (/user rejected|denied|rejected the request/i.test(message)) return 'You cancelled the transaction in your wallet.';
  if (/no available quotes|no route|not find any route|NotFound/i.test(message)) return 'No swap route is available for this token right now. Try another token.';
  return message;
}

function TokenIcon({ token }) {
  return token.logoURI
    ? <img src={token.logoURI} alt="" width="28" height="28" className="rounded-circle flex-shrink-0" />
    : <span className="rounded-circle bg-secondary flex-shrink-0" style={{ width: 28, height: 28 }} />;
}

function Panel({ opts, client, chain, transfer }) {
  const sdkProviders = useSDKProviders();
  useEffect(() => { client.setProviders(sdkProviders); }, [client, sdkProviders]);
  const { account } = useAccount({ chainType: chain.chainType });
  const { openWalletMenu } = useWalletMenu();
  const qrConnect = useQrConnect(chain);
  const [qrError, setQrError] = useState(null);
  const address = account?.isConnected ? account.address : null;

  const [balances, setBalances] = useState(null);
  const [balanceError, setBalanceError] = useState(null);
  const [token, setToken] = useState(null);
  const [choosing, setChoosing] = useState(false);
  const [quote, setQuote] = useState(null);
  const [quoteError, setQuoteError] = useState(null);
  const [status, setStatus] = useState('idle'); // idle | paying | more | sent | partial | failed
  const [payError, setPayError] = useState(null);
  const quoteSeq = useRef(0);
  const [usdRate, setUsdRate] = useState(null);
  const show = (usdValue) => money(usdValue, usdRate, opts.displayCurrency);

  // The store's currency per US dollar, from LI.FI's BTC price and the invoice's BTC rate.
  useEffect(() => {
    let cancelled = false;
    const price = Number(opts.btcPrice);
    if (!(price > 0)) return;
    getToken(client, BTC_CHAIN_ID, BTC_TOKEN)
      .then((btc) => { const usd = Number(btc?.priceUSD); if (!cancelled && usd > 0) setUsdRate(price / usd); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [client, opts.btcPrice]);

  // The customer's tokens on this chain, most valuable first.
  useEffect(() => {
    setBalances(null); setBalanceError(null); setToken(null); setQuote(null);
    if (!address) return;
    let cancelled = false;
    (async () => {
      try {
        const list = await getTokens(client, { chains: [chain.id], chainTypes: [chain.chainType] });
        const tokens = list.tokens[chain.id] || [];
        const amounts = await getTokenBalances(client, address, tokens);
        const held = amounts
          .filter((t) => t.amount && t.amount > 0n)
          .map((t) => ({ ...t, valueUsd: Number(formatUnits(t.amount, t.decimals)) * Number(t.priceUSD || 0) }))
          .filter((t) => t.valueUsd >= MIN_BALANCE_USD)
          .sort((a, b) => b.valueUsd - a.valueUsd);
        if (cancelled) return;
        setBalances(held);
        if (held.length) setToken(held[0]);
        else opts.onEvent('unpayable', 'no tokens');
      } catch (e) {
        if (!cancelled) setBalanceError(errorText(e));
      }
    })();
    return () => { cancelled = true; };
  }, [client, chain, address, opts]);

  // LI.FI's price for the chosen token: a route that delivers the BTC due.
  const lifiOffer = useCallback(async () => {
    const q = await getQuote(client, {
      fromChain: chain.id,
      fromToken: token.address,
      fromAddress: address,
      toChain: BTC_CHAIN_ID,
      toToken: BTC_TOKEN,
      toAddress: opts.btcAddress,
      toAmount: toSats(opts.btcDue),
      ...(opts.fee > 0 ? { fee: opts.fee } : {}),
    });
    // The route's worst case must still cover the invoice: otherwise the customer pays in full and the invoice stays partly unpaid.
    const minOut = q.estimate?.toAmountMin ? BigInt(q.estimate.toAmountMin) : null;
    return {
      route: 'lifi',
      lifi: q,
      fromAmount: BigInt(q.action.fromAmount),
      amountUsd: q.estimate?.fromAmountUSD,
      feesUsd: feesUsd(q),
      safe: minOut !== null && minOut * 100n >= BigInt(toSats(opts.btcDue)) * BigInt(Math.round(MIN_ACCEPTED_OUTPUT * 100)),
    };
  }, [client, chain, address, token, opts]);

  // NEAR Intents' price for the same token, through this store's server (which keeps the API key): an exact-output
  // quote for the BTC due, so it covers the invoice by construction. Only where the store offers it.
  const nearOffer = useCallback(async () => {
    if (!opts.nearQuoteUrl || !transfer) throw new Error('not offered');
    const r = await postJson(opts.nearQuoteUrl, { chainId: chain.id, token: token.address, decimals: token.decimals, fromAddress: address });
    // The amount is in the token's smallest unit: refuse one counted in other units.
    if (r.decimals !== token.decimals || !(BigInt(r.amountIn) > 0n)) throw new Error('unexpected price');
    return { route: 'near', fromAmount: BigInt(r.amountIn), amountUsd: r.amountInUsd, feesUsd: 0, safe: true };
  }, [chain, address, token, opts, transfer]);

  // What the chosen token must be to deliver the BTC due, from whichever asks for less. Refreshed while the customer decides.
  const requestQuote = useCallback(async () => {
    if (!address || !token) return;
    const seq = ++quoteSeq.current;
    setQuoteError(null);
    const [lifi, near] = await Promise.allSettled([lifiOffer(), nearOffer()]);
    if (seq !== quoteSeq.current) return;
    const lifiValue = lifi.status === 'fulfilled' ? lifi.value : null;
    const best = cheapest([lifiValue, near.status === 'fulfilled' ? near.value : null]);
    // Neither can pay: LI.FI's unsafe route is still shown, with its warning; otherwise LI.FI's reason.
    setQuote(best || lifiValue);
    if (!best && !lifiValue) {
      const reason = errorText(lifi.status === 'rejected' ? lifi.reason : new Error('No route'));
      setQuoteError(reason);
      // Lets the checkout offer the store's other networks.
      opts.onEvent('unpayable', reason);
    }
  }, [address, token, lifiOffer, nearOffer, opts]);

  useEffect(() => {
    setQuote(null);
    if (status !== 'idle' && status !== 'failed') return;
    requestQuote();
    const timer = setInterval(requestQuote, QUOTE_REFRESH_MS);
    return () => clearInterval(timer);
  }, [requestQuote, status]);

  const fromAmount = quote ? quote.fromAmount : null;
  const enough = fromAmount !== null && token && token.amount >= fromAmount;
  const safe = !!quote?.safe;

  // A step is signed once its swap or bridge transaction went out, unless it finally failed (a reverted one keeps
  // its txHash). A failure that is not final, such as lost tracking, may still land, so it still counts.
  const sent = (a) => a.txHash && (a.type === 'SWAP' || a.type === 'CROSS_CHAIN') && !(a.status === 'FAILED' && a.txFinal === true);
  const signed = (step) => (step.execution?.actions || []).some(sent);

  // The amount of a NEAR Intents deposit the customer was already shown, per token, so a second press sends it.
  const acceptedDeposit = useRef(null);

  // Through NEAR Intents: the server makes (or reuses) the deposit for this invoice, then the wallet sends the token
  // to it in one plain transfer. What NEAR Intents cannot swap, and any excess, goes back to this wallet.
  const payNear = async () => {
    setStatus('paying'); setPayError(null);
    let deposit;
    try {
      const r = await postJson(opts.nearDepositUrl, { chainId: chain.id, token: token.address, decimals: token.decimals, fromAddress: address });
      if (!r.depositAddress || r.decimals !== token.decimals) throw new Error('This payment could not be prepared. Please try again.');
      deposit = { address: r.depositAddress, amount: BigInt(r.amountIn) };
    } catch (e) {
      setStatus('failed'); setPayError(errorText(e));
      return;
    }
    const seen = acceptedDeposit.current;
    const shownBefore = seen && seen.token === token.address && seen.address === deposit.address && seen.amount === deposit.amount;
    // Never send more than the customer saw, beyond a rounding margin: show the new amount and let them press Pay again.
    if (!shownBefore && !withinShown(deposit.amount, quote.fromAmount)) {
      acceptedDeposit.current = { token: token.address, address: deposit.address, amount: deposit.amount };
      setStatus('failed');
      setPayError(`The amount needed went up to ${formatAmount(deposit.amount, token.decimals)} ${token.symbol}. Press Pay again to send it.`);
      return;
    }
    if (token.amount < deposit.amount) {
      setStatus('failed'); setPayError(`Not enough ${token.symbol} in this wallet. Pick another token.`);
      return;
    }
    opts.onEvent('started', { action: { fromAddress: address, fromToken: { symbol: token.symbol, decimals: token.decimals }, fromAmount: deposit.amount.toString() } });
    try {
      const txHash = await transfer({ client, chain, token, to: deposit.address, amount: deposit.amount, from: address });
      opts.onEvent('tx', { txHash, chainId: chain.id, type: 'TRANSFER', depositAddress: deposit.address });
      acceptedDeposit.current = null;
      setStatus('sent');
      opts.onEvent('completed', null);
    } catch (e) {
      setStatus('failed');
      setPayError(errorText(e));
      opts.onEvent('failed', e);
    }
  };

  const pay = async () => {
    if (!quote || !enough || !safe) return;
    if (quote.route === 'near') return payNear();
    const lifiQuote = quote.lifi;
    setStatus('paying'); setPayError(null);
    const requested = BigInt(toSats(opts.btcDue));
    const seen = new Set();
    let last = null;
    try {
      opts.onEvent('started', lifiQuote);
      await executeRoute(client, convertQuoteToRoute(lifiQuote), {
        acceptExchangeRateUpdateHook: async ({ newToAmount }) =>
          BigInt(newToAmount) * 100n >= requested * BigInt(Math.round(MIN_ACCEPTED_OUTPUT * 100)),
        updateRouteHook: (route) => {
          last = route;
          for (const step of route.steps) {
            for (const action of step.execution?.actions || []) {
              if (!action.txHash || seen.has(action.txHash)) continue;
              if (action.type !== 'SWAP' && action.type !== 'CROSS_CHAIN') continue;
              seen.add(action.txHash);
              opts.onEvent('tx', { txHash: action.txHash, txLink: action.txLink, chainId: chain.id, type: action.type });
            }
          }
          // Closing the page is safe only once every step is signed: a later step needs the wallet again.
          if (seen.size > 0) setStatus(route.steps.every(signed) ? 'sent' : 'more');
        },
      });
      setStatus('sent');
      opts.onEvent('completed', lifiQuote);
    } catch (e) {
      // Once every step is signed the BTC is on its way even if tracking fails.
      if (last?.steps.every(signed)) { setStatus('sent'); return; }
      // Part of a multi-step payment went out: paying again would pay twice.
      if (last?.steps.some(signed)) { setStatus('partial'); opts.onEvent('failed', e); return; }
      setStatus('failed');
      setPayError(errorText(e));
      opts.onEvent('failed', e);
    }
  };

  if (!address) {
    return (
      <div className="d-grid gap-2">
        <button type="button" className="btn btn-primary w-100" onClick={() => openWalletMenu()}>
          Connect wallet
        </button>
        {qrConnect && (
          <button
            type="button"
            className="btn btn-outline-primary w-100 d-flex align-items-center justify-content-center gap-2"
            onClick={async () => {
              setQrError(null);
              try { await qrConnect(); } catch (e) { setQrError(errorText(e)); }
            }}
          >
            <img src={WALLETCONNECT_ICON} alt="" width="20" height="20" />
            Scan QR code
          </button>
        )}
        {qrError && <div className="text-danger small text-center">{qrError}</div>}
      </div>
    );
  }

  if (status === 'partial') {
    return (
      <div className="text-center">
        <p className="mb-1 fw-semibold">Payment not finished</p>
        <p className="text-muted small mb-0">
          Part of this payment was sent, but your wallet did not send the rest. Do not pay again: contact the store with this invoice so they can help.
        </p>
      </div>
    );
  }

  if (status === 'paying' || status === 'more' || status === 'sent') {
    return (
      <div className="text-center">
        <p className="mb-1 fw-semibold">{status === 'sent' ? 'Payment sent' : 'Confirm the payment in your wallet…'}</p>
        <p className="text-muted small mb-0">
          {status === 'paying' ? 'Your wallet may ask you to approve the token first, then to send it.'
            : status === 'more' ? 'This payment takes more than one transaction. Keep this page open until your wallet has sent them all.'
            : 'It can take several minutes to arrive. You can close this page.'}
        </p>
      </div>
    );
  }

  return (
    <div className="stablecoins-pay">
      <div className="d-flex justify-content-between align-items-center small text-muted mb-2">
        <span>Paying from {chain.name}</span>
        <button type="button" className="btn btn-link btn-sm p-0" onClick={() => openWalletMenu()}>
          {address.slice(0, 6)}…{address.slice(-4)}
        </button>
      </div>

      {balanceError && <div className="alert alert-danger small">Could not read your balances: {balanceError}</div>}
      {!balances && !balanceError && <p className="text-center text-muted my-3">Reading your balances…</p>}
      {balances && balances.length === 0 && (
        <div className="alert alert-warning small">This wallet has no tokens on {chain.name}. Connect another wallet, or choose another way to pay.</div>
      )}

      {balances && balances.length > 0 && token && (
        <>
          <label className="form-label small text-muted mb-1">Pay with</label>
          <button type="button" className="form-control d-flex align-items-center gap-2 text-start mb-2"
            onClick={() => setChoosing(!choosing)} aria-expanded={choosing}>
            <TokenIcon token={token} />
            <span className="fw-semibold">{token.symbol}</span>
            <span className="text-muted small ms-auto">Balance {formatAmount(token.amount, token.decimals)}</span>
          </button>
          {choosing && (
            <div className="list-group mb-3 stablecoins-tokens">
              {balances.map((t) => (
                <button type="button" key={t.address}
                  className={`list-group-item list-group-item-action d-flex align-items-center gap-2 ${t.address === token.address ? 'active' : ''}`}
                  onClick={() => { setToken(t); setChoosing(false); }}>
                  <TokenIcon token={t} />
                  <span className="fw-semibold">{t.symbol}</span>
                  <span className="small ms-auto">{formatAmount(t.amount, t.decimals)}{show(t.valueUsd) ? ` · ${show(t.valueUsd)}` : ''}</span>
                </button>
              ))}
            </div>
          )}

          <div className="text-center my-3">
            {quote ? (
              <>
                <div className="text-muted small">Amount to pay</div>
                <div className="fs-3 fw-semibold">{formatAmount(fromAmount, token.decimals)} {token.symbol}</div>
                {show(quote.amountUsd) && (
                  <div className="text-muted small">
                    {`about ${show(quote.amountUsd)}`}
                    {quote.feesUsd > 0 && show(quote.feesUsd) ? ` · network fees about ${show(quote.feesUsd)}` : ''}
                  </div>
                )}
              </>
            ) : quoteError ? (
              <div className="alert alert-warning small mb-0">{quoteError}</div>
            ) : (
              <p className="text-muted mb-0">Calculating the amount…</p>
            )}
          </div>

          {quote && !enough && (
            <div className="alert alert-warning small">Not enough {token.symbol} in this wallet. Pick another token.</div>
          )}
          {payError && <div className="alert alert-warning small">{payError}</div>}
          {quote && enough && !safe && (
            <div className="alert alert-warning small">This token's route could deliver too little right now. Pick another token or try again shortly.</div>
          )}
          <button type="button" className="btn btn-primary w-100" disabled={!quote || !enough || !safe} onClick={pay}>
            {quote ? `Pay ${formatAmount(fromAmount, token.decimals)} ${token.symbol}` : 'Pay'}
          </button>
        </>
      )}
    </div>
  );
}

function App({ opts, providers, transfer }) {
  const queryClient = useMemo(() => new QueryClient(), []);
  const client = useMemo(() => createClient({
    integrator: opts.integrator,
    ...(opts.apiKey ? { apiKey: opts.apiKey } : {}),
  }), [opts.integrator, opts.apiKey]);
  const [chains, setChains] = useState(null);
  const [error, setError] = useState(null);
  const fromChain = Number(opts.fromChain);

  useEffect(() => {
    // LI.FI lists only EVM chains unless asked for the others.
    getChains(client, { chainTypes: [ChainType.EVM, ChainType.SVM, ChainType.TVM] }).then(setChains, (e) => setError(errorText(e)));
  }, [client]);

  // The wallet list is LI.FI's own dialog (MUI); it reads the theme through CSS variables.
  const theme = useMemo(() => createTheme({
    cssVariables: true,
    shape: { borderRadius: 12 },
    palette: {
      mode: opts.appearance === 'dark' || (opts.appearance !== 'light' && window.matchMedia?.('(prefers-color-scheme: dark)').matches) ? 'dark' : 'light',
      ...(opts.primaryColor ? { primary: { main: opts.primaryColor } } : {}),
    },
    typography: { fontFamily: 'inherit' },
  }), [opts.appearance, opts.primaryColor]);

  if (error) return <div className="alert alert-danger small">The payment panel could not load: {error}</div>;
  if (!chains) return <p className="text-center text-muted my-3">Loading…</p>;
  const chain = chains.find((c) => c.id === fromChain);
  if (!chain) return <div className="alert alert-danger small">This chain is not available right now.</div>;

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={theme}>
        <WalletManagementProviders
          config={{ enabledChainTypes: [chain.chainType] }}
          providers={providers}
          chains={[chain]}>
          <Panel opts={opts} client={client} chain={chain} transfer={transfer} />
        </WalletManagementProviders>
      </ThemeProvider>
    </QueryClientProvider>
  );
}

const roots = new WeakMap();

function mount(element, opts, providers, transfer) {
  unmount(element);
  const root = createRoot(element);
  roots.set(element, root);
  root.render(<App opts={{ onEvent: () => {}, ...opts }} providers={providers} transfer={transfer} />);
}

function unmount(element) {
  const root = roots.get(element);
  if (root) {
    root.unmount();
    roots.delete(element);
  }
}

// Each bundle carries the wallets for one family of chains, so a checkout only downloads what it needs.
// transfer({ client, chain, token, to, amount, from }) sends a plain token transfer from the connected wallet (for
// NEAR Intents deposits) and resolves to its transaction id.
export function register(family, makeProviders, transfer) {
  window.StablecoinsSwapPanels = window.StablecoinsSwapPanels || {};
  window.StablecoinsSwapPanels[family] = {
    mount: (element, opts) => mount(element, opts, makeProviders(opts), transfer),
    unmount,
  };
}
