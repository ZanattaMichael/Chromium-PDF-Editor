#!/usr/bin/env node
'use strict';

/**
 * Generates a realistic, self-contained sample PDF for documentation and screenshots.
 *
 * The document is deliberately "ordinary business report" shaped — a titled cover page with
 * wrapped body copy, a bar chart, a pie chart and an embedded raster logo, then a second page
 * with a gridded data table and a callout box. That mix (flowing text, vector graphics and a
 * real image) is what the editor's features act on, so a single file exercises redaction,
 * highlighting, inline text editing, move, forms, "remove hidden information" and the rest.
 *
 * It is hand-rolled — no PDF library — so it stays dependency-free and deterministic. Text is
 * laid out with the real Helvetica / Helvetica-Bold AFM advance widths so wrapping never
 * overflows the margins.
 *
 * Usage:  node scripts/generate-sample-pdf.mjs [output.pdf]
 * Default output: docs/sample/PDF-Editor-Sample.pdf
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, '..');

// ---------------------------------------------------------------- Helvetica metrics
// Advance widths (per 1000 em) for ASCII 32..126, from the standard Adobe AFM files.
const HELV = [278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584];
const HELV_BOLD = [278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584];

function charWidth(ch, bold) {
  const code = ch.charCodeAt(0);
  if (code < 32 || code > 126) return (bold ? HELV_BOLD : HELV)[65]; // fall back to 'A'
  return (bold ? HELV_BOLD : HELV)[code - 32];
}
/** Width of a string at the given font size, in points. */
function textWidth(str, size, bold = false) {
  let w = 0;
  for (const ch of str) w += charWidth(ch, bold);
  return (w / 1000) * size;
}
/** Greedily wraps text to a maximum width, returning an array of lines. */
function wrap(text, size, maxWidth, bold = false) {
  const words = text.split(/\s+/).filter(Boolean);
  const lines = [];
  let line = '';
  for (const word of words) {
    const candidate = line ? `${line} ${word}` : word;
    if (textWidth(candidate, size, bold) > maxWidth && line) {
      lines.push(line);
      line = word;
    } else {
      line = candidate;
    }
  }
  if (line) lines.push(line);
  return lines;
}

// ---------------------------------------------------------------- content-stream builder
const esc = (s) => s.replace(/([\\()])/g, '\\$1');

/** Accumulates PDF content-stream operators with a few text/vector convenience helpers. */
class Canvas {
  constructor() { this.ops = []; }
  raw(op) { this.ops.push(op); return this; }
  fill([r, g, b]) { return this.raw(`${r} ${g} ${b} rg`); }
  stroke([r, g, b]) { return this.raw(`${r} ${g} ${b} RG`); }
  lineWidth(w) { return this.raw(`${w} w`); }
  rect(x, y, w, h, mode = 'f') { return this.raw(`${x} ${y} ${w} ${h} re ${mode}`); }
  line(x1, y1, x2, y2) { return this.raw(`${x1} ${y1} m ${x2} ${y2} l S`); }
  /** One line of text, absolutely positioned. font: 'F1' regular, 'F2' bold. */
  text(str, x, y, size, font = 'F1', color = [0, 0, 0]) {
    return this.raw(
      `${color[0]} ${color[1]} ${color[2]} rg BT /${font} ${size} Tf ${x} ${y} Td (${esc(str)}) Tj ET`);
  }
  /** A filled quarter-circle-approximating disc slice (for the pie chart), centred at (cx,cy). */
  slice(cx, cy, radius, startDeg, endDeg, color) {
    const step = 6; // degrees per segment
    this.fill(color).raw(`${cx} ${cy} m`);
    for (let a = startDeg; a <= endDeg + 0.001; a += step) {
      const rad = (a * Math.PI) / 180;
      const x = cx + radius * Math.cos(rad);
      const y = cy + radius * Math.sin(rad);
      this.raw(`${x.toFixed(2)} ${y.toFixed(2)} l`);
    }
    return this.raw('f');
  }
  toString() { return this.ops.join('\n'); }
}

// ---------------------------------------------------------------- an embedded raster "logo"
// A small procedurally-generated DeviceRGB image: a diagonal teal→indigo gradient with a lighter
// stripe, so it visibly reads as a picture (not a flat fill) in move/redaction screenshots.
function buildLogoImage() {
  const W = 96, H = 64;
  const bytes = Buffer.alloc(W * H * 3);
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const t = (x / W + y / H) / 2;
      const stripe = Math.abs(((x - y) % 32)) < 6 ? 40 : 0;
      const r = Math.min(255, Math.round(20 + 60 * t) + stripe);
      const g = Math.min(255, Math.round(150 - 40 * t) + stripe);
      const b = Math.min(255, Math.round(160 + 80 * t) + stripe);
      const i = (y * W + x) * 3;
      bytes[i] = r; bytes[i + 1] = g; bytes[i + 2] = b;
    }
  }
  return { width: W, height: H, data: bytes };
}

