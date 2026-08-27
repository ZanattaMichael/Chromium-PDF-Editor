'use strict';

// The extension driven against a native host installed by the real Debian package.
//
// Every other test in this repository registers the host itself, into a throwaway browser profile.
// That proves the extension and the host talk to each other, and proves nothing about whether a
// user who installs the .deb ends up with a working editor — which is the failure this project
// actually shipped: a package that installed cleanly, put the host somewhere real, and was then
// never found by the browser, because the manifest was in a directory that browser does not read.
// Nothing about that is visible in a package build; you have to install it and connect.
//
// So this suite installs the package for real (see package-global-setup.js), launches Chromium
// with the extension loaded and *no* host registration of its own, and makes the extension do
// actual work through whatever the package left behind. It removes the package again afterwards.
//
// It works because the extension's manifest carries a "key", which pins its ID to the published
// Web Store one even when loaded unpacked — the same ID the package writes into allowed_origins.
// Without that the two could never match and this test would be impossible.

const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension, REPO_ROOT } = require('../helpers/harness');
const { buildPdf } = require('../helpers/pdf');

const MANIFEST = '/etc/chromium/native-messaging-hosts/com.pdfeditor.host.json';
const LAUNCH_PATH = '/usr/bin/pdf-editor-host';
const INSTALL_DIR = '/opt/pdf-editor-host';

/** @type {Awaited<ReturnType<typeof launchExtension>>} */
let ext;
let fixtureDir;

test.beforeAll(async () => {
  ext = await launchExtension({ host: 'system' });
  fixtureDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-package-'));
});

test.afterAll(async () => {
  await ext?.close();
  if (fixtureDir) fs.rmSync(fixtureDir, { recursive: true, force: true });
});

test('the package pins the host to the ID the browser gives this extension', () => {
  // If these ever diverge, every assertion below fails with "host not found" and no hint as to
  // why, so it is worth naming the mismatch directly.
  const manifest = JSON.parse(fs.readFileSync(MANIFEST, 'utf8'));
  expect(manifest.allowed_origins).toContain(`chrome-extension://${ext.extensionId}/`);
  expect(manifest.path).toBe(LAUNCH_PATH);
});

test('the extension connects to the packaged host and reports it as the packaged one', async () => {
  const page = await ext.context.newPage();
  await page.goto(ext.optionsUrl);

  const status = page.locator('#host-status');
  await expect(status).toHaveClass('ok');
  await expect(status).toContainText('✓ connected');

  // The host that answered has to be the one dpkg installed, not a stray build left in the tree
  // by another suite — otherwise this passes with the package broken.
  await expect(page.locator('#host-diagnostics'))
    .toContainText(`Executable: ${INSTALL_DIR}/`);

  await page.close();
});

test('the packaged host is the version this extension expects', async () => {
  // The version check the extension runs on every connection. A package whose host reports a
  // different feature version than the extension puts a warning in front of the user, so a green
  // "connected" here is also the assertion that Directory.Build.props and extension/manifest.json
  // agree *in the shipped artefact*, not just in the tree.
  const page = await ext.context.newPage();
  await page.goto(ext.optionsUrl);

  const expected = JSON.parse(
    fs.readFileSync(path.join(REPO_ROOT, 'extension', 'manifest.json'), 'utf8')).version;
  const [major, minor] = expected.split('.');

  const status = page.locator('#host-status');
  await expect(status).toContainText(new RegExp(`host v${major}\\.${minor}\\b`));
  // Not the mismatch wording, and no guidance panel — this install is correct.
  await expect(status).not.toContainText('but the host is');
  await expect(page.locator('#host-help')).toBeHidden();

  await page.close();
});

test('the toolbar badge is clear when the package is installed', async () => {
  await expect.poll(
    () => ext.worker.evaluate(() => chrome.action.getBadgeText({})),
    { message: 'a working install should leave the toolbar unbadged' },
  ).toBe('');
});

test('the viewer renders a PDF through the packaged host', async () => {
  // The point of the whole exercise: not "the host answered a ping" but "the user can open a
  // document", which is a real round trip — bytes to the host, a rendered page back.
  const file = path.join(fixtureDir, 'hello.pdf');
  fs.writeFileSync(file, buildPdf([[{ text: 'Installed from the package', x: 72, y: 700 }]]));

  const page = await ext.context.newPage();
  await page.goto(ext.viewerUrl);
  // The empty state must not be telling the user to install anything — the host is right there.
  await expect(page.locator('#host-status')).toContainText('✓ Native host connected.');

  const chooser = page.waitForEvent('filechooser');
  await page.click('#btn-open-empty');
  await (await chooser).setFiles(file);
  await expect(page.locator('.page[data-page="1"] .page-image'))
    .toHaveAttribute('src', /data:image\/png/);

  await page.close();
});
