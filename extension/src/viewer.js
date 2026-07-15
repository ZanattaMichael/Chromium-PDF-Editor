// PDF Editor viewer page. Talks straight to the native host; the working
// document lives here as bytes and every edit round-trips through the host.

import { HostClient, bytesToBase64, base64ToBytes } from './host-client.js';

const host = new HostClient();

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
  tool: 'select',
  regions: [],          // pending redaction regions {page,x,y,width,height} (PDF space)
  pendingEditRegion: null,
  pendingSignRegion: null,
  signatures: [],
};

// Rendered pages are cached in memory so navigating back and forth is instant instead of
// re-rendering (and re-uploading the whole document) every time. Entries are keyed by the
// document version, so any edit invalidates the whole cache automatically.
const renderCache = new Map(); // `${version}|${page}|${dpi}` -> png base64
const MAX_CACHED_PAGES = 24;
let prefetchToken = 0;

/** Installs new working bytes: encode once, bump the version, drop now-stale renders. */
function setWorkingPdf(bytes, base64) {
  state.pdf = bytes;
  state.pdfB64 = base64 ?? bytesToBase64(bytes);
  state.version++;
  renderCache.clear();
  prefetchToken++; // cancel any in-flight prefetch for the previous document
}

const $ = (id) => document.getElementById(id);
const pageImage = $('page-image');
const overlay = $('overlay');
const modal = $('modal');

// ------------------------------------------------------------------ utils

function setStatus(text, busy = false) {
  $('status').innerHTML = busy ? `<span class="spinner"></span>${text}` : text;
}

function toast(text) {
  setStatus(text);
  setTimeout(() => { if ($('status').textContent === text) setStatus(''); }, 5000);
}

function fail(err) {
  console.error(err);
  setStatus(`⚠ ${err.message ?? err}`);
}

function pageSize() {
  return state.info.pages[state.page - 1];
}

// The rendered image spans the page box [x, x+width] × [y, y+height] in PDF user space.
// x/y are the box's lower-left origin and are non-zero for PDFs whose MediaBox/CropBox
// doesn't start at (0,0); the image's bottom-left corresponds to (x, y), not (0, 0), so
// every screen↔document mapping must include that offset or redactions land shifted.

/** CSS pixel (relative to the page image) → PDF user-space point. */
function cssToPdf(cssX, cssY) {
  const p = pageSize();
  const scale = pageImage.clientWidth / p.width;
  return { x: p.x + cssX / scale, y: p.y + p.height - cssY / scale };
}

function pdfRectToCss(region) {
  const p = pageSize();
  const scale = pageImage.clientWidth / p.width;
  return {
    left: (region.x - p.x) * scale,
    top: (p.y + p.height - region.y - region.height) * scale,
    width: region.width * scale,
    height: region.height * scale,
  };
}

// ------------------------------------------------------------ doc lifecycle

