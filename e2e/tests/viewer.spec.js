'use strict';

const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension } = require('../helpers/harness');
const {
  buildPdf, buildLeftoverCtmPdf, buildFormPdf, buildJavaScriptPdf, buildLinkPdf,
} = require('../helpers/pdf');

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

/** Clicks a toolbar control, first opening its Reading/Editing dropdown when it lives in one. */
async function ui(page, sel) {
  const triggerId = await page.evaluate((s) => {
    const el = document.querySelector(s);
    const menu = el && el.closest('.menu-group');
    return menu ? menu.querySelector('.menu-trigger').id : null;
  }, sel);
  if (triggerId) await page.click('#' + triggerId);
  await page.click(sel);
}

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
    await expect(page.locator('#page-input')).toHaveValue('1');
    await expect(page.locator('#page-total')).toHaveText('1');
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

    await ui(page, '#tool-redact');
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

  test('search & mark: finds every occurrence of a phrase, marks boxes, redacts them', async () => {
    // Two copies of the secret word plus an unrelated line. Searching marks both copies as
    // redaction boxes; applying blacks out both spots and leaves the other line untouched.
    const file = fixture('search-redact.pdf', [[
      { text: 'CONFIDENTIAL summary', x: 72, y: 700 },
      { text: 'again CONFIDENTIAL here', x: 72, y: 600 },
      { text: 'ordinary public line', x: 72, y: 500 },
    ]]);
    const page = await openViewerWith(file);

    // The Redact panel is shown by the redact tool; the search box lives inside it.
    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'CONFIDENTIAL');
    await page.click('#redact-search-btn');

    // Both occurrences are marked as boxes (and the input is cleared for the next search).
    await expect(page.locator('#redact-list li')).toHaveCount(2);
    await expect(page.locator('#redact-search-text')).toHaveValue('');

    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // Both words are blacked out; the ordinary line survives (its glyphs are not a black box)
    // and blank paper stays white.
    expect(await pixelAt(page, 90, 704)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 120, 604)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 90, 504)).not.toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 400, 504)).toEqual([255, 255, 255, 255]);
    await page.close();
  });

  test('redaction lands on the text on a Chrome/Google-Docs PDF (leftover transform)', async () => {
    // Regression for the reported bug: Chrome / Google-Docs-exported PDFs leave a scale+flip
    // matrix active at the end of the page content. The black box used to inherit it and land
    // scaled/flipped away, while the text under it was correctly removed. Here we search for the
    // word (absolute coordinates, no screen mapping), redact it, and prove the black box covers
    // exactly the pixels the word occupied — end-to-end through the extension and native host.
    const file = path.join(fixtureDir, 'leftover-ctm.pdf');
    fs.writeFileSync(file, buildLeftoverCtmPdf('SECRET'));
    const page = await openViewerWith(file);

    // Find the centroid of the word's dark pixels on the rendered page (natural-image pixels).
    const darkCentroid = async () => page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const { data, width, height } = ctx.getImageData(0, 0, c.width, c.height);
      let sx = 0, sy = 0, n = 0;
      for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
          const i = (y * width + x) * 4;
          if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100) { sx += x; sy += y; n++; }
        }
      }
      return n ? { x: Math.round(sx / n), y: Math.round(sy / n), n, width, height } : null;
    });

    const before = await darkCentroid();
    expect(before).not.toBeNull();       // the word actually renders
    expect(before.n).toBeGreaterThan(50);

    // Search + mark + apply through the real UI / native host.
    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'SECRET');
    await page.click('#redact-search-btn');
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // The pixel at the word's former centroid is now opaque black — the box landed on the word.
    const pixel = await page.evaluate(async (pt) => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      // Sample relative to the (possibly re-rendered at a different scale) image.
      const x = Math.round(pt.x / pt.width * c.width);
      const y = Math.round(pt.y / pt.height * c.height);
      return [...ctx.getImageData(x, y, 1, 1).data];
    }, before);
    expect(pixel).toEqual([0, 0, 0, 255]);
    await page.close();
  });

  test('search & mark: reports when a phrase is not found and marks nothing', async () => {
    const file = fixture('search-none.pdf', [[{ text: 'nothing to hide here', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'MISSING');
    await page.click('#redact-search-btn');

    await expect(page.locator('#status')).toContainText('No matches');
    await expect(page.locator('#redact-list li')).toHaveCount(0);
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
    await expect(page.locator('#page-input')).toHaveValue('1');
    await expect(page.locator('#page-total')).toHaveText('3');
    await expect(page.locator('#btn-prev')).toBeDisabled();

    // Paging forward scrolls the next page into view; the counter follows what's visible,
    // and that page renders lazily once it's near the viewport.
    await page.click('#btn-next');
    await expect(page.locator('#page-input')).toHaveValue('2');
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);

    await page.click('#btn-next');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await expect(page.locator('#btn-next')).toBeDisabled();

    await page.click('#btn-prev');
    await expect(page.locator('#page-input')).toHaveValue('2');
    await page.close();
  });

  test('editable page counter: typing a number jumps to that page', async () => {
    const file = fixture('jump.pdf', [
      [{ text: 'One', x: 72, y: 700 }],
      [{ text: 'Two', x: 72, y: 700 }],
      [{ text: 'Three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await page.fill('#page-input', '3');
    await page.press('#page-input', 'Enter');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await expect(page.locator('#btn-next')).toBeDisabled();

    // Out-of-range input is clamped/rejected rather than navigating off the end.
    await page.fill('#page-input', '99');
    await page.press('#page-input', 'Enter');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await page.close();
  });

  test('thumbnail sidebar: toggles, lists every page, and navigates on click', async () => {
    const file = fixture('thumbs.pdf', [
      [{ text: 'Alpha', x: 72, y: 700 }],
      [{ text: 'Beta', x: 72, y: 700 }],
      [{ text: 'Gamma', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await expect(page.locator('#thumbnails')).toBeHidden();
    await page.click('#btn-sidebar');
    await expect(page.locator('#thumbnails')).toBeVisible();
    await expect(page.locator('#thumbnails .thumb')).toHaveCount(3);
    // The first thumbnail renders an image, and page 1 is marked current.
    await expect(page.locator('#thumbnails .thumb[data-page="1"] img')).toHaveAttribute('src', /data:image\/png/);
    await expect(page.locator('#thumbnails .thumb[data-page="1"]')).toHaveClass(/current/);

    // Clicking a thumbnail navigates to that page.
    await page.click('#thumbnails .thumb[data-page="3"]');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await expect(page.locator('#thumbnails .thumb[data-page="3"]')).toHaveClass(/current/);

    await page.click('#btn-sidebar');
    await expect(page.locator('#thumbnails')).toBeHidden();
    await page.close();
  });

  test('rotate: turns the current page a quarter turn (portrait -> landscape)', async () => {
    const file = fixture('rotate.pdf', [[{ text: 'Portrait', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    const portrait = await page.locator('.page[data-page="1"]').boundingBox();
    expect(portrait.height).toBeGreaterThan(portrait.width); // A4 starts portrait

    await ui(page, '#btn-rotate-right');
    await expect(page.locator('#status')).toContainText('Rotated page 1');

    // After a 90° turn the laid-out page is landscape (width/height swapped).
    await expect.poll(async () => {
      const b = await page.locator('.page[data-page="1"]').boundingBox();
      return b.width > b.height;
    }).toBe(true);
    await page.close();
  });

  test('add text: click to place a text box, type, and stamp it onto the page', async () => {
    const file = fixture('addtext.pdf', [[{ text: 'background', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-text');
    // Click (no drag) on the page to drop a default text box near the top.
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    await page.mouse.click(box.x + 120 * scale, box.y + (842 - 700) * scale);

    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-title')).toHaveText('Add text');
    await page.fill('#edit-text', 'STAMPED CAPTION');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text added');

    // The new text is really in the document (and the original still there).
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 675, width: 320, height: 45 });
    await expect(page.locator('#edit-text')).toHaveValue(/STAMPED CAPTION/);
    await page.close();
  });

  test('draw: freehand strokes are baked onto the page in the chosen colour', async () => {
    const file = fixture('draw.pdf', [[{ text: 'canvas', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-draw');
    await expect(page.locator('#panel-draw')).toBeVisible();
    // Use a pure-green pen so we can detect it unambiguously.
    await page.fill('#draw-color', '#00ff00');
    await page.fill('#draw-width', '8');

    // Draw a stroke straight across the middle of the page (display coordinates).
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const midY = box.y + box.height * 0.5;
    await page.mouse.move(box.x + box.width * 0.2, midY);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.4, midY, { steps: 6 });
    await page.mouse.move(box.x + box.width * 0.7, midY, { steps: 6 });
    await page.mouse.up();

    await page.click('#draw-apply');
    await expect(page.locator('#status')).toContainText('stroke');

    // A green pixel is now baked into the rendered page along the stroke.
    const green = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const { data, width, height } = ctx.getImageData(0, 0, c.width, c.height);
      for (let i = 0; i < data.length; i += 4) {
        if (data[i] < 120 && data[i + 1] > 150 && data[i + 2] < 120) return true;
      }
      return false;
    });
    expect(green).toBe(true);
    await page.close();
  });

  test('forms: lists AcroForm fields, fills a value, and reads it back', async () => {
    const file = path.join(fixtureDir, 'form.pdf');
    fs.writeFileSync(file, buildFormPdf('fullName', ''));
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    const field = page.locator('#forms-list [data-field="fullName"]');
    await expect(field).toHaveCount(1);

    await field.fill('Alan Turing');
    await page.click('#forms-apply');
    await expect(page.locator('#status')).toContainText('Form filled');

    // Re-opening the forms panel shows the value persisted into the document.
    await ui(page, '#btn-forms');
    await expect(page.locator('#forms-list [data-field="fullName"]')).toHaveValue('Alan Turing');
    await page.close();
  });

  test('forms: insert a new text field by drawing a box', async () => {
    const file = fixture('insertfield.pdf', [[{ text: 'blank form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    await page.selectOption('#field-type', 'text');
    await page.fill('#field-name', 'signature_name');
    await page.click('#field-place');
    await expect(page.locator('#status')).toContainText('Drag a box');

    await dragPdfRect(page, { x: 100, y: 600, width: 220, height: 24 });

    // The forms panel reopens and lists the newly inserted field.
    await expect(page.locator('#forms-list [data-field="signature_name"]')).toHaveCount(1);
    await page.close();
  });

  test('safety: JavaScript is detected, flagged with its source, and stripped by default', async () => {
    const file = path.join(fixtureDir, 'hasjs.pdf');
    fs.writeFileSync(file, buildJavaScriptPdf("app.alert('DISTINCTIVE_MARKER_123');"));
    const page = await openViewerWith(file);

    // The active-content badge appears and reads "disabled" by default.
    const badge = page.locator('#badges .badge.warn', { hasText: 'JavaScript' });
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('disabled');

    // The details dialog points at the actual script source and lets the user opt in to keeping it.
    await badge.click();
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('JavaScript');
    await expect(dialog.locator('.safety-samples')).toContainText('DISTINCTIVE_MARKER_123');
    await dialog.getByRole('button', { name: /Enable \(keep\)/ }).click();
    await expect(badge).toContainText('kept');
    await page.close();
  });

  test('links: a document with URLs warns, disables them, and can list the source', async () => {
    // Links are detected and disabled by default (a warning badge), with the panel listing every
    // URL (the source) and an opt-in to keep them.
    const file = path.join(fixtureDir, 'links.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://github.com/example/repo'));
    const page = await openViewerWith(file);

    // A warning badge appears reading "disabled".
    const badge = page.locator('#badges .badge.warn', { hasText: 'links' });
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('disabled');

    // Clicking it opens the Links panel, which lists the URL and offers to enable it.
    await badge.click();
    await expect(page.locator('#panel-links')).toBeVisible();
    await expect(page.locator('#links-list')).toContainText('github.com/example/repo');
    await expect(page.locator('#links-enable')).not.toBeChecked();

    // Enabling flips the badge to "enabled".
    await page.locator('#links-enable').check();
    await expect(badge).toContainText('enabled');
    await page.close();
  });

  test('links: a clickable hotspot is drawn over the link, coloured by risk', async () => {
    const file = path.join(fixtureDir, 'linkspot.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://github.com/example/repo'));
    const page = await openViewerWith(file);

    // A hotspot is laid over the link's rectangle; github rates yellow (code-hosting heuristic).
    const hotspot = page.locator('.page[data-page="1"] .link-hotspot');
    await expect(hotspot).toHaveCount(1, { timeout: 15000 });
    await expect(hotspot).toHaveClass(/risk-yellow/);
    await expect(hotspot.locator('.link-risk-dot.yellow')).toHaveCount(1);
    // It's a real link to the URL, opening in a new tab.
    await expect(hotspot).toHaveAttribute('href', 'https://github.com/example/repo');
    await expect(hotspot).toHaveAttribute('target', '_blank');

    // Rolling over shows a popup with the URL and its risk rating.
    await hotspot.hover();
    const popup = page.locator('#link-popup');
    await expect(popup).toBeVisible();
    await expect(popup.locator('.lp-url')).toHaveText('https://github.com/example/repo');
    await expect(popup.locator('.lp-risk.yellow')).toBeVisible();
    await page.close();
  });

  test('undo / redo: a change can be undone and then redone', async () => {
    const file = fixture('undoredo.pdf', [[{ text: 'Keep me', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await expect(page.locator('#btn-undo')).toBeDisabled();
    await expect(page.locator('#btn-redo')).toBeDisabled();

    // Make a change (find & replace), then undo and redo it.
    await ui(page, '#btn-find');
    await fillDialog(page, ['Keep me', 'Changed'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');
    await expect(page.locator('#btn-undo')).toBeEnabled();

    await page.click('#btn-undo');
    await expect(page.locator('#status')).toContainText('Undid last change');
    await expect(page.locator('#btn-redo')).toBeEnabled();

    await page.click('#btn-redo');
    await expect(page.locator('#status')).toContainText('Redid change');
    await expect(page.locator('#btn-redo')).toBeDisabled();
    await page.close();
  });

  test('redaction works on a page other than the first (per-page overlays)', async () => {
    // Text near the top of page 2 so it stays on-screen once page 2 is scrolled to the top.
    const file = fixture('multi-redact.pdf', [
      [{ text: 'first page', x: 72, y: 700 }],
      [{ text: 'SECOND SECRET', x: 72, y: 800 }],
    ]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    // Top-align page 2 *instantly* (no smooth-scroll animation, so boundingBox() below is
    // settled and the drag can't land on stale coordinates), then let it render.
    await page.evaluate(() =>
      document.querySelector('.page[data-page="2"]').scrollIntoView({ block: 'start', behavior: 'instant' }));
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);
    await expect(page.locator('#page-input')).toHaveValue('2');
    await expect(page.locator('#page-total')).toHaveText('2');

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

    await ui(page, '#tool-redact');
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

  test('redaction lands correctly when the CropBox exceeds the MediaBox', async () => {
    // Real-world regression: some PDFs set a CropBox larger than the MediaBox. The renderer
    // clamps to the media box, so if the viewer trusted the oversized crop box the whole page
    // was scaled and the redaction landed above where it was drawn. The page here is a normal
    // A4 media box with a crop box 160pt taller; text sits inside the real (media) area.
    const file = fixture('oversized-crop.pdf',
      [[{ text: 'CLAMP ME', x: 72, y: 500 }]],
      { mediaBox: [0, 0, 595, 842], cropBox: [0, 0, 595, 1002] });
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 60, y: 492, width: 220, height: 30 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // The drawn spot (not a shifted one) is what turns black.
    expect(await pixelAt(page, 150, 507)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 450, 300)).toEqual([255, 255, 255, 255]);
    await page.close();
  });

  test('redaction on a rotated (/Rotate 90) page lands where it is drawn', async () => {
    // On a rotated page PDFium renders a width/height-swapped image; if the viewer ignores
    // the rotation the box is drawn in one place and redacted in another. Draw a box at a
    // known spot on the *displayed* image and prove that exact spot goes black — a full
    // display -> PDF -> redact -> render round-trip that only closes if rotation is handled.
    const file = fixture('rotated.pdf', [[{ text: 'rotated secret', x: 120, y: 400 }]], { rotate: 90 });
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
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

  test('text layer: real text can be selected/copied and right-clicked to edit', async () => {
    const file = fixture('selecttext.pdf', [[{ text: 'Selectable Sentence Here', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // The invisible selectable text layer builds over the rendered page.
    const span = page.locator('.page[data-page="1"] .text-layer span', { hasText: 'Selectable' });
    await expect(span).toHaveCount(1);

    // Selecting it yields the real text (so Ctrl/Cmd+C copies actual characters, not an image).
    await span.click({ clickCount: 3 });
    const selected = await page.evaluate(() => window.getSelection().toString());
    expect(selected).toContain('Selectable');

    // Right-clicking opens the context menu; "Edit text" opens the edit panel pre-filled with
    // the selected run's text (edit in place).
    await span.click({ button: 'right' });
    await page.locator('#context-menu').getByRole('button', { name: /Edit text/ }).click();
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-title')).toHaveText('Edit text');
    await expect(page.locator('#edit-text')).toHaveValue(/Selectable Sentence Here/);
    await page.close();
  });

  test('highlight: dragging across text marks it, keeping the text readable', async () => {
    const file = fixture('highlight.pdf', [[{ text: 'HIGHLIGHT THIS LINE', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    // Let the text layer (span cache) build so the highlight snaps to the text run.
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Swipe horizontally across the text line.
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    const cy = box.y + (842 - 707) * scale;
    await page.mouse.move(box.x + 60 * scale, cy);
    await page.mouse.down();
    await page.mouse.move(box.x + 300 * scale, cy, { steps: 8 });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Highlighted');

    // Scan the highlighted line band for a yellow pixel (paper under the highlight) and a dark
    // one (the text is still legible through the multiply blend).
    const scan = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const scale = img.naturalWidth / 595;
      let yellow = false, dark = false;
      // The line sits around PDF y≈697..711 (text drawn at baseline 700).
      for (let py = 697; py <= 711; py++) {
        for (let px = 74; px <= 235; px++) {
          const d = ctx.getImageData(Math.round(px * scale), Math.round((842 - py) * scale), 1, 1).data;
          if (d[0] > 200 && d[1] > 180 && d[2] < 140) yellow = true;
          if (d[0] < 120 && d[1] < 120 && d[2] < 120) dark = true;
        }
      }
      return { yellow, dark };
    });
    expect(scan.yellow).toBe(true);
    expect(scan.dark).toBe(true);
    await page.close();
  });

  test('highlight: drawing a box over text highlights the covered words', async () => {
    const file = fixture('highlightbox.pdf', [[{ text: 'BOX HIGHLIGHT LINE', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Draw a rectangle over the line (not a thin swipe).
    await dragPdfRect(page, { x: 66, y: 694, width: 250, height: 22 });
    await expect(page.locator('#status')).toContainText('Highlighted');

    const scan = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
      const scale = img.naturalWidth / 595;
      let yellow = false, dark = false;
      for (let py = 697; py <= 711; py++)
        for (let px = 74; px <= 250; px++) {
          const d = ctx.getImageData(Math.round(px * scale), Math.round((842 - py) * scale), 1, 1).data;
          if (d[0] > 200 && d[1] > 180 && d[2] < 140) yellow = true;
          if (d[0] < 120 && d[1] < 120 && d[2] < 120) dark = true;
        }
      return { yellow, dark };
    });
    expect(scan.yellow).toBe(true);
    expect(scan.dark).toBe(true);
    await page.close();
  });

  test('text edit: reads existing text, replaces it in place', async () => {
    const file = fixture('edit.pdf', [[{ text: 'Amount Due: $500', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 250, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('Amount Due: $500');

    await page.fill('#edit-text', 'Amount Due: $750 (revised)');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Re-selecting the same region proves the old text is gone from the file.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 685, width: 300, height: 40 });
    await expect(page.locator('#edit-text')).toHaveValue(/\$750 \(revised\)/);
    await expect(page.locator('#edit-text')).not.toHaveValue(/\$500/);
    await page.close();
  });

  test('move text: grab a run of text and drag it to a new position', async () => {
    const file = fixture('movetext.pdf', [[{ text: 'MOVE ME', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    // Let the selectable text layer (span cache) build so the grab snaps to the run.
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-move');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    const cx = (px) => box.x + px * scale;
    const cy = (py) => box.y + (842 - py) * scale;
    // Grab the word (~90, 705) and drop it ~150 pt lower.
    await page.mouse.move(cx(90), cy(705));
    await page.mouse.down();
    await page.mouse.move(cx(110), cy(555), { steps: 8 });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Text moved');

    // The text now reads at the lower position...
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 540, width: 220, height: 42 });
    await expect(page.locator('#edit-text')).toHaveValue(/MOVE ME/);
    await page.click('#edit-cancel');

    // ...and is gone from where it started.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 220, height: 36 });
    await expect(page.locator('#edit-text')).not.toHaveValue(/MOVE ME/);
    await page.close();
  });

  test('move image: grab an image and drag it to a new position', async () => {
    const base = fixture('moveimg.pdf', [[{ text: 'Base', x: 72, y: 700 }]]);
    // Merge a small PNG so page 2 is an image we can grab and move.
    const png = Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
      'base64');
    const imgFile = path.join(fixtureDir, 'movepic.png');
    fs.writeFileSync(imgFile, png);
    const page = await openViewerWith(base);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(imgFile);
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#page-total')).toHaveText('2');

    // Bring page 2 (the image) into view and grab its centre with the Move tool.
    await page.evaluate(() =>
      document.querySelector('.page[data-page="2"]').scrollIntoView({ block: 'start', behavior: 'instant' }));
    await expect(page.locator('.page[data-page="2"] .page-image')).toHaveAttribute('src', /data:image\/png/);
    await ui(page, '#tool-move');
    const box = await page.locator('.page[data-page="2"] .page-image').boundingBox();
    await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.5);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.3, box.y + box.height * 0.3, { steps: 8 });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Image moved');
    await page.close();
  });

  test('context menu: right-clicking selected text offers Edit and Redact', async () => {
    const file = fixture('ctxsel.pdf', [[{ text: 'Right Click Me', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    const span = page.locator('.page[data-page="1"] .text-layer span', { hasText: 'Right' });
    await span.waitFor({ timeout: 15000 });
    await span.click({ clickCount: 3 }); // select the run
    await span.click({ button: 'right' });

    const menu = page.locator('#context-menu');
    await expect(menu).toBeVisible();
    await expect(menu).toContainText('Edit text');
    await expect(menu).toContainText('Redact this');
    await expect(menu).toContainText('Highlight');

    // "Redact this" marks the selection as a redaction region.
    await menu.getByRole('button', { name: /Redact this/ }).click();
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.close();
  });

  test('context menu: right-clicking with no selection offers document actions', async () => {
    const file = fixture('ctxdoc.pdf', [[{ text: 'plain', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // Right-click the visible centre of the scroll area (blank page area, no selection).
    const sa = await page.locator('#scroll-area').boundingBox();
    await page.mouse.click(sa.x + sa.width * 0.5, sa.y + sa.height * 0.5, { button: 'right' });

    const menu = page.locator('#context-menu');
    await expect(menu).toBeVisible();
    await expect(menu).toContainText('Make searchable');
    await expect(menu).toContainText('Show source code');
    await expect(menu).toContainText('Save');
    await expect(menu).toContainText('Print');
    await expect(menu).toContainText('Zoom in');
    await page.close();
  });

  test('text edit: change the font family, size, and style', async () => {
    const file = fixture('font-edit.pdf', [[{ text: 'Plain Heading', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
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
    await ui(page, '#tool-edit');
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

    await ui(page, '#btn-find');
    await fillDialog(page, ['OldCorp', 'NewCorp'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 2 occurrences');
    await page.close();
  });

  test('print: defers to the browser by loading the real PDF for printing', async () => {
    const file = fixture('print.pdf', [[{ text: 'Print me', x: 72, y: 700 }]]);
    const page = await ext.context.newPage();
    // Record any new-tab fallback so the test never actually spawns a tab.
    await page.addInitScript(() => { window.__opened = []; window.open = (u) => { window.__opened.push(u); return null; }; });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // Printing hands the actual PDF to the browser via an off-screen blob iframe (vector print,
    // with "Save as PDF" available in the browser's dialog).
    await page.click('#btn-print');
    await expect(page.locator('iframe[src^="blob:"]')).toHaveCount(1);
    await page.close();
  });

  test('merge appends an image as a new page', async () => {
    const base = fixture('merge-img-base.pdf', [[{ text: 'Base page', x: 72, y: 700 }]]);
    // A minimal 1x1 PNG written to disk for the merge picker.
    const png = Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
      'base64');
    const imgFile = path.join(fixtureDir, 'stamp.png');
    fs.writeFileSync(imgFile, png);
    const page = await openViewerWith(base);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(imgFile);
    // The merge dialog lets you arrange the files; confirm to combine them.
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-total')).toHaveText('2'); // image became a second page
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
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(two);
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-input')).toHaveValue('1');
    await expect(page.locator('#page-total')).toHaveText('3');
    await page.close();
  });

  test('merge & arrange: dropping the current document keeps only the appended file', async () => {
    const one = fixture('merge-drop-base.pdf', [[{ text: 'Base only', x: 72, y: 700 }]]);
    const two = fixture('merge-drop-extra.pdf', [
      [{ text: 'Extra A', x: 72, y: 700 }],
      [{ text: 'Extra B', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(one);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(two);

    // The dialog lists "This document" first; remove it so only the two appended pages remain.
    const dialog = page.locator('dialog#modal');
    await expect(dialog.locator('.organize-item')).toHaveCount(2);
    await dialog.locator('.organize-item').first().getByRole('button', { name: 'Remove' }).click();
    await expect(dialog.locator('.organize-item')).toHaveCount(1);
    await dialog.getByRole('button', { name: 'Merge' }).click();

    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-total')).toHaveText('2'); // base dropped, 2 extra pages kept
    await page.close();
  });

  test('organize pages: remove a page from the document', async () => {
    const file = fixture('organize.pdf', [
      [{ text: 'Keep one', x: 72, y: 700 }],
      [{ text: 'Delete two', x: 72, y: 700 }],
      [{ text: 'Keep three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);
    await expect(page.locator('#page-total')).toHaveText('3');

    await ui(page, '#btn-organize');
    await expect(page.locator('#panel-organize')).toBeVisible();
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(3);

    // Remove the middle page, then apply.
    await page.locator('#organize-list .organize-item').nth(1)
      .getByRole('button', { name: 'Remove page' }).click();
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(2);
    await page.click('#organize-apply');

    await expect(page.locator('#status')).toContainText('reorganized');
    await expect(page.locator('#page-total')).toHaveText('2');
    await page.close();
  });

  test('forms: insert a dropdown (choice) field with options', async () => {
    const file = fixture('dropdown.pdf', [[{ text: 'pick one', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    await page.selectOption('#field-type', 'dropdown');
    await expect(page.locator('#field-options-row')).toBeVisible();
    await page.fill('#field-name', 'country');
    await page.fill('#field-options', 'Australia\nCanada\nDenmark');
    await page.click('#field-place');
    await expect(page.locator('#status')).toContainText('Drag a box');

    await dragPdfRect(page, { x: 100, y: 600, width: 220, height: 24 });

    // The forms panel reopens and lists the new choice field as a <select>.
    await expect(page.locator('#forms-list [data-field="country"]')).toHaveCount(1);
    await page.close();
  });

  test('forms: an inserted field is visible on the page (not blank space)', async () => {
    const file = fixture('visfield.pdf', [[{ text: 'form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'text');
    await page.fill('#field-name', 'visible_field');
    await page.click('#field-place');
    await dragPdfRect(page, { x: 100, y: 600, width: 220, height: 26 });
    await expect(page.locator('#forms-list [data-field="visible_field"]')).toHaveCount(1);

    // Scan the field's rectangle on the rendered page for non-white pixels (its border/background).
    const drawn = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
      const s = img.naturalWidth / 595;
      let nonWhite = 0;
      for (let py = 600; py <= 626; py++)
        for (let px = 100; px <= 320; px++) {
          const d = ctx.getImageData(Math.round(px * s), Math.round((842 - py) * s), 1, 1).data;
          if (!(d[0] > 248 && d[1] > 248 && d[2] > 248)) nonWhite++;
        }
      return nonWhite;
    });
    expect(drawn).toBeGreaterThan(0);
    await page.close();
  });

  test('forms: insert an option (radio) group', async () => {
    const file = fixture('radio.pdf', [[{ text: 'choose', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'radio');
    await expect(page.locator('#field-options-row')).toBeVisible();
    await page.fill('#field-name', 'plan');
    await page.fill('#field-options', 'Basic\nPro\nEnterprise');
    await page.click('#field-place');
    await dragPdfRect(page, { x: 100, y: 560, width: 200, height: 90 });

    // Listed as a single option field, rendered as a <select> of the choices (minus the Off state).
    const select = page.locator('#forms-list [data-field="plan"]');
    await expect(select).toHaveCount(1);
    await expect(select.locator('option', { hasText: 'Enterprise' })).toHaveCount(1);
    await expect(select.locator('option', { hasText: 'Off' })).toHaveCount(0);
    await page.close();
  });

  test('javascript: author a document script in the code editor, kept on save', async () => {
    const file = fixture('addjs.pdf', [[{ text: 'form doc', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-js');
    await expect(page.locator('#js-dialog')).toBeVisible();
    await page.fill('#js-name', 'greet');
    await page.fill('#js-source', "app.alert('hello from the PDF');");
    await page.click('#js-add');
    await expect(page.locator('#status')).toContainText('added');

    // The script is now listed in the panel...
    await expect(page.locator('#js-list .organize-label', { hasText: 'greet' })).toHaveCount(1);
    // ...and the active-content badge shows it is being kept (not stripped) on save.
    const badge = page.locator('#badges .badge.warn');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('kept');
    await page.close();
  });

  test('javascript: a document script can be removed again', async () => {
    const file = fixture('rmjs.pdf', [[{ text: 'doc', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-js');
    await page.fill('#js-name', 'temp');
    await page.fill('#js-source', 'console.println("x");');
    await page.click('#js-add');
    await expect(page.locator('#js-list .organize-label', { hasText: 'temp' })).toHaveCount(1);

    await page.locator('#js-list .organize-item').first()
      .getByRole('button', { name: 'Remove script' }).click();
    await expect(page.locator('#status')).toContainText('removed');
    await expect(page.locator('#js-list .organize-item')).toHaveCount(0);
    await page.close();
  });

  test('forms: insert a JavaScript push-button', async () => {
    const file = fixture('jsbutton.pdf', [[{ text: 'form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'button');
    await expect(page.locator('#field-caption-row')).toBeVisible();
    await expect(page.locator('#field-script-row')).toBeVisible();
    await page.fill('#field-name', 'submitBtn');
    await page.fill('#field-caption', 'Submit');
    await page.fill('#field-script', "app.alert('submitted');");
    await page.click('#field-place');
    await expect(page.locator('#status')).toContainText('Drag a box');

    await dragPdfRect(page, { x: 100, y: 600, width: 120, height: 28 });

    // The button is listed as a form field, and the script it carries is kept on save.
    await expect(page.locator('#forms-list [data-field="submitBtn"]')).toHaveCount(1);
    await expect(page.locator('#badges .badge.warn')).toContainText('kept');
    await page.close();
  });

  test('ocr: make searchable runs, or reports Tesseract is required', async () => {
    const file = fixture('ocr.pdf', [[{ text: 'Scanned document', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-ocr');
    // Deterministic across environments: OCR either succeeds (status confirms "searchable") or,
    // when Tesseract is not installed, an in-app note naming it appears — never a silent failure.
    await expect.poll(async () => {
      const dialog = page.locator('dialog#modal');
      if (await dialog.isVisible() && /Tesseract/i.test((await dialog.textContent()) || '')) return 'note';
      if (/searchable/i.test((await page.locator('#status').textContent()) || '')) return 'done';
      return 'pending';
    }, { timeout: 60000 }).not.toBe('pending');
    await page.close();
  });

  test('compare versions: summarises added and removed words', async () => {
    const current = fixture('compare-new.pdf', [[{ text: 'Amount Due 750 dollars', x: 72, y: 700 }]]);
    const older = fixture('compare-old.pdf', [[{ text: 'Amount Due 500 dollars', x: 72, y: 700 }]]);
    const page = await openViewerWith(current);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooser).setFiles(older);

    await expect(page.locator('#panel-compare')).toBeVisible();
    await expect(page.locator('#compare-summary')).toContainText('1 page');
    // The changed page lists 750 as added and 500 as removed.
    await expect(page.locator('#compare-list .w-add', { hasText: '750' })).toHaveCount(1);
    await expect(page.locator('#compare-list .w-del', { hasText: '500' })).toHaveCount(1);
    await page.close();
  });

  test('compare versions: identical documents report no differences', async () => {
    const same = fixture('compare-same.pdf', [[{ text: 'Unchanged content here', x: 72, y: 700 }]]);
    const page = await openViewerWith(same);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooser).setFiles(same);

    await expect(page.locator('#compare-summary')).toContainText('no text differences');
    await page.close();
  });

  test('remove hidden info: detects and strips a document script', async () => {
    const file = fixture('sanitize.pdf', [[{ text: 'shareable', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // Seed some hidden data: add a document-level script.
    await ui(page, '#btn-js');
    await page.fill('#js-name', 'tracker');
    await page.fill('#js-source', "app.alert('phone home');");
    await page.click('#js-add');
    await expect(page.locator('#js-list .organize-label', { hasText: 'tracker' })).toHaveCount(1);
    await page.click('#js-close'); // the editor is a modal window — close it before the menu

    // Open the sanitiser: it should report the script and pre-check that category.
    await ui(page, '#btn-sanitize');
    await expect(page.locator('#panel-sanitize')).toBeVisible();
    const scriptRow = page.locator('#sanitize-items [data-opt="scriptsAndActions"]');
    await expect(scriptRow).toBeChecked();
    await expect(page.locator('#sanitize-items')).toContainText('JavaScript & actions — 1 found');

    await page.click('#sanitize-apply');
    await expect(page.locator('#status')).toContainText('Hidden information removed');

    // Re-opening the JavaScript panel shows the script is gone.
    await ui(page, '#btn-js');
    await expect(page.locator('#js-list .organize-item')).toHaveCount(0);
    await page.close();
  });

  test('remove hidden info: reports a clean document', async () => {
    const file = fixture('clean.pdf', [[{ text: 'nothing hidden', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-sanitize');
    await expect(page.locator('#sanitize-clean')).toBeVisible();
    await expect(page.locator('#sanitize-apply')).toBeDisabled();
    await page.close();
  });

  test('password protection encrypts the document', async () => {
    const file = fixture('protect.pdf', [[{ text: 'classified', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-protect');
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

    await ui(page, '#tool-sign');
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

    await ui(page, '#btn-digital');
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
    await ui(page, '#btn-find');
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
