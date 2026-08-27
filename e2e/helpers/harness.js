'use strict';

const { chromium } = require('@playwright/test');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const EXTENSION_DIR = path.join(REPO_ROOT, 'extension');
const HOST_DLL = path.join(
  REPO_ROOT, 'src', 'PdfEditor.NativeHost', 'bin', 'Release', 'net8.0', 'PdfEditor.NativeHost.dll');

/**
 * Generates the extension's icons if they are not already there (idempotent). Separate from
 * buildPrerequisites because loading the extension needs them but the package-install suite gets
 * its host from a .deb and has no use for a local build.
 */
function ensureIcons() {
  if (!fs.existsSync(path.join(EXTENSION_DIR, 'icons', 'icon128.png'))) {
    execFileSync('python3', [path.join(REPO_ROOT, 'scripts', 'generate-icons.py')], { stdio: 'inherit' });
  }
}

/** Builds the native host and generates the extension icons (idempotent). */
function buildPrerequisites() {
  execFileSync('dotnet', ['build', path.join(REPO_ROOT, 'src', 'PdfEditor.NativeHost'),
    '-c', 'Release', '--nologo', '-v', 'q'], { stdio: 'inherit' });
  ensureIcons();
}

// The system-wide manifest directories a Chromium build actually reads on Linux. Playwright ships
// an unbranded Chromium, which reads /etc/chromium; /etc/opt/chrome is what a branded Chrome reads
// and is listed so an installed package is detected whichever binary the run picked up. These are
// two of the directories the .deb/.rpm/Arch packages write to (scripts/linux-manifest-dirs.sh).
const SYSTEM_MANIFEST_DIRS = [
  '/etc/chromium/native-messaging-hosts',
  '/etc/opt/chrome/native-messaging-hosts',
];

/** Paths of any system-wide (package-installed) registrations of our host. */
function systemHostManifests() {
  return SYSTEM_MANIFEST_DIRS
    .map((dir) => path.join(dir, 'com.pdfeditor.host.json'))
    .filter((file) => fs.existsSync(file));
}

/**
 * Launches Chromium with the extension loaded in a fresh profile, and registers the native
 * messaging host the way the caller asks for, so chrome.runtime.connectNative spawns the actual
 * .NET host — the complete production pipeline.
 *
 * @param {object} [opts]
 * @param {'profile'|'none'|'system'} [opts.host='profile'] Where the host registration comes from.
 *   - 'profile' writes a manifest into this throwaway profile pointing at the freshly built host.
 *     The default: self-contained, needs no install, and is what the functional suite uses.
 *   - 'none' registers nothing, which is the state a user is in before they install anything —
 *     the case the extension's install guidance exists for.
 *   - 'system' registers nothing here either, because the package under test has already put a
 *     manifest in /etc; writing one into the profile would shadow the very registration being
 *     tested and the suite would pass with the package uninstalled.
 *
 *   'none' and 'system' each assert their precondition, because both fail *silently* otherwise:
 *   a leftover installed package turns "no host" into a connected one, and a missing package
 *   turns the package suite into a test of nothing.
 */
async function launchExtension({ host: hostSource = 'profile' } = {}) {
  const systemManifests = systemHostManifests();
  if (hostSource === 'none' && systemManifests.length > 0) {
    throw new Error(
      'This suite needs a browser with no native host registered anywhere, but the host is '
      + `installed system-wide (${systemManifests.join(', ')}). Remove the package first:\n`
      + '    sudo dpkg -r pdf-editor-host');
  }
  if (hostSource === 'system' && systemManifests.length === 0) {
    throw new Error(
      'This suite tests the host as the OS package registers it, but no system-wide manifest is '
      + `present (looked in ${SYSTEM_MANIFEST_DIRS.join(', ')}). Install the package first — the `
      + 'package suite\'s global setup does this for you.');
  }

  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-e2e-'));

  // Prefer the container's pre-provisioned Chromium; fall back to Playwright's own.
  const preinstalled = '/opt/pw-browsers/chromium';
  const executablePath = process.env.PDF_EDITOR_CHROMIUM
    ?? (fs.existsSync(preinstalled) ? preinstalled : undefined);

  const launchOptions = {
    headless: true,
    args: [
      `--disable-extensions-except=${EXTENSION_DIR}`,
      `--load-extension=${EXTENSION_DIR}`,
    ],
  };
  if (executablePath) {
    launchOptions.executablePath = executablePath;
  } else {
    // Extensions need the full browser in new-headless mode; plain headless:true
    // would pick the headless shell, which silently ignores --load-extension.
    launchOptions.channel = 'chromium';
  }
  const context = await chromium.launchPersistentContext(userDataDir, launchOptions);

  // The extension ID is the host of any of its pages/workers.
  let [worker] = context.serviceWorkers();
  if (!worker) worker = await context.waitForEvent('serviceworker', { timeout: 30_000 });
  const extensionId = new URL(worker.url()).host;

  // Native messaging host manifests are looked up under <user-data-dir>/NativeMessagingHosts
  // at connect time, so registering after launch is fine.
  if (hostSource === 'profile') {
    const launcher = path.join(userDataDir, 'pdf-editor-host.sh');
    fs.writeFileSync(launcher, `#!/usr/bin/env bash\nexec dotnet "${HOST_DLL}" "$@"\n`, { mode: 0o755 });
    const hostsDir = path.join(userDataDir, 'NativeMessagingHosts');
    fs.mkdirSync(hostsDir, { recursive: true });
    fs.writeFileSync(path.join(hostsDir, 'com.pdfeditor.host.json'), JSON.stringify({
      name: 'com.pdfeditor.host',
      description: 'PDF Editor native messaging host (e2e)',
      path: launcher,
      type: 'stdio',
      allowed_origins: [`chrome-extension://${extensionId}/`],
    }, null, 2));
  }

  return {
    context,
    extensionId,
    userDataDir,
    // A getter, not the worker captured above: an MV3 service worker is torn down when it goes
    // idle and comes back as a *new* object, and evaluating on the dead one throws.
    get worker() { return context.serviceWorkers()[0] ?? worker; },
    viewerUrl: `chrome-extension://${extensionId}/src/viewer.html`,
    optionsUrl: `chrome-extension://${extensionId}/src/options.html`,
    async close() {
      await context.close();
      fs.rmSync(userDataDir, { recursive: true, force: true });
    },
  };
}

module.exports = {
  buildPrerequisites, ensureIcons, launchExtension, systemHostManifests, REPO_ROOT,
};
