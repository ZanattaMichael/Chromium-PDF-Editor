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
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension, HOST_MANIFEST, REPO_ROOT } = require('../helpers/harness');
const { buildPdf } = require('../helpers/pdf');

const LAUNCH_PATH = '/usr/bin/pdf-editor-host';
const INSTALL_DIR = '/opt/pdf-editor-host';
const REGISTER_PATH = '/usr/bin/pdf-editor-host-register';

// Paths flatpak refuses to share into a sandbox, because the sandbox supplies its own. Kept in
// sync with FLATPAK_RESERVED_PREFIXES in scripts/register-host.sh.
const FLATPAK_RESERVED_PREFIXES =
  ['/usr/', '/etc/', '/app/', '/dev/', '/proc/', '/run/flatpak/', '/run/host/'];

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
  //
  // Read from the directory *this* browser reads, not a directory the package happens to write:
  // which one that is is compiled into the binary and differs between Chromium, Chrome and the
  // Chrome for Testing build Playwright downloads (see systemManifestDirs in helpers/harness.js).
  const manifests = ext.systemManifestDirs
    .map((dir) => path.join(dir, HOST_MANIFEST))
    .filter((file) => fs.existsSync(file));
  expect(manifests, `the package registered nothing in ${ext.systemManifestDirs.join(', ')}`)
    .not.toHaveLength(0);

  for (const file of manifests) {
    const manifest = JSON.parse(fs.readFileSync(file, 'utf8'));
    expect(manifest.allowed_origins, file).toContain(`chrome-extension://${ext.extensionId}/`);
    expect(manifest.path, file).toBe(LAUNCH_PATH);
  }
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

test('the register helper gives a flatpak browser a path flatpak will actually share', () => {
  // LAUNCH_PATH is the right answer for every other browser — a sandboxed browser is likelier to be
  // permitted to exec /usr/bin than /opt — and the wrong one here. Flatpak reserves /usr: the
  // sandbox's /usr is the runtime's, `flatpak override --filesystem=/usr/bin/pdf-editor-host` is
  // refused outright ("Path \"/usr\" is reserved by Flatpak"), and even --filesystem=host mounts
  // the host's /usr at /run/host/usr. A manifest naming LAUNCH_PATH points a flatpak browser at a
  // file it does not have, and the browser reports it exactly like a host that was never installed.
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-flatpak-'));
  try {
    // The helper only writes for sandboxes that exist, so give it one to find.
    fs.mkdirSync(path.join(home, '.var', 'app', 'com.google.Chrome'), { recursive: true });
    const listing = execFileSync(REGISTER_PATH, ['--list'], {
      encoding: 'utf8',
      env: { ...process.env, HOME: home, XDG_CONFIG_HOME: path.join(home, '.config') },
    });

    // `--list` prints "<manifest> -> <host path>" per target.
    const flatpakLines = listing.split('\n').filter((line) => line.includes('/.var/app/'));
    expect(flatpakLines, `no flatpak target in:\n${listing}`).not.toHaveLength(0);

    for (const line of flatpakLines) {
      const hostPath = line.split('->').pop().trim();
      expect(hostPath, line).toBe(`${INSTALL_DIR}/PdfEditor.NativeHost`);
      for (const reserved of FLATPAK_RESERVED_PREFIXES) {
        expect(hostPath.startsWith(reserved), `${hostPath} is under flatpak-reserved ${reserved}`)
          .toBe(false);
      }
    }

    // And the non-sandboxed browsers keep the symlink, which is what they should be given.
    const plainLines = listing.split('\n')
      .filter((line) => line.includes('/.config/') && line.includes('->'));
    expect(plainLines, `no plain target in:\n${listing}`).not.toHaveLength(0);
    for (const line of plainLines) {
      expect(line.split('->').pop().trim(), line).toBe(LAUNCH_PATH);
    }
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});
