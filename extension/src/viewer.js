// PDF Editor viewer page. Talks straight to the native host; the working
// document lives here as bytes and every edit round-trips through the host.

import { HostClient, bytesToBase64, base64ToBytes } from './host-client.js';

const host = new HostClient();

// URL/link scanning (the 🔗 Links panel + Cloudflare rating) is disabled for now. The backend
// (list-urls/scan-urls, UrlClassifier, CloudflareUrlScanner) stays in place; flip this to re-enable
// the button, the links badge, and stripping link URLs on save.
const URL_SCANNING_ENABLED = false;

const state = {
  pdf: null,            // Uint8Array — current working document
  pdfB64: null,         // base64 of pdf, recomputed once per version (not per request)
  version: 0,           // bumped whenever pdf changes; keys the render cache
  password: null,       // password for the working document, if encrypted
  info: null,           // { pageCount, encrypted, pages: [{number,x,y,width,height}] }
  page: 1,
  zoom: 1,
  dpi: 144,
  fileName: 'document.pdf',
  history: [],          // undo stack of previous byte states (cap 10)
  future: [],           // redo stack of states undone (cleared on any new edit)
  sidebarOpen: false,   // page-thumbnail sidebar visibility
  tool: 'select',
  regions: [],          // pending redaction regions {page,x,y,width,height} (PDF space)
  pendingEditRegion: null,
  pendingSignRegion: null,
  pendingField: null,   // { fieldType, name } while placing a new form field
  textMode: 'edit',     // 'edit' (replace existing) or 'add' (stamp new) for the text panel
  signatures: [],
  drawColor: '#e53935',
  drawWidth: 2.5,
  highlightColor: '#ffeb3b',
  safety: null,         // { hasActiveContent, javaScriptCount, urlCount, samples }
  keepActiveContent: false, // false = strip JavaScript on save until the user opts in
  keepLinks: false,     // false = strip link URLs on save until the user enables them
  links: [],            // extracted { page, url }
  urlVerdicts: [],      // [{ page, url, level, category, source, detail }] once scanned
  scripts: [],          // document-level JavaScript { name, script } once listed
};

// Freehand strokes captured for the draw tool, in CSS pixels per page: Map(pageNum -> [stroke]),
// each stroke an array of {x,y}. Converted to PDF user space when applied.
const inkByPage = new Map();

// Rendered pages are cached in memory so navigating back and forth is instant instead of
// re-rendering (and re-uploading the whole document) every time. Entries are keyed by the
// document version, so any edit invalidates the whole cache automatically.
const renderCache = new Map(); // `${version}|${page}|${dpi}` -> png base64
const MAX_CACHED_PAGES = 24;
// Only pages within this many of the current one are rendered ahead of time. The host renders
// serially, so a large radius keeps it endlessly busy on pages you may never look at, starving
// the page you're actually on. Keep it small; the cache still holds far more once you visit them.
const PREFETCH_RADIUS = 3;
let prefetchToken = 0;
let prefetchTimer = null;

// Thumbnails live in their own small-image cache so opening the sidebar can't evict the
// full-size page renders (they render at a different DPI and would otherwise thrash each other).
const thumbCache = new Map(); // `${version}|${page}` -> png base64
const MAX_CACHED_THUMBS = 400;

// Per-page text runs (bboxes in PDF space) for the selectable text layer.
const spanCache = new Map(); // `${version}|${page}` -> [{ text, x, y, width, height }]

/** Installs new working bytes: encode once, bump the version, drop now-stale renders. */
function setWorkingPdf(bytes, base64) {
  state.pdf = bytes;
  state.pdfB64 = base64 ?? bytesToBase64(bytes);
  state.version++;
  renderCache.clear();
  thumbCache.clear();
  spanCache.clear();
  inkByPage.clear();  // uncommitted freehand strokes belong to the old document
  prefetchToken++; // cancel any in-flight prefetch for the previous document
}

const $ = (id) => document.getElementById(id);
const pagesEl = $('pages');
const scrollArea = $('scroll-area');
const modal = $('modal');

// One entry per page: { wrap, img, overlay, ratio, renderedKey }. Rebuilt on every load.
let pageEls = [];
let nearObserver = null; // renders pages as they approach the viewport
let visObserver = null;  // tracks which page is currently front-and-centre

// ------------------------------------------------------------------ utils

function setStatus(text, busy = false) {
  $('status').innerHTML = busy ? `<span class="spinner"></span>${text}` : text;
  // The loading wheel rides along with the busy-status calls that already wrap every
  // host round-trip, so any operation that makes a button "hang" shows a spinner.
  const overlay = $('busy-overlay');
  if (busy) {
    $('busy-text').textContent = text;
    overlay.hidden = false;
  } else {
    overlay.hidden = true;
  }
}

function toast(text) {
  setStatus(text);
  setTimeout(() => { if ($('status').textContent === text) setStatus(''); }, 5000);
}

function fail(err) {
  console.error(err);
  setStatus(`⚠ ${err.message ?? err}`);
}

function pageSize(pageNum = state.page) {
  return state.info.pages[pageNum - 1];
}

// The rendered image is the page's crop box (origin x,y; size width×height in PDF user
// space) with the page's clockwise rotation applied. So mapping between the image and the
// document has to account for both the crop-box origin AND the rotation, or redactions land
// shifted/rotated. (fx,fy) below are fractions across the *unrotated* crop box — fx from its
// left, fy from its bottom — and (u,v) are fractions across the *displayed* image, u from
// its left, v from its top.

/** Fraction across the displayed image (u,v) → fraction across the unrotated crop box. */
function displayToPage(rotation, u, v) {
  switch (rotation) {
    case 90: return [v, u];
    case 180: return [1 - u, v];
    case 270: return [1 - v, 1 - u];
    default: return [u, 1 - v];
  }
}

/** Fraction across the unrotated crop box (fx,fy) → fraction across the displayed image. */
function pageToDisplay(rotation, fx, fy) {
  switch (rotation) {
    case 90: return [fy, fx];
    case 180: return [1 - fx, fy];
    case 270: return [1 - fy, 1 - fx];
    default: return [fx, 1 - fy];
  }
}

/** CSS pixel (relative to a page's image) → PDF user-space point on that page. */
function cssToPdf(pageNum, img, cssX, cssY) {
  const p = pageSize(pageNum);
  const [fx, fy] = displayToPage(p.rotation, cssX / img.clientWidth, cssY / img.clientHeight);
  return { x: p.x + fx * p.width, y: p.y + fy * p.height };
}

function pdfRectToCss(pageNum, img, region) {
  const p = pageSize(pageNum);
  const [ua, va] = pageToDisplay(p.rotation, (region.x - p.x) / p.width, (region.y - p.y) / p.height);
  const [ub, vb] = pageToDisplay(p.rotation,
    (region.x + region.width - p.x) / p.width, (region.y + region.height - p.y) / p.height);
  const w = img.clientWidth;
  const h = img.clientHeight;
  return {
    left: Math.min(ua, ub) * w,
    top: Math.min(va, vb) * h,
    width: Math.abs(ua - ub) * w,
    height: Math.abs(va - vb) * h,
  };
}

// ------------------------------------------------------------ doc lifecycle

async function loadDocument(bytes, fileName, { pushHistory = false, password } = {}) {
  if (pushHistory && state.pdf) {
    state.history.push({ pdf: state.pdf, pdfB64: state.pdfB64, info: state.info, password: state.password });
    if (state.history.length > 10) state.history.shift();
    state.future = []; // a fresh edit invalidates any redo history
  }
  const pdfB64 = bytesToBase64(bytes);
  let info;
  try {
    info = await host.call('info', { pdf: pdfB64, pdfPassword: password ?? state.password });
  } catch (e) {
    if (/password/i.test(e.message)) {
      const entered = await promptDialog('This PDF is password-protected', [
        { id: 'pw', label: 'Password', type: 'password' },
      ]);
      if (!entered) return;
      return loadDocument(bytes, fileName, { pushHistory, password: entered.pw });
    }
    throw e;
  }
  setWorkingPdf(bytes, pdfB64);
  state.password = password ?? state.password;
  state.info = info;
  if (fileName) state.fileName = fileName;
  state.page = Math.min(state.page, info.pageCount);
  state.regions = state.regions.filter((r) => r.page <= info.pageCount);
  // Paint the document first; the signature/active-content scans (which walk the whole file) then
  // run in the background and light up their badges a moment later rather than delaying first paint.
  state.signatures = [];
  state.safety = null;
  await showDocument();
  updateChrome();
  Promise.all([refreshSignatures(), refreshSafety()]).then(updateChrome);
}

async function applyResult(base64Pdf, message) {
  await loadDocument(base64ToBytes(base64Pdf), null, { pushHistory: true });
  toast(message);
}

/**
 * Fast apply for content-only edits (highlight / draw / add & edit text) that don't change page
 * geometry, signatures, or active-content: skips the info call, the signature/JS-URL rescans, and
 * the full page-column rebuild, re-rendering only the affected pages. Keeps highlighting snappy.
 */
async function applyContentEdit(base64Pdf, pages, message) {
  state.history.push(snapshot());
  if (state.history.length > 10) state.history.shift();
  state.future = [];
  setWorkingPdf(base64ToBytes(base64Pdf), base64Pdf);
  for (const p of pages) {
    const pe = pageEls[p - 1];
    if (pe) { pe.renderedKey = null; pe.textKey = null; }
  }
  await Promise.all(pages.map((p) => renderPageEl(p)));
  updateChrome();
  toast(message);
}

async function refreshSignatures() {
  try {
    const result = await host.call('signatures', {
      pdf: state.pdfB64, pdfPassword: state.password,
    });
    state.signatures = result.signatures ?? [];
  } catch {
    state.signatures = [];
  }
}

/** Scans for embedded JavaScript / URL actions so the UI can flag (and, by default, strip) them. */
async function refreshSafety() {
  try {
    state.safety = await host.call('scan-safety', {
      pdf: state.pdfB64, pdfPassword: state.password,
    });
  } catch {
    state.safety = null;
  }
}

function currentDpi() {
  return Math.min(300, Math.round(state.dpi * state.zoom));
}

function cacheKey(page, dpi) {
  return `${state.version}|${page}|${dpi}`;
}

/** Renders one page (or returns it from cache), memoising the result. */
async function renderToCache(page, dpi) {
  const key = cacheKey(page, dpi);
  const cached = renderCache.get(key);
  if (cached !== undefined) {
    renderCache.delete(key); // move to most-recently-used
    renderCache.set(key, cached);
    return cached;
  }
  const result = await host.call('render', {
    pdf: state.pdfB64, page, dpi, pdfPassword: state.password,
  });
  renderCache.set(key, result.png);
  while (renderCache.size > MAX_CACHED_PAGES) {
    renderCache.delete(renderCache.keys().next().value); // evict least-recently-used
  }
  return result.png;
}

