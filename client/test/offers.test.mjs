import test from 'node:test';
import assert from 'node:assert/strict';
import { cheapest, isNativeToken, withinShown } from '../src/offers.js';

const lifi = (amount, safe = true) => ({ route: 'lifi', fromAmount: amount, safe });
const near = (amount) => ({ route: 'near', fromAmount: amount, safe: true });

test('shows whichever asks for less of the token', () => {
  assert.equal(cheapest([lifi(1000n), near(900n)]).route, 'near');
  assert.equal(cheapest([lifi(900n), near(1000n)]).route, 'lifi');
});

test('LI.FI wins a tie', () => {
  assert.equal(cheapest([lifi(1000n), near(1000n)]).route, 'lifi');
});

test('NEAR Intents is the backup when LI.FI has no route, or only one that could deliver too little', () => {
  assert.equal(cheapest([null, near(1000n)]).route, 'near');
  assert.equal(cheapest([lifi(500n, false), near(1000n)]).route, 'near');
});

test('nothing to pay with when neither can', () => {
  assert.equal(cheapest([null, null]), null);
  assert.equal(cheapest([lifi(500n, false), null]), null);
});

test('knows every chain family native coin as LI.FI names it', () => {
  for (const address of ['0x0000000000000000000000000000000000000000', '0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE',
    '11111111111111111111111111111111', 'T9yD14Nj9j7xAB4dbGeiX9h8unkKHxuWwb']) {
    assert.ok(isNativeToken({ address }), address);
  }
  for (const address of ['EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v', 'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t',
    '0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48', 't9yd14nj9j7xab4dbgeix9h8unkkhxuwwb']) {
    assert.ok(!isNativeToken({ address }), address);
  }
});

test('a deposit is sent as shown only within 0.5% above the amount shown', () => {
  assert.ok(withinShown(1000n, 1000n));
  assert.ok(withinShown(1005n, 1000n));
  assert.ok(!withinShown(1006n, 1000n));
  assert.ok(withinShown(999n, 1000n));
});
