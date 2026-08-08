// Formats the native host's `diagnostics` response for display. Kept DOM-free so it can be unit
// tested and reused by both the options page and the viewer's activity console. The host may be an
// older build without the `diagnostics` action (or a field may be missing), so every row is
// optional — absent values are dropped rather than shown as "undefined".

const ROWS = [
  ['Host version', (d) => d.version],
  ['Runtime', (d) => d.runtime],
  ['Operating system', (d) => d.os],
  ['OS architecture', (d) => d.osArchitecture],
  ['Process architecture', (d) => d.processArchitecture],
  ['OCR available', (d) => (d.ocrAvailable === undefined ? undefined : d.ocrAvailable ? 'yes' : 'no')],
  ['Executable', (d) => d.executablePath],
  ['Reported at (UTC)', (d) => d.utc],
];

/** Returns `[label, value]` pairs for the fields the host actually reported. */
export function hostDiagnosticsRows(d) {
  if (!d || typeof d !== 'object') return [];
  return ROWS
    .map(([label, get]) => [label, get(d)])
    .filter(([, value]) => value !== undefined && value !== null && value !== '');
}

/** Returns the diagnostics as `Label: value` lines (for a <pre> block or a downloaded log). */
export function hostDiagnosticsLines(d) {
  return hostDiagnosticsRows(d).map(([label, value]) => `${label}: ${value}`);
}