async function loadDocument(bytes, fileName, { pushHistory = false, password } = {}) {
  if (pushHistory && state.pdf) {
    state.history.push({ pdf: state.pdf, pdfB64: state.pdfB64, info: state.info, password: state.password });
    if (state.history.length > 10) state.history.shift();
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
  await refreshSignatures();
  await renderPage();
  updateChrome();
}

async function applyResult(base64Pdf, message) {
  await loadDocument(base64ToBytes(base64Pdf), null, { pushHistory: true });
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

/** Fills the cache for pages near `centerPage`, nearest first, in the background. */
function prefetchAround(centerPage, dpi) {
  const token = ++prefetchToken;
  const total = state.info?.pageCount ?? 0;
  const order = [];
  for (let d = 1; d <= total && order.length < MAX_CACHED_PAGES - 1; d++) {
    if (centerPage - d >= 1) order.push(centerPage - d);
    if (centerPage + d <= total) order.push(centerPage + d);
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
}

function showPage(png) {
  pageImage.src = `data:image/png;base64,${png}`;
  pageImage.style.width = `${pageSize().width * state.zoom * (96 / 72)}px`;
  $('page-wrap').classList.add('loaded');
  $('empty-state').style.display = 'none';
  drawRegions();
}

async function renderPage() {
  const page = state.page;
  const dpi = currentDpi();
  const cached = renderCache.get(cacheKey(page, dpi));
  if (cached !== undefined) {
    showPage(cached); // instant — no host round-trip, no spinner
  } else {
    setStatus(`Rendering page ${page}…`, true);
    let png;
    try {
      png = await renderToCache(page, dpi);
    } catch (e) {
      setStatus('');
      throw e;
    }
    // If the user moved on while we awaited, let the newer render win.
    if (state.page !== page || currentDpi() !== dpi) return;
    showPage(png);
    setStatus('');
  }
  prefetchAround(page, dpi);
}

function updateChrome() {
  const loaded = !!state.pdf;
  for (const id of ['btn-save', 'tool-edit', 'tool-redact', 'tool-sign',
    'btn-find', 'btn-merge', 'btn-protect', 'btn-digital',
    'btn-prev', 'btn-next', 'btn-zoom-in', 'btn-zoom-out']) {
    $(id).disabled = !loaded;
  }
  $('btn-undo').disabled = state.history.length === 0;
  if (loaded) {
    $('page-label').textContent = `${state.page} / ${state.info.pageCount}`;
    $('btn-prev').disabled = state.page <= 1;
    $('btn-next').disabled = state.page >= state.info.pageCount;
    $('zoom-label').textContent = `${Math.round(state.zoom * 100)}%`;
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
  overlay.querySelectorAll('.region').forEach((el) => el.remove());
  for (const [index, region] of state.regions.entries()) {
    if (region.page !== state.page) continue;
    addRegionDiv(region, 'redact', `#${index + 1}`);
  }
  if (state.pendingEditRegion?.page === state.page) addRegionDiv(state.pendingEditRegion, 'edit');
  if (state.pendingSignRegion?.page === state.page) addRegionDiv(state.pendingSignRegion, 'sign');
  renderRedactList();
}

function addRegionDiv(region, kind, label = '') {
  const css = pdfRectToCss(region);
  const div = document.createElement('div');
  div.className = `region ${kind}`;
  div.style.cssText =
    `left:${css.left}px;top:${css.top}px;width:${css.width}px;height:${css.height}px;`;
  div.textContent = label;
  overlay.appendChild(div);
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

// Drag-to-draw on the overlay for edit/redact/sign tools.
let drag = null;

overlay.addEventListener('pointerdown', (e) => {
  if (state.tool === 'select' || !state.pdf) return;
  const rect = overlay.getBoundingClientRect();
  drag = { x0: e.clientX - rect.left, y0: e.clientY - rect.top, div: null };
  overlay.setPointerCapture(e.pointerId);
});

overlay.addEventListener('pointermove', (e) => {
  if (!drag) return;
  const rect = overlay.getBoundingClientRect();
  const x1 = e.clientX - rect.left;
  const y1 = e.clientY - rect.top;
  if (!drag.div) {
    drag.div = document.createElement('div');
    drag.div.className = `region ${state.tool === 'redact' ? '' : state.tool === 'edit' ? 'edit' : 'sign'}`;
    overlay.appendChild(drag.div);
  }
  const left = Math.min(drag.x0, x1);
  const top = Math.min(drag.y0, y1);
  drag.div.style.cssText =
    `left:${left}px;top:${top}px;width:${Math.abs(x1 - drag.x0)}px;height:${Math.abs(y1 - drag.y0)}px;`;
});

overlay.addEventListener('pointerup', async (e) => {
  if (!drag) return;
  const div = drag.div;
  const rect = overlay.getBoundingClientRect();
  const x1 = e.clientX - rect.left;
  const y1 = e.clientY - rect.top;
  const { x0, y0 } = drag;
  drag = null;
  if (div) div.remove();
  if (Math.abs(x1 - x0) < 4 || Math.abs(y1 - y0) < 4) return;

  const a = cssToPdf(Math.min(x0, x1), Math.max(y0, y1)); // bottom-left
  const b = cssToPdf(Math.max(x0, x1), Math.min(y0, y1)); // top-right
  const region = {
    page: state.page,
    x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y,
  };

  if (state.tool === 'redact') {
    state.regions.push(region);
    drawRegions();
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
  state.tool = tool;
  for (const button of document.querySelectorAll('.tool')) button.classList.remove('active');
  $(`tool-${tool}`).classList.add('active');
  overlay.classList.toggle('tool-active', tool !== 'select');
  if (tool === 'redact') showPanel('panel-redact');
  else if (tool === 'select') hidePanels();
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

async function beginTextEdit(region) {
  try {
    setStatus('Reading text in region…', true);
    const found = await host.call('get-region-text', {
      pdf: state.pdfB64, region, pdfPassword: state.password,
    });
    setStatus('');
    $('edit-text').value = found.text;
    $('edit-size').value = Number(found.fontSize).toFixed(1);
    showPanel('panel-edit');
    $('edit-text').focus();
  } catch (e) {
    fail(e);
  }
}

async function applyTextEdit() {
  const region = state.pendingEditRegion;
  if (!region) return;
  try {
    setStatus('Replacing text…', true);
    const result = await host.call('replace-region-text', {
      pdf: state.pdfB64,
      region,
      text: $('edit-text').value,
      fontSize: parseFloat($('edit-size').value) || undefined,
      pdfPassword: state.password,
    });
    hidePanels();
    await applyResult(result.pdf, 'Text replaced.');
  } catch (e) {
    fail(e);
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

async function mergeFiles() {
  $('merge-input').onchange = async () => {
    const files = [...$('merge-input').files];
    $('merge-input').value = '';
    if (files.length === 0) return;
    try {
      setStatus(`Merging ${files.length} file${files.length === 1 ? '' : 's'}…`, true);
      const pdfs = [state.pdfB64];
      for (const file of files) {
        pdfs.push(bytesToBase64(new Uint8Array(await file.arrayBuffer())));
      }
      const result = await host.call('merge', { pdfs });
      await applyResult(result.pdf, `Merged ${files.length} document${files.length === 1 ? '' : 's'} in.`);
    } catch (e) {
      fail(e);
    }
  };
  $('merge-input').click();
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
    state.password = null;
    state.regions = [];
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

async function save() {
  const suggested = state.fileName.replace(/\.pdf$/i, '') + '-edited.pdf';
  const blob = new Blob([state.pdf], { type: 'application/pdf' });
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

function undo() {
  const previous = state.history.pop();
  if (!previous) return;
  setWorkingPdf(previous.pdf, previous.pdfB64);
  state.info = previous.info;
  state.password = previous.password;
  state.page = Math.min(state.page, state.info.pageCount);
  refreshSignatures().then(() => {
    renderPage();
    updateChrome();
    toast('Undid last change.');
  });
}

// ------------------------------------------------------------------ wiring

function wire() {
  $('btn-open').addEventListener('click', openFilePicker);
  $('btn-open-empty').addEventListener('click', openFilePicker);
  $('btn-save').addEventListener('click', save);
  $('btn-undo').addEventListener('click', undo);

  $('tool-select').addEventListener('click', () => setTool('select'));
  $('tool-edit').addEventListener('click', () => setTool('edit'));
  $('tool-redact').addEventListener('click', () => setTool('redact'));
  $('tool-sign').addEventListener('click', () => setTool('sign'));

  $('btn-find').addEventListener('click', findReplace);
  $('btn-merge').addEventListener('click', mergeFiles);
  $('btn-protect').addEventListener('click', protect);
  $('btn-digital').addEventListener('click', digitallySign);

  $('btn-prev').addEventListener('click', () => { state.page--; renderPage(); updateChrome(); });
  $('btn-next').addEventListener('click', () => { state.page++; renderPage(); updateChrome(); });
  $('btn-zoom-in').addEventListener('click', () => { state.zoom = Math.min(3, state.zoom + 0.25); renderPage(); updateChrome(); });
  $('btn-zoom-out').addEventListener('click', () => { state.zoom = Math.max(0.5, state.zoom - 0.25); renderPage(); updateChrome(); });

  $('redact-preview').addEventListener('click', previewRedaction);
  $('redact-apply').addEventListener('click', () => applyRedaction());
  $('redact-clear').addEventListener('click', () => { state.regions = []; drawRegions(); });

  $('edit-apply').addEventListener('click', applyTextEdit);
  $('edit-cancel').addEventListener('click', () => { hidePanels(); setTool('select'); });

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

  initSignaturePad();
  window.addEventListener('resize', drawRegions);
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
