'use strict';

/**
 * Builds a minimal, uncompressed, single- or multi-page PDF fixture with
 * absolutely positioned Helvetica text. Deterministic and dependency-free —
 * default page size is A4 (595 x 842 pt), matching the .NET test fixtures.
 *
 * Pass `{ mediaBox: [llx, lly, urx, ury] }` to give the pages a non-(0,0) origin, and/or
 * `{ rotate: 90|180|270 }` to rotate them — both regression-test the coordinate mapping
 * used for redaction.
 */
function buildPdf(pages, { mediaBox = [0, 0, 595, 842], cropBox = null, rotate = 0 } = {}) {
  const objects = [];
  const pageObjectNumbers = pages.map((_, i) => 4 + i * 2);
  const box = mediaBox.join(' ');
  const rotateEntry = rotate ? ` /Rotate ${rotate}` : '';
  const cropEntry = cropBox ? ` /CropBox [${cropBox.join(' ')}]` : '';

  objects.push('<< /Type /Catalog /Pages 2 0 R >>'); // 1
  objects.push(`<< /Type /Pages /Kids [${pageObjectNumbers.map((n) => `${n} 0 R`).join(' ')}] /Count ${pages.length} >>`); // 2
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'); // 3

  for (const lines of pages) {
    const content = lines
      .map(({ text, x, y, size = 14 }) =>
        `BT /F1 ${size} Tf ${x} ${y} Td (${text.replace(/([\\()])/g, '\\$1')}) Tj ET`)
      .join('\n');
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [${box}]${cropEntry}${rotateEntry} ` +
      `/Resources << /Font << /F1 3 0 R >> >> /Contents ${4 + objects.length - 2} 0 R >>`);
    objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
  }

  let body = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((obj, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${obj}\nendobj\n`;
  });
  const xrefStart = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (let i = 1; i <= objects.length; i++) {
    body += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  }
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefStart}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

/**
 * Builds a page that mimics how Chrome / Skia print-to-PDF (Google Docs "Download as PDF")
 * structures content: a top-level scale + Y-flip matrix applied *outside* any q/Q, so it is never
 * restored and is still active at the end of the content stream. The text is drawn under that
 * matrix (via a text matrix) so it renders upright at a normal absolute position — but anything
 * naively appended to the page inherits the leftover matrix. Regression fixture for redaction/edit
 * landing in the wrong place on such documents. MediaBox is [0 0 400 600]; the word renders around
 * absolute (50, 300).
 */
function buildLeftoverCtmPdf(word = 'SECRET') {
  const content =
    '0.5 0 0 -0.5 0 600 cm\n' +   // unbalanced top-level transform (never restored)
    'q\n' +
    '0 0 800 1200 re W n\n' +     // clip to the page in the scaled space
    `BT\n/F1 48 Tf\n1 0 0 -1 100 600 Tm\n(${word.replace(/([\\()])/g, '\\$1')}) Tj\nET\n` +
    'Q\n';

  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] ' +
      '/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',
    `<< /Length ${content.length} >>\nstream\n${content}endstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];

  let body = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((obj, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${obj}\nendobj\n`;
  });
  const xrefStart = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (let i = 1; i <= objects.length; i++) {
    body += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  }
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefStart}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

module.exports = { buildPdf, buildLeftoverCtmPdf };
