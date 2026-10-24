// Fails the build on a high or critical advisory in a runtime dependency, except the ones accepted below.
import { execSync } from 'node:child_process';

// Each accepted advisory says why it does not apply to the payment panel.
const ACCEPTED = {
  // zod 3 (needed by viem, wagmi and Reown) has no fixed release. The panel only validates data from
  // LI.FI and the customer's own wallet, in the customer's browser: the most it can do is slow that tab.
  'GHSA-mrhx-c7xc-m3w5': 'zod memory use on oversized input, browser-only',
};

let report;
try {
  report = execSync('npm audit --omit=dev --json', { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
} catch (e) {
  report = e.stdout; // npm audit exits non-zero when it finds anything
}
let parsed;
try {
  parsed = JSON.parse(report);
} catch {
  parsed = null;
}
// npm audit also exits non-zero when it cannot run (registry down, auth): that must fail the build too.
if (!parsed || parsed.error || typeof parsed.vulnerabilities !== 'object') {
  console.error('npm audit did not return a report:', parsed?.error?.summary ?? report?.slice(0, 500));
  process.exit(1);
}
const advisories = Object.values(parsed.vulnerabilities)
  .flatMap((v) => v.via.filter((x) => typeof x === 'object'))
  .filter((a) => a.severity === 'high' || a.severity === 'critical')
  .filter((a) => !Object.keys(ACCEPTED).some((id) => a.url?.endsWith(id)));
const unique = [...new Map(advisories.map((a) => [a.url, a])).values()];
for (const a of unique) console.error(`${a.severity}: ${a.name} ${a.range}: ${a.title} ${a.url}`);
if (unique.length > 0) process.exit(1);
console.log('npm audit: no unaccepted high or critical advisories');
