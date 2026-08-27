'use strict';

// Installs the real .deb before the package suite runs, and takes it off again afterwards.
//
// Split out from the functional suite's global setup because the two want opposite worlds: that
// one registers the host inside a throwaway profile and touches nothing on the machine, this one
// deliberately installs a package system-wide and needs root to do it.

const { ensureIcons } = require('./helpers/harness');
const { installDeb, removeDeb } = require('./helpers/deb-package');

module.exports = async () => {
  ensureIcons();
  installDeb();
  // Playwright calls the returned function after the last test, pass or fail, so a red run still
  // leaves the machine as it found it.
  return () => { removeDeb(); };
};
