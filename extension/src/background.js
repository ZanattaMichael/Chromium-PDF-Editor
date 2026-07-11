// Service worker: routes PDFs into the editor and wires browser UI entry points.
// Document processing happens in the viewer page, which talks to the native
// host directly.

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

chrome.runtime.onInstalled.addListener(() => {
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
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === 'open-link-in-editor' && info.linkUrl) {
    chrome.tabs.create({ url: viewerUrlFor(info.linkUrl) });
  } else if (info.menuItemId === 'open-page-in-editor') {
    const src = tab?.url && looksLikePdfUrl(tab.url) ? tab.url : null;
    chrome.tabs.create({ url: viewerUrlFor(src) });
  }
});

// --- Messages from the content script overlay. --------------------------------

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'open-in-editor') {
    chrome.tabs.create({ url: viewerUrlFor(message.url) });
    sendResponse({ ok: true });
  }
  return false;
});
