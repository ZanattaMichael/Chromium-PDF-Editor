import { HostClient } from './host-client.js';
import { hostDiagnosticsLines } from './host-diagnostics.js';

const autoOpen = document.getElementById('auto-open');
const status = document.getElementById('host-status');
const diag = document.getElementById('host-diagnostics');
const copyDiag = document.getElementById('copy-diag');
const copyDiagStatus = document.getElementById('copy-diag-status');

{
  const value = await chrome.storage.sync.get({ autoOpen: true });
  autoOpen.checked = value.autoOpen;
}

autoOpen.addEventListener('change', () => {
  chrome.storage.sync.set({ autoOpen: autoOpen.checked });
});

async function test() {
  status.textContent = 'checking…';
  status.className = '';
  diag.textContent = '';
  copyDiag.hidden = true;
  copyDiagStatus.textContent = '';
  try {
    const client = new HostClient();
    const result = await client.call('ping');
    status.textContent = `✓ connected (host v${result.version ?? '?'})`;
    status.className = 'ok';
    // Pull the richer host self-report too. Tolerate an older host that predates the action.
    try {
      const lines = hostDiagnosticsLines(await client.call('diagnostics'));
      diag.textContent = lines.join('\n');
      // Offer a one-click copy only when there's actually a self-report to copy.
      copyDiag.hidden = lines.length === 0;
    } catch { /* host has no 'diagnostics' action — the ping status is enough */ }
  } catch (e) {
    status.textContent = `✗ ${e.message}`;
    status.className = 'bad';
  }
}

// Copies the connection status + host self-report so it can be pasted into a bug report.
copyDiag.addEventListener('click', async () => {
  const text = `${status.textContent}\n${diag.textContent}`.trim();
  try {
    await navigator.clipboard.writeText(text);
    copyDiagStatus.textContent = '✓ copied';
    copyDiagStatus.className = 'ok';
  } catch {
    copyDiagStatus.textContent = '✗ copy failed';
    copyDiagStatus.className = 'bad';
  }
  setTimeout(() => { copyDiagStatus.textContent = ''; }, 2500);
});

document.getElementById('test').addEventListener('click', test);
await test();

// -------------------------------------------------------- Cloudflare scanner
const cfAccount = document.getElementById('cf-account');
const cfToken = document.getElementById('cf-token');
const cfStatus = document.getElementById('cf-status');

{
  const v = await chrome.storage.local.get({ cfAccountId: '', cfApiToken: '' });
  cfAccount.value = v.cfAccountId;
  cfToken.value = v.cfApiToken;
}

document.getElementById('cf-save').addEventListener('click', async () => {
  await chrome.storage.local.set({
    cfAccountId: cfAccount.value.trim(),
    cfApiToken: cfToken.value.trim(),
  });
  cfStatus.textContent = '✓ saved';
  cfStatus.className = 'ok';
  setTimeout(() => { cfStatus.textContent = ''; }, 2500);
});
