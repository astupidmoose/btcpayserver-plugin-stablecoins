import { build } from 'esbuild';

// One bundle per wallet family; the checkout loads only the one for the chosen chain.
for (const family of ['evm', 'solana', 'tron']) {
  await build({
    entryPoints: [`src/${family}.jsx`],
    bundle: true,
    minify: true,
    format: 'iife',
    target: ['es2020'],
    jsx: 'automatic',
    define: { 'process.env.NODE_ENV': '"production"', global: 'globalThis' },
    legalComments: 'none',
    outfile: `../src/BTCPayServer.Plugins.Stablecoins/Resources/js/swappanel-${family}.js`,
    logLevel: 'info',
  });
}