/**
 * Fills the cache for a few pages either side of `centerPage`, nearest first, in the background.
 * Debounced so rapid scrolling doesn't restart it on every page and flood the serial host; the
 * radius is deliberately small so prefetch finishes quickly and leaves the host free for the page
 * you're actually viewing.
 */
function prefetchAround(centerPage, dpi) {
  clearTimeout(prefetchTimer);
  prefetchTimer = setTimeout(() => {
    const token = ++prefetchToken;
    const total = state.info?.pageCount ?? 0;
    const order = [];
    for (let d = 1; d <= PREFETCH_RADIUS; d++) {
      if (centerPage + d <= total) order.push(centerPage + d); // ahead first — the likely direction
      if (centerPage - d >= 1) order.push(centerPage - d);
    }
    (async () => {
      for (const page of order) {
        if (token !== prefetchToken) return; // navigation, zoom, or edit superseded us
        if (renderCache.has(cacheKey(page, dpi))) continue;
        try {
          await renderToCache(page, dpi);
        } catch {
          /* a prefetch failure is never fatal — the page renders on demand instead */
        }
        await new Promise((r) => setTimeout(r, 0)); // yield so the UI stays responsive
      }
    })();
  }, 180);
}

function displaySize(pageNum) {
  const p = pageSize(pageNum);
  const factor = state.zoom * (96 / 72);
  const swap = p.rotation === 90 || p.rotation === 270; // landscape when rotated a quarter-turn
  return {
    width: (swap ? p.height : p.width) * factor,
    height: (swap ? p.width : p.height) * factor,
  };
}

/** (Re)builds the scrollable column of page placeholders and starts lazy rendering. */
function buildPages() {
  nearObserver?.disconnect();
  visObserver?.disconnect();
  pageEls = [];
  pagesEl.innerHTML = '';
  $('empty-state').style.display = 'none';

  for (let n = 1; n <= state.info.pageCount; n++) {
    const { width, height } = displaySize(n);
    const wrap = document.createElement('div');
    wrap.className = 'page';
    wrap.dataset.page = String(n);
    wrap.style.width = `${width}px`;
    wrap.style.height = `${height}px`;

    const img = document.createElement('img');
    img.className = 'page-image';
    img.alt = `Page ${n}`;
    img.draggable = false;

    const overlay = document.createElement('div');
    overlay.className = 'overlay';
    if (state.tool !== 'select') overlay.classList.add('tool-active');

    wrap.append(img, overlay);
    pagesEl.appendChild(wrap);
    pageEls.push({ wrap, img, overlay, ratio: 0, renderedKey: null });
  }

  // Render pages a little before they scroll into view; keep the cache warm around them.
  nearObserver = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) renderPageEl(Number(e.target.dataset.page));
  }, { root: scrollArea, rootMargin: '500px 0px' });

  // Track which page is most in view so the page counter and nav stay correct.
  visObserver = new IntersectionObserver((entries) => {
    for (const e of entries) {
      const pe = pageEls[Number(e.target.dataset.page) - 1];
      if (pe) pe.ratio = e.isIntersecting ? e.intersectionRatio : 0;
    }
    let best = state.page;
    let bestRatio = -1;
    pageEls.forEach((pe, i) => { if (pe.ratio > bestRatio) { bestRatio = pe.ratio; best = i + 1; } });
    if (best !== state.page) {
      state.page = best;
      updateNav();
      prefetchAround(best, currentDpi());
    }
  }, { root: scrollArea, threshold: [0, 0.25, 0.5, 0.75, 1] });

  for (const pe of pageEls) { nearObserver.observe(pe.wrap); visObserver.observe(pe.wrap); }
  pagesEl.classList.toggle('select-mode', state.tool === 'select');
  drawRegions();
}

/** Renders one page's image (from cache when possible) into its placeholder. */
async function renderPageEl(pageNum) {
  const pe = pageEls[pageNum - 1];
  if (!pe) return;
  const dpi = currentDpi();
  const key = cacheKey(pageNum, dpi);
  if (pe.renderedKey === key) return;

  const cached = renderCache.get(key);
  if (cached !== undefined) {
    pe.img.src = `data:image/png;base64,${cached}`;
    pe.renderedKey = key;
    ensureTextLayer(pe, pageNum);
    return;
  }
  try {
    const png = await renderToCache(pageNum, dpi);
    if (cacheKey(pageNum, currentDpi()) !== key || !pe.wrap.isConnected) return;
    pe.img.src = `data:image/png;base64,${png}`;
    pe.renderedKey = key;
    ensureTextLayer(pe, pageNum);
  } catch {
    /* leave the placeholder blank; it renders again when it next scrolls into view */
  }
}

// ------------------------------------------------------ selectable text layer

/** Fetches (and caches) the page's text runs and lays an invisible selectable layer over it. */
async function ensureTextLayer(pe, pageNum) {
  const key = `${state.version}|${pageNum}`;
  let spans = spanCache.get(key);
  if (spans === undefined) {
    try {
      const result = await host.call('page-text', {
        pdf: state.pdfB64, page: pageNum, pdfPassword: state.password,
      });
      spans = result.spans ?? [];
      spanCache.set(key, spans);
    } catch {
      return; // selection is a nicety; never block rendering on it
    }
  }
  if (!pe.wrap.isConnected || pe.textKey === key) return;
  buildTextLayer(pe, pageNum, spans);
  pe.textKey = key;
}

function buildTextLayer(pe, pageNum, spans) {
  pe.wrap.querySelector('.text-layer')?.remove();
  if (spans.length === 0) return;
  const layer = document.createElement('div');
  layer.className = 'text-layer';
  for (const s of spans) {
    const css = pdfRectToCss(pageNum, pe.img, s);
    if (css.width <= 0 || css.height <= 0) continue;
    const el = document.createElement('span');
    el.textContent = s.text;
    el.style.cssText = `left:${css.left}px;top:${css.top}px;font-size:${css.height}px;`;
    el.dataset.w = css.width;
    // Absolute PDF-space box, so a right-click can edit exactly this run in place.
    el.dataset.region = JSON.stringify({ page: pageNum, x: s.x, y: s.y, width: s.width, height: s.height });
    layer.appendChild(el);
  }
  pe.wrap.insertBefore(layer, pe.overlay);
  // One measure/adjust pass: stretch each run horizontally to match its rendered width.
  for (const el of layer.children) {
    const natural = el.offsetWidth;
    const target = Number(el.dataset.w);
    if (natural > 0) el.style.transform = `scaleX(${(target / natural).toFixed(4)})`;
  }
}

/** Rebuilds the page column for the current document and renders the visible page now. */
async function showDocument() {
  buildPages();
  buildThumbnails();  // no-op unless the sidebar is open; keyed off the new document version
  await renderPageEl(state.page); // render the landing page eagerly rather than waiting on the observer
  prefetchAround(state.page, currentDpi());
}

/** Smoothly scrolls a page to the top of the viewport. */
function goToPage(pageNum, behavior = 'smooth') {
  const n = Math.max(1, Math.min(state.info?.pageCount ?? 1, pageNum));
  pageEls[n - 1]?.wrap.scrollIntoView({ behavior, block: 'start' });
}

/** Re-lays-out every page at a new zoom, keeping the current page in view. */
function setZoom(z) {
  if (!state.pdf) return;
  const clamped = Math.max(0.5, Math.min(3, Math.round(z * 100) / 100));
  if (clamped === state.zoom) return;
  state.zoom = clamped;
  const anchor = state.page;
  buildPages();
  redrawInk(); // overlays were rebuilt; repaint any in-progress strokes at the new scale
  renderPageEl(anchor);
  goToPage(anchor, 'auto'); // jump, not smooth, so the same page stays put
  updateChrome();
}

/** Parses the editable page box and jumps there. */
function jumpToTypedPage() {
  const n = parseInt($('page-input').value, 10);
  if (Number.isFinite(n)) goToPage(n, 'auto');
  $('page-input').value = String(state.page); // normalise (clamp / reject junk)
  $('page-input').blur();
}

// --------------------------------------------------------- thumbnail sidebar

const THUMB_DPI = 26;              // small, fixed resolution for the page rail
let thumbEls = [];                 // one { wrap, img, renderedKey } per page
let thumbObserver = null;          // renders thumbnails lazily as they scroll into view

function toggleSidebar(force) {
  state.sidebarOpen = force ?? !state.sidebarOpen;
  $('thumbnails').hidden = !state.sidebarOpen;
  $('btn-sidebar').classList.toggle('active', state.sidebarOpen);
  if (state.sidebarOpen) buildThumbnails();
}

/** (Re)builds the thumbnail rail for the current document. */
function buildThumbnails() {
  if (!state.sidebarOpen || !state.info) return;
  thumbObserver?.disconnect();
  const rail = $('thumbnails');
  rail.innerHTML = '';
  thumbEls = [];
  for (let n = 1; n <= state.info.pageCount; n++) {
    const wrap = document.createElement('div');
    wrap.className = 'thumb';
    wrap.dataset.page = String(n);
    const img = document.createElement('img');
    img.alt = `Page ${n}`;
    const skeleton = document.createElement('div');
    skeleton.className = 'thumb-skeleton';
    const num = document.createElement('span');
    num.className = 'thumb-num';
    num.textContent = String(n);
    wrap.append(skeleton, num);
    wrap.addEventListener('click', () => goToPage(n, 'smooth'));
    rail.appendChild(wrap);
    thumbEls.push({ wrap, img, skeleton, renderedKey: null });
  }
  thumbObserver = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) renderThumb(Number(e.target.dataset.page));
  }, { root: rail, rootMargin: '300px 0px' });
  for (const t of thumbEls) thumbObserver.observe(t.wrap);
  markCurrentThumb();
}

/** Renders one thumbnail (from its own cache), so it never evicts full-size page renders. */
async function renderThumbToCache(page) {
  const key = `${state.version}|${page}`;
  const cached = thumbCache.get(key);
  if (cached !== undefined) {
    thumbCache.delete(key);
    thumbCache.set(key, cached); // move to most-recently-used
    return cached;
  }
  const result = await host.call('render', {
    pdf: state.pdfB64, page, dpi: THUMB_DPI, pdfPassword: state.password,
  });
  thumbCache.set(key, result.png);
  while (thumbCache.size > MAX_CACHED_THUMBS) {
    thumbCache.delete(thumbCache.keys().next().value);
  }
  return result.png;
}