// ---------------------------------------------------------------- page geometry & palette
const PAGE_W = 595, PAGE_H = 842;         // A4
const MARGIN = 56;
const CONTENT_W = PAGE_W - 2 * MARGIN;
const NAVY = [0.12, 0.20, 0.38];
const TEAL = [0.13, 0.56, 0.55];
const AMBER = [0.90, 0.62, 0.15];
const CORAL = [0.85, 0.33, 0.31];
const SLATE = [0.35, 0.40, 0.48];
const LIGHT = [0.93, 0.95, 0.97];
const GRIDGREY = [0.80, 0.82, 0.85];
const INK = [0.13, 0.15, 0.18];

const BODY = [
  'Northwind Analytics compiles this quarterly review to give the leadership team a single, ' +
  'shareable snapshot of how the business is tracking against plan. The figures below are ' +
  'illustrative sample data, included so the document has the same shape as a real report: ' +
  'headings, flowing paragraphs, charts and a data table.',
  'Revenue grew for the fourth consecutive quarter, driven by strong renewals in the ' +
  'enterprise segment and the launch of the self-serve tier. Support costs held flat even as ' +
  'the customer base expanded, which pushed gross margin up by just over three points. The ' +
  'remainder of this document breaks those movements down by region and by product line.',
];

// ---------------------------------------------------------------- page 1: cover + charts
function drawPage1(logoName) {
  const c = new Canvas();

  // Header band with the report title.
  c.fill(NAVY).rect(0, PAGE_H - 96, PAGE_W, 96);
  c.text('Quarterly Business Review', MARGIN, PAGE_H - 56, 26, 'F2', [1, 1, 1]);
  c.text('Northwind Analytics  |  Fiscal Year 2026, Q3', MARGIN, PAGE_H - 78, 12, 'F1', [0.8, 0.86, 0.95]);

  // Embedded logo image, top-right of the header.
  c.raw('q').raw(`64 0 0 42 ${PAGE_W - MARGIN - 64} ${PAGE_H - 82} cm /${logoName} Do`).raw('Q');

  let y = PAGE_H - 132;

  // Intro section.
  c.text('Executive summary', MARGIN, y, 15, 'F2', NAVY);
  y -= 22;
  for (const para of BODY) {
    for (const ln of wrap(para, 11, CONTENT_W)) {
      c.text(ln, MARGIN, y, 11, 'F1', INK);
      y -= 16;
    }
    y -= 8;
  }

  // ---- Bar chart: Revenue by quarter ----
  y -= 6;
  c.text('Revenue by quarter ($M)', MARGIN, y, 13, 'F2', NAVY);
  y -= 14;
  const chartTop = y;
  const chartH = 130;
  const chartBottom = chartTop - chartH;
  const axisX = MARGIN + 24;
  const axisRight = MARGIN + CONTENT_W;
  // Axes.
  c.stroke(SLATE).lineWidth(1).line(axisX, chartTop, axisX, chartBottom).line(axisX, chartBottom, axisRight, chartBottom);
  const bars = [['Q4', 3.1], ['Q1', 3.6], ['Q2', 4.2], ['Q3', 5.0]];
  const maxVal = 6;
  const plotW = axisRight - axisX;
  const slot = plotW / bars.length;
  const barW = slot * 0.5;
  const colors = [SLATE, TEAL, AMBER, CORAL];
  // Gridlines + y labels.
  c.lineWidth(0.5).stroke(GRIDGREY);
  for (let v = 1; v <= maxVal; v++) {
    const gy = chartBottom + (v / maxVal) * chartH;
    c.line(axisX, gy, axisRight, gy);
    c.text(String(v), MARGIN, gy - 3, 8, 'F1', SLATE);
  }
  bars.forEach(([label, val], i) => {
    const bx = axisX + i * slot + (slot - barW) / 2;
    const bh = (val / maxVal) * chartH;
    c.fill(colors[i]).rect(bx, chartBottom, barW, bh);
    c.text(val.toFixed(1), bx + barW / 2 - textWidth(val.toFixed(1), 9) / 2, chartBottom + bh + 4, 9, 'F2', INK);
    c.text(label, bx + barW / 2 - textWidth(label, 9) / 2, chartBottom - 12, 9, 'F1', INK);
  });

  // ---- Pie chart: Market share ----
  const pieY = chartBottom - 56;
  c.text('Revenue mix by product line', MARGIN, pieY, 13, 'F2', NAVY);
  const cx = MARGIN + 78, cy = pieY - 78, radius = 62;
  const segments = [
    ['Platform', 46, TEAL],
    ['Add-ons', 27, AMBER],
    ['Services', 18, CORAL],
    ['Other', 9, SLATE],
  ];
  let angle = 90;
  for (const [, pct, color] of segments) {
    const sweep = (pct / 100) * 360;
    c.slice(cx, cy, radius, angle, angle + sweep, color);
    angle += sweep;
  }
  // Legend to the right of the pie.
  let ly = cy + 46;
  const lx = cx + radius + 40;
  for (const [label, pct, color] of segments) {
    c.fill(color).rect(lx, ly, 12, 12);
    c.text(`${label}  —  ${pct}%`, lx + 20, ly + 2, 10, 'F1', INK);
    ly -= 22;
  }

  // Footer.
  c.stroke(GRIDGREY).lineWidth(0.5).line(MARGIN, 48, PAGE_W - MARGIN, 48);
  c.text('Northwind Analytics — Confidential sample document', MARGIN, 34, 8, 'F1', SLATE);
  c.text('Page 1 of 2', PAGE_W - MARGIN - textWidth('Page 1 of 2', 8), 34, 8, 'F1', SLATE);
  return c.toString();
}

