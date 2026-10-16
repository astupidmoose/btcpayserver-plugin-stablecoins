import { build } from 'esbuild';

await build({
  entryPoints: ['src/index.jsx'],
  bundle: true,
  minify: true,
  format: 'iife',
  target: ['es2020'],
  jsx: 'automatic',
  define: { 'process.env.NODE_ENV': '"production"', global: 'globalThis' },
  legalComments: 'none',
  outfile: '../src/BTCPayServer.Plugins.Stablecoins/Resources/js/swappanel.js',
  logLevel: 'info',
});