async function renderThumb(pageNum) {
  const t = thumbEls[pageNum - 1];
  if (!t) return;
  const key = `${state.version}|${pageNum}`;
  if (t.renderedKey === key) return;
  try {
    const png = await renderThumbToCache(pageNum);
    if (!t.wrap.isConnected) return;
    t.img.src = `data:image/png;base64,${png}`;
    if (t.skeleton.parentNode) t.skeleton.replaceWith(t.img);
    t.renderedKey = key;
  } catch {
    /* leave the skeleton; it retries when it next scrolls into view */
  }
}

/** Highlights the thumbnail for the current page and scrolls it into view when needed. */
function markCurrentThumb() {
  if (!state.sidebarOpen) return;
  const current = thumbEls[state.page - 1];
  for (const t of thumbEls) t.wrap.classList.remove('current');
  if (!current) return;
  current.wrap.classList.add('current');
  // Only scroll the rail when the current thumbnail isn't already visible — scrolling it on
  // every scroll tick would fight the user and thrash layout.
  const rail = $('thumbnails').getBoundingClientRect();
  const box = current.wrap.getBoundingClientRect();
  if (box.top < rail.top || box.bottom > rail.bottom) current.wrap.scrollIntoView({ block: 'nearest' });
}

// ----------------------------------------------------------------- rotate

async function rotateCurrentPage(degrees) {
  if (!state.pdf) return;
  try {
    setStatus(`Rotating page ${state.page}…`, true);
    const result = await host.call('rotate', {
      pdf: state.pdfB64, pages: [state.page], degrees, pdfPassword: state.password,
    });
    const keepPage = state.page;
    await applyResult(result.pdf, `Rotated page ${keepPage}.`);
    goToPage(keepPage, 'auto');
  } catch (e) {
    fail(e);
  }
}

/** Updates just the page counter / nav buttons — cheap enough to call while scrolling. */
function updateNav() {
  if (!state.pdf) return;
  // Don't clobber what the user is typing into the page box while it has focus.
  if (document.activeElement !== $('page-input')) $('page-input').value = String(state.page);
  $('page-total').textContent = String(state.info.pageCount);
  $('btn-prev').disabled = state.page <= 1;
  $('btn-next').disabled = state.page >= state.info.pageCount;
  $('zoom-label').textContent = `${Math.round(state.zoom * 100)}%`;
  markCurrentThumb();
}

function updateChrome() {
  const loaded = !!state.pdf;
  for (const id of ['btn-save', 'btn-print', 'btn-sidebar', 'tool-text', 'tool-draw',
    'tool-highlight', 'tool-edit', 'tool-redact', 'tool-sign',
    'btn-rotate-left', 'btn-rotate-right', 'btn-forms', 'btn-organize', 'btn-js',
    'btn-find', 'btn-merge', 'btn-protect', 'btn-digital',
    'menu-read-trigger', 'menu-edit-trigger',
    'btn-prev', 'btn-next', 'btn-zoom-in', 'btn-zoom-out']) {
    $(id).disabled = !loaded;
  }
  $('page-input').disabled = !loaded;
  $('btn-undo').disabled = state.history.length === 0;
  $('btn-redo').disabled = state.future.length === 0;
  if (loaded) {
    updateNav();
    document.title = `${state.fileName} — PDF Editor`;
  }
  const badgesEl = $('badges');
  badgesEl.innerHTML = '';
  if (state.info?.encrypted || state.password) {
    const badge = document.createElement('span');
    badge.className = 'badge locked';
    badge.textContent = '🔒 encrypted';
    badgesEl.appendChild(badge);
  }
  if (loaded && state.safety?.javaScriptCount > 0) {
    const badge = document.createElement('span');
    badge.className = 'badge warn';
    badge.title = 'This document contains embedded JavaScript — click for details';
    badge.textContent = `⚠ JavaScript ${state.keepActiveContent ? 'kept' : 'disabled'}`;
    badge.addEventListener('click', showSafetyDialog);
    badgesEl.appendChild(badge);
  }
  if (loaded && URL_SCANNING_ENABLED && state.safety?.urlCount > 0) {
    const badge = document.createElement('span');
    badge.className = 'badge warn';
    badge.title = 'This document contains links — click to review';
    badge.textContent = `🔗 links ${state.keepLinks ? 'enabled' : 'disabled'}`;
    badge.addEventListener('click', openLinks);
    badgesEl.appendChild(badge);
  }
  for (const s of state.signatures) {
    // s.name/s.signer come from the PDF's (attacker-controlled) signature
    // metadata -- never treat them as HTML.
    const badge = document.createElement('span');
    badge.className = 'badge signed';
    badge.title = s.name ?? '';
    badge.textContent = `🖋 ${s.signer ?? 'signed'}${s.valid ? ' ✓' : ' ✗'}`;
    badgesEl.appendChild(badge);
  }
}

// -------------------------------------------------------------- region UI

function drawRegions() {
  for (const pe of pageEls) pe.overlay.querySelectorAll('.region').forEach((el) => el.remove());
  for (const [index, region] of state.regions.entries()) {
    addRegionDiv(region, 'redact', `#${index + 1}`);
  }
  if (state.pendingEditRegion) addRegionDiv(state.pendingEditRegion, 'edit');
  if (state.pendingSignRegion) addRegionDiv(state.pendingSignRegion, 'sign');
  renderRedactList();
}

function addRegionDiv(region, kind, label = '') {
  const pe = pageEls[region.page - 1];
  if (!pe) return; // page not currently laid out
  const css = pdfRectToCss(region.page, pe.img, region);
  const div = document.createElement('div');
  div.className = `region ${kind}`;
  div.style.cssText =
    `left:${css.left}px;top:${css.top}px;width:${css.width}px;height:${css.height}px;`;
  div.textContent = label;
  pe.overlay.appendChild(div);
}

function renderRedactList() {
  const list = $('redact-list');
  list.innerHTML = '';
  for (const [index, region] of state.regions.entries()) {
    const item = document.createElement('li');
    item.innerHTML = `<span>#${index + 1} — page ${region.page}</span>`;
    const remove = document.createElement('button');
    remove.textContent = '✕';
    remove.addEventListener('click', () => {
      state.regions.splice(index, 1);
      drawRegions();
    });
    item.appendChild(remove);
    list.appendChild(item);
  }
  const any = state.regions.length > 0;
  $('redact-preview').disabled = !any;
  $('redact-apply').disabled = !any;
  $('redact-clear').disabled = !any;
}

// --------------------------------------------------------- freehand drawing

/** Returns (creating if needed) the SVG layer that previews strokes on a page. */
function inkLayer(pe) {
  let svg = pe.overlay.querySelector('.ink-layer');
  if (!svg) {
    svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'ink-layer');
    pe.overlay.appendChild(svg);
  }
  return svg;
}

/** CSS pixels per PDF point on a page (for sizing the live stroke preview). */
function pxPerPoint(pageNum, img) {
  const p = pageSize(pageNum);
  const swap = p.rotation === 90 || p.rotation === 270;
  return img.clientWidth / (swap ? p.height : p.width);
}

let inkDrag = null; // { pe, pageNum, points: [{x,y}], poly }

/** Renders every captured stroke for a page (used after re-layout / zoom). */
function redrawInk() {
  for (const pe of pageEls) {
    const strokes = inkByPage.get(Number(pe.wrap.dataset.page));
    const svg = pe.overlay.querySelector('.ink-layer');
    if (svg) svg.innerHTML = '';
    if (!strokes || strokes.length === 0) continue;
    const layer = inkLayer(pe);
    const w = state.drawWidth * pxPerPoint(Number(pe.wrap.dataset.page), pe.img);
    for (const stroke of strokes) addPolyline(layer, stroke, w);
  }
}

function addPolyline(svg, points, widthPx) {
  const poly = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
  poly.setAttribute('points', points.map((pt) => `${pt.x},${pt.y}`).join(' '));
  poly.setAttribute('stroke', state.drawColor);
  poly.setAttribute('stroke-width', String(widthPx));
  svg.appendChild(poly);
  return poly;
}

// Drag-to-draw for edit/redact/sign/text tools. Delegated on the page column so a drag is
// attributed to whichever page it started on.
let drag = null;

pagesEl.addEventListener('pointerdown', (e) => {
  if (state.tool === 'select' || !state.pdf) return;
  const overlay = e.target.closest('.overlay');
  if (!overlay) return;
  const pe = pageEls.find((p) => p.overlay === overlay);
  if (!pe) return;
  const rect = overlay.getBoundingClientRect();

  if (state.tool === 'draw') {
    const pageNum = Number(pe.wrap.dataset.page);
    const point = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const poly = addPolyline(inkLayer(pe), [point], state.drawWidth * pxPerPoint(pageNum, pe.img));
    inkDrag = { pe, pageNum, points: [point], poly };
    overlay.setPointerCapture(e.pointerId);
    return;
  }

  drag = { pe, pageNum: Number(pe.wrap.dataset.page), x0: e.clientX - rect.left, y0: e.clientY - rect.top, div: null };
  overlay.setPointerCapture(e.pointerId);
});

pagesEl.addEventListener('pointermove', (e) => {
  if (inkDrag) {
    const rect = inkDrag.pe.overlay.getBoundingClientRect();
    inkDrag.points.push({ x: e.clientX - rect.left, y: e.clientY - rect.top });
    inkDrag.poly.setAttribute('points', inkDrag.points.map((pt) => `${pt.x},${pt.y}`).join(' '));
    return;
  }
  if (!drag) return;
  const rect = drag.pe.overlay.getBoundingClientRect();
  const x1 = e.clientX - rect.left;
  const y1 = e.clientY - rect.top;
  if (!drag.div) {
    drag.div = document.createElement('div');
    drag.div.className = `region ${
      state.tool === 'redact' ? '' : state.tool === 'sign' ? 'sign'
        : state.tool === 'highlight' ? 'highlight' : 'edit'}`;
    drag.pe.overlay.appendChild(drag.div);
  }
  const left = Math.min(drag.x0, x1);
  const top = Math.min(drag.y0, y1);
  drag.div.style.cssText =
    `left:${left}px;top:${top}px;width:${Math.abs(x1 - drag.x0)}px;height:${Math.abs(y1 - drag.y0)}px;`;
});

