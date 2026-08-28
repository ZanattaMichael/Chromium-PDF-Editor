// Unit tests for the native-host version check (see extension/src/host-version.js).
// Run with: node --test extension/test/host-version.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import {
  VERSION_STATE, checkHostVersion, compareVersions, hostVersionGuide, hostVersionGuideLines,
  parseVersion, versionStateSummary,
} from '../src/host-version.js';

// The two sides genuinely are formatted differently, and neither is going to change: the extension
// manifest is 3-part, .NET's Assembly.GetName().Version is always 4-part. If the comparison ever
// went back to comparing strings, this is the case that would break first.
test('a 3-part extension version equals the host’s 4-part form', () => {
  const v = checkHostVersion('2.0.0.0', '2.0.0');
  assert.equal(v.state, VERSION_STATE.OK);
  assert.equal(v.ok, true);
});

test('the release the packages actually ship matches the extension', () => {
  // Directory.Build.props pins the assembly version to extension/manifest.json's; before that,
  // every host reported 1.0.0.0 and would have been flagged as stale on a correct install.
  assert.equal(checkHostVersion('2.0.0.0', '2.0.0').state, VERSION_STATE.OK);
  assert.equal(checkHostVersion('1.0.0.0', '2.0.0').state, VERSION_STATE.HOST_OLDER);
});

test('an out-of-date host is flagged, which is the case users hit', () => {
  // The extension auto-updates from the Web Store; the host is a package installed by hand, so
  // it is the half that lags.
  const v = checkHostVersion('1.9.0.0', '2.0.0');
  assert.equal(v.state, VERSION_STATE.HOST_OLDER);
  assert.equal(v.ok, false);
});

test('a host ahead of the extension is flagged the other way', () => {
  const v = checkHostVersion('3.0.0.0', '2.0.0');
  assert.equal(v.state, VERSION_STATE.HOST_NEWER);
  assert.equal(v.ok, false);
});

test('a patch or build difference is not a mismatch', () => {
  // Warning on these would put a banner in front of everyone one hotfix behind, which teaches
  // people to ignore the banner that does matter.
  for (const host of ['2.0.1.0', '2.0.99.0', '2.0.0.7', '2.0']) {
    assert.equal(checkHostVersion(host, '2.0.0').state, VERSION_STATE.OK, host);
  }
});

test('a minor-version difference is a mismatch', () => {
  // Minor is where an added action or a renamed field lands, so it is the boundary that matters.
  assert.equal(checkHostVersion('2.1.0.0', '2.0.0').state, VERSION_STATE.HOST_NEWER);
  assert.equal(checkHostVersion('2.0.0.0', '2.1.0').state, VERSION_STATE.HOST_OLDER);
});

test('an unreadable version yields no verdict rather than a guess', () => {
  for (const bad of ['', '   ', 'unknown', '2.x.0', null, undefined, 42, {}]) {
    const v = checkHostVersion(bad, '2.0.0');
    assert.equal(v.state, VERSION_STATE.UNKNOWN, String(bad));
    // Not treated as a failure: an old host that reports nothing is already covered by the
    // connection states, and a warning here would fire on every format we have not seen yet.
    assert.equal(v.ok, true, String(bad));
  }
  assert.equal(checkHostVersion('2.0.0.0', '').state, VERSION_STATE.UNKNOWN);
});

test('parseVersion returns numbers, or null for anything non-numeric', () => {
  assert.deepEqual(parseVersion('2.0.0.0'), [2, 0, 0, 0]);
  assert.deepEqual(parseVersion(' 2.0.0 '), [2, 0, 0]);
  assert.equal(parseVersion('2.0.0-beta'), null);
  assert.equal(parseVersion('v2.0.0'), null);
  assert.equal(parseVersion(''), null);
});

test('compareVersions treats missing trailing components as zero', () => {
  assert.equal(compareVersions([2, 0], [2, 0, 0, 0]), 0);
  assert.equal(compareVersions([2, 0, 0], [2, 0, 1]), -1);
  assert.equal(compareVersions([2, 1], [2, 0, 9]), 1);
  // Numeric, not lexicographic: "10" sorts above "9".
  assert.equal(compareVersions([2, 10], [2, 9]), 1);
});

test('a matching pair produces no guide to show', () => {
  assert.equal(hostVersionGuide({
    hostVersion: '2.0.0.0', extensionVersion: '2.0.0', platform: 'linux',
  }), null);
  assert.deepEqual(hostVersionGuideLines(null), []);
});

test('an unreadable version produces no guide either', () => {
  assert.equal(hostVersionGuide({
    hostVersion: 'unknown', extensionVersion: '2.0.0', platform: 'linux',
  }), null);
});

test('an old host is told to install the package for its own platform', () => {
  const linux = hostVersionGuideLines(hostVersionGuide({
    hostVersion: '1.0.0.0', extensionVersion: '2.0.0', platform: 'linux',
  })).join('\n');
  assert.match(linux, /older than this extension/);
  // Both versions are named: "the host is out of date" is not actionable without them.
  assert.match(linux, /v1\.0\.0\.0/);
  assert.match(linux, /v2\.0\.0/);
  assert.match(linux, /sudo apt install/);
  assert.doesNotMatch(linux, /msiexec/);

  const windows = hostVersionGuideLines(hostVersionGuide({
    hostVersion: '1.0.0.0', extensionVersion: '2.0.0', platform: 'windows',
  })).join('\n');
  assert.match(windows, /msiexec/);
  assert.doesNotMatch(windows, /sudo apt install/);
});

test('a stale extension is told to update itself, not to reinstall the host', () => {
  const text = hostVersionGuideLines(hostVersionGuide({
    hostVersion: '3.0.0.0', extensionVersion: '2.0.0', platform: 'linux',
  })).join('\n');
  assert.match(text, /newer than this extension/);
  assert.match(text, /chrome:\/\/extensions/);
  // Reinstalling the host is the wrong fix here and would leave the user exactly where they were.
  assert.doesNotMatch(text, /sudo apt install/);
});

test('each state has its own summary line', () => {
  const seen = new Set();
  for (const state of Object.values(VERSION_STATE)) {
    const summary = versionStateSummary(state);
    assert.ok(summary.length > 0, state);
    assert.ok(!seen.has(summary), `duplicate summary for ${state}`);
    seen.add(summary);
  }
});
