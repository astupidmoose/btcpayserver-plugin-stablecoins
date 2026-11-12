import { SolanaProvider } from '@lifi/widget-provider-solana';
import { SolanaProvider as SolanaSdkProvider } from '@lifi/sdk-provider-solana';
import { solanaTransfer } from './transfer-solana.js';
import { register } from './core.jsx';

// The wallet LI.FI's provider uses, kept for plain transfers (NEAR Intents deposits). LI.FI routes run exactly as before.
const wallet = {};

register('solana', () => [
  SolanaProvider({
    // Same SDK provider the widget makes by default; it also hands over the wallet for transfers.
    sdkProvider: (deps) => {
      Object.assign(wallet, deps);
      return SolanaSdkProvider(deps);
    },
  }),
], (args) => solanaTransfer(wallet, args));
