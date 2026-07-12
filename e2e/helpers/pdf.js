'use strict';

/**
 * Builds a minimal, uncompressed, single- or multi-page PDF fixture with
 * absolutely positioned Helvetica text. Deterministic and dependency-free —
 * page size is A4 (595 x 842 pt), matching the .NET test fixtures.
 */
function buildPdf(pages) {
  const objects = [];
  const pageObjectNumbers = pages.map((_, i) => 4 + i * 2);

  objects.push('<< /Type /Catalog /Pages 2 0 R >>'); // 1
  objects.push(`<< /Type /Pages /Kids [${pageObjectNumbers.map((n) => `${n} 0 R`).join(' ')}] /Count ${pages.length} >>`); // 2
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'); // 3

  for (const lines of pages) {
    const content = lines
      .map(({ text, x, y, size = 14 }) =>
        `BT /F1 ${size} Tf ${x} ${y} Td (${text.replace(/([\\()])/g, '\\$1')}) Tj ET`)
      .join('\n');
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] ` +
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

module.exports = { buildPdf };