// ---------------------------------------------------------------- page 2: table + callout
function drawPage2() {
  const c = new Canvas();
  c.fill(NAVY).rect(0, PAGE_H - 72, PAGE_W, 72);
  c.text('Regional detail', MARGIN, PAGE_H - 46, 20, 'F2', [1, 1, 1]);

  let y = PAGE_H - 104;
  const intro = 'The table below reconciles reported revenue against plan for each region. ' +
    'Positive variance is shown where a region finished ahead of its target for the quarter.';
  for (const ln of wrap(intro, 11, CONTENT_W)) { c.text(ln, MARGIN, y, 11, 'F1', INK); y -= 16; }
  y -= 12;

  // ---- Data table ----
  const cols = [MARGIN, MARGIN + 200, MARGIN + 320, MARGIN + 430];
  const colRight = MARGIN + CONTENT_W;
  const rowH = 24;
  const header = ['Region', 'Actual', 'Plan', 'Variance'];
  const rows = [
    ['North America', '$2.4M', '$2.2M', '+9%'],
    ['Europe', '$1.3M', '$1.3M', '0%'],
    ['Asia Pacific', '$0.9M', '$0.7M', '+29%'],
    ['Latin America', '$0.4M', '$0.5M', '-20%'],
  ];
  const tableTop = y;
  // Header row shading.
  c.fill(NAVY).rect(MARGIN, y - rowH, CONTENT_W, rowH);
  header.forEach((h, i) => c.text(h, cols[i] + 8, y - 16, 11, 'F2', [1, 1, 1]));
  y -= rowH;
  // Body rows (zebra).
  rows.forEach((row, r) => {
    if (r % 2 === 1) c.fill(LIGHT).rect(MARGIN, y - rowH, CONTENT_W, rowH);
    row.forEach((cell, i) => {
      const color = i === 3 ? (cell.startsWith('+') ? TEAL : cell.startsWith('-') ? CORAL : SLATE) : INK;
      const font = i === 3 ? 'F2' : 'F1';
      c.text(cell, cols[i] + 8, y - 16, 11, font, color);
    });
    y -= rowH;
  });
  const tableBottom = y;
  // Grid.
  c.stroke(GRIDGREY).lineWidth(0.75);
  for (let r = 0; r <= rows.length + 1; r++) c.line(MARGIN, tableTop - r * rowH, colRight, tableTop - r * rowH);
  [...cols.slice(1), colRight].forEach((x) => c.line(x, tableTop, x, tableBottom));
  c.line(MARGIN, tableTop, MARGIN, tableBottom);

  // ---- Callout box ----
  y = tableBottom - 40;
  const boxH = 128;
  c.fill(LIGHT).rect(MARGIN, y - boxH, CONTENT_W, boxH);
  c.fill(TEAL).rect(MARGIN, y - boxH, 6, boxH); // accent bar
  c.text('Key highlights', MARGIN + 20, y - 24, 13, 'F2', NAVY);
  const bullets = [
    'Asia Pacific outperformed plan by 29%, the largest positive variance this quarter.',
    'Latin America finished behind plan; a recovery program starts next quarter.',
    'Enterprise renewals reached 94%, up from 89% a year ago.',
    'Self-serve signups contributed $0.3M of incremental platform revenue.',
  ];
  let by = y - 46;
  for (const b of bullets) {
    c.fill(TEAL).rect(MARGIN + 22, by + 3, 4, 4); // bullet
    for (const ln of wrap(b, 10.5, CONTENT_W - 60)) {
      c.text(ln, MARGIN + 34, by, 10.5, 'F1', INK);
      by -= 15;
    }
    by -= 3;
  }

  // Footer.
  c.stroke(GRIDGREY).lineWidth(0.5).line(MARGIN, 48, PAGE_W - MARGIN, 48);
  c.text('Northwind Analytics — Confidential sample document', MARGIN, 34, 8, 'F1', SLATE);
  c.text('Page 2 of 2', PAGE_W - MARGIN - textWidth('Page 2 of 2', 8), 34, 8, 'F1', SLATE);
  return c.toString();
}

