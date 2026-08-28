// Unit tests for the native-host install diagnosis (see extension/src/host-install.js).
// Run with: node --test extension/test/host-install.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import {
  HOST_STATE, PINNED_EXTENSION_ID, classifyHostError, detectPlatform,
  hostInstallGuide, hostInstallGuideLines, hostStateSummary,
} from '../src/host-install.js';

// A stand-in for the ID an unpacked, developer-mode extension gets: 32 characters from a-p,
// and deliberately not the pinned Web Store one the OS packages register.
const DEV_EXTENSION_ID = 'abcdefghijklmnopabcdefghijklmnop';

// The exact strings Chromium produces. Getting these wrong means the user is shown the wrong fix,
// which is worse than showing none, so they are pinned here verbatim.
test('Chromium’s "not found" message means the host was never registered', () => {
  assert.equal(
    classifyHostError('Specified native messaging host not found.'),
    HOST_STATE.MISSING);
});

test('a forbidden connection is not mistaken for a missing host', () => {
  assert.equal(
    classifyHostError('Access to the specified native messaging host is forbidden.'),
    HOST_STATE.FORBIDDEN);
});

test('a host that starts and dies is classified as crashed, not missing', () => {
  for (const message of [
    'Native host has exited.',
    'Failed to start native messaging host.',
    'Error when communicating with the native messaging host.',
  ]) {
    assert.equal(classifyHostError(message), HOST_STATE.CRASHED, message);
  }
});

test('an empty or unrecognised message is UNKNOWN rather than a guess', () => {
  assert.equal(classifyHostError(''), HOST_STATE.UNKNOWN);
  assert.equal(classifyHostError('   '), HOST_STATE.UNKNOWN);
  assert.equal(classifyHostError(undefined), HOST_STATE.UNKNOWN);
  assert.equal(classifyHostError(null), HOST_STATE.UNKNOWN);
  assert.equal(classifyHostError({}), HOST_STATE.UNKNOWN);
  assert.equal(classifyHostError('Something else went wrong.'), HOST_STATE.UNKNOWN);
});

test('a decorated message still classifies', () => {
  assert.equal(
    classifyHostError('Error: Specified native messaging host not found. (com.pdfeditor.host)'),
    HOST_STATE.MISSING);
});

test('platform detection separates the three desktop families', () => {
  assert.equal(detectPlatform('Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/128'), 'windows');
  assert.equal(detectPlatform('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/128'), 'macos');
  assert.equal(detectPlatform('Mozilla/5.0 (X11; Linux x86_64) Chrome/128'), 'linux');
});

test('Android and ChromeOS are not treated as desktop Linux', () => {
  // Android's UA contains "Linux", so a naive check would offer it a .deb.
  assert.equal(detectPlatform('Mozilla/5.0 (Linux; Android 14; Pixel 8) Chrome/128'), 'android');
  assert.equal(detectPlatform('Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) Chrome/128'), 'chromeos');
  assert.equal(detectPlatform(''), 'unknown');
  assert.equal(detectPlatform(), 'unknown');
});

test('a missing host on Linux is told how to install and how to reach a snap/flatpak browser', () => {
  const guide = hostInstallGuide({ state: HOST_STATE.MISSING, platform: 'linux' });
  const text = hostInstallGuideLines(guide).join('\n');
  assert.match(text, /sudo apt install \.\/pdf-editor-host_/);
  assert.match(text, /sudo dnf install/);
  assert.match(text, /sudo pacman -U/);
  // The system-wide manifest cannot reach a sandboxed browser; the per-user helper is the fix.
  assert.match(text, /pdf-editor-host-register/);
  assert.match(text, /pdf-editor-host --diagnostics/);
});

test('a forbidden host is told to re-register for this extension ID, not to reinstall', () => {
  const guide = hostInstallGuide({
    state: HOST_STATE.FORBIDDEN, platform: 'linux', extensionId: DEV_EXTENSION_ID,
  });
  const text = hostInstallGuideLines(guide).join('\n');
  assert.match(text, /pdf-editor-host-register --extension-id abcdefghijklmnopabcdefghijklmnop/);
  assert.doesNotMatch(text, /sudo apt install/);
});

// The published build carries a manifest "key" that forces the pinned ID, so the OS package's own
// manifest *does* list it. A forbidden result there is a different fault with the same message:
// some other manifest is being read first. Telling that user their ID is not the pinned one — and
// handing them a re-register command that writes the very per-user file that is shadowing the
// package — describes someone else's machine and can entrench the problem.
test('a forbidden published build is told a stale manifest is shadowing the package', () => {
  const guide = hostInstallGuide({
    state: HOST_STATE.FORBIDDEN, platform: 'linux', extensionId: PINNED_EXTENSION_ID,
  });
  const text = hostInstallGuideLines(guide).join('\n');
  assert.match(text, /takes precedence over the system-wide one/);
  assert.match(text, /pdf-editor-host-register --uninstall/);
  assert.match(text, /find ~\/\.config/);
  // The dev-mode fix must not be offered here: re-registering per-user rewrites the shadowing
  // manifest rather than removing it, and the ID it would pin is the one already allowed.
  assert.doesNotMatch(text, /--extension-id/);
});

