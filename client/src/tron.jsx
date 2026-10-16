import { TronProvider } from '@lifi/widget-provider-tron';
import { register } from './core.jsx';

// WalletConnect lets phone wallets (TronLink, Trust...) connect by QR code.
register('tron', () => [TronProvider({ walletConnect: true })]);
