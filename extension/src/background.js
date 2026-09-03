// Service worker: routes PDFs into the editor and wires browser UI entry points.
// Document processing happens in the viewer page, which talks to the native
// host directly.

import { probeHost } from './host-client.js';
import { HOST_STATE, hostStateSummary } from './host-install.js';
import { checkHostVersion, versionStateSummary } from './host-version.js';

const VIEWER = chrome.runtime.getURL('src/viewer.html');

// The viewer loads its `?src=` URL with `fetch()` (viewer.js), which for a file:// URL needs the
// separate "Allow access to file URLs" grant — a different toggle than "Allow in Incognito", and
// one this extension never asks for on its own. Without it the fetch always fails; sending the
// tab there anyway doesn't even fail gracefully everywhere — Brave has been seen blocking the
// whole navigation outright ("ERR_BLOCKED_BY_CLIENT") rather than letting the page load and the
// fetch report the real error. So check first, and never build a src the viewer can't read.
let fileAccessPromise;
function hasFileAccess() {
  fileAccessPromise ??= new Promise((resolve) => chrome.extension.isAllowedFileSchemeAccess(resolve));
  return fileAccessPromise;
}

async function viewerUrlFor(pdfUrl) {
  if (pdfUrl?.startsWith('file:') && !(await hasFileAccess())) pdfUrl = null;
  return pdfUrl ? `${VIEWER}?src=${encodeURIComponent(pdfUrl)}` : VIEWER;
}

function looksLikePdfUrl(rawUrl) {
  try {
    const url = new URL(rawUrl);
    if (!/^https?:|^file:/.test(url.protocol)) return false;
    return url.pathname.toLowerCase().endsWith('.pdf');
  } catch {
    return false;
  }
}

// Requirement: sit on top of the browser's own PDF viewing, but leave Adobe's
// products alone (their Acrobat extension and adobe.com viewers take priority).
function isAdobeContext(rawUrl) {
  try {
    const host = new URL(rawUrl).hostname;
    return host === 'adobe.com' || host.endsWith('.adobe.com');
  } catch {
    return false;
  }
}

// --- Intercept top-level navigations to PDF files (opt-out in options). -----

chrome.webNavigation.onBeforeNavigate.addListener(async (details) => {
  if (details.frameId !== 0) return;
  if (!looksLikePdfUrl(details.url) || isAdobeContext(details.url)) return;
  const { autoOpen } = await chrome.storage.sync.get({ autoOpen: true });
  if (!autoOpen) return;
  // Without file access a file:// tab can't be redirected into a working editor (see
  // viewerUrlFor above) — leave the browser's own PDF view in place rather than swap it for
  // one that's certain to fail or, in Brave, simply won't load at all.
  if (details.url.startsWith('file:') && !(await hasFileAccess())) return;
  chrome.tabs.update(details.tabId, { url: await viewerUrlFor(details.url) });
});

// --- Toolbar button. ---------------------------------------------------------

chrome.action.onClicked.addListener(async (tab) => {
  const src = tab?.url && looksLikePdfUrl(tab.url) && !isAdobeContext(tab.url) ? tab.url : null;
  chrome.tabs.create({ url: await viewerUrlFor(src) });
});

// --- Context menus. -----------------------------------------------------------

chrome.runtime.onInstalled.addListener((details) => {
  chrome.contextMenus.create({
    id: 'open-link-in-editor',
    title: 'Open link in reDACT',
    contexts: ['link'],
    targetUrlPatterns: ['*://*/*.pdf*', 'file://*/*.pdf'],
  });
  chrome.contextMenus.create({
    id: 'open-page-in-editor',
    title: 'Open this PDF in reDACT',
    contexts: ['page'],
  });
  // On a fresh install, take the user straight to the instructions if the host is not there — the
  // extension is inert without it, and finding that out by opening a PDF and watching it fail is a
  // worse first experience than being told up front. Updates stay silent.
  checkHost({ openOptionsIfMissing: details.reason === 'install' });
});

// --- Native host availability. -------------------------------------------------
//
// Everything this extension does happens in the native host, so "is it installed?" is worth
// answering before the user opens a document rather than after. There is no way to ask the
// filesystem, so the check is a real connection attempt; the answer becomes a badge on the toolbar
// icon, which is the only always-visible surface an MV3 extension has.

const BADGE_MISSING = '!';
// A host that answered but is the wrong version gets its own badge rather than the missing one:
// the two need different fixes, and telling someone to install a host they already have sends them
// looking in the wrong place. Distinct text, not just a distinct colour — the badge has to say
// something different to a user who cannot tell red from amber.
const BADGE_STALE = 'v!';

async function checkHost({ openOptionsIfMissing = false } = {}) {
  const probe = await probeHost();
  const connected = probe.state === HOST_STATE.CONNECTED;
  const version = connected
    ? checkHostVersion(probe.version, chrome.runtime.getManifest().version)
    : null;
  // Connected but mismatched is not success: the host answers `ping` and then fails, or silently
  // does nothing, on any action added since it was built.
  const ok = connected && version.ok;

  let badge = '';
  let colour = '';
  let title = 'Open reDACT';
  if (!connected) {
    badge = BADGE_MISSING;
    colour = '#b3261e';
    title = `reDACT — ${hostStateSummary(probe.state)} Click for instructions.`;
  } else if (!ok) {
    badge = BADGE_STALE;
    colour = '#8a6100';
    title = `reDACT — ${versionStateSummary(version.state)} `
      + `Host v${probe.version}, extension v${chrome.runtime.getManifest().version}. `
      + 'Click for instructions.';
  }

  await chrome.action.setBadgeText({ text: badge });
  if (badge) await chrome.action.setBadgeBackgroundColor({ color: colour });
  await chrome.action.setTitle({ title });

  // Only a missing host opens the options page by itself. A version mismatch is worth a badge but
  // not a stolen tab on install: the extension still works for everything the old host supports.
  if (!connected && openOptionsIfMissing) chrome.runtime.openOptionsPage();
  return probe;
}

// Re-check when the browser starts: the usual fix is "install the host, then restart the browser",
// and this is what clears the badge afterwards without the user having to hunt for a re-test.
chrome.runtime.onStartup.addListener(() => { checkHost(); });

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'open-link-in-editor' && info.linkUrl) {
    chrome.tabs.create({ url: await viewerUrlFor(info.linkUrl) });
  } else if (info.menuItemId === 'open-page-in-editor') {
    const src = tab?.url && looksLikePdfUrl(tab.url) ? tab.url : null;
    chrome.tabs.create({ url: await viewerUrlFor(src) });
  }
});

// --- Messages from the content script overlay and the extension's own pages. ---

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'open-in-editor') {
    viewerUrlFor(message.url).then((url) => {
      chrome.tabs.create({ url });
      sendResponse({ ok: true });
    });
    return true; // the response is async, so keep the channel open
  }
  // The options page re-tests on demand; let its result clear or restore the badge too.
  if (message?.type === 'recheck-host') {
    checkHost().then(sendResponse);
    return true; // the response is async, so keep the channel open
  }
  return false;
});
