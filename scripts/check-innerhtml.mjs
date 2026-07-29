#!/usr/bin/env node
// Fails the build when anything under extension/src/ assigns a non-literal to innerHTML /
// outerHTML, or passes a non-literal to insertAdjacentHTML (#74).
//
// Why a rule and not a review habit: the viewer is an extension page with chrome.* privileges and
// it renders text that comes straight out of the document being edited — field names, file names,
// link URLs, host error strings. A real XSS shipped here because such text reached a helper whose
// innerHTML was two calls away (#73). Clearing a container with `innerHTML = ''` (about twenty
// places) and building static chrome from a string literal are both still fine; interpolating
// anything is not.
//
// Usage: node scripts/check-innerhtml.mjs [dir...]     (default: extension/src)
// Exits 1 and prints `file:line:col message` for every violation.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const SINKS = ['innerHTML', 'outerHTML'];
const SCANNED_EXTENSIONS = new Set(['.js', '.mjs']);

/**
 * Rewrites `source` so that every string, template and comment body becomes a filler character,
 * keeping the original length and offsets. That lets the checks below use plain indexOf/regex on
 * real code without matching the word "innerHTML" inside a comment or a string, while still being
 * able to tell a literal apart from an expression.
 *
 * Filler alphabet: `S` for the body of a literal that interpolates nothing, `T` for an entire
 * template literal that does interpolate (delimiters included, so it can never look like a
 * literal), and a space for comments.
 */
export function maskSource(source) {
  const out = source.split('');
  const blank = (from, to, fill) => { for (let i = from; i < to; i++) out[i] = fill; };
  let i = 0;
  while (i < source.length) {
    const c = source[i];
    if (c === '/' && source[i + 1] === '/') {
      const end = source.indexOf('\n', i);
      const stop = end === -1 ? source.length : end;
      blank(i, stop, ' ');
      i = stop;
    } else if (c === '/' && source[i + 1] === '*') {
      const end = source.indexOf('*/', i + 2);
      const stop = end === -1 ? source.length : end + 2;
      for (let j = i; j < stop; j++) if (source[j] !== '\n') out[j] = ' ';
      i = stop;
    } else if (c === "'" || c === '"') {
      i = maskQuoted(source, out, i, c);
    } else if (c === '`') {
      i = maskTemplate(source, out, i);
    } else {
      i++;
    }
  }
  return out.join('');
}

/** Masks a '…' / "…" literal starting at `start`; returns the index just past it. */
function maskQuoted(source, out, start, quote) {
  let i = start + 1;
  while (i < source.length) {
    if (source[i] === '\\') { out[i] = 'S'; out[i + 1] = 'S'; i += 2; continue; }
    if (source[i] === quote) return i + 1;
    if (source[i] !== '\n') out[i] = 'S';
    i++;
  }
  return i;
}

/** Masks a `…` template starting at `start`; returns the index just past it. */
function maskTemplate(source, out, start) {
  let i = start + 1;
  let interpolates = false;
  let depth = 0;
  while (i < source.length) {
    const c = source[i];
    if (c === '\\') { i += 2; continue; }
    if (depth === 0 && c === '$' && source[i + 1] === '{') { interpolates = true; depth = 1; i += 2; continue; }
    if (depth > 0) {
      if (c === '{') depth++;
      else if (c === '}') depth--;
      i++;
      continue;
    }
    if (c === '`') { i++; break; }
    i++;
  }
  // `T` for an interpolating template (delimiters included, so it cannot pass as a literal),
  // `S` for a plain one — which is just a string with nicer quoting.
  const fill = interpolates ? 'T' : 'S';
  for (let j = start; j < i; j++) if (source[j] !== '\n') out[j] = fill;
  if (!interpolates) { out[start] = '`'; out[i - 1] = '`'; }
  return i;
}

