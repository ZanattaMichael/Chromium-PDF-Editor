// Unit tests for the pure page/display coordinate transforms (see extension/src/geometry.js).
// Run with: node --test extension/test/geometry.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';

import { displayToPage, pageToDisplay } from '../src/geometry.js';

const ROTATIONS = [0, 90, 180, 270];
const POINTS = [[0, 0], [1, 0], [0, 1], [1, 1], [0.3, 0.7], [0.25, 0.9]];

test('pageToDisplay is the inverse of displayToPage for every rotation', () => {
  for (const r of ROTATIONS) {
    for (const [u, v] of POINTS) {
      const [fx, fy] = displayToPage(r, u, v);
      const [u2, v2] = pageToDisplay(r, fx, fy);
      assert.ok(Math.abs(u2 - u) < 1e-9 && Math.abs(v2 - v) < 1e-9,
        `rotation ${r}: (${u},${v}) → (${fx},${fy}) → (${u2},${v2})`);
    }
  }
});

test('rotation 0 flips only the vertical axis (image top-down vs page bottom-up)', () => {
  // Display top-left (0,0) is the crop box's top-left: fx=0, fy=1.
  assert.deepEqual(displayToPage(0, 0, 0), [0, 1]);
  assert.deepEqual(displayToPage(0, 1, 1), [1, 0]);
});

test('rotation 90 maps the display top-left to the crop box bottom-left', () => {
  // A quarter turn clockwise: the image's top-left corner is the unrotated box's bottom-left.
  assert.deepEqual(displayToPage(90, 0, 0), [0, 0]);   // fx=0 (left), fy=0 (bottom)
  assert.deepEqual(displayToPage(90, 1, 0), [0, 1]);   // moving down the image → along the box
});

test('the corners of the display map to the four corners of the box, for every rotation', () => {
  for (const r of ROTATIONS) {
    const mapped = POINTS.slice(0, 4).map(([u, v]) => displayToPage(r, u, v).join(','));
    assert.equal(new Set(mapped).size, 4, `rotation ${r} collapsed corners: ${mapped}`);
  }
});
