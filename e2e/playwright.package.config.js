'use strict';

// The package-install suite: the extension driven against a native host installed by the real OS
// package, rather than one registered by the test harness.
//
// It is a separate config, not another directory under the functional suite, because it needs a
// different world: root, a package installed system-wide, and no profile-local host registration.
// Keeping it separate means `npx playwright test` stays the fast, self-contained suite a developer
// runs, and this one is opted into (see the package-install job in .github/workflows/ci.yml).
//
// Usage: npx playwright test --config playwright.package.config.js

const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './package-tests',
  // Generous: the first run publishes a self-contained .NET host and builds a 35MB package.
  timeout: 180_000,
  expect: { timeout: 20_000 },
  workers: 1,
  fullyParallel: false,
  // No retries. A flaky result here would mean the package's registration is unreliable, which is
  // exactly the thing under test — retrying would hide it.
  retries: 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  globalSetup: require.resolve('./package-global-setup'),
});
