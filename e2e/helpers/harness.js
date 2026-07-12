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

/** Builds the native host and generates the extension icons (idempotent). */
function buildPrerequisites() {
  execFileSync('dotnet', ['build', path.join(REPO_ROOT, 'src', 'PdfEditor.NativeHost'),
    '-c', 'Release', '--nologo', '-v', 'q'], { stdio: 'inherit' });
  if (!fs.existsSync(path.join(EXTENSION_DIR, 'icons', 'icon128.png'))) {
    execFileSync('python3', [path.join(REPO_ROOT, 'scripts', 'generate-icons.py')], { stdio: 'inherit' });
  }
}

/**
 * Launches Chromium with the extension loaded in a fresh profile, then registers
 * the real native messaging host inside that profile so chrome.runtime.connectNative
 * spawns the actual .NET host — the complete production pipeline.
 */
async function launchExtension() {
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-e2e-'));

  // Prefer the container's pre-provisioned Chromium; fall back to Playwright's own.
  const preinstalled = '/opt/pw-browsers/chromium';
  const executablePath = process.env.PDF_EDITOR_CHROMIUM
    ?? (fs.existsSync(preinstalled) ? preinstalled : undefined);

  const context = await chromium.launchPersistentContext(userDataDir, {
    headless: true,
    executablePath,
    args: [
      `--disable-extensions-except=${EXTENSION_DIR}`,
      `--load-extension=${EXTENSION_DIR}`,
    ],
  });

  // The extension ID is the host of any of its pages/workers.
  let [worker] = context.serviceWorkers();
  if (!worker) worker = await context.waitForEvent('serviceworker');
  const extensionId = new URL(worker.url()).host;

  // Native messaging host manifests are looked up under <user-data-dir>/NativeMessagingHosts
  // at connect time, so registering after launch is fine.
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

  return {
    context,
    extensionId,
    userDataDir,
    viewerUrl: `chrome-extension://${extensionId}/src/viewer.html`,
    optionsUrl: `chrome-extension://${extensionId}/src/options.html`,
    async close() {
      await context.close();
      fs.rmSync(userDataDir, { recursive: true, force: true });
    },
  };
}

module.exports = { buildPrerequisites, launchExtension, REPO_ROOT };
