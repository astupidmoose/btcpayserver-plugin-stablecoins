import { EthereumProvider } from '@lifi/widget-provider-ethereum';
import { EthereumProvider as EthereumSdkProvider } from '@lifi/sdk-provider-ethereum';
import { erc20Abi } from 'viem';
import { getChainId, sendTransaction, writeContract } from 'viem/actions';
import { isNativeToken, register } from './core.jsx';

// The wallet LI.FI's provider uses, kept for plain transfers (NEAR Intents deposits). LI.FI routes run exactly as before.
const wallet = {};

/** Sends `amount` of the token from the connected wallet to `to` on `chain`, switching the wallet to that chain first. */
async function transfer({ chain, token, to, amount }) {
  if (!wallet.getWalletClient) throw new Error('Connect your wallet first.');
  let client = await wallet.getWalletClient();
  if ((await getChainId(client)) !== chain.id) {
    client = wallet.switchChain ? await wallet.switchChain(chain.id) : null;
    if (!client || (await getChainId(client)) !== chain.id) throw new Error(`Switch your wallet to ${chain.name} and try again.`);
  }
  const common = { account: client.account, chain: client.chain ?? null };
  return isNativeToken(token)
    ? sendTransaction(client, { ...common, to, value: amount })
    : writeContract(client, { ...common, address: token.address, abi: erc20Abi, functionName: 'transfer', args: [to, amount] });
}

// WalletConnect (scan a QR code with a phone wallet) is always offered: with the store's own Reown project ID
// when one is set, otherwise with LI.FI's default one. MetaMask and Coinbase also get their mobile app flows.
register('evm', (opts) => [
  EthereumProvider({
    walletConnect: opts.walletConnectProjectId ? { projectId: opts.walletConnectProjectId } : true,
    metaMask: true,
    coinbase: true,
    // Same SDK provider the widget makes by default; it also hands over the wallet for transfer().
    sdkProvider: (deps) => {
      Object.assign(wallet, deps);
      return EthereumSdkProvider(deps);
    },
  }),
], transfer);
