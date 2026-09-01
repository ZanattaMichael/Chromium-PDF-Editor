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

const HOST_MANIFEST = 'com.pdfeditor.host.json';

/**
 * The browser this suite drives, as a path — the same one launchExtension() hands Playwright when
 * it can, because the tests below need to inspect the binary and not just run it.
 *
 * Returns null when the browser is left to Playwright to resolve and none of its downloads can be
 * found, which is not fatal: only the system-host checks need the path.
 */
function browserExecutablePath() {
  const explicit = process.env.PDF_EDITOR_CHROMIUM;
  if (explicit) return explicit;
  // The container images this repo is developed in ship a browser here.
  const preinstalled = '/opt/pw-browsers/chromium';
  if (fs.existsSync(preinstalled)) return preinstalled;
  // Playwright's own download. The directory inside differs between the Chromium builds it used
  // to ship (chrome-linux/) and the Chrome for Testing builds it ships now (chrome-linux64/).
  const cache = process.env.PLAYWRIGHT_BROWSERS_PATH
    || path.join(os.homedir(), '.cache', 'ms-playwright');
  if (!fs.existsSync(cache)) return null;
  // Newest revision first: an image that has accumulated several keeps the one Playwright would
  // resolve today. 'chromium_headless_shell-*' is excluded by the prefix — it ignores extensions.
  const revisions = fs.readdirSync(cache)
    .filter((d) => /^chromium-\d+$/.test(d))
    .sort((a, b) => Number(b.split('-')[1]) - Number(a.split('-')[1]));
  for (const entry of revisions) {
    for (const layout of ['chrome-linux64', 'chrome-linux']) {
      const exe = path.join(cache, entry, layout, 'chrome');
      if (fs.existsSync(exe)) return exe;
    }
  }
  return null;
}

/**
 * The system-wide manifest directory a particular browser binary reads, read out of the binary.
 *
 * It has to be discovered rather than assumed, because it is compiled in and differs per build
 * (chrome/common/chrome_paths.cc): branded Chrome reads /etc/opt/chrome, Chrome for Testing —
 * which is what Playwright downloads — reads /etc/opt/chrome_for_testing, and an unbranded
 * Chromium reads /etc/chromium. Assuming the wrong one is invisible from the outside: the browser
 * answers "Specified native messaging host not found", exactly as it would for a package that was
 * never installed. That is precisely how this suite came to pass against a developer's Chromium
 * and fail against CI's Chrome for Testing.
 *
 * Falls back to the directories the common browsers read if the binary cannot be scanned, so a
 * platform where this trick does not work degrades to the old assumption rather than failing.
 */
const FALLBACK_MANIFEST_DIRS = [
  '/etc/chromium/native-messaging-hosts',
  '/etc/opt/chrome/native-messaging-hosts',
  '/etc/opt/chrome_for_testing/native-messaging-hosts',
];

const MANIFEST_DIR_PATTERN = /\/etc\/[A-Za-z0-9_./-]*native-messaging-hosts/g;

// Scanning a few hundred MB takes a few seconds, and the answer cannot change while the tests run.
const scannedDirs = new Map();

function systemManifestDirs(executable = browserExecutablePath()) {
  if (!executable || !fs.existsSync(executable)) return FALLBACK_MANIFEST_DIRS;
  const cached = scannedDirs.get(executable);
  if (cached) return cached;
  // Read in chunks: the binary is a few hundred MB, and the string is a plain literal in it.
  // Consecutive chunks overlap so a path split across a boundary is still matched.
  const CHUNK = 8 << 20;
  const OVERLAP = 256;
  const found = new Set();
  const fd = fs.openSync(executable, 'r');
  try {
    const buf = Buffer.alloc(CHUNK);
    let carry = '';
    for (;;) {
      const read = fs.readSync(fd, buf, 0, CHUNK, null);
      if (read <= 0) break;
      const text = carry + buf.toString('latin1', 0, read);
      for (const [match] of text.matchAll(MANIFEST_DIR_PATTERN)) found.add(match);
      carry = text.slice(-OVERLAP);
    }
  } finally {
    fs.closeSync(fd);
  }
  const dirs = found.size > 0 ? [...found] : FALLBACK_MANIFEST_DIRS;
  scannedDirs.set(executable, dirs);
  return dirs;
}

/** Paths of any system-wide (package-installed) registrations of our host. */
function systemHostManifests() {
  return [...new Set([...systemManifestDirs(), ...FALLBACK_MANIFEST_DIRS])]
    .map((dir) => path.join(dir, HOST_MANIFEST))
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
async function launchExtension({ host: hostSource = 'profile', viewport } = {}) {
  // Only the two host sources that depend on what is installed system-wide look; the default
  // registers its own host into the profile and would just be paying for the scan.
  const systemManifests = hostSource === 'profile' ? [] : systemHostManifests();
  if (hostSource === 'none' && systemManifests.length > 0) {
    throw new Error(
      'This suite needs a browser with no native host registered anywhere, but the host is '
      + `installed system-wide (${systemManifests.join(', ')}). Remove the package first:\n`
      + '    sudo dpkg -r pdf-editor-host');
  }
  const browserDirs = hostSource === 'profile' ? FALLBACK_MANIFEST_DIRS : systemManifestDirs();
  if (hostSource === 'system' && systemManifests.length === 0) {
    throw new Error(
      'This suite tests the host as the OS package registers it, but no system-wide manifest is '
      + `present (looked in ${browserDirs.join(', ')}). Install the package first — the `
      + 'package suite\'s global setup does this for you.');
  }
  // Being installed somewhere is not enough: it has to be installed where *this* browser looks.
  // Without this the whole suite fails four tests deep with "native messaging host not found",
  // which reads as a broken package rather than a browser the packages do not register for.
  if (hostSource === 'system' && !browserDirs.some(
    (dir) => fs.existsSync(path.join(dir, HOST_MANIFEST)))) {
    throw new Error(
      `The package is installed (${systemManifests.join(', ')}), but ${browserExecutablePath()} `
      + `reads ${browserDirs.join(', ')}, where it did not register. Add that directory to `
      + 'scripts/linux-manifest-dirs.sh if the browsers it belongs to should be supported, or '
      + 'point PDF_EDITOR_CHROMIUM at a browser the packages do register for.');
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
  // Undefined leaves Playwright's own context-level default for every caller except
  // doc-shots.js. Note this is only the *context* default: a page that later calls
  // page.setViewportSize() (as doc-shots.js's openViewer() does) overrides it per-page.
  if (viewport) {
    launchOptions.viewport = viewport;
  }
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
    // Where this browser reads its system-wide registrations, so the package suite can assert
    // against the manifest the browser will actually open rather than one it hopes it reads.
    systemManifestDirs: browserDirs,
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
  browserExecutablePath, buildPrerequisites, ensureIcons, launchExtension, systemHostManifests,
  systemManifestDirs, HOST_MANIFEST, REPO_ROOT,
};
