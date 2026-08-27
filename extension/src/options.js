import { HostClient, probeHost } from './host-client.js';
import { hostDiagnosticsLines } from './host-diagnostics.js';
import {
  HOST_STATE, detectPlatform, hostInstallGuide, hostInstallGuideLines,
} from './host-install.js';

const autoOpen = document.getElementById('auto-open');
const status = document.getElementById('host-status');
const diag = document.getElementById('host-diagnostics');
const copyDiag = document.getElementById('copy-diag');
const copyDiagStatus = document.getElementById('copy-diag-status');
const help = document.getElementById('host-help');

// The guide currently on screen (null when connected), so "Copy diagnostics" carries the same
// advice the user is looking at into whatever bug report they paste it into.
let currentGuide = null;

{
  const value = await chrome.storage.sync.get({ autoOpen: true });
  autoOpen.checked = value.autoOpen;
}

autoOpen.addEventListener('change', () => {
  chrome.storage.sync.set({ autoOpen: autoOpen.checked });
});

/**
 * Renders one guide step as a list item: the instruction, and — when the step has a command — a
 * <pre> holding it plus a button that copies it. Everything is a text node; nothing here builds
 * HTML from a string (#74).
 */
function stepItem(step) {
  const li = document.createElement('li');
  li.appendChild(document.createTextNode(step.text));
  if (!step.code) return li;

  const pre = document.createElement('pre');
  pre.textContent = step.code;
  li.appendChild(pre);

  const copy = document.createElement('button');
  copy.className = 'step-copy';
  copy.type = 'button';
  copy.textContent = 'Copy command';
  copy.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(step.code);
      copy.textContent = '✓ copied';
    } catch {
      copy.textContent = '✗ copy failed';
    }
    setTimeout(() => { copy.textContent = 'Copy command'; }, 2000);
  });
  li.appendChild(copy);
  return li;
}

/** Shows (or hides, when connected) the install guidance for a probe result. */
function renderHelp(guide) {
  currentGuide = guide;
  if (!guide || guide.state === HOST_STATE.CONNECTED) {
    help.hidden = true;
    return;
  }
  document.getElementById('host-help-headline').textContent = guide.headline;
  document.getElementById('host-help-detail').textContent = guide.detail;
  document.getElementById('host-help-id').textContent = chrome.runtime.id;

  const steps = document.getElementById('host-help-steps');
  steps.replaceChildren(...guide.steps.map(stepItem));

  const errorLine = document.getElementById('host-help-error');
  errorLine.textContent = guide.error ? `Browser reported: ${guide.error}` : '';
  errorLine.hidden = !guide.error;

  help.hidden = false;
}

async function test() {
  status.textContent = 'checking…';
  status.className = '';
  diag.textContent = '';
  copyDiag.hidden = true;
  copyDiagStatus.textContent = '';
  help.hidden = true;

  const client = new HostClient();
  const probe = await probeHost(client);

  if (probe.state === HOST_STATE.CONNECTED) {
    status.textContent = `✓ connected (host v${probe.version ?? '?'})`;
    status.className = 'ok';
    renderHelp(null);
    // Pull the richer host self-report too. Tolerate an older host that predates the action.
    try {
      diag.textContent = hostDiagnosticsLines(await client.call('diagnostics')).join('\n');
    } catch { /* host has no 'diagnostics' action — the ping status is enough */ }
  } else {
    // Show what the state means, not just what the browser said: "Specified native messaging host
    // not found" is the same message whether the package was never installed or the browser reads
    // a directory it was not written to, and the fixes differ.
    status.textContent = `✗ ${probe.error ?? 'could not reach the native host'}`;
    status.className = 'bad';
    renderHelp(hostInstallGuide({
      state: probe.state,
      platform: detectPlatform(navigator.userAgent),
      extensionId: chrome.runtime.id,
      error: probe.error,
    }));
  }

  // Always offer the copy once the check has run — the status line is worth copying into a bug
  // report even when the host is disconnected (that's exactly when diagnostics matter most).
  copyDiag.hidden = false;

  // Let the service worker refresh the toolbar badge from its own probe. Fire-and-forget: the
  // badge is a convenience, and the worker may be asleep with no listener yet registered.
  chrome.runtime.sendMessage({ type: 'recheck-host' }).catch(() => {});
}

// Copies the environment + connection status + host self-report so it can be pasted into a bug
// report. Includes the extension version and browser even when the host is disconnected.
copyDiag.addEventListener('click', async () => {
  const { version } = chrome.runtime.getManifest();
  const text = [
    `Extension: v${version} (${chrome.runtime.id})`,
    `Browser: ${navigator.userAgent}`,
    `Host: ${status.textContent}`,
    diag.textContent,
    ...hostInstallGuideLines(currentGuide),
  ].join('\n').trim();
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
