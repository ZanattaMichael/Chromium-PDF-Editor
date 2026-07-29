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
 *   failWith(text)  — set the message the injected error carries. The viewer surfaces host error
 *                    strings verbatim (status line, activity console), and a hostile document can
 *                    influence them, so a test needs to be able to choose that text.
 *   stopHolding()   — let *new* requests through; already-parked responses stay parked
 *   release()       — deliver every parked response, in arrival order
 *   heldCount()     — how many responses are currently parked
 */
async function installHostGate(page, { hold = [], fail = [], failMessage } = {}) {
  await page.addInitScript(([heldActions, failedActions, failedMessage]) => {
    const gate = {
      held: new Set(heldActions),
      failed: new Set(failedActions),
      message: failedMessage,
      parked: [],
      hold(actions) { gate.held = new Set(actions); },
      fail(actions) { gate.failed = new Set(actions); },
      failWith(text) { gate.message = text; },
      stopHolding() { gate.held = new Set(); },
      release() {
        const queued = gate.parked.splice(0);
        for (const deliver of queued) deliver();
        return queued.length;
      },
      heldCount() { return gate.parked.length; },
      /**
       * Resolves once the page has had a chance to act on whatever was just delivered: the
       * microtask queue drains (so every `await` resumed by a released response has run), then a
       * frame is painted (so anything those continuations wrote is in the DOM). Lets a test that
       * asserts a *negative* — "the stale result was discarded" — synchronise on an observable
       * condition instead of guessing at a sleep duration.
       */
      settled() {
        return new Promise((resolve) => {
          requestAnimationFrame(() => setTimeout(() => requestAnimationFrame(resolve), 0));
        });
      },
    };
    window.__hostGate = gate;

    // Hoisted rather than written inline inside the port wrapper. The natural nesting —
    // init script > connectNative > addListener > deliver — is five functions deep, one past
    // the limit; keeping these at the top level holds the wrapper itself to four.
    const notify = (listeners, msg) => { for (const cb of listeners) cb(msg); };

    const parkOrDeliver = (parkedIds, listeners, msg) => {
      if (parkedIds.has(msg.id)) gate.parked.push(() => notify(listeners, msg));
      else notify(listeners, msg);
    };

    const answerWithFailure = (listeners, id) => setTimeout(
      () => notify(listeners, { id, ok: false, result: { error: gate.message } }), 0);

    const connect = chrome.runtime.connectNative.bind(chrome.runtime);
    chrome.runtime.connectNative = (name) => {
      const port = connect(name);
      const parkedIds = new Set();
      const post = port.postMessage.bind(port);
      const listeners = [];
      port.onMessage.addListener((msg) => parkOrDeliver(parkedIds, listeners, msg));
      return {
        postMessage(msg) {
          if (msg.action && gate.failed.has(msg.action)) {
            answerWithFailure(listeners, msg.id);
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
  }, [hold, fail, failMessage ?? 'injected host failure']);
}

module.exports = { installHostGate };
