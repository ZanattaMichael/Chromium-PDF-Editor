'use strict';

const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension } = require('../helpers/harness');
const { buildPdf } = require('../helpers/pdf');

/** @type {Awaited<ReturnType<typeof launchExtension>>} */
let ext;
let fixtureDir;

test.beforeAll(async () => {
  ext = await launchExtension();
  fixtureDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-fixtures-'));
});

test.afterAll(async () => {
  await ext?.close();
  if (fixtureDir) fs.rmSync(fixtureDir, { recursive: true, force: true });
});

function fixture(name, pages, opts) {
  const file = path.join(fixtureDir, name);
  fs.writeFileSync(file, buildPdf(pages, opts));
  return file;
}

// In the continuous-scroll layout each page is `.page[data-page="N"]` with its own image.
const pageImageSel = (n = 1) => `.page[data-page="${n}"] .page-image`;

/** Opens a fresh viewer page and loads the given fixture through the Open button. */
async function openViewerWith(file) {
  const page = await ext.context.newPage();
  await page.goto(ext.viewerUrl);
  const chooser = page.waitForEvent('filechooser');
  await page.click('#btn-open-empty');
  await (await chooser).setFiles(file);
  await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
  return page;
}

// PDF user-space coordinate helpers. The page box defaults to A4 at the origin; pass a
// [llx, lly, urx, ury] box to work with pages whose MediaBox does not start at (0,0) — the
// rendered image's bottom-left is (llx, lly), so mappings must subtract that origin.
const A4 = [0, 0, 595, 842];

/** Drags a rectangle on the overlay, in PDF user-space coordinates. */
async function dragPdfRect(page, { x, y, width, height }, mediaBox = A4) {
  const [llx, lly, urx, ury] = mediaBox;
  const box = await page.locator(pageImageSel(1)).boundingBox();
  const scale = box.width / (urx - llx);
  const cssX = (pdfX) => box.x + (pdfX - llx) * scale;
  const cssY = (pdfY) => box.y + (ury - pdfY) * scale;
  await page.mouse.move(cssX(x), cssY(y + height));
  await page.mouse.down();
  await page.mouse.move(cssX(x + width), cssY(y), { steps: 5 });
  await page.mouse.up();
}

/** Samples a rendered-page pixel at a PDF user-space point (returns [r,g,b,a]). */
async function pixelAt(page, pdfX, pdfY, mediaBox = A4) {
  return page.evaluate(async ([px, py, [llx, lly, urx, ury]]) => {
    const img = document.querySelector('.page[data-page="1"] .page-image');
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(img, 0, 0);
    const scale = img.naturalWidth / (urx - llx);
    const data = ctx.getImageData(
      Math.round((px - llx) * scale), Math.round((ury - py) * scale), 1, 1).data;
    return [...data];
  }, [pdfX, pdfY, mediaBox]);
}

/** Fills the promptDialog() form (inputs in creation order) and confirms. */
async function fillDialog(page, values, confirmText) {
  const dialog = page.locator('dialog#modal');
  await expect(dialog).toBeVisible();
  const inputs = dialog.locator('input');
  for (let i = 0; i < values.length; i++) {
    if (values[i] !== null) await inputs.nth(i).fill(values[i]);
  }
  await dialog.getByRole('button', { name: confirmText }).click();
}

