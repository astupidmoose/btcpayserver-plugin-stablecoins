import { EthereumProvider } from '@lifi/widget-provider-ethereum';
import { register } from './core.jsx';

register('evm', (opts) => [
  EthereumProvider(opts.walletConnectProjectId ? { walletConnect: { projectId: opts.walletConnectProjectId } } : undefined),
]);
