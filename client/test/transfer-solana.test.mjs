import test from 'node:test';
import assert from 'node:assert/strict';
import { AccountRole, address } from '@solana/kit';
import {
  associatedTokenAccount,
  createTokenAccountIdempotent,
  systemTransfer,
  transferChecked,
  transferInstructions,
} from '../src/transfer-solana.js';

const TOKEN = address('TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA');
const USDC = 'EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v';
const PAYER = '7xKXtg2CW87d97TXJSDpbD5jBkheTqA83TZRuJosgAsU';
const DEPOSIT = '5tzFkiKscXHK5ZXCGbXZxdw7gTjjD1mBwuoFbhUvuAi9';

test('derives the associated token account as the chain does', async () => {
  // Checked on mainnet: this account exists, holds USDC and belongs to this wallet.
  assert.equal(await associatedTokenAccount(address(DEPOSIT), address(USDC), TOKEN), 'FzbcyEZ9m8xjtergWgWDq7mfPoHEbboBF791B6cTpzbq');
});

test('a SOL transfer is System instruction 2 with the lamports, signed by the payer', () => {
  const ix = systemTransfer(address(PAYER), address(DEPOSIT), 1_500_000_000n);
  assert.equal(ix.programAddress, '11111111111111111111111111111111');
  assert.deepEqual([...ix.data], [2, 0, 0, 0, 0, 0x2f, 0x68, 0x59, 0, 0, 0, 0]);
  assert.deepEqual(ix.accounts, [{ address: PAYER, role: AccountRole.WRITABLE_SIGNER }, { address: DEPOSIT, role: AccountRole.WRITABLE }]);
});

test('a token transfer is TransferChecked (12) with amount and decimals, so a wrong unit fails on chain', () => {
  const ix = transferChecked(address(PAYER), address(USDC), address(DEPOSIT), address(PAYER), 84_000_000n, 6, TOKEN);
  assert.equal(ix.programAddress, TOKEN);
  assert.deepEqual([...ix.data], [12, 0x00, 0xbd, 0x01, 0x05, 0, 0, 0, 0, 6]);
  assert.equal(ix.accounts[3].role, AccountRole.READONLY_SIGNER);
});

test('the deposit token account is made idempotently, paid by the customer', () => {
  const ix = createTokenAccountIdempotent(address(PAYER), address(DEPOSIT), address(DEPOSIT), address(USDC), TOKEN);
  assert.deepEqual([...ix.data], [1]);
  assert.equal(ix.accounts[0].role, AccountRole.WRITABLE_SIGNER);
  assert.equal(ix.accounts[5].address, TOKEN);
});

test('SOL needs one instruction; a token needs the account and the checked transfer, under the mint own program', async () => {
  const rpc = { getAccountInfo: () => ({ send: async () => ({ value: { owner: TOKEN } }) }) };
  assert.equal((await transferInstructions(rpc, { address: '11111111111111111111111111111111' }, PAYER, DEPOSIT, 1n)).length, 1);
  const ixs = await transferInstructions(rpc, { address: USDC, decimals: 6, symbol: 'USDC' }, PAYER, DEPOSIT, 5n);
  assert.deepEqual(ixs.map((i) => [...i.data][0]), [1, 12]);
  assert.equal(ixs[1].accounts[2].address, 'FzbcyEZ9m8xjtergWgWDq7mfPoHEbboBF791B6cTpzbq');
});

test('refuses a mint that no token program owns', async () => {
  const rpc = { getAccountInfo: () => ({ send: async () => ({ value: { owner: '11111111111111111111111111111111' } }) }) };
  await assert.rejects(transferInstructions(rpc, { address: USDC, decimals: 6, symbol: 'USDC' }, PAYER, DEPOSIT, 5n), /not a token/);
});

test('tries a known-good endpoint first, LI.FI next, the official one last, each once', async () => {
  const { rpcUrls } = await import('../src/transfer-solana.js');
  assert.deepEqual(rpcUrls(['https://api.mainnet-beta.solana.com', 'https://solana-rpc.publicnode.com', 'https://other.example']),
    ['https://solana-rpc.publicnode.com', 'https://other.example', 'https://api.mainnet-beta.solana.com']);
  assert.deepEqual(rpcUrls(undefined), ['https://solana-rpc.publicnode.com', 'https://api.mainnet-beta.solana.com']);
});

test('an endpoint answering HTTP 403 is skipped, as the browser saw from the official one', async () => {
  const { failoverRpc } = await import('../src/transfer-solana.js');
  const httpError = Object.assign(new Error('Solana error #8100002'), { context: { __code: 8100002, statusCode: 403 } });
  const calls = [];
  const blocked = { getLatestBlockhash: () => ({ send: async () => { calls.push('blocked'); throw httpError; } }) };
  const good = { getLatestBlockhash: () => ({ send: async () => { calls.push('good'); return { value: 'hash' }; } }) };
  const rpc = failoverRpc([blocked, good]);
  assert.deepEqual(await rpc.getLatestBlockhash().send(), { value: 'hash' });
  assert.deepEqual(calls, ['blocked', 'good']);
});

test('an answer from the chain is final, even an error: never sent to another endpoint', async () => {
  const { failoverRpc } = await import('../src/transfer-solana.js');
  const chainError = Object.assign(new Error('Transaction simulation failed'), { context: { __code: -32002 } });
  let second = false;
  const rpc = failoverRpc([
    { sendTransaction: () => ({ send: async () => { throw chainError; } }) },
    { sendTransaction: () => ({ send: async () => { second = true; return 'sig'; } }) },
  ]);
  await assert.rejects(rpc.sendTransaction('tx').send(), /simulation failed/);
  assert.equal(second, false);
});

test('no endpoint reachable gives a plain message', async () => {
  const { failoverRpc } = await import('../src/transfer-solana.js');
  const down = { getAccountInfo: () => ({ send: async () => { throw new TypeError('Failed to fetch'); } }) };
  await assert.rejects(failoverRpc([down, down]).getAccountInfo('x').send(), /could not be reached/);
});

test('a bare Solana error code is shown in plain words', async () => {
  const { solanaErrorText } = await import('../src/offers.js');
  assert.match(solanaErrorText("Solana error #8100002; Decode this error by running `npx @solana/errors decode -- 8100002 'X19j'`"), /could not be reached/);
  assert.match(solanaErrorText('Solana error #7050001'), /Solana error 7050001/);
  assert.equal(solanaErrorText('You cancelled the transaction in your wallet.'), null);
});
