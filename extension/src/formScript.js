// A deliberately tiny, safe interpreter for the handful of Acrobat-style JavaScript patterns that
// show up on ordinary form "Calculate" / "Reset" / "show-hide" buttons — NOT a JavaScript engine.
// The viewer otherwise only rasterises pages (see PdfSafety.cs / JavaScriptTool.cs), so a full
// engine is out of scope; this covers the common, safe subset instead of leaving buttons dead:
//   this.getField("Total").value = this.getField("A").value + this.getField("B").value;
//   this.getField("Extra").display = display.hidden;   // or display.visible
//   this.resetForm();
// Anything outside that grammar (conditionals, loops, app.alert, string concatenation, calls to
// user-defined functions, …) is reported as unsupported rather than partially executed — we never
// want to silently do half of what a script asked for, and we never `eval`/`Function` PDF content.

const FIELD_REF = /this\s*\.\s*getField\(\s*(["'])((?:(?!\1).)*)\1\s*\)\s*\.\s*value/g;
const SET_STATEMENT =
  /^this\s*\.\s*getField\(\s*(["'])((?:(?!\1).)*)\1\s*\)\s*\.\s*value\s*=\s*(.+)$/;
const DISPLAY_STATEMENT =
  /^this\s*\.\s*getField\(\s*(["'])((?:(?!\1).)*)\1\s*\)\s*\.\s*display\s*=\s*display\s*\.\s*(hidden|visible)$/;
const RESET_STATEMENT = /^this\s*\.\s*resetForm\(\s*\)$/;

/**
 * Runs the given field-click script against the current field values.
 *
 * @param {string} script - the raw JavaScript source from the button's /A action.
 * @param {(name: string) => string} getValue - reads a field's current (string) value.
 * @returns {{ok: true, sets: {name: string, value: string}[], display: {name: string, hidden: boolean}[], reset: boolean} | {ok: false}}
 *   `ok: false` means the script wasn't (fully) recognised — the caller should tell the user it
 *   needs a real PDF viewer rather than silently doing nothing or half-applying it.
 */
function runFormScript(script, getValue) {
  const statements = String(script ?? '')
    .split(';')
    .map((s) => s.trim())
    .filter(Boolean);
  if (statements.length === 0) return { ok: false };

  const sets = [];
  const display = [];
  let reset = false;

  for (const statement of statements) {
    if (RESET_STATEMENT.test(statement)) {
      reset = true;
      continue;
    }

    const displayMatch = DISPLAY_STATEMENT.exec(statement);
    if (displayMatch) {
      display.push({ name: displayMatch[2], hidden: displayMatch[3] === 'hidden' });
      continue;
    }

    const setMatch = SET_STATEMENT.exec(statement);
    if (setMatch) {
      const value = evaluateExpression(setMatch[3], getValue);
      if (value === undefined) return { ok: false };
      sets.push({ name: setMatch[2], value: String(value) });
      continue;
    }

    return { ok: false }; // an unrecognised statement — don't half-run the script
  }
  return { ok: true, sets, display, reset };
}

/**
 * Evaluates a numeric expression made only of field-value references, Number()/parseFloat()/
 * parseInt() wrappers, numeric literals, and +, -, *, /, parentheses. Returns `undefined` for
 * anything outside that grammar (the caller then reports the whole script as unsupported).
 */
function evaluateExpression(expr, getValue) {
  const substituted = expr.replace(FIELD_REF, (_match, _quote, name) => {
    const n = parseFloat(getValue(name));
    return Number.isFinite(n) ? String(n) : '0';
  });
  // Strip single-argument Number(...)/parseFloat(...)/parseInt(...) wrappers down to plain
  // parentheses — handles the common (non-nested-call) case used by "calculate" buttons.
  const bare = substituted.replace(/\b(?:Number|parseFloat|parseInt)\(([^()]*)\)/g, '($1)');
  if (!/^[\d\s+\-*/().]+$/.test(bare)) return undefined;
  try {
    const parser = new ArithmeticParser(bare);
    const value = parser.parseExpression();
    if (!parser.atEnd() || !Number.isFinite(value)) return undefined;
    return value;
  } catch {
    return undefined;
  }
}

/**
 * Minimal recursive-descent parser for +, -, *, / and parenthesised numeric expressions.
 * Deliberately not `eval`/`new Function` — a hostile script can't smuggle in arbitrary JS here.
 */
class ArithmeticParser {
  constructor(text) {
    this.text = text.replace(/\s+/g, '');
    this.pos = 0;
  }

  atEnd() {
    return this.pos >= this.text.length;
  }

  peek() {
    return this.text[this.pos];
  }

  parseExpression() {
    let value = this.parseTerm();
    while (this.peek() === '+' || this.peek() === '-') {
      const op = this.text[this.pos++];
      const rhs = this.parseTerm();
      value = op === '+' ? value + rhs : value - rhs;
    }
    return value;
  }

  parseTerm() {
    let value = this.parseFactor();
    while (this.peek() === '*' || this.peek() === '/') {
      const op = this.text[this.pos++];
      const rhs = this.parseFactor();
      value = op === '*' ? value * rhs : value / rhs;
    }
    return value;
  }

  parseFactor() {
    if (this.peek() === '(') {
      this.pos++;
      const inner = this.parseExpression();
      if (this.peek() !== ')') throw new Error('expected )');
      this.pos++;
      return inner;
    }
    if (this.peek() === '-') {
      this.pos++;
      return -this.parseFactor();
    }
    if (this.peek() === '+') {
      this.pos++;
      return this.parseFactor();
    }
    const start = this.pos;
    while (/[\d.]/.test(this.peek() || '')) this.pos++;
    if (this.pos === start) throw new Error('expected number');
    return parseFloat(this.text.slice(start, this.pos));
  }
}

export { runFormScript, evaluateExpression };