/** True when `masked` is a string literal, or several joined with `+` — and nothing else. */
export function isStaticLiteral(masked) {
  const text = masked.replace(/\s+/g, '');
  if (text === '') return false;
  let i = 0;
  let expectLiteral = true;
  while (i < text.length) {
    if (expectLiteral) {
      const quote = text[i];
      if (quote !== "'" && quote !== '"' && quote !== '`') return false;
      i++;
      while (text[i] === 'S') i++;
      if (text[i] !== quote) return false;
      i++;
      expectLiteral = false;
    } else {
      if (text[i] !== '+') return false;
      i++;
      expectLiteral = true;
    }
  }
  return !expectLiteral;
}

/** Scans forward from `from` for the `;` that ends the statement, ignoring nested brackets. */
function statementEnd(masked, from) {
  let depth = 0;
  for (let i = from; i < masked.length; i++) {
    const c = masked[i];
    if ('([{'.includes(c)) depth++;
    else if (')]}'.includes(c)) { if (depth === 0) return i; depth--; }
    else if (c === ';' && depth === 0) return i;
  }
  return masked.length;
}

/** Splits a masked argument list (without the outer parens) at top-level commas. */
function splitArgs(masked) {
  const args = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < masked.length; i++) {
    const c = masked[i];
    if ('([{'.includes(c)) depth++;
    else if (')]}'.includes(c)) depth--;
    else if (c === ',' && depth === 0) { args.push(masked.slice(start, i)); start = i + 1; }
  }
  args.push(masked.slice(start));
  return args.filter((a) => a.trim() !== '');
}

function positionOf(source, index) {
  const before = source.slice(0, index);
  const line = before.split('\n').length;
  const col = index - (before.lastIndexOf('\n') + 1) + 1;
  return { line, col };
}

/** Returns every violation in one file's source: [{ line, col, sink, message }]. */
export function findViolations(source, file = '<source>') {
  const masked = maskSource(source);
  const violations = [];
  const report = (index, sink, message) => {
    const { line, col } = positionOf(source, index);
    violations.push({ file, line, col, sink, message });
  };

  for (const sink of SINKS) {
    const pattern = new RegExp(`\\.${sink}\\s*(\\+?=)(?!=)`, 'g');
    let match;
    while ((match = pattern.exec(masked)) !== null) {
      const rhsStart = match.index + match[0].length;
      const rhs = masked.slice(rhsStart, statementEnd(masked, rhsStart));
      if (!isStaticLiteral(rhs)) {
        report(match.index, sink,
          `${sink} is assigned a non-literal — build the node with createElement/textContent instead`);
      }
    }
  }

  const insert = /\.insertAdjacentHTML\s*\(/g;
  let call;
  while ((call = insert.exec(masked)) !== null) {
    const open = call.index + call[0].length - 1;
    const close = statementEnd(masked, open + 1);
    for (const arg of splitArgs(masked.slice(open + 1, close))) {
      if (!isStaticLiteral(arg)) {
        report(call.index, 'insertAdjacentHTML',
          'insertAdjacentHTML is passed a non-literal — build the node with createElement/textContent instead');
        break;
      }
    }
  }
  return violations;
}

/** Every scannable file under `dir`, recursively. */
export function collectFiles(dir) {
  const files = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) files.push(...collectFiles(full));
    else if (SCANNED_EXTENSIONS.has(path.extname(entry.name))) files.push(full);
  }
  return files;
}

/** Scans a directory tree and returns every violation found. */
export function checkDirectory(dir) {
  return collectFiles(dir).flatMap((file) => findViolations(fs.readFileSync(file, 'utf8'), file));
}

export const EXTENSION_SRC = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)), '..', 'extension', 'src');

function main(argv) {
  const dirs = argv.length > 0 ? argv : [EXTENSION_SRC];
  const violations = dirs.flatMap((dir) => checkDirectory(dir));
  for (const v of violations) {
    process.stderr.write(`${v.file}:${v.line}:${v.col} ${v.message}\n`);
  }
  if (violations.length > 0) {
    process.stderr.write(`\n${violations.length} HTML-injection sink(s) found. `
      + 'Only a static string literal (including the empty string used to clear a container) '
      + 'may be assigned to innerHTML/outerHTML.\n');
    return 1;
  }
  process.stdout.write(`No interpolated innerHTML/outerHTML/insertAdjacentHTML under ${dirs.join(', ')}.\n`);
  return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  process.exit(main(process.argv.slice(2)));
}
