'use strict';

// Captures documentation screenshots of the editor's main features by driving the REAL extension
// and native host against the generated sample document (docs/sample/PDF-Editor-Sample.pdf).
//
//   node e2e/scripts/doc-shots.js [outputDir]
//
// Default output: docs/screenshots/. Each feature is captured in its own try/catch so one
// finicky interaction can't wipe out the whole set — the run prints which shots succeeded.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { launchExtension, buildPrerequisites, REPO_ROOT } = require('../helpers/harness');

const SAMPLE = path.join(REPO_ROOT, 'docs', 'sample', 'PDF-Editor-Sample.pdf');

function resolveOutputDir(rawArg) {
  const target = rawArg || path.join(REPO_ROOT, 'docs', 'screenshots');
  if (typeof target !== 'string' || target.length === 0 || target.includes('\0')) {
    throw new Error(`invalid output directory argument: ${JSON.stringify(rawArg)}`);
  }
  return path.resolve(target);
}

const OUT = resolveOutputDir(process.argv[2]);
fs.mkdirSync(OUT, { recursive: true });

const pageImageSel = (n = 1) => `.page[data-page="${n}"] .page-image`;

/** Clicks a control, first opening its Reading/Editing dropdown when it lives in one. */
async function ui(page, sel) {
  const triggerId = await page.evaluate((s) => {
    const el = document.querySelector(s);
    const menu = el && el.closest('.menu-group');
    return menu ? menu.querySelector('.menu-trigger').id : null;
  }, sel);
  if (triggerId) await page.click('#' + triggerId);
  await page.click(sel);
}

async function openViewer(ext) {
  const page = await ext.context.newPage();
  // The Chrome/Edge store listings want a 1280x800 screenshot exactly.
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(ext.viewerUrl);
  const chooser = page.waitForEvent('filechooser');
  await page.click('#btn-open-empty');
  await (await chooser).setFiles(SAMPLE);
  await page.waitForSelector(`${pageImageSel(1)}[src^="data:image/png"]`, { timeout: 30000 });
  await page.waitForTimeout(400);
  return page;
}

/** Maps a PDF user-space point on page 1 (A4) to CSS coordinates for mouse actions. */
async function pdfPoint(page, x, y, pageNum = 1) {
  const box = await page.locator(pageImageSel(pageNum)).boundingBox();
  const scale = box.width / 595; // A4 width in points
  return { x: box.x + x * scale, y: box.y + (842 - y) * scale };
}

async function dragPdf(page, x0, y0, x1, y1, pageNum = 1) {
  const a = await pdfPoint(page, x0, y0, pageNum);
  const b = await pdfPoint(page, x1, y1, pageNum);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 8 });
  await page.mouse.up();
}

const results = [];
async function shot(page, name, fn) {
  try {
    if (fn) await fn();
    await page.screenshot({ path: path.join(OUT, name) });
    results.push(`  ✓ ${name}`);
  } catch (e) {
    results.push(`  ✗ ${name} — ${e.message.split('\n')[0]}`);
  }
}

