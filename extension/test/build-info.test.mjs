// Unit tests for the About dialog's build-identity formatting (see extension/src/build-info.js).
// Run with: node --test extension/test/build-info.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import { formatBuildInfo, formatBuiltAt, loadBuildMeta } from '../src/build-info.js';

test('a packaged build shows the version and a 12-char short commit', () => {
  const info = formatBuildInfo({
    version: '1.0.3.43',
    commit: '18596d768d03600d5f200fd447dc181f6e315b3a',
    builtAt: '2026-08-05T22:54:00Z',
  });
  assert.equal(info.version, '1.0.3.43');
  assert.equal(info.commit, '18596d768d03'); // first 12 chars
  assert.equal(info.commitFull, '18596d768d03600d5f200fd447dc181f6e315b3a');
  assert.equal(info.isRelease, true);
  assert.equal(info.builtAt, '2026-08-05 22:54 UTC');
});

test('a development build (no commit) says so instead of showing a blank', () => {
  const info = formatBuildInfo({ version: '1.0.0' });
  assert.equal(info.version, '1.0.0');
  assert.equal(info.commit, 'development build (unpackaged)');
  assert.equal(info.isRelease, false);
  assert.equal(info.builtAt, '—');
});

test('everything missing degrades to safe placeholders', () => {
  const info = formatBuildInfo();
  assert.equal(info.version, 'unknown');
  assert.equal(info.isRelease, false);
  assert.equal(info.builtAt, '—');
});

test('formatBuiltAt renders UTC regardless of the local timezone', () => {
  // A fixed instant, formatted without depending on the machine's timezone.
  assert.equal(formatBuiltAt('2026-01-02T03:04:05Z'), '2026-01-02 03:04 UTC');
});

test('formatBuiltAt shows an unparseable timestamp verbatim rather than "Invalid Date"', () => {
  assert.equal(formatBuiltAt('not-a-date'), 'not-a-date');
});

test('loadBuildMeta returns the parsed JSON when the file is present', async () => {
  const meta = await loadBuildMeta(
    (p) => `chrome-extension://x/${p}`,
    async () => ({ ok: true, json: async () => ({ commit: 'abc', builtAt: 'z' }) }),
  );
  assert.deepEqual(meta, { commit: 'abc', builtAt: 'z' });
});

test('loadBuildMeta returns {} when the file is absent (dev build) or fetch throws', async () => {
  const missing = await loadBuildMeta(
    (p) => p,
    async () => ({ ok: false }),
  );
  assert.deepEqual(missing, {});

  const threw = await loadBuildMeta(
    (p) => p,
    async () => { throw new Error('no such file'); },
  );
  assert.deepEqual(threw, {});
});