pagesEl.addEventListener('pointerup', async (e) => {
  if (inkDrag) {
    if (inkDrag.points.length > 0) {
      const list = inkByPage.get(inkDrag.pageNum) ?? [];
      list.push(inkDrag.points);
      inkByPage.set(inkDrag.pageNum, list);
    }
    inkDrag = null;
    return;
  }
  if (!drag) return;
  const { pe, pageNum, x0, y0, div } = drag;
  const rect = pe.overlay.getBoundingClientRect();
  const x1 = e.clientX - rect.left;
  const y1 = e.clientY - rect.top;
  drag = null;
  if (div) div.remove();

  const tiny = Math.abs(x1 - x0) < 4 || Math.abs(y1 - y0) < 4;

  // The text tool accepts a plain click: drop a default-sized text box at the point.
  if (state.tool === 'text' && tiny) {
    const at = cssToPdf(pageNum, pe.img, x0, y0);
    const h = 26;
    beginAddText({ page: pageNum, x: at.x, y: at.y - h, width: 240, height: h });
    return;
  }
  // A highlight swipe is naturally thin (dragging along a line of text), so it only needs enough
  // horizontal travel — the tiny check below (which wants a 2-D box) would otherwise reject it.
  if (state.tool === 'highlight') {
    if (Math.abs(x1 - x0) < 6) return;
    const a = cssToPdf(pageNum, pe.img, Math.min(x0, x1), Math.max(y0, y1));
    const b = cssToPdf(pageNum, pe.img, Math.max(x0, x1), Math.min(y0, y1));
    applyHighlight({ page: pageNum, x: a.x, y: a.y, width: b.x - a.x, height: Math.max(b.y - a.y, 1) });
    return;
  }
  if (tiny) return;

  const a = cssToPdf(pageNum, pe.img, Math.min(x0, x1), Math.max(y0, y1)); // bottom-left
  const b = cssToPdf(pageNum, pe.img, Math.max(x0, x1), Math.min(y0, y1)); // top-right
  const region = {
    page: pageNum,
    x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y,
  };

  if (state.tool === 'redact') {
    state.regions.push(region);
    drawRegions();
  } else if (state.tool === 'field') {
    placeField(region);
  } else if (state.tool === 'text') {
    beginAddText(region);
  } else if (state.tool === 'edit') {
    state.pendingEditRegion = region;
    drawRegions();
    await beginTextEdit(region);
  } else if (state.tool === 'sign') {
    state.pendingSignRegion = region;
    drawRegions();
    showPanel('panel-sign');
  }
});

// Right-click a word (in select mode) to edit that run of text in place.
pagesEl.addEventListener('contextmenu', (e) => {
  if (state.tool !== 'select' || !state.pdf) return;
  const span = e.target.closest('.text-layer span');
  if (!span?.dataset.region) return;
  e.preventDefault();
  const r = JSON.parse(span.dataset.region);
  const pad = 1; // capture the whole run comfortably
  const region = { page: r.page, x: r.x - pad, y: r.y - pad, width: r.width + 2 * pad, height: r.height + 2 * pad };
  state.pendingEditRegion = region;
  beginTextEdit(region);
});

// ----------------------------------------------------------------- panels

function showPanel(id) {
  $('panel').hidden = false;
  for (const section of document.querySelectorAll('.panel-section')) {
    section.hidden = section.id !== id;
  }
}

function hidePanels() {
  $('panel').hidden = true;
  state.pendingEditRegion = null;
  state.pendingSignRegion = null;
  drawRegions();
}

function setTool(tool) {
  if (state.tool === 'draw' && tool !== 'draw') clearDrawing(); // drop uncommitted strokes
  state.tool = tool;
  for (const button of document.querySelectorAll('.tool')) button.classList.remove('active');
  $(`tool-${tool}`).classList.add('active');
  // Surface on the Edit menu trigger that an editing tool is active (select lives outside the menu).
  $('menu-edit-trigger').classList.toggle('has-active', tool !== 'select');
  for (const pe of pageEls) pe.overlay.classList.toggle('tool-active', tool !== 'select');
  // In select mode the text layer is interactive (select/copy); tools capture the overlay instead.
  pagesEl.classList.toggle('select-mode', tool === 'select');
  if (tool === 'redact') showPanel('panel-redact');
  else if (tool === 'draw') showPanel('panel-draw');
  else if (tool === 'highlight') showPanel('panel-highlight');
  else hidePanels(); // select/text/edit/sign: panel appears once a box is drawn
}

// ---------------------------------------------------------------- dialogs

/** Small form dialog; resolves with {fieldId: value} or null when cancelled. */
function promptDialog(title, fields, confirmLabel = 'OK') {
  return new Promise((resolve) => {
    modal.innerHTML = `<h2>${title}</h2>`;
    const inputs = {};
    for (const field of fields) {
      const label = document.createElement('label');
      label.textContent = field.label;
      const input = document.createElement('input');
      input.type = field.type ?? 'text';
      input.value = field.value ?? '';
      if (field.placeholder) input.placeholder = field.placeholder;
      label.appendChild(input);
      modal.appendChild(label);
      inputs[field.id] = input;
    }
    const actions = document.createElement('div');
    actions.className = 'actions';
    const ok = document.createElement('button');
    ok.textContent = confirmLabel;
    ok.className = 'danger';
    const cancel = document.createElement('button');
    cancel.textContent = 'Cancel';
    actions.append(ok, cancel);
    modal.appendChild(actions);

    const done = (value) => { modal.close(); resolve(value); };
    ok.addEventListener('click', () =>
      done(Object.fromEntries(Object.entries(inputs).map(([k, i]) => [k, i.value]))));
    cancel.addEventListener('click', () => done(null));
    modal.showModal();
    Object.values(inputs)[0]?.focus();
  });
}

// ------------------------------------------------------------- redaction

/** Finds every occurrence of a phrase and marks each as a redaction box. */
async function searchAndMarkRedactions() {
  const phrase = $('redact-search-text').value.trim();
  if (!phrase) { toast('Enter some text to search for.'); return; }
  if (!state.pdf) return;
  try {
    setStatus(`Searching for “${phrase}”…`, true);
    const result = await host.call('find-text', {
      pdf: state.pdfB64, phrase, pdfPassword: state.password,
    });
    const matches = result.matches ?? [];
    setStatus('');
    if (matches.length === 0) { toast(`No matches for “${phrase}”.`); return; }
    // Pad each match slightly so the box fully covers the glyphs' edges.
    const pad = 1;
    for (const m of matches) {
      state.regions.push({
        page: m.page,
        x: m.x - pad, y: m.y - pad,
        width: m.width + 2 * pad, height: m.height + 2 * pad,
      });
    }
    $('redact-search-text').value = '';
    drawRegions();
    toast(`Marked ${matches.length} match${matches.length === 1 ? '' : 'es'} of “${phrase}” — review, then Preview or Apply.`);
  } catch (e) {
    fail(e);
  }
}

async function previewRedaction() {
  try {
    setStatus('Building redaction preview…', true);
    const result = await host.call('redact', {
      pdf: state.pdfB64, regions: state.regions, pdfPassword: state.password,
    });
    const pages = [...new Set(state.regions.map((r) => r.page))].sort((a, b) => a - b);
    const images = [];
    for (const page of pages) {
      const rendered = await host.call('render', {
        pdf: result.pdf, page, dpi: 110, pdfPassword: state.password,
      });
      images.push({ page, png: rendered.png });
    }
    setStatus('');

    modal.innerHTML = '<h2>Redaction preview</h2>' +
      '<p class="muted">This is how the affected pages will look. Applying removes the ' +
      'content behind each box permanently — this cannot be undone after saving.</p>';
    const container = document.createElement('div');
    container.className = 'preview-pages';
    for (const image of images) {
      const caption = document.createElement('p');
      caption.className = 'muted';
      caption.textContent = `Page ${image.page}`;
      const img = document.createElement('img');
      img.src = `data:image/png;base64,${image.png}`;
      container.append(caption, img);
    }
    modal.appendChild(container);
    const actions = document.createElement('div');
    actions.className = 'actions';
    const apply = document.createElement('button');
    apply.className = 'danger';
    apply.textContent = 'Apply redaction';
    const close = document.createElement('button');
    close.textContent = 'Close preview';
    actions.append(apply, close);
    modal.appendChild(actions);
    modal.showModal();

    apply.addEventListener('click', async () => {
      modal.close();
      await applyRedaction(result);
    });
    close.addEventListener('click', () => modal.close());
  } catch (e) {
    fail(e);
  }
}

