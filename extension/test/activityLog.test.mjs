// Tests for the activity console's store (see extension/src/activity-log.js).
// Run with: node --test extension/test/activityLog.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import { ActivityLog, formatEntry, formatTime } from '../src/activity-log.js';

test('records level, message and detail in order', () => {
  const log = new ActivityLog();
  log.add('info', 'render', '42 ms');
  log.add('error', 'render failed', 'out of memory');
  assert.deepEqual(log.entries.map((e) => [e.level, e.message, e.detail]), [
    ['info', 'render', '42 ms'],
    ['error', 'render failed', 'out of memory'],
  ]);
});

test('falls back to info for an unknown level', () => {
  const log = new ActivityLog();
  assert.equal(log.add('shout', 'x').level, 'info');
});

test('entries are not diagnostic by default; the opt-in flag marks and tags them (#97)', () => {
  const log = new ActivityLog();
  const plain = log.add('info', 'render', '42 ms');
  const diag = log.add('info', 'browser', 'Chrome 128', { diagnostic: true });
  assert.equal(plain.diagnostic, false);
  assert.equal(diag.diagnostic, true);
  // The plain-text form marks diagnostic lines so a copied/downloaded log distinguishes them.
  assert.ok(!formatEntry(plain).includes('[diag]'));
  assert.ok(formatEntry(diag).includes('[diag]'));
});

test('caps retention so a long session cannot grow without bound', () => {
  const log = new ActivityLog(3);
  for (const n of [1, 2, 3, 4, 5]) log.add('info', `entry ${n}`);
  assert.deepEqual(log.entries.map((e) => e.message), ['entry 3', 'entry 4', 'entry 5']);
  assert.equal(log.dropped, 2);
});

test('a capacity below one is still usable', () => {
  const log = new ActivityLog(0);
  log.add('info', 'a');
  log.add('info', 'b');
  assert.deepEqual(log.entries.map((e) => e.message), ['b']);
});

test('clear() empties the log and resets the dropped count', () => {
  const log = new ActivityLog(1);
  log.add('info', 'a');
  log.add('info', 'b');
  assert.equal(log.dropped, 1);
  log.clear();
  assert.deepEqual(log.entries, []);
  assert.equal(log.dropped, 0);
});

test('subscribers see additions, and null on clear', () => {
  const log = new ActivityLog();
  const seen = [];
  const unsubscribe = log.subscribe((entry) => seen.push(entry === null ? 'clear' : entry.message));
  log.add('info', 'a');
  log.clear();
  unsubscribe();
  log.add('info', 'b');
  assert.deepEqual(seen, ['a', 'clear']);
});

test('sequence numbers keep rising so a renderer can append only what is new', () => {
  const log = new ActivityLog(2);
  const seqs = ['a', 'b', 'c'].map((m) => log.add('info', m).seq);
  assert.deepEqual(seqs, [1, 2, 3]);
});

test('formats a timestamp to millisecond precision', () => {
  assert.equal(formatTime(new Date(2026, 0, 2, 3, 4, 5, 6)), '03:04:05.006');
});

test('formats one entry as a padded plain-text line', () => {
  const entry = { time: new Date(2026, 0, 2, 3, 4, 5, 6), level: 'warn', message: 'links', detail: 'none' };
  assert.equal(formatEntry(entry), '03:04:05.006  WARN   links — none');
});

test('omits the separator when there is no detail', () => {
  const entry = { time: new Date(2026, 0, 2, 3, 4, 5, 6), level: 'info', message: 'ping', detail: '' };
  assert.equal(formatEntry(entry), '03:04:05.006  INFO   ping');
});

test('toText() renders the whole log for Copy all, one entry per line', () => {
  const log = new ActivityLog();
  log.add('info', 'a');
  log.add('error', 'b', 'boom');
  const lines = log.toText().split('\n');
  assert.equal(lines.length, 2);
  assert.match(lines[1], /ERROR {2}b — boom$/);
});

test('stores document-derived text verbatim, without interpreting it', () => {
  // The console renders untrusted text (host errors, file names, field names, URLs). The store
  // must not transform it, and the renderer must use textContent (#74).
  const log = new ActivityLog();
  const nasty = '<img src=x onerror=alert(1)>';
  assert.equal(log.add('error', nasty, nasty).message, nasty);
  assert.ok(log.toText().includes(nasty));
});
