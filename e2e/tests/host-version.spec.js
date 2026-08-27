'use strict';

// What the user sees when the host is installed but is the wrong version.
//
// This is the drift that actually happens: the extension updates itself from the Web Store
// overnight, while the host is an OS package somebody installed by hand months ago. Nothing about
// it announces itself — an old host answers `ping` and looks perfectly healthy, then fails or
// silently no-ops on any action added since it was built. The extension is supposed to name that
// before the user hits it.
//
// The mismatch is produced by making the *extension* claim a version, not by shipping a second
// host: the real, freshly built host answers every message here, and only the version this page
// compares against is overridden. That keeps the probe, the comparison and the rendering all real.

const { test, expect } = require('@playwright/test');
const { launchExtension } = require('../helpers/harness');

/** @type {Awaited<ReturnType<typeof launchExtension>>} */
let ext;

test.beforeAll(async () => {
  ext = await launchExtension();
});

test.afterAll(async () => {
  await ext?.close();
});

/**
 * Makes the page believe this extension is `version`, leaving everything else about the manifest
 * (and every host message) untouched.
 */
async function claimExtensionVersion(page, version) {
  await page.addInitScript((v) => {
    const real = chrome.runtime.getManifest.bind(chrome.runtime);
    chrome.runtime.getManifest = () => ({ ...real(), version: v });
  }, version);
}

test('the options page flags a host older than the extension', async () => {
  const page = await ext.context.newPage();
  await claimExtensionVersion(page, '99.0.0');
  await page.goto(ext.optionsUrl);

  const status = page.locator('#host-status');
  // Warned, not failed: the host answered, so this is neither the ok-green nor the bad-red state.
  await expect(status).toHaveClass('warn');
  await expect(status).toContainText('connected, but the host is v');
  await expect(status).toContainText('this extension is v99.0.0');

  // The same guidance panel a missing host gets, because the fix is the same install.
  const help = page.locator('#host-help');
  await expect(help).toBeVisible();
  await expect(help.locator('#host-help-headline'))
    .toHaveText('The native host is older than this extension.');
  await expect(help.locator('#host-help-steps > li')).not.toHaveCount(0);

  await page.close();
});

test('the options page flags an extension older than the host, with the other fix', async () => {
  const page = await ext.context.newPage();
  await claimExtensionVersion(page, '0.1.0');
  await page.goto(ext.optionsUrl);

  await expect(page.locator('#host-status')).toHaveClass('warn');
  await expect(page.locator('#host-help-headline'))
    .toHaveText('The native host is newer than this extension.');
  // Reinstalling the host is the wrong fix here; the extension is the stale half.
  await expect(page.locator('#host-help-steps')).toContainText('chrome://extensions');
  await expect(page.locator('#host-help-steps')).not.toContainText('sudo apt install');

  await page.close();
});

test('a matching host is reported as plainly connected', async () => {
  // The negative case, and the one that matters most: a correct install must not be warned about.
  const page = await ext.context.newPage();
  await page.goto(ext.optionsUrl);

  await expect(page.locator('#host-status')).toHaveClass('ok');
  await expect(page.locator('#host-status')).toContainText('✓ connected (host v');
  await expect(page.locator('#host-help')).toBeHidden();

  await page.close();
});

test('the viewer empty state warns instead of showing a bare tick', async () => {
  const page = await ext.context.newPage();
  await claimExtensionVersion(page, '99.0.0');
  await page.goto(ext.viewerUrl);

  const status = page.locator('#host-status');
  await expect(status).toContainText('The native host is older than this extension.');
  await expect(status).not.toContainText('✓ Native host connected.');
  await expect(status.locator('button')).toHaveText(/Full update instructions/);

  await page.close();
});

test('the toolbar badge distinguishes a mismatched host from a missing one', async () => {
  // The third surface, and a separate code path: the service worker runs its own probe. Its badge
  // has to say something different from the missing-host "!", because "install the host" sends a
  // user who already has one looking in the wrong place.
  //
  // Kept last, and undone afterwards, because the override lives in the worker for the rest of
  // this context's life rather than in a single page.
  await ext.worker.evaluate(() => {
    globalThis.__realGetManifest = chrome.runtime.getManifest.bind(chrome.runtime);
    chrome.runtime.getManifest = () => ({ ...globalThis.__realGetManifest(), version: '99.0.0' });
  });
  try {
    const page = await ext.context.newPage();
    await page.goto(ext.optionsUrl);
    // The options page asks the worker to re-probe once its own check has run.
    await expect.poll(
      () => ext.worker.evaluate(() => chrome.action.getBadgeText({})),
      { message: 'a version mismatch should get its own badge, not the missing-host one' },
    ).toBe('v!');

    const title = await ext.worker.evaluate(() => chrome.action.getTitle({}));
    expect(title).toContain('The native host is older than this extension.');
    await page.close();
  } finally {
    await ext.worker.evaluate(() => { chrome.runtime.getManifest = globalThis.__realGetManifest; });
  }
});
