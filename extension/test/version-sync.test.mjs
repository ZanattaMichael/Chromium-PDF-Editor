// Guards the one version number this project actually has, against the files that must agree
// on it. Run with: node --test extension/test/version-sync.test.mjs
//
// extension/manifest.json is the source of truth. Both release workflows stamp the computed
// release version into it and nothing else; package-deb.sh, package-rpm.sh, package-arch.sh,
// package-bundle.sh and package-msi.ps1 all read their package version out of it; and
// Directory.Build.props derives the assembly version from it, which is what the host reports
// to the extension over `ping`.
//
// It is worth a test because the failure is silent in every direction. Before
// Directory.Build.props set a version at all, every assembly defaulted to 1.0.0.0: the .deb was
// *named* 2.0.0, installed cleanly, connected, and told the extension it was v1.0.0.0. Then it
// carried a hardcoded <Version> that no workflow ever stamped, which is the same bug wearing a
// different number — packages named with their release version, containing a host that reported
// whatever was last committed. Nothing fails either way; the number is just wrong everywhere it
// is shown, and host-version.js compares only major.minor, so the drift raises no banner.
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repoFile = (rel) => readFileSync(fileURLToPath(new URL(`../../${rel}`, import.meta.url)), 'utf8');
const repoDir = (rel) => readdirSync(fileURLToPath(new URL(`../../${rel}`, import.meta.url)));

const manifestVersion = JSON.parse(repoFile('extension/manifest.json')).version;
const props = repoFile('Directory.Build.props');

// Every workflow that stamps a release version and then builds something shipped.
const RELEASE_WORKFLOWS = [
  '.github/workflows/release-candidate.yml',
  '.github/workflows/release-extension.yml',
  '.github/workflows/populate-draft-release.yml',
];

test('the extension manifest carries a plain numeric version', () => {
  // parseVersion() in host-version.js rejects anything else, which would make every install
  // "unknown" instead of checked. Up to four parts: a release candidate stamps "2.0.3.51".
  assert.match(manifestVersion, /^\d+(\.\d+){0,3}$/);
});

test('Directory.Build.props derives the assembly version from the manifest', () => {
  assert.match(
    props, /ManifestJsonPath[\s\S]*extension\/manifest\.json/,
    'Directory.Build.props no longer reads extension/manifest.json — the assembly version would '
    + 'stop tracking the release version');
  assert.match(
    props, /<Version Condition=/,
    'Directory.Build.props should set <Version> only when the manifest could be read, so a build '
    + 'from a checkout without it still has a defined version');
});

test('Directory.Build.props hardcodes no version of its own', () => {
  // The bug this replaced: a literal <Version>2.0.0</Version> that no workflow stamped, so every
  // release shipped packages named correctly around a host reporting 2.0.0. A literal here would
  // reintroduce it silently, because it would still *look* right on the release it was written.
  const literal = /<Version>\s*\d+(\.\d+)*\s*<\/Version>/.exec(props);
  assert.equal(
    literal, null,
    `Directory.Build.props hardcodes a version (${literal?.[0]}); derive it from the manifest `
    + 'instead — nothing stamps this file at release time');
});

test('no workflow stamps a version into Directory.Build.props', () => {
  // A release-time substitution into this file was the other way to fix the same bug, and it is
  // the one that breaks now: it matched a literal <Version>…</Version>, which the derivation
  // above no longer contains, and it exits non-zero when it finds nothing to replace. Stamping
  // the manifest is the whole job — the props file follows from it.
  for (const wf of RELEASE_WORKFLOWS) {
    assert.doesNotMatch(
      repoFile(wf), /Stamp Directory\.Build\.props/,
      `${wf} stamps Directory.Build.props; it derives its version from the manifest instead, and `
      + 'a substitution step there fails the release when it finds no literal to replace');
  }
});

