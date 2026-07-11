// Content script: extends existing browser-based PDF viewing with an
// "Edit in PDF Editor" entry point. It covers two cases:
//   1. The tab itself is a PDF rendered by the browser's built-in viewer.
//   2. The page embeds PDFs via <embed>, <object>, or <iframe>.
// Adobe properties are left alone by the background worker's checks; this
// script additionally never injects on adobe.com pages.

(() => {
  if (/(^|\.)adobe\.com$/.test(location.hostname)) return;

  const BUTTON_ID = '__pdf_editor_overlay_button__';

  function makeButton(pdfUrl, fixed) {
    if (document.getElementById(BUTTON_ID)) return null;
    const button = document.createElement('button');
    button.id = BUTTON_ID;
    button.textContent = '✏ Edit in PDF Editor';
    button.style.cssText = [
      'position:' + (fixed ? 'fixed' : 'absolute'),
      'z-index:2147483647',
      'top:12px',
      'right:12px',
      'padding:8px 14px',
      'background:#b3261e',
      'color:#fff',
      'border:none',
      'border-radius:20px',
      'font:600 13px system-ui,sans-serif',
      'cursor:pointer',
      'box-shadow:0 2px 8px rgba(0,0,0,.35)',
    ].join(';');
    button.addEventListener('click', (e) => {
      e.stopPropagation();
      chrome.runtime.sendMessage({ type: 'open-in-editor', url: pdfUrl });
    });
    return button;
  }

  // Case 1: the document itself is a PDF (built-in viewer).
  if (document.contentType === 'application/pdf') {
    const button = makeButton(location.href, true);
    if (button) (document.body || document.documentElement).appendChild(button);
    return;
  }

  // Case 2: embedded PDF viewers inside a normal page.
  function isPdfEmbed(el) {
    const type = (el.getAttribute('type') || '').toLowerCase();
    const src = el.getAttribute('src') || el.getAttribute('data') || '';
    return type === 'application/pdf' || /\.pdf($|[?#])/i.test(src);
  }

  function decorate(el) {
    if (!isPdfEmbed(el) || el.dataset.pdfEditorDecorated) return;
    el.dataset.pdfEditorDecorated = '1';
    const src = el.getAttribute('src') || el.getAttribute('data');
    if (!src) return;
    const absolute = new URL(src, location.href).href;

    const wrapper = document.createElement('div');
    wrapper.style.cssText = 'position:relative;display:inline-block;width:100%;';
    el.parentNode.insertBefore(wrapper, el);
    wrapper.appendChild(el);
    const button = makeButton(absolute, false);
    if (button) {
      button.id = ''; // allow one per embed
      wrapper.appendChild(button);
    }
  }

  function scan() {
    document.querySelectorAll('embed, object, iframe').forEach(decorate);
  }

  scan();
  new MutationObserver(scan).observe(document.documentElement, {
    childList: true,
    subtree: true,
  });
})();
