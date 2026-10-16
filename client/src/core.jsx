// Shared core of the swap panel bundles (evm.jsx, solana.jsx, tron.jsx).
// Swap panel for BTCPay checkout: pay a BTC invoice from tokens on another chain.
// LI.FI quotes the exact BTC still due, the customer signs in their own wallet,
// and the BTC is delivered by LI.FI's bridges straight to the invoice's own
// BTC address. BTCPay then sees an ordinary on-chain payment.
import { createRoot } from 'react-dom/client';
import { ChainType, LiFiWidget, useWidgetEvents, WidgetEvent } from '@lifi/widget';
import { useEffect } from 'react';

export const BTC_CHAIN_ID = 20000000000001;
export const BTC_TOKEN = 'bitcoin';
export const SOLANA_CHAIN_ID = 1151111081099710;
export const TRON_CHAIN_ID = 728126428;

function Events({ onEvent }) {
  const emitter = useWidgetEvents();
  useEffect(() => {
    const started = (route) => onEvent('started', route);
    const completed = (route) => onEvent('completed', route);
    const failed = (update) => onEvent('failed', update);
    // Reports each signed transaction once, so the invoice can link support to the swap in LI.FI's explorer.
    const seen = new Set();
    const updated = ({ action }) => {
      if (!action || !action.txHash || seen.has(action.txHash)) return;
      if (action.type !== 'SWAP' && action.type !== 'CROSS_CHAIN') return;
      seen.add(action.txHash);
      onEvent('tx', { txHash: action.txHash, txLink: action.txLink, chainId: action.chainId, type: action.type });
    };
    emitter.on(WidgetEvent.RouteExecutionStarted, started);
    emitter.on(WidgetEvent.RouteExecutionUpdated, updated);
    emitter.on(WidgetEvent.RouteExecutionCompleted, completed);
    emitter.on(WidgetEvent.RouteExecutionFailed, failed);
    return () => {
      emitter.off(WidgetEvent.RouteExecutionStarted, started);
      emitter.off(WidgetEvent.RouteExecutionUpdated, updated);
      emitter.off(WidgetEvent.RouteExecutionCompleted, completed);
      emitter.off(WidgetEvent.RouteExecutionFailed, failed);
    };
  }, [emitter, onEvent]);
  return null;
}

// Palette that follows the BTCPay checkout's own CSS variables, so the panel
// picks up the store's brand color and the light or dark theme.
function themeFromCss(primary) {
  const palette = primary ? { primary: { main: primary }, secondary: { main: primary } } : undefined;
  return {
    colorSchemes: palette ? { light: { palette }, dark: { palette } } : undefined,
    shape: { borderRadius: 12, borderRadiusSecondary: 999 },
    typography: { fontFamily: 'inherit' },
    container: { boxShadow: 'none', border: 'none', borderRadius: 0, maxWidth: '100%' },
  };
}

export function buildConfig(opts, providers) {
  const fromChain = Number(opts.fromChain);
  const config = {
    integrator: opts.integrator,
    fromChain,
    toChain: BTC_CHAIN_ID,
    toToken: BTC_TOKEN,
    // The amount still due on the invoice, in BTC. LI.FI computes what to send.
    toAmount: opts.btcDue,
    toAddress: {
      address: opts.btcAddress,
      chainType: ChainType.UTXO,
      name: opts.invoiceLabel || 'This invoice',
    },
    // Each BTCPay payment method is one source chain; the destination is always BTC.
    chains: {
      from: { allow: [fromChain] },
      to: { allow: [BTC_CHAIN_ID] },
    },
    disabledUI: { toToken: true, toAddress: true },
    hiddenUI: { reverseTokensButton: true, appearance: true, history: true, language: true, toAddress: true },
    variant: 'compact',
    appearance: opts.appearance || 'auto',
    theme: themeFromCss(opts.primaryColor),
    providers,
  };
  if (opts.apiKey) config.apiKey = opts.apiKey;
  if (opts.fee > 0) {
    config.feeConfig = { fee: opts.fee, name: opts.feeName || 'Service fee', showFeePercentage: true, showFeeTooltip: true };
  }
  return config;
}

const roots = new WeakMap();

function mount(element, opts, providers) {
  unmount(element);
  const root = createRoot(element);
  roots.set(element, root);
  root.render(
    <>
      <Events onEvent={opts.onEvent || (() => {})} />
      <LiFiWidget integrator={opts.integrator} config={buildConfig(opts, providers)} />
    </>
  );
}

function unmount(element) {
  const root = roots.get(element);
  if (root) {
    root.unmount();
    roots.delete(element);
  }
}

// Each bundle carries the wallets for one family of chains, so a checkout only downloads what it needs.
export function register(family, makeProviders) {
  window.StablecoinsSwapPanels = window.StablecoinsSwapPanels || {};
  window.StablecoinsSwapPanels[family] = {
    mount: (element, opts) => mount(element, opts, makeProviders(opts)),
    unmount,
    buildConfig: (opts) => buildConfig(opts, makeProviders(opts)),
  };
}