test('no workflow pushes to the default branch', () => {
  // The bump has to be a human step, and this is why. A GITHUB_TOKEN push to the default branch
  // is rejected by the branch ruleset ("Changes must be made through a pull request", GH013), so
  // a job that syncs the manifest there cannot work no matter how it retries. One did: it ran on
  // the v2.0.4 release, hit GH013 three times, downgraded the failure to a ::warning so it would
  // not fail a release whose artifacts were already out, and reported success — leaving 2.0.4
  // shipped from a manifest that still said 2.0.2, with nothing red to notice. Writing to the
  // default branch from CI is the shape of that bug; pushing a branch and opening a pull request
  // (generate-screenshots.yml) is the shape that works.
  const pushesToDefault = /git push[^\n]*(\$\{?DEFAULT_BRANCH|default_branch|HEAD:main\b|origin\s+main\b)/;
  for (const wf of repoDir('.github/workflows').filter((f) => /\.ya?ml$/.test(f))) {
    const match = pushesToDefault.exec(repoFile(`.github/workflows/${wf}`));
    assert.equal(
      match, null,
      `.github/workflows/${wf} pushes to the default branch (${match?.[0]}); the ruleset rejects `
      + 'that, so it can only ever fail or silently no-op — push a branch and open a pull request');
  }
});

test('a final release verifies the committed manifest against its tag', () => {
  // With nothing writing the manifest back, the only thing keeping the repository's version equal
  // to the last thing shipped is that a mismatch fails the release. Deleting this check would
  // restore the silent drift the sync job left behind, just without the warning.
  const wf = repoFile('.github/workflows/release-extension.yml');
  assert.match(
    wf, /committed manifest version must match the release tag/i,
    'release-extension.yml no longer checks extension/manifest.json against the release tag, so a '
    + 'release cut without bumping it would ship a version the repository never claims');
  assert.match(
    wf, /prerelease == false/,
    'the manifest/tag check must be scoped to final releases: a "vX.Y.Z-<build>" prerelease '
    + 'packages as the four-part "X.Y.Z.<build>", which never equals a committed three-part version');
});

test('the packagers all read the version from the manifest, not a copy of their own', () => {
  // If one of these ever grew its own hardcoded version, the guards above would still pass while
  // the package name and the binary inside it disagreed.
  for (const script of [
    'scripts/package-deb.sh', 'scripts/package-rpm.sh',
    'scripts/package-arch.sh', 'scripts/package-bundle.sh',
  ]) {
    assert.match(repoFile(script), /manifest\.json/, `${script} should derive VERSION from the manifest`);
  }
  assert.match(repoFile('scripts/package-msi.ps1'), /manifest\.json/);
});

test('the release workflows stamp the manifest before anything is built', () => {
  // Directory.Build.props reads the manifest at evaluation time, so within a job that packages,
  // a compile that runs before the stamp bakes in the committed version and the packagers can
  // reuse those outputs. Jobs that only build and test (release-extension.yml's `verify`) never
  // stamp and are not part of this — they check out separately and ship nothing.
  const jobsOf = (yaml) => {
    const body = yaml.slice(yaml.indexOf('\njobs:\n'));
    // Job names sit at exactly two spaces of indent; everything up to the next one is that job.
    return body.split(/\n {2}(?=[a-z][\w-]*:\n)/).slice(1);
  };
  for (const wf of RELEASE_WORKFLOWS) {
    const stamping = jobsOf(repoFile(wf)).filter((job) => job.includes('Stamp manifest.json'));
    assert.ok(stamping.length > 0, `${wf} should stamp manifest.json with the release version`);
    for (const job of stamping) {
      const stamp = job.indexOf('Stamp manifest.json');
      const compile = job.search(/dotnet (build|publish|test)|\.\/scripts\/package-/);
      assert.ok(
        compile === -1 || compile > stamp,
        `${wf}: ${job.slice(0, job.indexOf(':'))} compiles before stamping the manifest, so its `
        + 'assemblies would carry the committed version rather than the release version');
    }
  }
});
