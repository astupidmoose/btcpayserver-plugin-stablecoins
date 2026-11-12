// Choosing between the prices for the customer's token (LI.FI and NEAR Intents). No React: tested with node --test.

// How LI.FI names each chain family's native coin.
const NATIVE_TOKENS = new Set([
  '0x0000000000000000000000000000000000000000',
  '0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
  '11111111111111111111111111111111',
  'T9yD14Nj9j7xAB4dbGeiX9h8unkKHxuWwb',
]);

export function isNativeToken(token) {
  const a = String(token.address);
  return NATIVE_TOKENS.has(a) || (a.startsWith('0x') && NATIVE_TOKENS.has(a.toLowerCase()));
}

/**
 * The offer to show: the one asking for the least of the token, among those that cover the invoice.
 * LI.FI wins a tie (its route needs no deposit to be made first). Null when neither can pay.
 */
export function cheapest(offers) {
  const usable = offers.filter((o) => o && o.safe);
  if (!usable.length) return null;
  return usable.reduce((best, o) => (o.fromAmount < best.fromAmount ? o : best));
}

/**
 * Whether a NEAR Intents deposit can be sent as the customer saw it: at most `tolerance` thousandths above the amount
 * shown (1005 = 0.5% more), or exactly an amount they were already shown.
 */
export function withinShown(depositAmount, shownAmount, tolerance = 1005n) {
  return depositAmount * 1000n <= shownAmount * tolerance;
}

/**
 * True when an EVM wallet client signs as `from`: the account a NEAR Intents deposit was made for, and refunds to.
 * A wallet that changed accounts meanwhile must not pay it.
 */
export function signsAs(client, from) {
  const signer = client?.account?.address;
  return typeof signer === 'string' && typeof from === 'string' && signer.toLowerCase() === from.toLowerCase();
}
