// Decides whether the native host that answered is one this extension can actually talk to.
//
// The extension and the host ship separately: the extension updates itself from the Web Store,
// while the host is an OS package a user installs by hand. So they drift, and the interesting
// direction is the common one — the extension auto-updated last night and the host is still last
// release's. Nothing about that failure is self-announcing: an older host answers `ping` perfectly
// well and then rejects, mis-handles, or silently ignores an action added after it was built. The
// user sees "connected" and a feature that does nothing.
//
// This module turns the two version strings into a verdict, so the mismatch is named before it is
// hit rather than diagnosed afterwards from a bug report.
//
// Kept DOM-free (like host-diagnostics.js and host-install.js) so it is unit testable and usable
// from the options page, the viewer and the service worker alike.

import { installSteps } from './host-install.js';

/** Version verdicts, from the extension's point of view. */
export const VERSION_STATE = {
  /** Same feature version. Patch/build digits may still differ. */
  OK: 'ok',
  /** The host predates this extension — the case a user actually hits. */
  HOST_OLDER: 'host-older',
  /** The host is ahead: an extension left behind, usually an unpacked dev copy. */
  HOST_NEWER: 'host-newer',
  /** One of the two versions was missing or unparseable; no verdict is safe. */
  UNKNOWN: 'unknown',
};

/**
 * Splits a version string into numeric components.
 *
 * The two sides are formatted differently and neither is going to change: the extension manifest
 * carries a 3-part "2.0.0", while the host reports .NET's `Assembly.GetName().Version`, which is
 * always 4-part ("2.0.0.0"). Comparing the strings would call those two different forever, so both
 * are reduced to numbers here. Returns null for anything non-numeric rather than guessing.
 */
export function parseVersion(raw) {
  if (typeof raw !== 'string') return null;
  const trimmed = raw.trim();
  if (trimmed === '') return null;
  const parts = trimmed.split('.');
  const nums = parts.map((p) => (/^\d+$/.test(p) ? Number(p) : NaN));
  if (nums.some(Number.isNaN)) return null;
  return nums;
}

/**
 * Orders two parsed versions: -1 if a < b, 1 if a > b, 0 if equal. Missing trailing components
 * count as zero, so "2.0" and "2.0.0.0" are the same version.
 */
export function compareVersions(a, b) {
  const len = Math.max(a.length, b.length);
  for (let i = 0; i < len; i++) {
    const diff = (a[i] ?? 0) - (b[i] ?? 0);
    if (diff !== 0) return diff < 0 ? -1 : 1;
  }
  return 0;
}

/**
 * Compares the host's version against the extension's on the first two components only.
 *
 * Major and minor are what the message protocol is allowed to change: a new action, a renamed
 * field, a different payload shape. Patch and build carry fixes that keep the protocol intact, and
 * flagging those would put a warning in front of every user whose package is one hotfix behind —
 * noise that trains people to ignore the banner that matters.
 */
export function checkHostVersion(hostVersion, extensionVersion) {
  const host = parseVersion(hostVersion);
  const extension = parseVersion(extensionVersion);
  if (!host || !extension) {
    return { state: VERSION_STATE.UNKNOWN, ok: true, hostVersion, extensionVersion };
  }
  const cmp = compareVersions(host.slice(0, 2), extension.slice(0, 2));
  let state = VERSION_STATE.OK;
  if (cmp < 0) state = VERSION_STATE.HOST_OLDER;
  else if (cmp > 0) state = VERSION_STATE.HOST_NEWER;
  return { state, ok: state === VERSION_STATE.OK, hostVersion, extensionVersion };
}

/** A one-line summary of a verdict, for a status field. */
export function versionStateSummary(state) {
  switch (state) {
    case VERSION_STATE.HOST_OLDER: return 'The native host is older than this extension.';
    case VERSION_STATE.HOST_NEWER: return 'The native host is newer than this extension.';
    case VERSION_STATE.UNKNOWN: return 'The native host did not report a usable version.';
    default: return 'The native host version matches this extension.';
  }
}

/**
 * Turns a version verdict into the same {headline, detail, steps} shape {@link hostInstallGuide}
 * produces, so a caller renders a mismatch with the code it already has for a missing host.
 *
 * Returns null when there is nothing to say — a matching pair, or a version we could not read.
 * An unreadable version is deliberately silent: a host too old to report one at all is a case the
 * "connected" path already handles, and inventing a warning from a parse failure would fire on
 * every future format we have not seen yet.
 *
 * @param {object} o
 * @param {string} o.hostVersion       what the host reported to `ping`
 * @param {string} o.extensionVersion  chrome.runtime.getManifest().version
 * @param {string} o.platform          from {@link detectPlatform}
 */
export function hostVersionGuide({ hostVersion, extensionVersion, platform } = {}) {
  const verdict = checkHostVersion(hostVersion, extensionVersion);
  if (verdict.state === VERSION_STATE.OK || verdict.state === VERSION_STATE.UNKNOWN) return null;

  const guide = {
    state: verdict.state,
    headline: versionStateSummary(verdict.state),
    hostVersion: verdict.hostVersion,
    extensionVersion: verdict.extensionVersion,
    detail: '',
    steps: [],
  };

  if (verdict.state === VERSION_STATE.HOST_OLDER) {
    guide.detail = `The host is v${verdict.hostVersion} but this extension is v`
      + `${verdict.extensionVersion}. It will answer, so the connection looks healthy, but any `
      + 'action added since it was built fails or does nothing. Install the matching host package '
      + 'to bring the two back in step.';
    // Updating an out-of-date host is the same operation as installing one from scratch: download
    // the current package and run it over the top. Reusing the install steps keeps one set of
    // per-platform commands rather than a near-identical second set that can drift.
    guide.steps = installSteps(platform);
  } else {
    guide.detail = `The host is v${verdict.hostVersion} but this extension is v`
      + `${verdict.extensionVersion}. The extension is the stale half — it may not know about `
      + 'actions this host expects. Update it, then restart the browser.';
    guide.steps = [
      {
        text: 'From the Chrome Web Store, open chrome://extensions, enable Developer mode and '
          + 'press Update to pull the current version.',
      },
      {
        text: 'Loaded unpacked? Pull the matching source and press the reload arrow on the '
          + 'extension card at chrome://extensions.',
      },
    ];
  }
  return guide;
}

/** The verdict as plain text, for the copyable diagnostics blob and downloaded logs. */
export function hostVersionGuideLines(guide) {
  if (!guide) return [];
  const lines = [guide.headline, guide.detail];
  guide.steps.forEach((step, i) => {
    lines.push(`${i + 1}. ${step.text}`);
    if (step.code) step.code.split('\n').forEach((l) => lines.push(`     ${l}`));
  });
  return lines;
}
