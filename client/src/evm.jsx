import { EthereumProvider } from '@lifi/widget-provider-ethereum';
import { register } from './core.jsx';

// WalletConnect (scan a QR code with a phone wallet) is always offered: with the store's own Reown project ID
// when one is set, otherwise with LI.FI's default one. MetaMask and Coinbase also get their mobile app flows.
register('evm', (opts) => [
  EthereumProvider({
    walletConnect: opts.walletConnectProjectId ? { projectId: opts.walletConnectProjectId } : true,
    metaMask: true,
    coinbase: true,
  }),
]);
