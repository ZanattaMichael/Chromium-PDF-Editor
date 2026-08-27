// Service worker: routes PDFs into the editor and wires browser UI entry points.
// Document processing happens in the viewer page, which talks to the native
// host directly.

import { probeHost } from './host-client.js';
import { HOST_STATE, hostStateSummary } from './host-install.js';

const VIEWER = chrome.runtime.getURL('src/viewer.html');

function viewerUrlFor(pdfUrl) {
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
  chrome.tabs.update(details.tabId, { url: viewerUrlFor(details.url) });
});

// --- Toolbar button. ---------------------------------------------------------

chrome.action.onClicked.addListener((tab) => {
  const src = tab?.url && looksLikePdfUrl(tab.url) && !isAdobeContext(tab.url) ? tab.url : null;
  chrome.tabs.create({ url: viewerUrlFor(src) });
});

// --- Context menus. -----------------------------------------------------------

chrome.runtime.onInstalled.addListener((details) => {
  chrome.contextMenus.create({
    id: 'open-link-in-editor',
    title: 'Open link in PDF Editor',
    contexts: ['link'],
    targetUrlPatterns: ['*://*/*.pdf*', 'file://*/*.pdf'],
  });
  chrome.contextMenus.create({
    id: 'open-page-in-editor',
    title: 'Open this PDF in PDF Editor',
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

async function checkHost({ openOptionsIfMissing = false } = {}) {
  const probe = await probeHost();
  const ok = probe.state === HOST_STATE.CONNECTED;

  await chrome.action.setBadgeText({ text: ok ? '' : BADGE_MISSING });
  if (!ok) {
    await chrome.action.setBadgeBackgroundColor({ color: '#b3261e' });
  }
  await chrome.action.setTitle({
    title: ok
      ? 'Open PDF Editor'
      : `PDF Editor — ${hostStateSummary(probe.state)} Click for instructions.`,
  });

  if (!ok && openOptionsIfMissing) chrome.runtime.openOptionsPage();
  return probe;
}

// Re-check when the browser starts: the usual fix is "install the host, then restart the browser",
// and this is what clears the badge afterwards without the user having to hunt for a re-test.
chrome.runtime.onStartup.addListener(() => { checkHost(); });

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === 'open-link-in-editor' && info.linkUrl) {
    chrome.tabs.create({ url: viewerUrlFor(info.linkUrl) });
  } else if (info.menuItemId === 'open-page-in-editor') {
    const src = tab?.url && looksLikePdfUrl(tab.url) ? tab.url : null;
    chrome.tabs.create({ url: viewerUrlFor(src) });
  }
});

// --- Messages from the content script overlay and the extension's own pages. ---

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'open-in-editor') {
    chrome.tabs.create({ url: viewerUrlFor(message.url) });
    sendResponse({ ok: true });
    return false;
  }
  // The options page re-tests on demand; let its result clear or restore the badge too.
  if (message?.type === 'recheck-host') {
    checkHost().then(sendResponse);
    return true; // the response is async, so keep the channel open
  }
  return false;
});
