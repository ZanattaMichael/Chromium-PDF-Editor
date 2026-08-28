// Guards the one version number this project actually has, against the two files that must agree
// on it. Run with: node --test extension/test/version-sync.test.mjs
//
// extension/manifest.json is the source of truth: package-deb.sh, package-rpm.sh, package-arch.sh
// and package-msi.ps1 all read their package version out of it. Directory.Build.props has to carry
// the same number separately, because MSBuild cannot read JSON — that is the drift this guards.
//
// It is worth a test because the failure is silent in both directions. Before Directory.Build.props
// set a version at all, every assembly defaulted to 1.0.0.0: the .deb was *named* 2.0.0, installed
// cleanly, connected, and told the extension it was v1.0.0.0. Nothing failed; the number was just
// wrong everywhere it was shown. Now that the extension compares the two (host-version.js), a drift
// in the other direction is worse — it puts a "your host is out of date" warning in front of users
// whose host is perfectly current.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repoFile = (rel) => readFileSync(fileURLToPath(new URL(`../../${rel}`, import.meta.url)), 'utf8');

const manifestVersion = JSON.parse(repoFile('extension/manifest.json')).version;

test('the extension manifest carries a plain numeric version', () => {
  // parseVersion() in host-version.js rejects anything else, which would make every install
  // "unknown" instead of checked.
  assert.match(manifestVersion, /^\d+(\.\d+)*$/);
});

test('Directory.Build.props pins the assembly version to the manifest’s', () => {
  const props = repoFile('Directory.Build.props');
  const match = /<Version>([^<]*)<\/Version>/.exec(props);
  assert.ok(match, 'Directory.Build.props has no <Version> — the host would report 1.0.0.0');
  assert.equal(
    match[1].trim(), manifestVersion,
    'Directory.Build.props <Version> and extension/manifest.json version have drifted; the host '
    + 'would report a version the extension flags as a mismatch');
});

test('the packagers all read the version from the manifest, not a copy of their own', () => {
  // If one of these ever grew its own hardcoded version, the guard above would still pass while
  // the package name and the binary inside it disagreed.
  for (const script of ['scripts/package-deb.sh', 'scripts/package-rpm.sh', 'scripts/package-arch.sh']) {
    assert.match(repoFile(script), /manifest\.json/, `${script} should derive VERSION from the manifest`);
  }
  assert.match(repoFile('scripts/package-msi.ps1'), /manifest\.json/);
});
