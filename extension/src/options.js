import { HostClient } from './host-client.js';

const autoOpen = document.getElementById('auto-open');
const status = document.getElementById('host-status');

chrome.storage.sync.get({ autoOpen: true }).then((value) => {
  autoOpen.checked = value.autoOpen;
});

autoOpen.addEventListener('change', () => {
  chrome.storage.sync.set({ autoOpen: autoOpen.checked });
});

async function test() {
  status.textContent = 'checking…';
  status.className = '';
  try {
    const result = await new HostClient().call('ping');
    status.textContent = `✓ connected (host v${result.version ?? '?'})`;
    status.className = 'ok';
  } catch (e) {
    status.textContent = `✗ ${e.message}`;
    status.className = 'bad';
  }
}

document.getElementById('test').addEventListener('click', test);
test();

// -------------------------------------------------------- Cloudflare scanner
const cfAccount = document.getElementById('cf-account');
const cfToken = document.getElementById('cf-token');
const cfStatus = document.getElementById('cf-status');

chrome.storage.local.get({ cfAccountId: '', cfApiToken: '' }).then((v) => {
  cfAccount.value = v.cfAccountId;
  cfToken.value = v.cfApiToken;
});

document.getElementById('cf-save').addEventListener('click', async () => {
  await chrome.storage.local.set({
    cfAccountId: cfAccount.value.trim(),
    cfApiToken: cfToken.value.trim(),
  });
  cfStatus.textContent = '✓ saved';
  cfStatus.className = 'ok';
  setTimeout(() => { cfStatus.textContent = ''; }, 2500);
});
