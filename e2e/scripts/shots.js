'use strict';

// Drives the real extension + native host to capture screenshots of the link overlay states.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension } = require('../helpers/harness');
const { buildLinkPdf, buildJsLinkPdf, buildLinkOnPage2Pdf } = require('../helpers/pdf');

const OUT = process.argv[2] || path.join(os.tmpdir(), 'pdf-shots');
fs.mkdirSync(OUT, { recursive: true });

const pageImageSel = (n = 1) => `.page[data-page="${n}"] .page-image`;

async function openViewerWith(ext, bytes) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'shots-fix-'));
  const file = path.join(dir, 'doc.pdf');
  fs.writeFileSync(file, bytes);
  const page = await ext.context.newPage();
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto(ext.viewerUrl);
  const chooser = page.waitForEvent('filechooser');
  await page.click('#btn-open-empty');
  await (await chooser).setFiles(file);
  await page.waitForSelector(`${pageImageSel(1)}[src^="data:image/png"]`);
  return page;
}

async function ui(page, sel) {
  const triggerId = await page.evaluate((s) => {
    const el = document.querySelector(s);
    const menu = el && el.closest('.menu-group');
    return menu ? menu.querySelector('.menu-trigger').id : null;
  }, sel);
  if (triggerId) await page.click('#' + triggerId);
  await page.click(sel);
}

(async () => {
  const ext = await launchExtension();
  try {
    // 1) A web link on load: risk-coloured yellow but DISABLED (inert), with the rollover popup.
    let page = await openViewerWith(ext, buildLinkPdf('https://github.com/example/repo'));
    const spot = page.locator('.page[data-page="1"] .link-hotspot');
    await spot.waitFor({ timeout: 15000 });
    await spot.hover();
    await page.locator('#link-popup').waitFor();
    await page.screenshot({ path: path.join(OUT, '1-link-disabled-inert.png') });

    // 2) Same link after enabling: it becomes a clickable anchor (popup no longer shows "disabled").
    await ui(page, '#btn-links');
    await page.locator('#links-enable').check();
    await page.waitForTimeout(200);
    await spot.hover();
    await page.locator('#link-popup').waitFor();
    await page.screenshot({ path: path.join(OUT, '2-link-enabled-clickable.png') });
    await page.close();

    // 3) A non-URL JavaScript action link: highlighted but explained as an in-document action.
    page = await openViewerWith(ext, buildJsLinkPdf('window.close();'));
    const js = page.locator('.page[data-page="1"] .link-hotspot');
    await js.waitFor({ timeout: 15000 });
    await js.hover();
    await page.locator('#link-popup').waitFor();
    await page.screenshot({ path: path.join(OUT, '3-js-action-link.png') });
    await page.close();

    // 4) Overlay drawn on a later page after scrolling (the cached-render regression).
    page = await openViewerWith(ext, buildLinkOnPage2Pdf('https://github.com/example/repo'));
    await page.fill('#page-input', '2');
    await page.press('#page-input', 'Enter');
    await page.waitForSelector(`${pageImageSel(2)}[src^="data:image/png"]`);
    const p2 = page.locator('.page[data-page="2"] .link-hotspot');
    await p2.waitFor({ timeout: 15000 });
    await p2.hover();
    await page.locator('#link-popup').waitFor();
    await page.screenshot({ path: path.join(OUT, '4-overlay-on-page-2.png') });
    await page.close();

    console.log('Saved screenshots to', OUT);
  } finally {
    await ext.close();
  }
})().catch((e) => { console.error(e); process.exit(1); });
