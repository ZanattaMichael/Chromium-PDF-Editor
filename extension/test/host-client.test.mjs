// Unit tests for probeHost (see extension/src/host-client.js) against a stubbed chrome.runtime.
// Run with: node --test extension/test/host-client.test.mjs
//
// probeHost is the one place that turns "connectNative failed" into a state the UI can act on, and
// its three failure paths (no manifest / manifest disallows us / process died) are exactly the ones
// that are awkward to reproduce by hand. Stubbing chrome.runtime makes them a unit test.
import assert from 'node:assert/strict';
import test from 'node:test';

import { HOST_STATE } from '../src/host-install.js';

/**
 * Installs a fake `chrome.runtime` whose connectNative returns a port that behaves the way Chrome's
 * does: it either answers the ping, or disconnects with `chrome.runtime.lastError` set.
 *
 * @param {object} o
 * @param {object} [o.reply]   message to deliver back (a successful host)
 * @param {string} [o.failWith] lastError message to disconnect with (a failing host)
 * @param {boolean} [o.throwOnConnect] make connectNative itself throw
 */
function stubChrome({ reply, failWith, throwOnConnect = false } = {}) {
  const state = { disconnected: 0, connects: 0 };
  globalThis.chrome = {
    runtime: {
      lastError: undefined,
      connectNative() {
        state.connects += 1;
        if (throwOnConnect) throw new Error(failWith ?? 'connectNative exploded');
        const listeners = { message: [], disconnect: [] };
        const port = {
          onMessage: { addListener: (fn) => listeners.message.push(fn) },
          onDisconnect: { addListener: (fn) => listeners.disconnect.push(fn) },
          disconnect: () => { state.disconnected += 1; },
          postMessage: (msg) => {
            // Answer on a later turn, the way a real port does.
            queueMicrotask(() => {
              if (failWith !== undefined) {
                globalThis.chrome.runtime.lastError = { message: failWith };
                listeners.disconnect.forEach((fn) => fn());
                globalThis.chrome.runtime.lastError = undefined;
              } else {
                listeners.message.forEach((fn) => fn({ id: msg.id, ok: true, result: reply }));
              }
            });
          },
        };
        return port;
      },
    },
  };
  return state;
}

// The module reads chrome.* lazily inside methods, so a fresh import is not needed per test.
const { probeHost, HostClient } = await import('../src/host-client.js');

test('a host that answers ping reports CONNECTED with its version', async () => {
  stubChrome({ reply: { version: '2.0.1.46' } });
  const probe = await probeHost();
  assert.equal(probe.state, HOST_STATE.CONNECTED);
  assert.equal(probe.version, '2.0.1.46');
  assert.equal(probe.error, undefined);
});

test('probeHost closes the port it opened itself', async () => {
  // Otherwise a probe from the MV3 service worker leaves a host process running behind it.
  const state = stubChrome({ reply: { version: '1' } });
  await probeHost();
  assert.equal(state.disconnected, 1);
});

test('probeHost leaves a caller-supplied client connected', async () => {
  const state = stubChrome({ reply: { version: '1' } });
  const client = new HostClient();
  await probeHost(client);
  assert.equal(state.disconnected, 0, 'the options page goes on using its own client');
});

test('each failure message maps to the state that has the right fix', async () => {
  const cases = [
    ['Specified native messaging host not found.', HOST_STATE.MISSING],
    ['Access to the specified native messaging host is forbidden.', HOST_STATE.FORBIDDEN],
    ['Native host has exited.', HOST_STATE.CRASHED],
    ['Something nobody has seen before.', HOST_STATE.UNKNOWN],
  ];
  for (const [message, expected] of cases) {
    stubChrome({ failWith: message });
    const probe = await probeHost();
    assert.equal(probe.state, expected, message);
    // The browser's own wording survives, so a bug report carries it.
    assert.equal(probe.error, message);
  }
});

test('a connectNative that throws is classified too, not left as an unhandled rejection', async () => {
  stubChrome({ throwOnConnect: true, failWith: 'Specified native messaging host not found.' });
  const probe = await probeHost();
  assert.equal(probe.state, HOST_STATE.MISSING);
});

test('a disconnect rejects the pending call with the browser error attached', async () => {
  stubChrome({ failWith: 'Native host has exited.' });
  const client = new HostClient();
  await assert.rejects(
    () => client.call('ping'),
    (err) => {
      assert.equal(err.hostError, 'Native host has exited.');
      assert.equal(err.hostState, HOST_STATE.CRASHED);
      // The user-facing message still points at the options page.
      assert.match(err.message, /native host installed/);
      return true;
    });
});