(async () => {
  if (!fs.existsSync(SAMPLE)) {
    execFileSync('node', [path.join(REPO_ROOT, 'scripts', 'generate-sample-pdf.mjs')], { stdio: 'inherit' });
  }
  buildPrerequisites();
  // The context-level default; every page this script actually screenshots is opened by
  // openViewer() below, which sets its own per-page size that wins over this -- keep both in
  // sync. This one only matters for a page opened some other way (there is currently none).
  const ext = await launchExtension({ viewport: { width: 1280, height: 800 } });
  try {
    // 1) Overview — the document open, both charts visible on page 1.
    let page = await openViewer(ext);
    await shot(page, '01-overview.png');

    // 2) Redaction — queue a box over a table value and open the before/after preview.
    await ui(page, '#tool-redact');
    await page.fill('#page-input', '2');
    await page.press('#page-input', 'Enter');
    await page.waitForSelector(`${pageImageSel(2)}[src^="data:image/png"]`);
    await page.waitForTimeout(300);
    await dragPdf(page, 250, 560, 340, 700, 2); // over the "Actual" column figures
    await page.waitForTimeout(300);
    await shot(page, '02-redact-marked.png');
    await shot(page, '03-redact-preview.png', async () => {
      await page.click('#redact-preview');
      await page.waitForTimeout(600);
    });
    await page.keyboard.press('Escape').catch(() => {});
    await page.click('#redact-clear').catch(() => {});
    await page.close();

    // 3) Highlight — sweep across a heading on page 1.
    page = await openViewer(ext);
    await ui(page, '#tool-highlight');
    await page.waitForTimeout(200);
    await dragPdf(page, 56, 706, 250, 724); // across "Executive summary"
    await page.waitForTimeout(400);
    await shot(page, '04-highlight.png');
    await page.close();

    // 4) Add text — drop a text box and type into it.
    page = await openViewer(ext);
    await ui(page, '#tool-text');
    const tp = await pdfPoint(page, 330, 470);
    await page.mouse.click(tp.x, tp.y);
    await page.waitForTimeout(200);
    await page.keyboard.type('DRAFT — for review');
    await page.waitForTimeout(300);
    await shot(page, '05-add-text.png');
    await page.close();

    // 5) Edit existing text — drag a box around a line to load it into the editor.
    page = await openViewer(ext);
    await ui(page, '#tool-edit');
    await dragPdf(page, 54, 704, 260, 726); // around "Executive summary"
    await page.waitForSelector('#panel-edit:not([hidden]) #edit-text', { timeout: 8000 });
    await page.waitForTimeout(300);
    await shot(page, '06-edit-text.png');
    await page.close();

    // 6) Find & replace.
    page = await openViewer(ext);
    await shot(page, '07-find-replace.png', async () => {
      await ui(page, '#btn-find');
      await page.waitForSelector('#modal[open] input', { timeout: 8000 });
      const inputs = page.locator('#modal[open] input');
      await inputs.nth(0).fill('Northwind Analytics');
      await inputs.nth(1).fill('Contoso Corporation');
      await page.waitForTimeout(300);
    });
    await page.close();

    // 7) Forms panel.
    page = await openViewer(ext);
    await shot(page, '08-forms.png', async () => {
      await ui(page, '#btn-forms');
      await page.waitForSelector('#panel-forms:not([hidden])', { timeout: 8000 });
      await page.waitForTimeout(300);
    });
    await page.close();

    // 8) Organize pages.
    page = await openViewer(ext);
    await shot(page, '09-organize.png', async () => {
      await ui(page, '#btn-organize');
      await page.waitForSelector('#panel-organize:not([hidden])', { timeout: 8000 });
      await page.waitForTimeout(300);
    });
    await page.close();

    // 9) Remove hidden information (sanitize) — shows the scan of what the file carries.
    page = await openViewer(ext);
    await shot(page, '10-remove-hidden-info.png', async () => {
      await ui(page, '#btn-sanitize');
      await page.waitForSelector('#panel-sanitize:not([hidden])', { timeout: 8000 });
      await page.waitForTimeout(500);
    });
    await page.close();

    // 10) Draw freehand.
    page = await openViewer(ext);
    await ui(page, '#tool-draw');
    await page.waitForTimeout(200);
    {
      const p1 = await pdfPoint(page, 320, 250);
      const p2 = await pdfPoint(page, 420, 300);
      const p3 = await pdfPoint(page, 500, 240);
      await page.mouse.move(p1.x, p1.y);
      await page.mouse.down();
      await page.mouse.move(p2.x, p2.y, { steps: 10 });
      await page.mouse.move(p3.x, p3.y, { steps: 10 });
      await page.mouse.up();
    }
    await page.waitForTimeout(300);
    await shot(page, '11-draw.png');
    await page.close();

    // 11) Signature pad. The sign panel only opens once a placement region is marked on the page.
    page = await openViewer(ext);
    await shot(page, '12-sign.png', async () => {
      await ui(page, '#tool-sign');
      await dragPdf(page, 360, 455, 520, 500); // mark where the signature goes (kept in view)
      await page.waitForSelector('#panel-sign:not([hidden])', { timeout: 8000 });
      // Scribble on the pad so it reads as a real signature.
      const pad = await page.locator('#sign-pad').boundingBox();
      await page.mouse.move(pad.x + 30, pad.y + 80);
      await page.mouse.down();
      await page.mouse.move(pad.x + 90, pad.y + 30, { steps: 8 });
      await page.mouse.move(pad.x + 150, pad.y + 90, { steps: 8 });
      await page.mouse.move(pad.x + 220, pad.y + 40, { steps: 8 });
      await page.mouse.up();
      await page.waitForTimeout(200);
    });
    await page.close();

    // 12) Activity console.
    page = await openViewer(ext);
    await shot(page, '13-activity-console.png', async () => {
      await ui(page, '#btn-console');
      await page.waitForSelector('#console-pane:not([hidden])', { timeout: 8000 });
      await page.waitForTimeout(400);
    });
    await page.close();

    // 14) Watermark dialog.
    page = await openViewer(ext);
    await shot(page, '14-watermark.png', async () => {
      await ui(page, '#btn-watermark');
      await page.waitForSelector('dialog#modal[open]', { timeout: 8000 });
      await page.waitForTimeout(200);
    });
    await page.close();

    // 15) Bates numbering dialog.
    page = await openViewer(ext);
    await shot(page, '15-bates.png', async () => {
      await ui(page, '#btn-bates');
      await page.waitForSelector('dialog#modal[open]', { timeout: 8000 });
      await page.waitForTimeout(200);
    });
    await page.close();

    // 16) Flatten dialog.
    page = await openViewer(ext);
    await shot(page, '16-flatten.png', async () => {
      await ui(page, '#btn-flatten');
      await page.waitForSelector('dialog#modal[open]', { timeout: 8000 });
      await page.waitForTimeout(200);
    });
    await page.close();

    // 17) About dialog.
    page = await openViewer(ext);
    await shot(page, '17-about.png', async () => {
      await ui(page, '#btn-about');
      await page.waitForSelector('dialog#modal[open]', { timeout: 8000 });
      await page.waitForTimeout(200);
    });
    await page.close();

    // 18) Redaction compliance report — mark a value, apply, and show the audit modal.
    page = await openViewer(ext);
    await shot(page, '18-redaction-report.png', async () => {
      await ui(page, '#tool-redact');
      await page.fill('#page-input', '2');
      await page.press('#page-input', 'Enter');
      await page.waitForSelector(`${pageImageSel(2)}[src^="data:image/png"]`);
      await page.waitForTimeout(300);
      await dragPdf(page, 250, 560, 340, 700, 2);
      await page.waitForTimeout(200);
      await page.click('#redact-apply');
      await page.waitForSelector('dialog#modal[open]', { timeout: 8000 });
      await page.waitForTimeout(300);
    });
    await page.close();

    console.log(`\nScreenshots written to ${OUT}:`);
    console.log(results.join('\n'));
  } finally {
    await ext.close();
  }
})().catch((e) => { console.error(e); process.exit(1); });