// ---------------------------------------------------------------- PDF assembly
function assemble() {
  const logo = buildLogoImage();
  const page1 = drawPage1('Im0');
  const page2 = drawPage2();

  // Object layout:
  // 1 Catalog, 2 Pages, 3 Page1, 4 Page2, 5 Content1, 6 Content2,
  // 7 Helvetica, 8 Helvetica-Bold, 9 Image, 10 Info (document metadata)
  const objects = [];
  objects[1] = '<< /Type /Catalog /Pages 2 0 R >>';
  objects[2] = '<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>';
  const resources = '/Resources << /Font << /F1 7 0 R /F2 8 0 R >> /XObject << /Im0 9 0 R >> >>';
  objects[3] = `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${PAGE_W} ${PAGE_H}] ${resources} /Contents 5 0 R >>`;
  objects[4] = `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${PAGE_W} ${PAGE_H}] ${resources} /Contents 6 0 R >>`;
  objects[5] = `<< /Length ${Buffer.byteLength(page1, 'latin1')} >>\nstream\n${page1}\nendstream`;
  objects[6] = `<< /Length ${Buffer.byteLength(page2, 'latin1')} >>\nstream\n${page2}\nendstream`;
  objects[7] = '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>';
  objects[8] = '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>';
  objects[9] = `<< /Type /XObject /Subtype /Image /Width ${logo.width} /Height ${logo.height} ` +
    `/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length ${logo.data.length} >>\nstream\n` +
    `${logo.data.toString('latin1')}\nendstream`;
  // A realistic /Info dictionary. Real documents always carry this kind of hidden metadata
  // (author, tool, timestamps), so including it makes the sample true to life — and gives the
  // "Remove hidden information" feature something concrete to find.
  objects[10] = '<< /Title (Quarterly Business Review) /Author (Northwind Analytics) ' +
    '/Subject (FY2026 Q3 sample report) /Keywords (sample, report, quarterly) ' +
    '/Creator (Northwind Report Builder) /Producer (PDF Editor sample generator) ' +
    '/CreationDate (D:20260701090000Z) /ModDate (D:20260731120000Z) >>';

  const chunks = [Buffer.from('%PDF-1.5\n%\xE2\xE3\xCF\xD3\n', 'latin1')];
  const offsets = [];
  let pos = chunks[0].length;
  for (let i = 1; i < objects.length; i++) {
    const buf = Buffer.from(`${i} 0 obj\n${objects[i]}\nendobj\n`, 'latin1');
    offsets[i] = pos;
    chunks.push(buf);
    pos += buf.length;
  }
  const xrefStart = pos;
  let xref = `xref\n0 ${objects.length}\n0000000000 65535 f \n`;
  for (let i = 1; i < objects.length; i++) xref += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  xref += `trailer\n<< /Size ${objects.length} /Root 1 0 R /Info 10 0 R >>\nstartxref\n${xrefStart}\n%%EOF\n`;
  chunks.push(Buffer.from(xref, 'latin1'));
  return Buffer.concat(chunks);
}

const outArg = process.argv[2] || path.join(REPO_ROOT, 'docs', 'sample', 'PDF-Editor-Sample.pdf');
fs.mkdirSync(path.dirname(outArg), { recursive: true });
fs.writeFileSync(outArg, assemble());
console.log(`Wrote ${outArg} (${fs.statSync(outArg).size} bytes)`);
