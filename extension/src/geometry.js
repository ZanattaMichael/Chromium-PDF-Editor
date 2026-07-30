'use strict';

// Coordinate transforms between the displayed page image and the document's unrotated crop box.
//
// The rendered image is the page's crop box (origin x,y; size width×height in PDF user space)
// with the page's clockwise rotation applied. Mapping between the image and the document has to
// account for both the crop-box origin AND the rotation, or redactions and edits land shifted or
// rotated. These two functions are the rotation half, kept pure (no DOM, no viewer state) so the
// rotation logic — the subtlety behind rotated-page placement bugs — can be reasoned about and
// tested on its own.
//
// (fx, fy) are fractions across the *unrotated* crop box — fx from its left, fy from its bottom.
// (u, v) are fractions across the *displayed* image — u from its left, v from its top.

/** Fraction across the displayed image (u,v) → fraction across the unrotated crop box (fx,fy). */
export function displayToPage(rotation, u, v) {
  switch (rotation) {
    case 90: return [v, u];
    case 180: return [1 - u, v];
    case 270: return [1 - v, 1 - u];
    default: return [u, 1 - v];
  }
}

/** Fraction across the unrotated crop box (fx,fy) → fraction across the displayed image (u,v). */
export function pageToDisplay(rotation, fx, fy) {
  switch (rotation) {
    case 90: return [fy, fx];
    case 180: return [1 - fx, fy];
    case 270: return [1 - fy, 1 - fx];
    default: return [fx, 1 - fy];
  }
}
