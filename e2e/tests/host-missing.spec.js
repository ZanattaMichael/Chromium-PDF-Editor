'use strict';

// What a user sees before they have installed anything.
//
// The extension is inert without the native host, and the browser's own words for that are
// "Specified native messaging host not found." — which tells a non-developer nothing. Every page
// that can be opened in that state is supposed to say, in plain English, that the host is not
// installed and what to do about it. That guidance is the one part of the product a broken install
// leaves reachable, so it is worth an end-to-end test of its own rather than unit tests of the
// strings alone: it only works if the probe classifies the failure, the page renders the guide,
// and the service worker badges the toolbar — three separate pieces, in three separate contexts.
//
// This suite launches the browser with *no* host registered anywhere (host: 'none', which also
// refuses to run if a package has left a system-wide manifest behind), so the failure the pages
// react to is the real one, produced by Chromium.

const { test, expect } = require('@playwright/test');
const { launchExtension } = require('../helpers/harness');

/** @type {Awaited<ReturnType<typeof launchExtension>>} */
let ext;

/** Opens Help ▸ Activity console. The pane only renders entries while it is open. */
async function openActivityConsole(page) {
  const trigger = await page.evaluate(
    () => document.getElementById('btn-console').closest('.menu-group')
      .querySelector('.menu-trigger').id);
  await page.click(`#${trigger}`);
  await page.click('#btn-console');
}

test.beforeAll(async () => {
  ext = await launchExtension({ host: 'none' });
  // A fresh install with no host opens the options page by itself (see the onInstalled handler in
  // background.js). Wait it out before opening pages of our own: it navigates whichever blank tab
  // is going spare, which would otherwise interrupt a goto here at random.
  await expect
    .poll(() => ext.context.pages().map((p) => p.url()),
      { message: 'a first install with no host should open the options page by itself' })
    .toContain(ext.optionsUrl);
});

test.afterAll(async () => {
  await ext?.close();
});

test('a first install with no host takes the user straight to the instructions', async () => {
  // Asserted in beforeAll (it has to be waited for there either way); named here so the behaviour
  // is a stated requirement rather than a quirk of the setup. Opening a PDF and watching it fail
  // is a worse first experience than being told up front.
  const options = ext.context.pages().find((p) => p.url() === ext.optionsUrl);
  expect(options).toBeDefined();
  await expect(options.locator('#host-help')).toBeVisible();
});

test('the viewer empty state says the host is not installed, and how to install it', async () => {
  const page = await ext.context.newPage();
  await page.goto(ext.viewerUrl);

  const status = page.locator('#host-status');
  // The exact sentence hostStateSummary() produces for HOST_STATE.MISSING. Asserting the wording
  // and not just "some warning appeared" is the point: this is the text the user has to act on.
  await expect(status).toContainText('The native host is not installed for this browser.');
  // ...and the first concrete step, which on a Linux runner is the distro package command.
  await expect(status).toContainText('sudo apt install');
  await expect(status.locator('button')).toHaveText(/Full install instructions/);

  // The same guidance is written to the activity log, so a log copied or downloaded for a bug
  // report shows what the user was actually told. The pane renders from the store on open, so
  // opening it after the fact is exactly what a user reporting the problem would do.
  await openActivityConsole(page);
  await expect(page.locator('#console-log'))
    .toContainText('The native host is not installed for this browser.');

  await page.close();
});

test('the options page reports the failure and shows the full guide', async () => {
  const page = await ext.context.newPage();
  await page.goto(ext.optionsUrl);

  const status = page.locator('#host-status');
  await expect(status).toHaveClass('bad');
  // The browser's own message, verbatim — it is what a bug report needs.
  await expect(status).toContainText(/✗ .*native messaging host/i);

  const help = page.locator('#host-help');
  await expect(help).toBeVisible();
  await expect(help.locator('#host-help-headline'))
    .toHaveText('The native host is not installed for this browser.');
  // Every step is actionable, and at least one carries a command to copy.
  await expect(help.locator('#host-help-steps > li')).not.toHaveCount(0);
  await expect(help.locator('#host-help-steps pre').first()).toContainText('sudo apt install');
  // The extension's own ID, because the manifest has to allow exactly this one.
  await expect(help.locator('#host-help-id')).toHaveText(ext.extensionId);

  await page.close();
});

test('the toolbar badge flags the missing host', async () => {
  // The badge is the only always-visible surface an MV3 extension has, and it is set from the
  // service worker's own probe — a separate code path from the two pages above.
  await expect.poll(
    () => ext.worker.evaluate(() => chrome.action.getBadgeText({})),
    { message: 'the service worker should badge the toolbar icon when the host is missing' },
  ).toBe('!');

  const title = await ext.worker.evaluate(() => chrome.action.getTitle({}));
  expect(title).toContain('The native host is not installed for this browser.');
});