test.describe('PDF Editor end-to-end (extension + native host)', () => {
  test('options page reports the native host as connected', async () => {
    const page = await ext.context.newPage();
    await page.goto(ext.optionsUrl);
    await expect(page.locator('#host-status')).toContainText('✓ connected');
    await page.close();
  });

  test('opens a PDF and renders the first page', async () => {
    const file = fixture('render.pdf', [[{ text: 'Hello Playwright', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await expect(page.locator('#page-label')).toHaveText('1 / 1');
    // The rendered page is white paper, not a blank/black failure.
    expect(await pixelAt(page, 300, 400)).toEqual([255, 255, 255, 255]);
    await page.close();
  });

  test('redaction: draw, preview window, apply — content removed and box painted', async () => {
    const file = fixture('redact.pdf', [[
      { text: 'TOP SECRET DATA', x: 72, y: 700 },
      { text: 'public information', x: 72, y: 600 },
    ]]);
    const page = await openViewerWith(file);

    await page.click('#tool-redact');
    await dragPdfRect(page, { x: 60, y: 690, width: 260, height: 34 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);

    // Preview window renders the redacted copy without touching the document.
    await page.click('#redact-preview');
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('.preview-pages img')).toHaveCount(1);
    await expect(dialog).toContainText('permanently');

    // Apply from the preview.
    await dialog.getByRole('button', { name: 'Apply redaction' }).click();
    await expect(page.locator('#status')).toContainText('content removed');
    await expect(page.locator('#redact-list li')).toHaveCount(0);

    // The region renders as opaque black; untouched text area stays white.
    expect(await pixelAt(page, 180, 707)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 400, 400)).toEqual([255, 255, 255, 255]);
    await page.close();
  });

  test('continuous scroll: all pages stack and the counter tracks the visible page', async () => {
    const file = fixture('scroll.pdf', [
      [{ text: 'Page one', x: 72, y: 700 }],
      [{ text: 'Page two', x: 72, y: 700 }],
      [{ text: 'Page three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await expect(page.locator('.page')).toHaveCount(3); // every page laid out in the column
    await expect(page.locator('#page-label')).toHaveText('1 / 3');
    await expect(page.locator('#btn-prev')).toBeDisabled();

    // Paging forward scrolls the next page into view; the counter follows what's visible,
    // and that page renders lazily once it's near the viewport.
    await page.click('#btn-next');
    await expect(page.locator('#page-label')).toHaveText('2 / 3');
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);

    await page.click('#btn-next');
    await expect(page.locator('#page-label')).toHaveText('3 / 3');
    await expect(page.locator('#btn-next')).toBeDisabled();

    await page.click('#btn-prev');
    await expect(page.locator('#page-label')).toHaveText('2 / 3');
    await page.close();
  });

  test('redaction works on a page other than the first (per-page overlays)', async () => {
    // Text near the top of page 2 so it stays on-screen once page 2 is scrolled to the top.
    const file = fixture('multi-redact.pdf', [
      [{ text: 'first page', x: 72, y: 700 }],
      [{ text: 'SECOND SECRET', x: 72, y: 800 }],
    ]);
    const page = await openViewerWith(file);

    await page.click('#tool-redact');
    // Top-align page 2 *instantly* (no smooth-scroll animation, so boundingBox() below is
    // settled and the drag can't land on stale coordinates), then let it render.
    await page.evaluate(() =>
      document.querySelector('.page[data-page="2"]').scrollIntoView({ block: 'start', behavior: 'instant' }));
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);
    await expect(page.locator('#page-label')).toHaveText('2 / 2');

    // Draw on page 2's own overlay, in that page's A4 user-space (near its top).
    const box = await page.locator(pageImageSel(2)).boundingBox();
    const scale = box.width / 595;
    const cx = (px) => box.x + px * scale;
    const cy = (py) => box.y + (842 - py) * scale;
    await page.mouse.move(cx(60), cy(790));
    await page.mouse.down();
    await page.mouse.move(cx(320), cy(825), { steps: 5 });
    await page.mouse.up();

    await expect(page.locator('#redact-list li')).toHaveText(/page 2/);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // Page 2's region renders black — proving the drag mapped to page 2, not page 1.
    const pixel = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="2"] .page-image');
      await img.decode();
      const canvas = document.createElement('canvas');
      canvas.width = img.naturalWidth;
      canvas.height = img.naturalHeight;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const s = img.naturalWidth / 595;
      return [...ctx.getImageData(Math.round(180 * s), Math.round((842 - 807) * s), 1, 1).data];
    });
    expect(pixel).toEqual([0, 0, 0, 255]);
    await page.close();
  });

  test('redaction lands correctly on a page whose box origin is not (0,0)', async () => {
    // Regression: the viewer used to assume the page's lower-left is (0,0). PDFium renders
    // the MediaBox at its true origin, so on a box like [100 200 695 1042] every redaction
    // landed offset by (100,200). Draw a box over the text and prove the *text's* location
    // (not the shifted one) is what gets blacked out.
    const box = [100, 200, 695, 1042];
    const file = fixture('offset-redact.pdf',
      [[{ text: 'OFFSET SECRET', x: 150, y: 900 }]], { mediaBox: box });
    const page = await openViewerWith(file);

    await page.click('#tool-redact');
    await dragPdfRect(page, { x: 140, y: 892, width: 240, height: 30 }, box);
    await expect(page.locator('#redact-list li')).toHaveCount(1);

    await page.click('#redact-preview');
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Apply redaction' }).click();
    await expect(page.locator('#status')).toContainText('content removed');

    // The redaction box is painted exactly over the text (origin honoured), and a point
    // outside it stays white.
    expect(await pixelAt(page, 200, 905, box)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 600, 400, box)).toEqual([255, 255, 255, 255]);
    await page.close();
  });

  test('redaction on a rotated (/Rotate 90) page lands where it is drawn', async () => {
    // On a rotated page PDFium renders a width/height-swapped image; if the viewer ignores
    // the rotation the box is drawn in one place and redacted in another. Draw a box at a
    // known spot on the *displayed* image and prove that exact spot goes black — a full
    // display -> PDF -> redact -> render round-trip that only closes if rotation is handled.
    const file = fixture('rotated.pdf', [[{ text: 'rotated secret', x: 120, y: 400 }]], { rotate: 90 });
    const page = await openViewerWith(file);

    await page.click('#tool-redact');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    // Landscape image (rotated): draw a rectangle across the middle in display coordinates.
    await page.mouse.move(box.x + box.width * 0.30, box.y + box.height * 0.40);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.62, box.y + box.height * 0.62, { steps: 5 });
    await page.mouse.up();

    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // The centre of the drawn rectangle (display fractions ~0.46, 0.51) is now opaque black.
    const pixel = await page.evaluate(async (sel) => {
      const img = document.querySelector(sel);
      await img.decode();
      const canvas = document.createElement('canvas');
      canvas.width = img.naturalWidth;
      canvas.height = img.naturalHeight;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const px = Math.round(0.46 * img.naturalWidth);
      const py = Math.round(0.51 * img.naturalHeight);
      return [...ctx.getImageData(px, py, 1, 1).data];
    }, pageImageSel(1));
    expect(pixel).toEqual([0, 0, 0, 255]);
    await page.close();
  });

  test('text edit: reads existing text, replaces it in place', async () => {
    const file = fixture('edit.pdf', [[{ text: 'Amount Due: $500', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await page.click('#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 250, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('Amount Due: $500');

    await page.fill('#edit-text', 'Amount Due: $750 (revised)');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Re-selecting the same region proves the old text is gone from the file.
    await page.click('#tool-edit');
    await dragPdfRect(page, { x: 60, y: 685, width: 300, height: 40 });
    await expect(page.locator('#edit-text')).toHaveValue(/\$750 \(revised\)/);
    await expect(page.locator('#edit-text')).not.toHaveValue(/\$500/);
    await page.close();
  });

  test('text edit: change the font family, size, and style', async () => {
    const file = fixture('font-edit.pdf', [[{ text: 'Plain Heading', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await page.click('#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 260, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('Plain Heading');
    // Plain Helvetica text pre-fills the controls with the sans-serif default.
    await expect(page.locator('#edit-font')).toHaveValue('helvetica');
    await expect(page.locator('#edit-bold')).not.toHaveClass(/active/);

    // Change the text, switch to Times, bump the size, and turn on bold.
    await page.fill('#edit-text', 'Styled Heading');
    await page.selectOption('#edit-font', 'times');
    await page.fill('#edit-size', '20');
    await page.click('#edit-bold');
    await expect(page.locator('#edit-bold')).toHaveClass(/active/);
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Re-selecting the region shows the new text, and the detected font/style round-trips.
    await page.click('#tool-edit');
    await dragPdfRect(page, { x: 55, y: 685, width: 300, height: 45 });
    await expect(page.locator('#edit-text')).toHaveValue(/Styled Heading/);
    await expect(page.locator('#edit-font')).toHaveValue('times');
    await expect(page.locator('#edit-bold')).toHaveClass(/active/);
    await page.close();
  });

  test('find & replace across the document', async () => {
    const file = fixture('replace.pdf', [[
      { text: 'Contract with OldCorp', x: 72, y: 700 },
      { text: 'OldCorp shall deliver', x: 72, y: 650 },
    ]]);
    const page = await openViewerWith(file);

    await page.click('#btn-find');
    await fillDialog(page, ['OldCorp', 'NewCorp'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 2 occurrences');
    await page.close();
  });

  test('merge appends another document', async () => {
    const one = fixture('merge-base.pdf', [[{ text: 'Base page', x: 72, y: 700 }]]);
    const two = fixture('merge-extra.pdf', [
      [{ text: 'Extra page 1', x: 72, y: 700 }],
      [{ text: 'Extra page 2', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(one);

    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-merge');
    await (await chooser).setFiles(two);
    await expect(page.locator('#status')).toContainText('Merged 1 document');
    await expect(page.locator('#page-label')).toHaveText('1 / 3');
    await page.close();
  });

  test('password protection encrypts the document', async () => {
    const file = fixture('protect.pdf', [[{ text: 'classified', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await page.click('#btn-protect');
    await fillDialog(page, ['s3cret', null], 'Encrypt');
    await expect(page.locator('#status')).toContainText('encrypted');
    await expect(page.locator('#badges .badge.locked')).toBeVisible();

    // The document stays editable with the retained password (re-render works).
    await page.click('#btn-zoom-in');
    await expect(page.locator('#zoom-label')).toHaveText('125%');
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    await page.close();
  });

  test('drawn signature is placed on the page', async () => {
    // Keep the signature area in the upper part of the page: the drag helper works in
    // viewport coordinates, and A4 at 100% zoom extends below the fold.
    const file = fixture('sign-image.pdf', [[{ text: 'Sign here:', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await page.click('#tool-sign');
    await dragPdfRect(page, { x: 200, y: 640, width: 160, height: 50 });
    await expect(page.locator('#panel-sign')).toBeVisible();

    // Scribble on the signature pad.
    const pad = await page.locator('#sign-pad').boundingBox();
    await page.mouse.move(pad.x + 12, pad.y + pad.height - 20);
    await page.mouse.down();
    await page.mouse.move(pad.x + pad.width / 2, pad.y + 14, { steps: 8 });
    await page.mouse.move(pad.x + pad.width - 12, pad.y + pad.height - 20, { steps: 8 });
    await page.mouse.up();

    await page.click('#sign-apply');
    await expect(page.locator('#status')).toContainText('Signature placed');
    await page.close();
  });

  test('digital signature with a generated self-signed certificate', async () => {
    const file = fixture('sign-digital.pdf', [[{ text: 'Agreement', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await page.click('#btn-digital');
    await fillDialog(page, ['Approval', '', 'certpw'], 'Continue');

    const dialog = page.locator('dialog#modal');
    await dialog.getByRole('button', { name: 'Create self-signed' }).click();
    await fillDialog(page, ['E2E Signer', 'certpw'], 'Create & sign');

    await expect(page.locator('#status')).toContainText('digitally signed');
    await expect(page.locator('#badges .badge.signed')).toContainText('E2E Signer');
    await expect(page.locator('#badges .badge.signed')).toContainText('✓');
    await page.close();
  });

  test('save falls back to the downloads API, undo restores the previous state', async () => {
    const file = fixture('save.pdf', [[{ text: 'Original', x: 72, y: 700 }]]);
    const page = await ext.context.newPage();
    // Force the chrome.downloads path (the file-picker dialog can't be driven headlessly).
    await page.addInitScript(() => { delete window.showSaveFilePicker; });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // Make one change so there is something to save/undo.
    await page.click('#btn-find');
    await fillDialog(page, ['Original', 'Changed'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');

    await page.click('#btn-save');
    await expect(page.locator('#status')).toContainText('Saving via downloads');

    await expect(page.locator('#btn-undo')).toBeEnabled();
    await page.click('#btn-undo');
    await expect(page.locator('#status')).toContainText('Undid last change');
    await page.close();
  });
});
