// Unit tests for the native-host diagnostics formatting (see extension/src/host-diagnostics.js).
// Run with: node --test extension/test/host-diagnostics.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import { hostDiagnosticsRows, hostDiagnosticsLines } from '../src/host-diagnostics.js';

const full = {
  host: 'com.pdfeditor.host',
  version: '2.0.1.46',
  runtime: '.NET 8.0.29',
  os: 'Ubuntu 24.04.4 LTS',
  osArchitecture: 'X64',
  processArchitecture: 'X64',
  ocrAvailable: true,
  executablePath: '/opt/pdf-editor-host/PdfEditor.NativeHost',
  utc: '2026-08-08T10:46:51Z',
};

test('a full diagnostics response renders every field as Label: value lines', () => {
  const lines = hostDiagnosticsLines(full);
  assert.ok(lines.includes('Host version: 2.0.1.46'));
  assert.ok(lines.includes('Runtime: .NET 8.0.29'));
  assert.ok(lines.includes('Operating system: Ubuntu 24.04.4 LTS'));
  assert.ok(lines.includes('OCR available: yes'));
  assert.ok(lines.includes('Executable: /opt/pdf-editor-host/PdfEditor.NativeHost'));
});

test('ocrAvailable=false renders as "no", not a blank or "false"', () => {
  const lines = hostDiagnosticsLines({ ...full, ocrAvailable: false });
  assert.ok(lines.includes('OCR available: no'));
});

test('missing fields are dropped, not shown as "undefined"', () => {
  const rows = hostDiagnosticsRows({ version: '2.0.0' });
  assert.deepEqual(rows, [['Host version', '2.0.0']]);
  assert.ok(!hostDiagnosticsLines({ version: '2.0.0' }).some((l) => l.includes('undefined')));
});

test('a null/empty/non-object response yields no rows rather than throwing', () => {
  assert.deepEqual(hostDiagnosticsRows(null), []);
  assert.deepEqual(hostDiagnosticsRows(undefined), []);
  assert.deepEqual(hostDiagnosticsRows('nope'), []);
  assert.deepEqual(hostDiagnosticsLines({}), []);
});
