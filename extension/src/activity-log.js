// In-memory activity log behind the viewer's activity console (Help ▸ Activity console).
//
// This is deliberately a *store* only: it holds entries, caps how many it retains, and knows how
// to format one as plain text. Nothing here touches the DOM, so the rendering side (viewer.js)
// stays free to build every entry with createElement/textContent — entries carry document-derived
// text (error messages, file names, field names, URLs) and must never reach innerHTML (#74).

/** Levels, in the order a reader cares about them. */
export const LEVELS = ['info', 'warn', 'error'];

/** Two digits, four for the year, three for milliseconds — no locale surprises across machines. */
function pad(value, width) {
  return String(value).padStart(width, '0');
}

/** `HH:MM:SS.mmm` in local time; the console is a session tool, so the date is noise. */
export function formatTime(date) {
  return `${pad(date.getHours(), 2)}:${pad(date.getMinutes(), 2)}:${pad(date.getSeconds(), 2)}`
    + `.${pad(date.getMilliseconds(), 3)}`;
}

/** One line of plain text for an entry — what "Copy all" writes to the clipboard. */
export function formatEntry(entry) {
  const level = entry.level.toUpperCase().padEnd(5, ' ');
  const detail = entry.detail ? ` — ${entry.detail}` : '';
  return `${formatTime(entry.time)}  ${level}  ${entry.message}${detail}`;
}

export const DEFAULT_CAPACITY = 500;

export class ActivityLog {
  #entries = [];
  #capacity;
  #seq = 0;
  #dropped = 0;
  #listeners = new Set();

  constructor(capacity = DEFAULT_CAPACITY) {
    // A session can run for hours and every host round-trip logs a line, so retention is capped
    // and the oldest entries fall off the front rather than growing without bound.
    this.#capacity = Math.max(1, capacity);
  }

  get capacity() { return this.#capacity; }

  /** How many entries have been evicted by the cap since the last clear(). */
  get dropped() { return this.#dropped; }

  /** Live entries, oldest first. Treat as read-only. */
  get entries() { return this.#entries; }

  /**
   * Records one entry. `level` is 'info' | 'warn' | 'error'; `message` is the headline (an action
   * name, say) and `detail` the optional extra (an error message, a duration, a file name).
   * Both may be attacker-controlled text — they are stored verbatim and never parsed.
   */
  add(level, message, detail = '') {
    const entry = {
      seq: ++this.#seq,
      time: new Date(),
      level: LEVELS.includes(level) ? level : 'info',
      message: String(message),
      detail: detail ? String(detail) : '',
    };
    this.#entries.push(entry);
    while (this.#entries.length > this.#capacity) {
      this.#entries.shift();
      this.#dropped++;
    }
    for (const listener of this.#listeners) listener(entry);
    return entry;
  }

  clear() {
    this.#entries = [];
    this.#dropped = 0;
    for (const listener of this.#listeners) listener(null);
  }

  /** Subscribes to additions (called with the new entry) and clears (called with null). */
  subscribe(listener) {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  /** The whole log as plain text, one entry per line. */
  toText() {
    return this.#entries.map(formatEntry).join('\n');
  }
}
