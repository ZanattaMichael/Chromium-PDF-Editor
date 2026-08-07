// Build identity for the "About" dialog (#103).
//
// Two halves, kept apart so the formatting is testable without a browser (the same
// DOM-free split as activity-log.js / geometry.js):
//   - formatBuildInfo(): a pure function that turns the raw {version, commit, builtAt}
//     into display-ready strings, tolerating anything missing.
//   - loadBuildMeta(): the runtime half that fetches the generated build-info.json.
//
// build-info.json is written at package time by scripts/generate-build-info.py and is
// absent from an unpacked development checkout, so every field must degrade gracefully.

/** Turns a commit SHA + build timestamp into the fields the About dialog renders. */
export function formatBuildInfo(raw = {}) {
  const version = String(raw.version ?? '').trim() || 'unknown';
  const commit = String(raw.commit ?? '').trim();
  const builtAt = String(raw.builtAt ?? '').trim();
  return {
    version,
    // A packaged build records its full SHA; show the conventional 12-char short form.
    // A development build has none — say so plainly rather than showing a blank.
    commit: commit ? commit.slice(0, 12) : 'development build (unpackaged)',
    commitFull: commit,
    isRelease: commit.length > 0,
    builtAt: builtAt ? formatBuiltAt(builtAt) : '—',
  };
}

/** Formats an ISO-8601 instant as a stable, timezone-independent "YYYY-MM-DD HH:MM UTC". */
export function formatBuiltAt(iso) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return String(iso); // not parseable: show it verbatim
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} `
    + `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`;
}

/**
 * Reads the generated build-info.json. Returns {} when it is absent (a development
 * checkout) or unreadable — the caller falls back to the manifest version alone.
 * getUrl/fetchImpl are injected so this is exercisable outside the extension.
 */
export async function loadBuildMeta(getUrl, fetchImpl = fetch) {
  try {
    const res = await fetchImpl(getUrl('build-info.json'));
    if (!res.ok) return {};
    return await res.json();
  } catch {
    // Expected on an unpacked dev build where the file was never generated. No file,
    // no build stamp — the dialog still shows the manifest version.
    return {};
  }
}
