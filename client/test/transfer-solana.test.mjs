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
