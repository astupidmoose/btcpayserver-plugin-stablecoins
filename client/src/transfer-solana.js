// A plain SOL or SPL token transfer from the connected Solana wallet, for NEAR Intents deposits.
import {
  AccountRole,
  address,
  appendTransactionMessageInstructions,
  compileTransaction,
  createSolanaRpc,
  createTransactionMessage,
  getAddressEncoder,
  getBase64Decoder,
  getProgramDerivedAddress,
  getTransactionEncoder,
  pipe,
  setTransactionMessageFeePayer,
  setTransactionMessageLifetimeUsingBlockhash,
} from '@solana/kit';
import { SolanaSignAndSendTransaction, SolanaSignTransaction } from '@solana/wallet-standard-features';
import bs58 from 'bs58';

export const NATIVE_SOL = '11111111111111111111111111111111';
const SYSTEM_PROGRAM = address('11111111111111111111111111111111');
const ATA_PROGRAM = address('ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL');
const TOKEN_PROGRAMS = new Set(['TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA', 'TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb']);
const SOLANA_MAINNET = 'solana:mainnet';
const FALLBACK_RPC = 'https://api.mainnet-beta.solana.com';

function u64(value) {
  const bytes = new Uint8Array(8);
  new DataView(bytes.buffer).setBigUint64(0, BigInt(value), true);
  return bytes;
}

/** System program Transfer (instruction 2): lamports from `from` to `to`. */
export function systemTransfer(from, to, lamports) {
  const data = new Uint8Array(12);
  new DataView(data.buffer).setUint32(0, 2, true);
  data.set(u64(lamports), 4);
  return {
    programAddress: SYSTEM_PROGRAM,
    accounts: [{ address: from, role: AccountRole.WRITABLE_SIGNER }, { address: to, role: AccountRole.WRITABLE }],
    data,
  };
}

/** The associated token account of `owner` for `mint` under `tokenProgram`. */
export async function associatedTokenAccount(owner, mint, tokenProgram) {
  const encoder = getAddressEncoder();
  const [account] = await getProgramDerivedAddress({
    programAddress: ATA_PROGRAM,
    seeds: [encoder.encode(owner), encoder.encode(tokenProgram), encoder.encode(mint)],
  });
  return account;
}

/** Creates `owner`'s token account for `mint` if it does not exist yet (CreateIdempotent), paid by `payer`. */
export function createTokenAccountIdempotent(payer, account, owner, mint, tokenProgram) {
  return {
    programAddress: ATA_PROGRAM,
    accounts: [
      { address: payer, role: AccountRole.WRITABLE_SIGNER },
      { address: account, role: AccountRole.WRITABLE },
      { address: owner, role: AccountRole.READONLY },
      { address: mint, role: AccountRole.READONLY },
      { address: SYSTEM_PROGRAM, role: AccountRole.READONLY },
      { address: tokenProgram, role: AccountRole.READONLY },
    ],
    data: new Uint8Array([1]),
  };
}

/** Token program TransferChecked (instruction 12): checks the mint and its decimals, so a wrong token or unit fails. */
export function transferChecked(source, mint, destination, authority, amount, decimals, tokenProgram) {
  const data = new Uint8Array(10);
  data[0] = 12;
  data.set(u64(amount), 1);
  data[9] = decimals;
  return {
    programAddress: tokenProgram,
    accounts: [
      { address: source, role: AccountRole.WRITABLE },
      { address: mint, role: AccountRole.READONLY },
      { address: destination, role: AccountRole.WRITABLE },
      { address: authority, role: AccountRole.READONLY_SIGNER },
    ],
    data,
  };
}

/**
 * The instructions that send `amount` of the token (LI.FI's address for it) from `from` to `to`: a System transfer for
 * SOL; for a token, the deposit's token account (made if missing, at the payer's cost) and a checked transfer into it.
 */
export async function transferInstructions(rpc, token, from, to, amount) {
  const owner = address(from);
  const destination = address(to);
  if (token.address === NATIVE_SOL) return [systemTransfer(owner, destination, amount)];
  const mint = address(token.address);
  const info = await rpc.getAccountInfo(mint, { encoding: 'base64' }).send();
  const tokenProgram = info?.value?.owner;
  if (!TOKEN_PROGRAMS.has(tokenProgram)) throw new Error(`${token.symbol} is not a token this wallet can send here.`);
  const program = address(tokenProgram);
  const source = await associatedTokenAccount(owner, mint, program);
  const target = await associatedTokenAccount(destination, mint, program);
  return [
    createTokenAccountIdempotent(owner, target, destination, mint, program),
    transferChecked(source, mint, target, owner, amount, token.decimals, program),
  ];
}

/** Sends the transfer with the connected wallet and resolves to its signature. */
export async function solanaTransfer(wallet, { client, chain, token, to, amount, from }) {
  if (!wallet.getWallet) throw new Error('Connect your wallet first.');
  const connected = await wallet.getWallet();
  const account = connected.accounts.find((a) => a.address === from);
  if (!account) throw new Error('Your wallet changed. Reconnect it and try again.');
  const urls = (await client.getRpcUrlsByChainId(chain.id).catch(() => [])) || [];
  const rpc = createSolanaRpc(urls[0] || FALLBACK_RPC);
  const instructions = await transferInstructions(rpc, token, from, to, amount);
  const { value: blockhash } = await rpc.getLatestBlockhash({ commitment: 'confirmed' }).send();
  const message = pipe(
    createTransactionMessage({ version: 0 }),
    (m) => setTransactionMessageFeePayer(address(from), m),
    (m) => setTransactionMessageLifetimeUsingBlockhash(blockhash, m),
    (m) => appendTransactionMessageInstructions(instructions, m),
  );
  const transaction = getTransactionEncoder().encode(compileTransaction(message));
  const features = connected.features;
  // Prefer the wallet sending it itself; otherwise sign there and send it here.
  if (features[SolanaSignAndSendTransaction]) {
    const [output] = await features[SolanaSignAndSendTransaction].signAndSendTransaction({ account, transaction, chain: SOLANA_MAINNET });
    return bs58.encode(output.signature);
  }
  if (!features[SolanaSignTransaction]) throw new Error('This wallet cannot send this payment.');
  const [signed] = await features[SolanaSignTransaction].signTransaction({ account, transaction, chain: SOLANA_MAINNET });
  const encoded = getBase64Decoder().decode(signed.signedTransaction);
  return rpc.sendTransaction(encoded, { encoding: 'base64', preflightCommitment: 'confirmed' }).send();
}
