// Client for the PdfEditor.NativeHost process (Chrome native messaging).
// Any extension page may create its own port; the viewer talks to the host
// directly so multi-megabyte documents never pass through the service worker.

import { HOST_STATE, classifyHostError } from './host-install.js';

export const HOST_NAME = 'com.pdfeditor.host';

// Chrome rejects host->browser messages of 1MB+; the host chunks those.
// Browser->host messages may be large, but we chunk conservatively anyway.
const REQUEST_CHUNK_THRESHOLD = 16 * 1024 * 1024;
const REQUEST_CHUNK_SIZE = 8 * 1024 * 1024;

export class HostClient {
  #port = null;
  #pending = new Map();
  #seq = 0;

  #connect() {
    this.#port = chrome.runtime.connectNative(HOST_NAME);
    this.#port.onMessage.addListener((msg) => this.#onMessage(msg));
    this.#port.onDisconnect.addListener(() => {
      const reason = chrome.runtime.lastError?.message ?? 'Native host disconnected.';
      for (const entry of this.#pending.values()) {
        const error = new Error(
          `${reason} — is the native host installed? See the extension options page.`);
        // The browser's own wording is the only thing that distinguishes "never installed" from
        // "installed but not allowed" from "installed but crashed", and each needs different
        // advice. Carry it through unmodified so callers can classify rather than re-parse the
        // decorated message above.
        error.hostError = reason;
        error.hostState = classifyHostError(reason);
        entry.reject(error);
      }
      this.#pending.clear();
      this.#port = null;
    });
  }

  #onMessage(msg) {
    const entry = this.#pending.get(msg.id);
    if (!entry) return;

    let response = msg;
    if (msg.chunkIndex !== undefined) {
      entry.chunks[msg.chunkIndex] = msg.data;
      entry.received = (entry.received ?? 0) + 1;
      if (entry.received < msg.chunkCount) return;
      const binary = atob(entry.chunks.join(''));
      const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
      response = JSON.parse(new TextDecoder().decode(bytes));
    }

    this.#pending.delete(msg.id);
    if (response.ok) entry.resolve(response.result);
    else entry.reject(new Error(response.result?.error ?? 'Unknown host error.'));
  }

  /**
   * Drops the port, ending the host process. Callers that keep a client for the life of a page
   * (the viewer) never need this; a one-shot check does, because a native port keeps an MV3
   * service worker — and the host process behind it — alive until it is closed.
   */
  disconnect() {
    this.#port?.disconnect();
    this.#port = null;
  }

  /** Sends one action to the host and resolves with its result. */
  call(action, payload = {}) {
    return new Promise((resolve, reject) => {
      if (!this.#port) {
        try {
          this.#connect();
        } catch (e) {
          reject(e);
          return;
        }
      }
      const id = `req-${++this.#seq}-${Date.now()}`;
      this.#pending.set(id, { resolve, reject, chunks: [] });

      const message = { id, action, payload };
      const json = JSON.stringify(message);
      try {
        if (json.length <= REQUEST_CHUNK_THRESHOLD) {
          this.#port.postMessage(message);
        } else {
          // Split base64(json) into slices; the host reassembles and decodes.
          const encoded = bytesToBase64(new TextEncoder().encode(json));
          const chunkCount = Math.ceil(encoded.length / REQUEST_CHUNK_SIZE);
          for (let i = 0; i < chunkCount; i++) {
            this.#port.postMessage({
              id,
              chunkIndex: i,
              chunkCount,
              data: encoded.slice(i * REQUEST_CHUNK_SIZE, (i + 1) * REQUEST_CHUNK_SIZE),
            });
          }
        }
      } catch (e) {
        this.#pending.delete(id);
        reject(e);
      }
    });
  }
}

/**
 * One-shot "is the host there?" check: connects, pings, and reports what happened as a state from
 * {@link HOST_STATE} rather than as a thrown error. Every caller that asks the question — the
 * options page, the viewer's empty state, the service worker's badge — wants the same three
 * outcomes, so the classification lives here instead of in each of them.
 *
 * @returns {Promise<{state: string, version?: string, error?: string}>}
 */
export async function probeHost(client) {
  // A client passed in belongs to the caller and stays connected (the viewer and options page both
  // go on using theirs). One created here is ours, so it is closed again — otherwise a probe from
  // the service worker would leave a host process running behind an open port.
  const owned = client === undefined;
  const target = client ?? new HostClient();
  try {
    const pong = await target.call('ping');
    return { state: HOST_STATE.CONNECTED, version: pong?.version };
  } catch (e) {
    // A throw from connectNative itself (a bad host name, no nativeMessaging permission) never
    // reaches onDisconnect, so it has no hostState — classify its message the same way.
    const raw = e?.hostError ?? e?.message ?? String(e);
    return { state: e?.hostState ?? classifyHostError(raw), error: raw };
  } finally {
    if (owned) target.disconnect();
  }
}

export function bytesToBase64(bytes) {
  let binary = '';
  const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
  }
  return btoa(binary);
}

export function base64ToBytes(base64) {
  const binary = atob(base64);
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
}
