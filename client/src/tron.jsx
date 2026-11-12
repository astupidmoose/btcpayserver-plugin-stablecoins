import { TronProvider } from '@lifi/widget-provider-tron';
import { TronProvider as TronSdkProvider } from '@lifi/sdk-provider-tron';
import { TronWeb } from 'tronweb';
import { isNativeToken, register } from './core.jsx';

// The wallet LI.FI's provider uses, kept for plain transfers (NEAR Intents deposits). LI.FI routes run exactly as before.
const wallet = {};
const TRONGRID = 'https://api.trongrid.io';
// The most energy a TRC-20 transfer may burn, in sun (100 TRX). A plain transfer uses far less.
const FEE_LIMIT = 100_000_000;

/** Sends `amount` of TRX or a TRC-20 token from the connected wallet to `to`; resolves to the transaction id. */
async function transfer({ token, to, amount, from }) {
  if (!wallet.getWallet) throw new Error('Connect your wallet first.');
  const adapter = await wallet.getWallet();
  // Only from the account the deposit was made for (and refunds to): Tron addresses are case-sensitive.
  if (adapter.address !== from) throw new Error('Your wallet changed accounts. Reconnect it and try again.');
  const tronWeb = new TronWeb({ fullHost: TRONGRID });
  let unsigned;
  if (isNativeToken(token)) {
    unsigned = await tronWeb.transactionBuilder.sendTrx(to, Number(amount), from);
  } else {
    const built = await tronWeb.transactionBuilder.triggerSmartContract(token.address, 'transfer(address,uint256)', { feeLimit: FEE_LIMIT },
      [{ type: 'address', value: to }, { type: 'uint256', value: amount.toString() }], from);
    if (!built?.result?.result || !built.transaction) throw new Error('This payment could not be prepared. Please try again.');
    unsigned = built.transaction;
  }
  const signed = await adapter.signTransaction(unsigned);
  const sent = await tronWeb.trx.sendRawTransaction(signed);
  if (!sent?.result) {
    const reason = sent?.message ? tronWeb.toUtf8(sent.message) : '';
    throw new Error(`The Tron network refused the payment${reason ? `: ${reason}` : ''}.`);
  }
  return sent.txid || signed.txID;
}

// WalletConnect lets phone wallets (TronLink, Trust...) connect by QR code.
register('tron', () => [
  TronProvider({
    walletConnect: true,
    // Same SDK provider the widget makes by default; it also hands over the wallet for transfer().
    sdkProvider: (deps) => {
      Object.assign(wallet, deps);
      return TronSdkProvider({ ...deps, multicallBatchSize: 40 });
    },
  }),
], transfer);