async function applyRedaction(precomputed) {
  try {
    setStatus('Applying redaction…', true);
    const result = precomputed ?? await host.call('redact', {
      pdf: state.pdfB64, regions: state.regions, pdfPassword: state.password,
    });
    const count = state.regions.length;
    state.regions = [];
    await applyResult(result.pdf,
      `Redacted ${count} region${count === 1 ? '' : 's'} — content removed.` +
      (result.warnings?.length ? ` ⚠ ${result.warnings.join(' ')}` : ''));
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------- text edit

function setStyleToggle(id, on) {
  $(id).classList.toggle('active', !!on);
}

async function beginTextEdit(region) {
  try {
    setStatus('Reading text in region…', true);
    const found = await host.call('get-region-text', {
      pdf: state.pdfB64, region, pdfPassword: state.password,
    });
    setStatus('');
    state.textMode = 'edit';
    $('edit-title').textContent = 'Edit text';
    $('edit-hint').textContent = 'Text found in the selected region:';
    $('edit-text').value = found.text;
    $('edit-size').value = Number(found.fontSize).toFixed(1);
    // Pre-fill the font controls with what was detected in the region.
    $('edit-font').value = ['helvetica', 'times', 'courier'].includes(found.fontFamily)
      ? found.fontFamily : 'helvetica';
    setStyleToggle('edit-bold', found.bold);
    setStyleToggle('edit-italic', found.italic);
    $('edit-color').value = '#000000';
    showPanel('panel-edit');
    $('edit-text').focus();
  } catch (e) {
    fail(e);
  }
}

/** Opens the text panel in "add" mode for stamping brand-new text into a region. */
function beginAddText(region) {
  state.textMode = 'add';
  state.pendingEditRegion = region;
  $('edit-title').textContent = 'Add text';
  $('edit-hint').textContent = 'Type the text to place on the page:';
  $('edit-text').value = '';
  // Default the size to roughly the box height so a dragged box sets the type size.
  $('edit-size').value = Math.max(8, Math.min(72, Math.round(region.height))).toFixed(1);
  $('edit-font').value = 'helvetica';
  setStyleToggle('edit-bold', false);
  setStyleToggle('edit-italic', false);
  $('edit-color').value = '#000000';
  showPanel('panel-edit');
  $('edit-text').focus();
}

async function applyTextEdit() {
  const region = state.pendingEditRegion;
  if (!region) return;
  const adding = state.textMode === 'add';
  if (adding && !$('edit-text').value.trim()) { toast('Type some text first.'); return; }
  try {
    setStatus(adding ? 'Adding text…' : 'Replacing text…', true);
    const result = await host.call(adding ? 'add-text' : 'replace-region-text', {
      pdf: state.pdfB64,
      region,
      text: $('edit-text').value,
      fontSize: parseFloat($('edit-size').value) || undefined,
      fontFamily: $('edit-font').value,
      bold: $('edit-bold').classList.contains('active'),
      italic: $('edit-italic').classList.contains('active'),
      color: $('edit-color').value,
      pdfPassword: state.password,
    });
    hidePanels();
    if (adding) setTool('text');
    await applyContentEdit(result.pdf, [region.page], adding ? 'Text added.' : 'Text replaced.');
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------- highlighter

function rectsIntersect(a, b) {
  return a.x < b.x + b.width && b.x < a.x + a.width &&
         a.y < b.y + b.height && b.y < a.y + a.height;
}

/** Highlights the text runs a dragged box covers (or the box itself if the page has no text). */
async function applyHighlight(region) {
  const spans = spanCache.get(`${state.version}|${region.page}`) ?? [];
  const covered = spans.filter((s) => rectsIntersect(s, region))
    .map((s) => ({ x: s.x, y: s.y, width: s.width, height: s.height }));
  const rects = covered.length > 0
    ? covered
    : [{ x: region.x, y: region.y, width: region.width, height: region.height }];
  try {
    setStatus('Highlighting…', true);
    const result = await host.call('add-highlight', {
      pdf: state.pdfB64, page: region.page, rects,
      color: state.highlightColor, pdfPassword: state.password,
    });
    await applyContentEdit(result.pdf, [region.page],
      `Highlighted ${rects.length} ${rects.length === 1 ? 'run' : 'runs'}.`);
  } catch (e) {
    fail(e);
  }
}

// ----------------------------------------------------------- draw (ink) tool

/** Converts captured CSS strokes for a page to PDF-space strokes and stamps them in. */
async function applyDrawing() {
  const pages = [...inkByPage.entries()].filter(([, s]) => s.length > 0);
  if (pages.length === 0) { toast('Draw something first.'); return; }
  try {
    setStatus('Applying drawing…', true);
    let pdfB64 = state.pdfB64;
    let total = 0;
    for (const [pageNum, strokes] of pages) {
      const pe = pageEls[pageNum - 1];
      const pdfStrokes = strokes.map((stroke) =>
        stroke.map((pt) => {
          const p = cssToPdf(pageNum, pe.img, pt.x, pt.y);
          return { x: p.x, y: p.y };
        }));
      const result = await host.call('add-drawing', {
        pdf: pdfB64, page: pageNum, strokes: pdfStrokes,
        color: state.drawColor, width: state.drawWidth, pdfPassword: state.password,
      });
      pdfB64 = result.pdf;      // chain each page's strokes onto the growing document
      total += strokes.length;
    }
    const affectedPages = pages.map(([pageNum]) => pageNum);
    clearDrawing();
    await applyContentEdit(pdfB64, affectedPages, `Added ${total} stroke${total === 1 ? '' : 's'}.`);
    setTool('draw');
  } catch (e) {
    fail(e);
  }
}

function clearDrawing() {
  inkByPage.clear();
  for (const pe of pageEls) {
    const svg = pe.overlay.querySelector('.ink-layer');
    if (svg) svg.remove();
  }
}

// ------------------------------------------------------------- signatures

let padDrawing = false;
let padDirty = false;

function initSignaturePad() {
  const pad = $('sign-pad');
  const ctx = pad.getContext('2d');
  ctx.lineWidth = 2.2;
  ctx.lineCap = 'round';
  ctx.strokeStyle = '#1a237e';
  const position = (e) => {
    const rect = pad.getBoundingClientRect();
    return {
      x: (e.clientX - rect.left) * (pad.width / rect.width),
      y: (e.clientY - rect.top) * (pad.height / rect.height),
    };
  };
  pad.addEventListener('pointerdown', (e) => {
    padDrawing = true;
    padDirty = true;
    const p = position(e);
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    pad.setPointerCapture(e.pointerId);
  });
  pad.addEventListener('pointermove', (e) => {
    if (!padDrawing) return;
    const p = position(e);
    ctx.lineTo(p.x, p.y);
    ctx.stroke();
  });
  pad.addEventListener('pointerup', () => { padDrawing = false; });
  $('sign-pad-clear').addEventListener('click', () => {
    ctx.clearRect(0, 0, pad.width, pad.height);
    padDirty = false;
  });
}

async function applyImageSignature() {
  const region = state.pendingSignRegion;
  if (!region) return;
  try {
    let pngB64;
    if (!$('sign-upload').hidden) {
      const file = $('sign-file').files[0];
      if (!file) { toast('Choose an image first.'); return; }
      pngB64 = bytesToBase64(new Uint8Array(await file.arrayBuffer()));
    } else {
      if (!padDirty) { toast('Draw a signature first.'); return; }
      pngB64 = $('sign-pad').toDataURL('image/png').split(',')[1];
    }
    setStatus('Placing signature…', true);
    const result = await host.call('sign-image', {
      pdf: state.pdfB64, region, png: pngB64, pdfPassword: state.password,
    });
    hidePanels();
    setTool('select');
    await applyResult(result.pdf, 'Signature placed.');
  } catch (e) {
    fail(e);
  }
}

async function digitallySign() {
  const choice = await promptDialog('Digital certificate signature', [
    { id: 'reason', label: 'Reason (optional)', placeholder: 'Approved' },
    { id: 'location', label: 'Location (optional)' },
    { id: 'password', label: 'Certificate password', type: 'password' },
  ], 'Continue');
  if (!choice) return;

  modal.innerHTML = '<h2>Certificate</h2>' +
    '<p class="muted">Pick an existing PKCS#12 certificate (.p12/.pfx) or create a ' +
    'self-signed one. A digital signature proves the document has not been altered ' +
    'since signing.</p>';
  const actions = document.createElement('div');
  actions.className = 'actions';
  const useFile = document.createElement('button');
  useFile.textContent = '📄 Use certificate file…';
  const create = document.createElement('button');
  create.textContent = '✨ Create self-signed';
  const cancel = document.createElement('button');
  cancel.textContent = 'Cancel';
  actions.append(useFile, create, cancel);
  modal.appendChild(actions);
  modal.showModal();

  const sign = async (pfxB64, pfxPassword) => {
    try {
      setStatus('Signing…', true);
      const result = await host.call('sign-digital', {
        pdf: state.pdfB64,
        pfx: pfxB64,
        pfxPassword,
        reason: choice.reason || undefined,
        location: choice.location || undefined,
        pdfPassword: state.password,
      });
      await applyResult(result.pdf, 'Document digitally signed.');
    } catch (e) {
      fail(e);
    }
  };

  useFile.addEventListener('click', () => {
    modal.close();
    $('pfx-input').onchange = async () => {
      const file = $('pfx-input').files[0];
      $('pfx-input').value = '';
      if (!file) return;
      await sign(bytesToBase64(new Uint8Array(await file.arrayBuffer())), choice.password);
    };
    $('pfx-input').click();
  });
  create.addEventListener('click', async () => {
    modal.close();
    const details = await promptDialog('Create self-signed certificate', [
      { id: 'name', label: 'Your name', placeholder: 'Jane Citizen' },
      { id: 'pw', label: 'New certificate password', type: 'password' },
    ], 'Create & sign');
    if (!details) return;
    try {
      setStatus('Creating certificate…', true);
      const cert = await host.call('create-cert', {
        name: details.name || 'PDF Editor User', password: details.pw,
      });
      const blob = new Blob([base64ToBytes(cert.pfx)], { type: 'application/x-pkcs12' });
      chrome.downloads.download({
        url: URL.createObjectURL(blob),
        filename: 'pdf-editor-certificate.p12',
        saveAs: false,
      });
      toast('Certificate saved to your downloads for future use.');
      await sign(cert.pfx, details.pw);
    } catch (e) {
      fail(e);
    }
  });
  cancel.addEventListener('click', () => modal.close());
}

// ------------------------------------------------------ merge and protect

/** Classifies a picked file as a pdf, image, or Word doc by type/extension. */
function mergeKind(file) {
  const name = (file.name || '').toLowerCase();
  if (file.type.startsWith('image/') || /\.(png|jpe?g|gif|bmp|tiff?|webp)$/.test(name)) return 'image';
  if (/\.docx?$/.test(name) ||
      file.type === 'application/vnd.openxmlformats-officedocument.wordprocessingml.document') return 'docx';
  return 'pdf';
}

const MERGE_ICON = { pdf: '📄', image: '🖼', docx: '📝' };

async function mergeFiles() {
  $('merge-input').onchange = async () => {
    const files = [...$('merge-input').files];
    $('merge-input').value = '';
    if (files.length === 0) return;
    // Build the ordered entry list: the current document first, then each picked file tagged with
    // its kind so the host can convert images/Word to PDF pages before concatenating.
    const entries = [{ label: 'This document', data: state.pdfB64, kind: 'pdf', base: true }];
    for (const file of files) {
      entries.push({
        label: file.name || 'file',
        data: bytesToBase64(new Uint8Array(await file.arrayBuffer())),
        kind: mergeKind(file),
      });
    }
    showMergeDialog(entries);
  };
  $('merge-input').click();
}

/** Lets the user arrange (and drop) files before combining them, then merges in that order. */
function showMergeDialog(entries) {
  let order = entries.map((_, i) => i);

  modal.innerHTML = '<h2>Merge &amp; arrange</h2>' +
    '<p class="muted">Drag to set the order the files are combined in, or remove any you don\'t ' +
    'want. The current document is included first by default.</p>';
  const list = document.createElement('ol');
  list.className = 'reorder-list';
  list.id = 'merge-list';
  modal.appendChild(list);

  const move = (from, to) => {
    if (to < 0 || to >= order.length) return;
    const [it] = order.splice(from, 1);
    order.splice(to, 0, it);
    render();
  };
  const remove = (pos) => { if (order.length > 1) { order.splice(pos, 1); render(); } };

  let dragPos = null;
  const render = () => {
    list.innerHTML = '';
    order.forEach((entryIndex, pos) => {
      const e = entries[entryIndex];
      const li = document.createElement('li');
      li.className = 'organize-item';
      li.draggable = true;
      li.dataset.pos = String(pos);

      const grip = document.createElement('span');
      grip.className = 'organize-grip';
      grip.textContent = '⠿';
      const label = document.createElement('span');
      label.className = 'organize-label';
      label.textContent = `${MERGE_ICON[e.kind] ?? '📄'} ${e.label}`;

      const up = actionBtn('▲', 'Move up', pos === 0, () => move(pos, pos - 1));
      const down = actionBtn('▼', 'Move down', pos === order.length - 1, () => move(pos, pos + 1));
      const del = actionBtn('🗑', 'Remove', order.length <= 1, () => remove(pos));
      del.classList.add('organize-del');

      li.append(grip, label, up, down, del);
      wireReorderDnD(li, pos, () => dragPos, (v) => { dragPos = v; }, move);
      list.appendChild(li);
    });
  };
  render();

  const actions = document.createElement('div');
  actions.className = 'actions';
  const ok = document.createElement('button');
  ok.textContent = 'Merge';
  ok.className = 'danger';
  const cancel = document.createElement('button');
  cancel.textContent = 'Cancel';
  actions.append(ok, cancel);
  modal.appendChild(actions);

  ok.addEventListener('click', async () => {
    modal.close();
    const chosen = order.map((i) => entries[i]);
    try {
      setStatus('Merging…', true);
      const result = await host.call('merge-files', {
        files: chosen.map((e) => ({ data: e.data, kind: e.kind })),
      });
      const added = chosen.filter((e) => !e.base).length;
      await applyResult(result.pdf, `Merged ${added} file${added === 1 ? '' : 's'} in.`);
    } catch (e) {
      fail(e);
    }
  });
  cancel.addEventListener('click', () => modal.close());
  modal.showModal();
}

/** A small toolbar-style button used inside reorder rows. */
function actionBtn(text, title, disabled, onClick) {
  const b = document.createElement('button');
  b.textContent = text;
  b.title = title;
  b.setAttribute('aria-label', title); // accessible name (the label is an emoji glyph)
  b.disabled = disabled;
  b.addEventListener('click', onClick);
  return b;
}

/** Wires HTML5 drag-and-drop reordering onto a row; getPos/setPos hold the dragged position. */
function wireReorderDnD(li, pos, getPos, setPos, move) {
  li.addEventListener('dragstart', (e) => {
    setPos(pos);
    li.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
  });
  li.addEventListener('dragend', () => { li.classList.remove('dragging'); setPos(null); });
  li.addEventListener('dragover', (e) => { e.preventDefault(); li.classList.add('drop-target'); });
  li.addEventListener('dragleave', () => li.classList.remove('drop-target'));
  li.addEventListener('drop', (e) => {
    e.preventDefault();
    li.classList.remove('drop-target');
    const from = getPos();
    if (from !== null && from !== pos) move(from, pos);
  });
}

// ------------------------------------------------------------- page organizer

/** Opens the organizer: a reorderable, deletable list of the document's pages. */
async function openOrganize() {
  if (!state.info) return;
  state.organizeOrder = Array.from({ length: state.info.pageCount }, (_, i) => i + 1);
  showPanel('panel-organize');
  renderOrganizeList();
}

function renderOrganizeList() {
  const list = $('organize-list');
  list.innerHTML = '';
  const move = (from, to) => {
    if (to < 0 || to >= state.organizeOrder.length) return;
    const [it] = state.organizeOrder.splice(from, 1);
    state.organizeOrder.splice(to, 0, it);
    renderOrganizeList();
  };
  state.organizeOrder.forEach((pageNum, index) => {
    const li = document.createElement('li');
    li.className = 'organize-item';
    li.draggable = true;
    li.dataset.index = String(index);

    const grip = document.createElement('span');
    grip.className = 'organize-grip';
    grip.textContent = '⠿';
    const img = document.createElement('img');
    img.alt = `Page ${pageNum}`;
    const label = document.createElement('span');
    label.className = 'organize-label';
    label.textContent = `Page ${pageNum}`;

    const up = actionBtn('▲', 'Move up', index === 0, () => move(index, index - 1));
    const down = actionBtn('▼', 'Move down', index === state.organizeOrder.length - 1,
      () => move(index, index + 1));
    const del = actionBtn('🗑', 'Remove page', state.organizeOrder.length <= 1, () => {
      if (state.organizeOrder.length > 1) { state.organizeOrder.splice(index, 1); renderOrganizeList(); }
    });
    del.classList.add('organize-del');

    li.append(grip, img, label, up, down, del);
    wireReorderDnD(li, index, () => organizeDragIndex, (v) => { organizeDragIndex = v; }, move);
    list.appendChild(li);

    renderThumbToCache(pageNum)
      .then((png) => { if (li.isConnected) img.src = `data:image/png;base64,${png}`; })
      .catch(() => {});
  });
}

let organizeDragIndex = null;

async function applyOrganize() {
  const order = state.organizeOrder;
  const original = Array.from({ length: state.info.pageCount }, (_, i) => i + 1);
  const unchanged = order.length === original.length && order.every((v, i) => v === original[i]);
  if (unchanged) { hidePanels(); toast('No page changes to apply.'); return; }
  try {
    setStatus('Reorganizing pages…', true);
    const result = await host.call('arrange-pages', {
      pdf: state.pdfB64, order, pdfPassword: state.password,
    });
    hidePanels();
    await applyResult(result.pdf, 'Pages reorganized.');
  } catch (e) {
    fail(e);
  }
}

async function protect() {
  const value = await promptDialog('Password-protect (AES-256 encryption)', [
    { id: 'user', label: 'Password to open the document', type: 'password' },
    { id: 'owner', label: 'Owner password (optional, defaults to the same)', type: 'password' },
  ], 'Encrypt');
  if (!value) return;
  if (!value.user) { toast('A password is required.'); return; }
  if (state.signatures.length > 0) {
    const confirmed = await promptDialog(
      'This document is digitally signed. Encrypting rewrites the file and breaks ' +
      'existing signatures — sign again afterwards. Type YES to continue.',
      [{ id: 'confirm', label: 'Confirmation' }], 'Continue');
    if (!confirmed || confirmed.confirm !== 'YES') return;
  }
  try {
    setStatus('Encrypting…', true);
    const result = await host.call('encrypt', {
      pdf: state.pdfB64,
      userPassword: value.user,
      ownerPassword: value.owner || undefined,
      pdfPassword: state.password,
    });
    state.password = value.user;
    await applyResult(result.pdf, 'Document encrypted. Keep the password safe!');
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------- find and replace

async function findReplace() {
  const value = await promptDialog('Find & replace across the document', [
    { id: 'find', label: 'Find' },
    { id: 'replace', label: 'Replace with' },
  ], 'Replace all');
  if (!value || !value.find) return;
  try {
    setStatus('Replacing…', true);
    const result = await host.call('replace-all', {
      pdf: state.pdfB64,
      phrase: value.find,
      replacement: value.replace,
      pdfPassword: state.password,
    });
    if (result.count === 0) {
      setStatus('');
      toast(`No matches for “${value.find}”.`);
      return;
    }
    await applyResult(result.pdf, `Replaced ${result.count} occurrence${result.count === 1 ? '' : 's'}.`);
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------ open / save

async function openFromBytes(bytes, name) {
  try {
    state.history = [];
    state.future = [];
    state.password = null;
    state.regions = [];
    state.keepActiveContent = false; // re-arm the strip-on-save default for each new document
    state.keepLinks = false;
    state.urlVerdicts = [];
    await loadDocument(bytes, name);
    toast(`Opened ${name}.`);
  } catch (e) {
    fail(e);
  }
}

async function openFilePicker() {
  $('file-input').onchange = async () => {
    const file = $('file-input').files[0];
    $('file-input').value = '';
    if (file) await openFromBytes(new Uint8Array(await file.arrayBuffer()), file.name);
  };
  $('file-input').click();
}

// Only http(s)/file URLs ending in .pdf are legitimate here -- this page is
// opened with a `src=` query param that (in principle) reflects whatever the
// caller passed, so re-validate it ourselves rather than trusting that every
// caller already did (defense in depth; this must never become an arbitrary-
// URL-fetch-with-credentials primitive).
function looksLikePdfUrl(rawUrl) {
  try {
    const parsed = new URL(rawUrl);
    if (!/^https?:|^file:/.test(parsed.protocol)) return false;
    return parsed.pathname.toLowerCase().endsWith('.pdf');
  } catch {
    return false;
  }
}

async function openFromUrl(url) {
  if (!looksLikePdfUrl(url)) {
    fail(new Error('Refusing to open a non-PDF or unsupported URL.'));
    return;
  }
  try {
    setStatus(`Fetching ${url}…`, true);
    const response = await fetch(url, { credentials: 'include' });
    if (!response.ok) throw new Error(`Could not fetch the PDF (HTTP ${response.status}).`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    const name = decodeURIComponent(new URL(url).pathname.split('/').pop() || 'document.pdf');
    await openFromBytes(bytes, name);
  } catch (e) {
    fail(e);
  }
}

// ----------------------------------------------------- forms + JS/URL safety

/** Explains detected active content and lets the user keep it (default: strip on save). */
function showSafetyDialog() {
  const s = state.safety;
  if (!s?.javaScriptCount) return;
  modal.innerHTML = '<h2>⚠ Embedded JavaScript</h2>';
  const p = document.createElement('p');
  p.className = 'muted';
  p.textContent = `This document contains ${s.javaScriptCount} embedded JavaScript ` +
    `action${s.javaScriptCount === 1 ? '' : 's'}, which can run when the file is opened in ` +
    'another PDF viewer. It is ' +
    (state.keepActiveContent ? 'currently kept.' : 'disabled and will be removed when you save.');
  modal.appendChild(p);
  if (s.samples?.length) {
    const pre = document.createElement('pre');
    pre.className = 'safety-samples';
    pre.textContent = s.samples.join('\n'); // textContent — untrusted script/URL text is never executed
    modal.appendChild(pre);
  }
  const actions = document.createElement('div');
  actions.className = 'actions';
  const toggle = document.createElement('button');
  toggle.className = state.keepActiveContent ? '' : 'danger';
  toggle.textContent = state.keepActiveContent ? 'Disable & strip on save' : 'Enable (keep) active content';
  const close = document.createElement('button');
  close.textContent = 'Close';
  actions.append(toggle, close);
  modal.appendChild(actions);
  modal.showModal();
  toggle.addEventListener('click', () => {
    state.keepActiveContent = !state.keepActiveContent;
    modal.close();
    updateChrome();
    toast(state.keepActiveContent
      ? 'Active content will be kept when you save.'
      : 'Active content will be stripped when you save.');
  });
  close.addEventListener('click', () => modal.close());
}

async function openForms() {
  try {
    setStatus('Reading form fields…', true);
    const result = await host.call('form-fields', { pdf: state.pdfB64, pdfPassword: state.password });
    setStatus('');
    const fields = result.fields ?? [];
    const list = $('forms-list');
    list.innerHTML = '';
    $('forms-empty').hidden = fields.length > 0;
    $('forms-flatten-row').hidden = fields.length === 0;
    $('forms-apply').disabled = fields.length === 0;
    for (const f of fields) {
      const row = document.createElement('div');
      row.className = 'form-field';
      const label = document.createElement('span');
      label.textContent = f.name; // textContent — field names come from the document
      row.appendChild(label);
      let input;
      if (f.type === 'checkbox') {
        input = document.createElement('input');
        input.type = 'checkbox';
        input.dataset.on = (f.options || []).find((o) => o && o !== 'Off') || 'Yes';
        input.checked = !!f.value && f.value !== 'Off';
      } else if (f.type === 'choice' && f.options?.length) {
        input = document.createElement('select');
        for (const o of f.options) {
          const opt = document.createElement('option');
          opt.value = o;
          opt.textContent = o;
          input.appendChild(opt);
        }
        input.value = f.value;
      } else {
        input = document.createElement('input');
        input.type = 'text';
        input.value = f.value ?? '';
      }
      input.dataset.field = f.name;
      if (f.readOnly) input.disabled = true;
      row.appendChild(input);
      list.appendChild(row);
    }
    showPanel('panel-forms');
  } catch (e) {
    fail(e);
  }
}

async function applyForms() {
  const values = {};
  for (const input of $('forms-list').querySelectorAll('[data-field]')) {
    if (input.disabled) continue;
    values[input.dataset.field] = input.type === 'checkbox'
      ? (input.checked ? input.dataset.on : 'Off')
      : input.value;
  }
  const flatten = $('forms-flatten').checked;
  try {
    setStatus('Filling form…', true);
    const result = await host.call('fill-form', {
      pdf: state.pdfB64, values, flatten, pdfPassword: state.password,
    });
    hidePanels();
    await applyResult(result.pdf, flatten ? 'Form filled and flattened.' : 'Form filled.');
  } catch (e) {
    fail(e);
  }
}

const FIELD_LABELS = {
  text: 'Text field', multiline: 'Text area', checkbox: 'Checkbox', dropdown: 'Dropdown',
  button: 'Button',
};

/** Shows only the extra inputs (options / caption+script) the chosen field type needs. */
function updateFieldTypeRows() {
  const type = $('field-type').value;
  $('field-options-row').hidden = type !== 'dropdown';
  $('field-caption-row').hidden = type !== 'button';
  $('field-script-row').hidden = type !== 'button';
}

/** Enters "place a field" mode: the next box drawn on a page becomes a new form field. */
function beginPlaceField() {
  if (!state.pdf) return;
  const fieldType = $('field-type').value;
  const options = fieldType === 'dropdown'
    ? $('field-options').value.split('\n').map((o) => o.trim()).filter(Boolean)
    : [];
  if (fieldType === 'dropdown' && options.length === 0) {
    toast('Add at least one dropdown option first.');
    return;
  }
  state.pendingField = {
    fieldType, name: $('field-name').value.trim(), options,
    caption: fieldType === 'button' ? $('field-caption').value.trim() : '',
    script: fieldType === 'button' ? $('field-script').value : '',
  };
  state.tool = 'field';
  for (const b of document.querySelectorAll('.tool')) b.classList.remove('active');
  for (const pe of pageEls) pe.overlay.classList.add('tool-active');
  pagesEl.classList.remove('select-mode'); // let the overlay capture the placement drag
  hidePanels();
  toast('Drag a box where the field should go.');
}

async function placeField(region) {
  const pf = state.pendingField;
  if (!pf) return;
  state.pendingField = null;
  try {
    setStatus('Adding field…', true);
    const result = await host.call('add-form-field', {
      pdf: state.pdfB64, region, fieldType: pf.fieldType,
      name: pf.name || undefined, options: pf.options?.length ? pf.options : undefined,
      caption: pf.caption || undefined, script: pf.script || undefined,
      pdfPassword: state.password,
    });
    // A button carrying a script is deliberately-authored active content — keep it on save.
    if (pf.script) state.keepActiveContent = true;
    setTool('select');
    await applyResult(result.pdf, `${FIELD_LABELS[pf.fieldType] ?? 'Field'} added.`);
    openForms(); // show the updated field list (and let them add another)
  } catch (e) {
    fail(e);
  }
}

// --------------------------------------------------------- document JavaScript

/** Opens the JavaScript panel: a list of the document's scripts plus a small code editor. */
async function openJavaScript() {
  if (!state.pdf) return;
  $('js-name').value = '';
  $('js-source').value = '';
  showPanel('panel-js');
  try {
    setStatus('Reading scripts…', true);
    await refreshScripts();
    setStatus('');
  } catch (e) {
    fail(e);
  }
}

/** Reloads the document's script list into the panel without disturbing the status line. */
async function refreshScripts() {
  const result = await host.call('list-scripts', { pdf: state.pdfB64, pdfPassword: state.password });
  state.scripts = result.scripts ?? [];
  renderScriptList();
}

function renderScriptList() {
  const list = $('js-list');
  list.innerHTML = '';
  for (const s of state.scripts) {
    const li = document.createElement('li');
    li.className = 'organize-item';
    const label = document.createElement('span');
    label.className = 'organize-label';
    label.textContent = s.name; // textContent — names come from the document
    // Load a script into the editor for editing.
    li.addEventListener('click', (e) => {
      if (e.target.closest('button')) return;
      $('js-name').value = s.name;
      $('js-source').value = s.script;
      for (const el of list.querySelectorAll('.organize-item')) el.classList.remove('active');
      li.classList.add('active');
    });
    const del = actionBtn('🗑', 'Remove script', false, () => removeScript(s.name));
    del.classList.add('organize-del');
    li.append(label, del);
    list.appendChild(li);
  }
}

async function addScript() {
  const name = $('js-name').value.trim();
  const script = $('js-source').value;
  if (!name) { toast('Give the script a name.'); return; }
  if (!script.trim()) { toast('The script is empty.'); return; }
  try {
    setStatus('Adding script…', true);
    const result = await host.call('add-script', {
      pdf: state.pdfB64, name, script, pdfPassword: state.password,
    });
    state.keepActiveContent = true; // the user added this on purpose — keep it on save
    await applyResult(result.pdf, `Script “${name}” added.`);
    $('js-name').value = '';
    $('js-source').value = '';
    await refreshScripts(); // update the list, leaving the "added" status visible
  } catch (e) {
    fail(e);
  }
}

async function removeScript(name) {
  try {
    setStatus('Removing script…', true);
    const result = await host.call('remove-script', {
      pdf: state.pdfB64, name, pdfPassword: state.password,
    });
    await applyResult(result.pdf, `Script “${name}” removed.`);
    await refreshScripts();
  } catch (e) {
    fail(e);
  }
}

/** Lets Tab indent inside a code editor instead of moving focus out of it. */
function enableCodeEditorTab(textarea) {
  textarea.addEventListener('keydown', (e) => {
    if (e.key !== 'Tab') return;
    e.preventDefault();
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    textarea.value = textarea.value.slice(0, start) + '  ' + textarea.value.slice(end);
    textarea.selectionStart = textarea.selectionEnd = start + 2;
  });
}

// ---------------------------------------------------------------- links / URLs

async function openLinks() {
  try {
    setStatus('Finding links…', true);
    const result = await host.call('list-urls', { pdf: state.pdfB64, pdfPassword: state.password });
    setStatus('');
    state.links = result.links ?? [];
    $('links-enable').checked = state.keepLinks;
    showPanel('panel-links');
    if (state.keepLinks && state.urlVerdicts.length === 0) await scanLinks();
    else renderLinks();
  } catch (e) {
    fail(e);
  }
}

function renderLinks() {
  const list = $('links-list');
  list.innerHTML = '';
  const has = state.links.length > 0;
  $('links-empty').hidden = has;
  $('links-enable-row').hidden = !has;
  $('links-hint').hidden = !has || state.keepLinks;
  $('links-rescan').hidden = !has || !state.keepLinks;

  const verdictFor = (l) => state.urlVerdicts.find((v) => v.url === l.url && v.page === l.page);
  for (const link of state.links) {
    const li = document.createElement('li');
    if (!state.keepLinks) li.className = 'link-disabled';
    const row = document.createElement('div');
    row.className = 'link-row';
    const verdict = state.keepLinks ? verdictFor(link) : null;

    const dot = document.createElement('span');
    dot.className = `link-dot ${verdict ? verdict.level : 'unknown'}`;
    const body = document.createElement('div');
    body.className = 'link-url';
    // Show the URL as inert text unless links are enabled; never auto-navigate.
    if (state.keepLinks) {
      const a = document.createElement('a');
      a.href = link.url;
      a.target = '_blank';
      a.rel = 'noreferrer nofollow';
      a.textContent = link.url;
      body.appendChild(a);
    } else {
      body.textContent = link.url;
    }
    const meta = document.createElement('div');
    meta.className = 'link-meta';
    meta.textContent = `page ${link.page}` +
      (verdict ? ` · ${verdict.level.toUpperCase()} · ${verdict.category}` +
        (verdict.source === 'cloudflare' ? ' (Cloudflare)' : '') : '');
    body.appendChild(meta);
    if (verdict?.detail) { body.title = verdict.detail; }
    row.append(dot, body);
    li.appendChild(row);
    list.appendChild(li);
  }
}

async function scanLinks() {
  if (state.links.length === 0) { renderLinks(); return; }
  const creds = await chrome.storage.local.get({ cfAccountId: '', cfApiToken: '' });
  const usingCf = !!(creds.cfAccountId && creds.cfApiToken);
  try {
    setStatus(usingCf ? 'Scanning links with Cloudflare…' : 'Rating links…', true);
    const result = await host.call('scan-urls', {
      pdf: state.pdfB64, pdfPassword: state.password,
      cfAccountId: creds.cfAccountId, cfApiToken: creds.cfApiToken,
    });
    state.urlVerdicts = result.verdicts ?? [];
    setStatus('');
    renderLinks();
    if (!usingCf) toast('Rated links offline. Add a Cloudflare token in Options for live scanning.');
  } catch (e) {
    fail(e);
    renderLinks();
  }
}

async function toggleLinks() {
  state.keepLinks = $('links-enable').checked;
  if (state.keepLinks && state.urlVerdicts.length === 0) await scanLinks();
  else renderLinks();
  updateChrome();
}

/**
 * Hands the current document to the browser's own print flow so its print dialog — including the
 * "Save as PDF" destination — prints the real, vector PDF. The PDF is loaded into an off-screen
 * iframe whose print is triggered directly; if the browser won't print the embedded PDF
 * programmatically (or never loads it), it's opened in a new tab so the user can print from the
 * browser's built-in PDF viewer.
 */
function printDocument() {
  if (!state.pdf) return;
  const url = URL.createObjectURL(new Blob([state.pdf], { type: 'application/pdf' }));
  let handled = false;

  const frame = document.createElement('iframe');
  frame.setAttribute('aria-hidden', 'true');
  frame.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0;';

  const cleanup = () => setTimeout(() => { URL.revokeObjectURL(url); frame.remove(); }, 60000);
  const openInTab = () => {
    if (handled) return;
    handled = true;
    window.open(url, '_blank', 'noopener');
    toast('Opened the document in a new tab — use your browser to print or save as PDF.');
    cleanup();
  };

  frame.addEventListener('load', () => {
    if (handled) return;
    handled = true;
    try {
      frame.contentWindow.focus();
      frame.contentWindow.print();
      toast('Opening the browser print dialog…');
      cleanup();
    } catch {
      handled = false; // let the tab fallback take over
      openInTab();
    }
  });
  // If the embedded PDF never loads (some browsers block plugin printing), fall back to a tab.
  setTimeout(openInTab, 3000);

  frame.src = url;
  document.body.appendChild(frame);
}

async function save() {
  let bytes = state.pdf;
  const stripJs = state.safety?.javaScriptCount > 0 && !state.keepActiveContent;
  // URL scanning is off for now: leave link URLs untouched on save.
  const stripUrls = URL_SCANNING_ENABLED && state.safety?.urlCount > 0 && !state.keepLinks;
  // Strip embedded JavaScript and/or link URLs unless the user chose to keep them.
  if (stripJs || stripUrls) {
    try {
      setStatus('Removing active content…', true);
      const stripped = await host.call('strip-active', {
        pdf: state.pdfB64, javaScript: stripJs, urls: stripUrls, pdfPassword: state.password,
      });
      bytes = base64ToBytes(stripped.pdf);
      setStatus('');
    } catch (e) {
      fail(e);
      return;
    }
  }
  const suggested = state.fileName.replace(/\.pdf$/i, '') + '-edited.pdf';
  const blob = new Blob([bytes], { type: 'application/pdf' });
  try {
    if (window.showSaveFilePicker) {
      const handle = await window.showSaveFilePicker({
        suggestedName: suggested,
        types: [{ description: 'PDF document', accept: { 'application/pdf': ['.pdf'] } }],
      });
      const writable = await handle.createWritable();
      await writable.write(blob);
      await writable.close();
      toast(`Saved ${handle.name}.`);
      return;
    }
  } catch (e) {
    if (e.name === 'AbortError') return;
    // fall through to the downloads API
  }
  chrome.downloads.download({
    url: URL.createObjectURL(blob),
    filename: suggested,
    saveAs: true,
  });
  toast('Saving via downloads…');
}

/** Snapshot of the current working document, for the undo/redo stacks. */
function snapshot() {
  return { pdf: state.pdf, pdfB64: state.pdfB64, info: state.info, password: state.password };
}

/** Restores a previously captured snapshot and re-renders. */
async function restore(snap, message) {
  setWorkingPdf(snap.pdf, snap.pdfB64);
  state.info = snap.info;
  state.password = snap.password;
  state.page = Math.min(state.page, state.info.pageCount);
  state.regions = state.regions.filter((r) => r.page <= state.info.pageCount);
  state.signatures = [];
  state.safety = null;
  await showDocument();
  updateChrome();
  Promise.all([refreshSignatures(), refreshSafety()]).then(updateChrome);
  toast(message);
}

function undo() {
  const previous = state.history.pop();
  if (!previous) return;
  state.future.push(snapshot());       // remember where we were so Redo can return
  restore(previous, 'Undid last change.');
}

function redo() {
  const next = state.future.pop();
  if (!next) return;
  state.history.push(snapshot());
  if (state.history.length > 10) state.history.shift();
  restore(next, 'Redid change.');
}

// ------------------------------------------------------------------ wiring

function wire() {
  $('btn-open').addEventListener('click', openFilePicker);
  $('btn-open-empty').addEventListener('click', openFilePicker);
  $('btn-save').addEventListener('click', save);
  $('btn-print').addEventListener('click', printDocument);
  $('btn-undo').addEventListener('click', undo);
  $('btn-redo').addEventListener('click', redo);
  $('btn-sidebar').addEventListener('click', () => toggleSidebar());

  $('btn-rotate-left').addEventListener('click', () => rotateCurrentPage(-90));
  $('btn-rotate-right').addEventListener('click', () => rotateCurrentPage(90));

  $('page-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); jumpToTypedPage(); }
  });
  $('page-input').addEventListener('blur', () => {
    $('page-input').value = String(state.page); // discard an unsubmitted edit
  });

  $('tool-select').addEventListener('click', () => setTool('select'));
  $('tool-text').addEventListener('click', () => setTool('text'));
  $('tool-draw').addEventListener('click', () => setTool('draw'));
  $('tool-highlight').addEventListener('click', () => setTool('highlight'));
  $('tool-edit').addEventListener('click', () => setTool('edit'));
  $('tool-redact').addEventListener('click', () => setTool('redact'));
  $('tool-sign').addEventListener('click', () => setTool('sign'));

  $('highlight-color').addEventListener('input', () => { state.highlightColor = $('highlight-color').value; });
  $('highlight-done').addEventListener('click', () => setTool('select'));

  $('draw-color').addEventListener('input', () => { state.drawColor = $('draw-color').value; redrawInk(); });
  $('draw-width').addEventListener('input', () => {
    state.drawWidth = parseFloat($('draw-width').value) || 2.5; redrawInk();
  });
  $('draw-apply').addEventListener('click', applyDrawing);
  $('draw-clear').addEventListener('click', clearDrawing);
  $('draw-cancel').addEventListener('click', () => setTool('select'));

  $('btn-forms').addEventListener('click', openForms);
  $('forms-apply').addEventListener('click', applyForms);
  $('forms-cancel').addEventListener('click', () => hidePanels());
  $('field-place').addEventListener('click', beginPlaceField);
  $('field-type').addEventListener('change', updateFieldTypeRows);
  enableCodeEditorTab($('field-script'));

  $('btn-organize').addEventListener('click', openOrganize);
  $('organize-apply').addEventListener('click', applyOrganize);
  $('organize-reset').addEventListener('click', openOrganize); // rebuild the original order
  $('organize-cancel').addEventListener('click', () => hidePanels());

  $('btn-js').addEventListener('click', openJavaScript);
  $('js-add').addEventListener('click', addScript);
  $('js-clear').addEventListener('click', () => {
    $('js-name').value = ''; $('js-source').value = '';
    for (const el of $('js-list').querySelectorAll('.organize-item')) el.classList.remove('active');
  });
  $('js-close').addEventListener('click', () => hidePanels());
  enableCodeEditorTab($('js-source'));

  $('btn-links').hidden = !URL_SCANNING_ENABLED; // URL scanning disabled for now
  $('btn-links').addEventListener('click', openLinks);
  $('links-enable').addEventListener('change', toggleLinks);
  $('links-rescan').addEventListener('click', scanLinks);
  $('links-close').addEventListener('click', () => hidePanels());

  $('btn-find').addEventListener('click', findReplace);
  $('btn-merge').addEventListener('click', mergeFiles);
  $('btn-protect').addEventListener('click', protect);
  $('btn-digital').addEventListener('click', digitallySign);

  $('btn-prev').addEventListener('click', () => goToPage(state.page - 1));
  $('btn-next').addEventListener('click', () => goToPage(state.page + 1));
  $('btn-zoom-in').addEventListener('click', () => setZoom(state.zoom + 0.25));
  $('btn-zoom-out').addEventListener('click', () => setZoom(state.zoom - 0.25));

  $('redact-preview').addEventListener('click', previewRedaction);
  $('redact-apply').addEventListener('click', () => applyRedaction());
  $('redact-clear').addEventListener('click', () => { state.regions = []; drawRegions(); });
  $('redact-search-btn').addEventListener('click', searchAndMarkRedactions);
  $('redact-search-text').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); searchAndMarkRedactions(); }
  });

  $('edit-apply').addEventListener('click', applyTextEdit);
  $('edit-cancel').addEventListener('click', () => { hidePanels(); setTool('select'); });
  $('edit-bold').addEventListener('click', () => $('edit-bold').classList.toggle('active'));
  $('edit-italic').addEventListener('click', () => $('edit-italic').classList.toggle('active'));

  $('sign-tab-draw').addEventListener('click', () => {
    $('sign-tab-draw').classList.add('active');
    $('sign-tab-upload').classList.remove('active');
    $('sign-draw').hidden = false;
    $('sign-upload').hidden = true;
  });
  $('sign-tab-upload').addEventListener('click', () => {
    $('sign-tab-upload').classList.add('active');
    $('sign-tab-draw').classList.remove('active');
    $('sign-draw').hidden = true;
    $('sign-upload').hidden = false;
  });
  $('sign-apply').addEventListener('click', applyImageSignature);
  $('sign-cancel').addEventListener('click', () => { hidePanels(); setTool('select'); });

  initMenus();
  initSignaturePad();
  window.addEventListener('resize', drawRegions);
}

/** Wires the Reading/Editing dropdown menus: click the trigger to toggle, click away to close. */
function initMenus() {
  for (const menu of document.querySelectorAll('.menu-group')) {
    const trigger = menu.querySelector('.menu-trigger');
    trigger.addEventListener('click', (e) => {
      e.stopPropagation();
      const open = menu.classList.contains('open');
      closeAllMenus();
      if (!open) { menu.classList.add('open'); trigger.setAttribute('aria-expanded', 'true'); }
    });
    // Choosing an item runs its own handler and then closes the menu.
    for (const item of menu.querySelectorAll('.menu-item')) {
      item.addEventListener('click', () => closeAllMenus());
    }
  }
  document.addEventListener('click', closeAllMenus);
}

function closeAllMenus() {
  for (const menu of document.querySelectorAll('.menu-group.open')) {
    menu.classList.remove('open');
    menu.querySelector('.menu-trigger')?.setAttribute('aria-expanded', 'false');
  }
}

async function start() {
  wire();
  try {
    await host.call('ping');
    $('host-status').textContent = '✓ Native host connected.';
  } catch (e) {
    const statusEl = $('host-status');
    statusEl.textContent = `⚠ ${e.message}`;
    statusEl.appendChild(document.createElement('br'));
    statusEl.appendChild(document.createTextNode('Open the extension options for install instructions.'));
  }
  const src = new URLSearchParams(location.search).get('src');
  if (src) await openFromUrl(src);
}

start();
