// Swap panel for BTCPay checkout: pay a BTC invoice from tokens on another chain.
// LI.FI quotes the exact BTC still due, the customer signs in their own wallet,
// and the BTC is delivered by LI.FI's bridges straight to the invoice's own
// BTC address. BTCPay then sees an ordinary on-chain payment.
import { createRoot } from 'react-dom/client';
import { ChainType, LiFiWidget, useWidgetEvents, WidgetEvent } from '@lifi/widget';
import { EthereumProvider } from '@lifi/widget-provider-ethereum';
import { SolanaProvider } from '@lifi/widget-provider-solana';
import { useEffect } from 'react';

export const BTC_CHAIN_ID = 20000000000001;
export const BTC_TOKEN = 'bitcoin';
export const SOLANA_CHAIN_ID = 1151111081099710;

function Events({ onEvent }) {
  const emitter = useWidgetEvents();
  useEffect(() => {
    const started = (route) => onEvent('started', route);
    const completed = (route) => onEvent('completed', route);
    const failed = (update) => onEvent('failed', update);
    emitter.on(WidgetEvent.RouteExecutionStarted, started);
    emitter.on(WidgetEvent.RouteExecutionCompleted, completed);
    emitter.on(WidgetEvent.RouteExecutionFailed, failed);
    return () => {
      emitter.off(WidgetEvent.RouteExecutionStarted, started);
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

export function buildConfig(opts) {
  const fromChain = Number(opts.fromChain);
  const isSolana = fromChain === SOLANA_CHAIN_ID;
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
    providers: isSolana
      ? [SolanaProvider()]
      : [EthereumProvider(opts.walletConnectProjectId ? { walletConnect: { projectId: opts.walletConnectProjectId } } : undefined)],
  };
  if (opts.apiKey) config.apiKey = opts.apiKey;
  if (opts.fee > 0) {
    config.feeConfig = { fee: opts.fee, name: opts.feeName || 'Service fee', showFeePercentage: true, showFeeTooltip: true };
  }
  return config;
}

const roots = new WeakMap();

function mount(element, opts) {
  unmount(element);
  const root = createRoot(element);
  roots.set(element, root);
  root.render(
    <>
      <Events onEvent={opts.onEvent || (() => {})} />
      <LiFiWidget integrator={opts.integrator} config={buildConfig(opts)} />
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

window.StablecoinsSwapPanel = { mount, unmount, buildConfig };
