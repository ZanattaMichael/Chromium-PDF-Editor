// Guards the permission list against creep. Run with:
//   node --test extension/test/manifest-permissions.test.mjs
//
// The Chrome Web Store rejects a version that asks for a permission it does not need, and every
// permission here has to be justified by hand in the dashboard at submission time. That makes an
// unused entry expensive in a way a normal unused declaration is not: it costs a review cycle, and
// the reviewer finds it before we do.
//
// `tabs` is the specific mistake this exists to prevent a second time. It looks required — the
// service worker calls chrome.tabs.create() and chrome.tabs.update(), and reads tab.url — but none
// of that needs it. create/update need no permission at all, and `tabs` only gates the sensitive
// Tab properties (url, title, favIconUrl), which Chrome also populates for any tab matched by a
// host permission. We hold <all_urls>, so tab.url arrives either way; verified in a real browser
// with the permission removed. It was declared for years and never did anything except draw
// scrutiny to the submission.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repoFile = (rel) => readFileSync(fileURLToPath(new URL(`../../${rel}`, import.meta.url)), 'utf8');

const manifest = JSON.parse(repoFile('extension/manifest.json'));

// Each of these is backed by a call site that stops working without it.
const EXPECTED = {
  nativeMessaging: 'chrome.runtime.connectNative in src/host-client.js — the extension does nothing without the host',
  downloads: 'chrome.downloads.download in src/viewer.js — saved PDF, redaction report, activity log, signing certificate',
  storage: 'chrome.storage.sync/local in src/options.js, src/background.js and src/viewer.js',
  webNavigation: 'chrome.webNavigation.onBeforeNavigate in src/background.js — the auto-open-a-PDF feature',
  contextMenus: 'chrome.contextMenus.create in src/background.js — the two right-click entries',
};

test('the manifest declares every permission it needs and nothing else', () => {
  assert.deepEqual(
    [...manifest.permissions].sort(), Object.keys(EXPECTED).sort(),
    'the permission list changed; a new entry needs a call site that fails without it, and a store '
    + 'justification written for it — see docs/CHROME_WEB_STORE.md §4');
});

test('the manifest does not ask for the tabs permission', () => {
  // Called out separately from the list above so the failure names the reason rather than just
  // showing a diff of two arrays.
  assert.ok(
    !manifest.permissions.includes('tabs'),
    'tabs is redundant: chrome.tabs.create/update need no permission, and tab.url is already '
    + 'populated by the <all_urls> host permission. Declaring it only adds review scrutiny.');
});

test('the permissions the store scrutinises most are still the ones we justified', () => {
  // <all_urls> is what the content script needs to spot a PDF on an arbitrary page. If it is ever
  // narrowed, the justification in docs/CHROME_WEB_STORE.md has to be rewritten to match.
  assert.deepEqual(manifest.host_permissions, ['<all_urls>']);
  assert.ok(
    manifest.content_scripts.every((entry) => entry.matches.includes('<all_urls>')),
    'the content script match pattern and the host permission have to tell the same story');
});
