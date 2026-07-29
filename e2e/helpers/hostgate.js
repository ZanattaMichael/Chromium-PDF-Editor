'use strict';

/**
 * Test-side control over the viewer's native-host port, so a slow host response can be
 * reproduced deterministically instead of hoping a real one is slow enough.
 *
 * It wraps `chrome.runtime.connectNative` in the viewer page (via an init script, so no
 * production code learns about the tests) and, for the named actions, parks the host's
 * response until the test releases it.
 *
 * In the page, `window.__hostGate` exposes:
 *   hold(actions)   — start holding responses for these actions (replaces the current set)
 *   fail(actions)   — answer these actions with an error instead of forwarding them
 *   stopHolding()   — let *new* requests through; already-parked responses stay parked
 *   release()       — deliver every parked response, in arrival order
 *   heldCount()     — how many responses are currently parked
 */
async function installHostGate(page, { hold = [], fail = [] } = {}) {
  await page.addInitScript(([heldActions, failedActions]) => {
    const gate = {
      held: new Set(heldActions),
      failed: new Set(failedActions),
      parked: [],
      hold(actions) { gate.held = new Set(actions); },
      fail(actions) { gate.failed = new Set(actions); },
      stopHolding() { gate.held = new Set(); },
      release() {
        const queued = gate.parked.splice(0);
        for (const deliver of queued) deliver();
        return queued.length;
      },
      heldCount() { return gate.parked.length; },
    };
    window.__hostGate = gate;

    const connect = chrome.runtime.connectNative.bind(chrome.runtime);
    chrome.runtime.connectNative = (name) => {
      const port = connect(name);
      const parkedIds = new Set();
      const post = port.postMessage.bind(port);
      const listeners = [];
      port.onMessage.addListener((msg) => {
        const deliver = () => { for (const cb of listeners) cb(msg); };
        if (parkedIds.has(msg.id)) gate.parked.push(deliver);
        else deliver();
      });
      return {
        postMessage(msg) {
          if (msg.action && gate.failed.has(msg.action)) {
            const error = { id: msg.id, ok: false, result: { error: 'injected host failure' } };
            setTimeout(() => { for (const cb of listeners) cb(error); }, 0);
            return;
          }
          if (msg.action && gate.held.has(msg.action)) parkedIds.add(msg.id);
          post(msg);
        },
        onMessage: { addListener: (cb) => listeners.push(cb) },
        onDisconnect: port.onDisconnect,
        disconnect: () => port.disconnect(),
      };
    };
  }, [hold, fail]);
}

module.exports = { installHostGate };