test('the shadowed-manifest advice is Windows-specific on Windows', () => {
  const text = hostInstallGuideLines(hostInstallGuide({
    state: HOST_STATE.FORBIDDEN, platform: 'windows', extensionId: PINNED_EXTENSION_ID,
  })).join('\n');
  assert.match(text, /HKCU/);
  assert.match(text, /register-host\.ps1" -Uninstall/);
  assert.doesNotMatch(text, /pdf-editor-host-register/);
});

test('a forbidden host with no known ID still yields a runnable-looking command', () => {
  const text = hostInstallGuideLines(
    hostInstallGuide({ state: HOST_STATE.FORBIDDEN, platform: 'windows' })).join('\n');
  assert.match(text, /register-host\.ps1" -ExtensionId <your-extension-id>/);
});

test('Windows advice only names paths an MSI install actually has', () => {
  // register-host.ps1 is shipped next to the host by the MSI; scripts\install-host.ps1 exists only
  // in a checkout or an unzipped bundle, so pointing someone who ran the installer at it sends
  // them to a file that is not on their machine.
  for (const state of [HOST_STATE.FORBIDDEN, HOST_STATE.CRASHED, HOST_STATE.MISSING]) {
    const text = hostInstallGuideLines(
      hostInstallGuide({ state, platform: 'windows', extensionId: DEV_EXTENSION_ID }),
    ).join('\n');
    assert.doesNotMatch(text, /scripts\\install-host\.ps1/);
  }

  const forbidden = hostInstallGuideLines(hostInstallGuide({
    state: HOST_STATE.FORBIDDEN, platform: 'windows', extensionId: DEV_EXTENSION_ID,
  })).join('\n');
  assert.match(forbidden, /PDF Editor Host\\register-host\.ps1" -ExtensionId abcdefghijklmnopabcdefghijklmnop/);
});

// Windows client editions default to a Restricted execution policy, under which running a .ps1
// at all is refused before its first line executes. Any command we hand a user that runs one must
// therefore go through powershell.exe with an explicit policy, or it fails for most of them. This
// sweeps every state x platform rather than the handful of cases spelled out above, so a new step
// added later cannot quietly reintroduce a bare invocation.
test('every PowerShell command sets an execution policy', () => {
  const platforms = ['windows', 'macos', 'linux', 'chromeos', 'android', 'unknown'];
  for (const state of Object.values(HOST_STATE)) {
    for (const platform of platforms) {
      const guide = hostInstallGuide({ state, platform, extensionId: DEV_EXTENSION_ID });
      for (const step of guide.steps) {
        for (const line of (step.code ?? '').split('\n')) {
          if (!line.includes('.ps1')) continue;
          assert.match(
            line, /powershell -NoProfile -ExecutionPolicy Bypass -File/,
            `${state}/${platform}: a .ps1 is invoked without an execution-policy bypass: ${line}`);
        }
      }
    }
  }
});

test('a crashed host is pointed at --diagnostics and the missing runtime libraries', () => {
  const text = hostInstallGuideLines(
    hostInstallGuide({ state: HOST_STATE.CRASHED, platform: 'linux' })).join('\n');
  assert.match(text, /--diagnostics/);
  assert.match(text, /libicu/);
  assert.doesNotMatch(text, /sudo apt install \.\/pdf-editor-host_/);
});

test('each platform gets its own install command and no other platform’s', () => {
  const win = hostInstallGuideLines(
    hostInstallGuide({ state: HOST_STATE.MISSING, platform: 'windows' })).join('\n');
  assert.match(win, /msiexec/);
  assert.doesNotMatch(win, /apt install/);

  const mac = hostInstallGuideLines(
    hostInstallGuide({ state: HOST_STATE.MISSING, platform: 'macos' })).join('\n');
  assert.match(mac, /install-host\.sh/);
  assert.doesNotMatch(mac, /msiexec/);
});

test('platforms with no host build say so instead of offering an installer', () => {
  for (const platform of ['android', 'chromeos']) {
    const guide = hostInstallGuide({ state: HOST_STATE.MISSING, platform });
    const text = hostInstallGuideLines(guide).join('\n');
    assert.match(text, /no build for this platform/);
    assert.doesNotMatch(text, /apt install|msiexec/);
  }
});

test('a connected host produces no steps', () => {
  const guide = hostInstallGuide({ state: HOST_STATE.CONNECTED, platform: 'linux' });
  assert.deepEqual(guide.steps, []);
  assert.equal(hostStateSummary(HOST_STATE.CONNECTED), 'Connected.');
});

test('the raw browser error is carried through verbatim for bug reports', () => {
  const raw = 'Specified native messaging host not found.';
  const text = hostInstallGuideLines(
    hostInstallGuide({ state: HOST_STATE.MISSING, platform: 'linux', error: raw })).join('\n');
  assert.match(text, /Browser error: Specified native messaging host not found\./);
});

test('hostInstallGuide tolerates being called with nothing', () => {
  const guide = hostInstallGuide();
  assert.ok(guide.headline);
  assert.ok(Array.isArray(guide.steps));
  assert.deepEqual(hostInstallGuideLines(null), []);
});

test('the pinned ID is a well-formed extension ID', () => {
  // The packages pin allowed_origins to exactly this; a typo here would be invisible until a
  // browser silently refused the connection.
  assert.match(PINNED_EXTENSION_ID, /^[a-p]{32}$/);
});
