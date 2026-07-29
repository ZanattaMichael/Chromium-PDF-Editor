// Tests for the interpolated-innerHTML rule (see scripts/check-innerhtml.mjs, issue #74).
// Run with: node --test extension/test/checkInnerHtml.test.mjs
//
// The last test is the rule itself: it scans the real extension/src tree and fails CI on any new
// violation. The rest exist so that rule is trustworthy — a detector that answers "fine" for
// everything passes its own happy-path tests perfectly.
import assert from 'node:assert/strict';
import test from 'node:test';

import {
  EXTENSION_SRC, checkDirectory, collectFiles, findViolations, isStaticLiteral, maskSource,
} from '../../scripts/check-innerhtml.mjs';

const violationsIn = (src) => findViolations(src, 'test.js');

// ---------------------------------------------------------------- rejected

test('rejects a template literal that interpolates', () => {
  const found = violationsIn('modal.innerHTML = `<h2>${title}</h2>`;');
  assert.equal(found.length, 1);
  assert.equal(found[0].sink, 'innerHTML');
  assert.equal(found[0].line, 1);
});

test('rejects a bare variable', () => {
  assert.equal(violationsIn('el.innerHTML = markup;').length, 1);
});

test('rejects concatenation with a variable', () => {
  assert.equal(violationsIn("el.innerHTML = '<b>' + name + '</b>';").length, 1);
});

test('rejects a function call', () => {
  assert.equal(violationsIn('el.innerHTML = render(field);').length, 1);
});

test('rejects `+=` as well as `=`', () => {
  assert.equal(violationsIn('el.innerHTML += row;').length, 1);
});

test('rejects outerHTML the same way', () => {
  const found = violationsIn('el.outerHTML = `<i>${x}</i>`;');
  assert.equal(found.length, 1);
  assert.equal(found[0].sink, 'outerHTML');
});

test('rejects insertAdjacentHTML with a non-literal payload', () => {
  const found = violationsIn("el.insertAdjacentHTML('beforeend', `<li>${url}</li>`);");
  assert.equal(found.length, 1);
  assert.equal(found[0].sink, 'insertAdjacentHTML');
});

test('rejects a conditional expression', () => {
  assert.equal(violationsIn("el.innerHTML = ok ? '<b>y</b>' : '<b>n</b>';").length, 1);
});

test('reports the line each violation is on', () => {
  const found = violationsIn('const a = 1;\n// nothing\nel.innerHTML = value;\n');
  assert.equal(found.length, 1);
  assert.equal(found[0].line, 3);
});

// ---------------------------------------------------------------- allowed

test("allows innerHTML = '' — the ~20 places that clear a container", () => {
  assert.deepEqual(violationsIn("list.innerHTML = '';"), []);
  assert.deepEqual(violationsIn('list.innerHTML = "";'), []);
  assert.deepEqual(violationsIn('list.innerHTML = ``;'), []);
});

test('allows a static string literal', () => {
  assert.deepEqual(violationsIn("modal.innerHTML = '<h2>Certificate</h2>';"), []);
});

test('allows several static literals concatenated across lines', () => {
  const src = "modal.innerHTML =\n  '<h2>Merge</h2>' +\n  '<p class=\"muted\">Drag to order.</p>';";
  assert.deepEqual(violationsIn(src), []);
});

test('allows a template literal that interpolates nothing', () => {
  assert.deepEqual(violationsIn('el.innerHTML = `<hr>`;'), []);
});

test('allows insertAdjacentHTML with only literal arguments', () => {
  assert.deepEqual(violationsIn("el.insertAdjacentHTML('beforeend', '<hr>');"), []);
});

test('ignores a comparison, which assigns nothing', () => {
  assert.deepEqual(violationsIn('if (el.innerHTML === markup) return;'), []);
});

// ------------------------------------------------- masking (no false positives)

test('does not flag the word innerHTML inside a comment', () => {
  assert.deepEqual(violationsIn('// never do el.innerHTML = markup;\nconst a = 1;'), []);
  assert.deepEqual(violationsIn('/* el.innerHTML = markup; */\nconst a = 1;'), []);
});

test('does not flag the word innerHTML inside a string', () => {
  assert.deepEqual(violationsIn("const warn = 'el.innerHTML = markup';"), []);
});

test('masking keeps offsets and hides literal bodies', () => {
  const masked = maskSource("const a = 'xy';");
  assert.equal(masked.length, "const a = 'xy';".length);
  assert.equal(masked, "const a = 'SS';");
});

test('masking marks an interpolating template so it cannot pass as a literal', () => {
  assert.ok(!isStaticLiteral(maskSource('`a${b}c`')));
  assert.ok(isStaticLiteral(maskSource('`abc`')));
});

test('an escaped quote does not end a literal early', () => {
  assert.deepEqual(violationsIn("el.innerHTML = '<p>don\\'t</p>';"), []);
});

// --------------------------------------------------------------- the rule

test('scans every JS file under extension/src', () => {
  const files = collectFiles(EXTENSION_SRC);
  assert.ok(files.some((f) => f.endsWith('viewer.js')), 'viewer.js must be scanned');
  assert.ok(files.some((f) => f.endsWith('activity-log.js')), 'activity-log.js must be scanned');
});

test('extension/src contains no interpolated innerHTML/outerHTML/insertAdjacentHTML', () => {
  const violations = checkDirectory(EXTENSION_SRC);
  assert.deepEqual(
    violations.map((v) => `${v.file}:${v.line} ${v.message}`), [],
    'Build the node with createElement/textContent instead — see issue #74.');
});
