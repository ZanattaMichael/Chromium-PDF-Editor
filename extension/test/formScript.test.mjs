// Unit tests for the minimal form-button script interpreter (see extension/src/formScript.js).
// Run with: node --test extension/test/formScript.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import { runFormScript } from '../src/formScript.js';

function valuesOf(map) {
  return (name) => map[name] ?? '';
}

test('calculates a sum across two fields into a third', () => {
  const result = runFormScript(
    'this.getField("total").value = this.getField("a").value + this.getField("b").value;',
    valuesOf({ a: '2', b: '3' }),
  );
  assert.equal(result.ok, true);
  assert.deepEqual(result.sets, [{ name: 'total', value: '5' }]);
});

test('supports Number()-wrapped operands and multiple operators', () => {
  const result = runFormScript(
    'this.getField("total").value = Number(this.getField("a").value) * 2 + this.getField("b").value;',
    valuesOf({ a: '4', b: '1' }),
  );
  assert.equal(result.ok, true);
  assert.deepEqual(result.sets, [{ name: 'total', value: '9' }]);
});

test('treats a blank/non-numeric field as 0', () => {
  const result = runFormScript(
    'this.getField("total").value = this.getField("a").value + this.getField("missing").value;',
    valuesOf({ a: '10' }),
  );
  assert.equal(result.ok, true);
  assert.deepEqual(result.sets, [{ name: 'total', value: '10' }]);
});

test('supports multiple statements separated by semicolons', () => {
  const result = runFormScript(
    'this.getField("total").value = this.getField("a").value + this.getField("b").value; ' +
      'this.getField("extra").display = display.hidden;',
    valuesOf({ a: '1', b: '1' }),
  );
  assert.equal(result.ok, true);
  assert.deepEqual(result.sets, [{ name: 'total', value: '2' }]);
  assert.deepEqual(result.display, [{ name: 'extra', hidden: true }]);
});

test('supports a plain display toggle', () => {
  const hide = runFormScript('this.getField("extra").display = display.hidden;', valuesOf({}));
  assert.equal(hide.ok, true);
  assert.deepEqual(hide.display, [{ name: 'extra', hidden: true }]);

  const show = runFormScript('this.getField("extra").display = display.visible;', valuesOf({}));
  assert.equal(show.ok, true);
  assert.deepEqual(show.display, [{ name: 'extra', hidden: false }]);
});

test('supports resetForm()', () => {
  const result = runFormScript('this.resetForm();', valuesOf({}));
  assert.equal(result.ok, true);
  assert.equal(result.reset, true);
});

test('honours parentheses and subtraction/division', () => {
  const result = runFormScript(
    'this.getField("total").value = (this.getField("a").value - this.getField("b").value) / 2;',
    valuesOf({ a: '10', b: '4' }),
  );
  assert.equal(result.ok, true);
  assert.deepEqual(result.sets, [{ name: 'total', value: '3' }]);
});

test('rejects scripts outside the supported grammar instead of guessing', () => {
  const cases = [
    'if (this.getField("a").value > 0) this.getField("b").value = 1;',
    'this.getField("a").value = "literal string";',
    'this.submitForm("https://example.com");',
    'this.getField("a").value = someFunction(1);',
];
  for (const script of cases) {
    const result = runFormScript(script, valuesOf({}));
    assert.equal(result.ok, false, `expected "${script}" to be unsupported`);
  }
});

test('empty script is unsupported', () => {
  assert.equal(runFormScript('', valuesOf({})).ok, false);
  assert.equal(runFormScript('   ', valuesOf({})).ok, false);
});

test('supports app.alert with a string argument', () => {
  const result = runFormScript("app.alert('thanks!');", valuesOf({}));
  assert.equal(result.ok, true);
  assert.deepEqual(result.alerts, ['thanks!']);
});

test('supports app.alert with trailing icon/type arguments', () => {
  const result = runFormScript('app.alert("Saved", 3);', valuesOf({}));
  assert.equal(result.ok, true);
  assert.deepEqual(result.alerts, ['Saved']);
});

test('supports the app.alert({cMsg: ...}) named-argument form', () => {
  const result = runFormScript('app.alert({cMsg: "Done", cTitle: "Info"});', valuesOf({}));
  assert.equal(result.ok, true);
  assert.deepEqual(result.alerts, ['Done']);
});

test('app.alert combines with field updates in one script', () => {
  const result = runFormScript(
    'this.getField("total").value = this.getField("a").value + 1; app.alert("done");',
    valuesOf({ a: '4' }));
  assert.equal(result.ok, true);
  assert.deepEqual(result.sets, [{ name: 'total', value: '5' }]);
  assert.deepEqual(result.alerts, ['done']);
});

test('a non-alert app.* call is still unsupported', () => {
  assert.equal(runFormScript('app.launchURL("https://example.com");', valuesOf({})).ok, false);
});
