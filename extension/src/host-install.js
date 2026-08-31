// Works out *why* the native host is unreachable and what the user should do about it.
//
// The extension cannot look at the filesystem, so "is the host installed?" is only ever answered by
// trying to connect and reading the failure. Chrome's messages are precise but unhelpful on their
// own — "Specified native messaging host not found." is what you get whether the package was never
// installed, was installed for a browser that reads a different directory, or was installed fine
// but the manifest names an extension ID that is not this one. Each of those needs a different fix,
// so this module maps the message onto a state and turns the state into concrete instructions.
//
// Kept DOM-free (like host-diagnostics.js) so it can be unit tested and used by the options page,
// the viewer and the service worker alike.

/** The pinned Chrome Web Store ID. The manifest "key" forces this ID even when loaded unpacked. */
export const PINNED_EXTENSION_ID = 'ikbkielkpaloojhibinmcfbeekhkdblc';

export const RELEASES_URL = 'https://github.com/ZanattaMichael/Chromium-PDF-Editor/releases/latest';

/** Connection states, in the order of "how much of the install is already working". */
export const HOST_STATE = {
  CONNECTED: 'connected',
  /** Nothing answered: no manifest in any directory this browser reads. */
  MISSING: 'missing',
  /** A manifest was found, but its allowed_origins does not list this extension. */
  FORBIDDEN: 'forbidden',
  /** The manifest was found and the process was launched, but it died or never spoke. */
  CRASHED: 'crashed',
  /** Something we do not have specific advice for. */
  UNKNOWN: 'unknown',
};

// Chrome/Chromium, Edge and Brave all use the Chromium strings; the Firefox wording is included
// because the same module is cheap to keep portable, and matching is substring-based so a browser
// that decorates the message ("... Error: ...") still classifies.
// Resolves the host's install directory into $hostDir before any command uses it.
//
// Naming a single Program Files root is wrong on a real share of machines. `wix build` defaults to
// -arch x86, and until scripts/package-msi.ps1 passed -arch x64 the MSI was a 32-bit package --
// which Windows Installer redirects to "C:\\Program Files (x86)". Anyone who installed one of
// those has the host there, and "$env:ProgramFiles\\PDF Editor Host" names a directory they do not
// have. They are also precisely the people reading this guidance, so it has to find either.
//
// Split across two segments for one reason: ${env:ProgramFiles(x86)} is PowerShell's syntax for a
// variable whose name contains parentheses, and a template literal -- String.raw included, since it
// is still a tagged template -- would read that ${ as JS interpolation. So the second segment stays
// a plain quoted string (it also ends in a real \n, which String.raw could not produce). The first
// carries only backslashes, so String.raw keeps them unescaped there.
const WINDOWS_HOST_DIR = String.raw`$hostDir = "$env:ProgramFiles\PDF Editor Host",`
  + '"${env:ProgramFiles(x86)}\\PDF Editor Host" | ?{ Test-Path $_ } | select -First 1\n';

const PATTERNS = [
  { state: HOST_STATE.MISSING, pattern: /not found|not registered|no such native application|not installed/i },
  { state: HOST_STATE.FORBIDDEN, pattern: /forbidden|not allowed|access to the specified native messaging host/i },
  { state: HOST_STATE.CRASHED, pattern: /has exited|failed to start|error when communicating|not responding|terminated/i },
];

/**
 * Maps a `chrome.runtime.lastError` message from a failed native-messaging connection onto a
 * {@link HOST_STATE}. An empty or unrecognised message is UNKNOWN rather than a guess — the
 * generic advice is still useful and a wrong diagnosis is worse than none.
 */
export function classifyHostError(message) {
  if (typeof message !== 'string' || message.trim() === '') return HOST_STATE.UNKNOWN;
  for (const { state, pattern } of PATTERNS) {
    if (pattern.test(message)) return state;
  }
  return HOST_STATE.UNKNOWN;
}

/** The OS family, from a user-agent string. Only what changes the instructions is distinguished. */
export function detectPlatform(userAgent = '') {
  const ua = String(userAgent);
  if (/Windows/i.test(ua)) return 'windows';
  // Order matters: Chrome on macOS says "Macintosh", on iOS "like Mac OS X" — neither is Linux,
  // and Android says "Linux; Android", which is not a desktop Linux install.
  if (/Macintosh|Mac OS X/i.test(ua)) return 'macos';
  if (/CrOS/i.test(ua)) return 'chromeos';
  if (/Android/i.test(ua)) return 'android';
  if (/Linux|X11/i.test(ua)) return 'linux';
  return 'unknown';
}

/** A one-line summary suitable for a status field. */
export function hostStateSummary(state) {
  switch (state) {
    case HOST_STATE.CONNECTED: return 'Connected.';
    case HOST_STATE.MISSING: return 'The native host is not installed for this browser.';
    case HOST_STATE.FORBIDDEN: return 'The native host is installed, but it does not allow this extension.';
    case HOST_STATE.CRASHED: return 'The native host is installed but did not start.';
    default: return 'The native host could not be reached.';
  }
}

// --------------------------------------------------------------------- steps

// Per-platform "install it from scratch" steps. Keyed by the platform names detectPlatform returns.
// Exported because host-version.js reuses them: replacing an out-of-date host is the same download
// and the same command as installing one, and a second copy of these would drift from this one.
export function installSteps(platform) {
  if (platform === 'windows') {
    return [
      {
        text: 'Download pdf-editor-host-<version>-x64.msi from the latest release and run it '
          + '(or install it from a terminal).',
        code: 'msiexec /i pdf-editor-host-<version>-x64.msi',
      },
      {
        text: 'Check the install worked — this prints the host’s version and environment '
          + '(in PowerShell):',
        code: WINDOWS_HOST_DIR
          + String.raw`& "$hostDir\PdfEditor.NativeHost.exe" --diagnostics`,
      },
      { text: 'Close every window of this browser and start it again.' },
    ];
  }
  if (platform === 'macos') {
    return [
      {
        text: 'Download pdf-editor-bundle-osx-x64.zip (or osx-arm64 on Apple Silicon) from the '
          + 'latest release, unzip it, and run the installer from inside it.',
        code: './scripts/install-host.sh',
      },
      { text: 'Quit this browser completely (⌘Q) and start it again.' },
    ];
  }
  if (platform === 'linux') {
    return [
      {
        text: 'Download the package for your distribution from the latest release and install it. '
          + 'It registers the host for Chrome, Chromium, Edge, Brave, Vivaldi and Opera at once.',
        code: 'sudo apt install ./pdf-editor-host_<version>_amd64.deb            # Debian / Ubuntu\n'
          + 'sudo dnf install ./pdf-editor-host-<version>-1.x86_64.rpm         # Fedora / RHEL\n'
          + 'sudo pacman -U ./pdf-editor-host-<version>-1-x86_64.pkg.tar.zst   # Arch',
      },
      {
        text: 'Check the install worked — this prints the host’s version and environment:',
        code: 'pdf-editor-host --diagnostics',
      },
      {
        text: 'Using a snap or flatpak browser? Those are sandboxed away from /etc, so the '
          + 'system-wide registration cannot reach them. Register for your user as well:',
        code: 'pdf-editor-host-register',
      },
      { text: 'Quit this browser completely and start it again.' },
    ];
  }
  // ChromeOS, Android and anything unrecognised: there is no native host for these.
  if (platform === 'chromeos' || platform === 'android') {
    return [{
      text: 'The native host is a desktop application, and there is no build for this platform. '
        + 'reDACT needs Chrome, Chromium, Edge, Brave, Vivaldi or Opera on Windows, macOS or Linux.',
    }];
  }
  return [{
    text: 'Download the bundle for your platform from the latest release and run the installer '
      + 'script inside it:',
    // The Windows line goes through powershell.exe with an explicit policy for the same reason
    // reRegisterStep does: a default-configured client machine refuses to run a .ps1 outright.
    code: './scripts/install-host.sh <extension-id>'
      + '                                  # Linux / macOS\n'
      + 'powershell -NoProfile -ExecutionPolicy Bypass -File '
      + String.raw`.\scripts\install-host.ps1 -ExtensionId <extension-id>   # Windows`,
  }];
}

// Every platform's re-registration advice opens with the same sentence; kept in one place so the
// three variants cannot drift apart, and so only the platform-specific half is spelled out below.
const RE_REGISTER_LEAD = 'Re-register the host for your user against this extension’s ID. A per-user ';

// The command that re-registers the host for the current user against a specific extension ID.
function reRegisterStep(platform, extensionId) {
  if (platform === 'windows') {
    // The MSI installs register-host.ps1 next to the host, so this is a command someone who has
    // only ever run the installer can actually run. Pointing at scripts\install-host.ps1 instead
    // would name a path they do not have: that file ships only in a checkout or a release bundle.
    return {
      text: RE_REGISTER_LEAD
        + 'registration takes precedence over the machine-wide one the installer wrote '
        + '(in PowerShell):',
      // Launched through powershell.exe with an explicit policy: Windows client machines default
      // to a Restricted execution policy, under which invoking the shipped .ps1 directly fails.
      code: WINDOWS_HOST_DIR
        + 'powershell -NoProfile -ExecutionPolicy Bypass -File '
        + String.raw`"$hostDir\register-host.ps1" -ExtensionId ${extensionId}`,
    };
  }
  if (platform === 'linux') {
    return {
      text: RE_REGISTER_LEAD
        + 'manifest takes precedence over the system-wide one:',
      code: `pdf-editor-host-register --extension-id ${extensionId}`,
    };
  }
  return {
    text: RE_REGISTER_LEAD
      + 'manifest takes precedence over the system-wide one:',
    code: `./scripts/install-host.sh ${extensionId}`,
  };
}

// Lists every manifest for this host that the browser might read, per-user ones first. When the
// package's own registration would have allowed this extension and the browser still says
// forbidden, some *other* manifest is being read instead, and the only way to see which is to look
// at the files: Chromium reads the per-user directory in preference to the system-wide one, so a
// stale per-user manifest left by an earlier install silently wins over a correct package.
function listManifestsStep(platform) {
  if (platform === 'windows') {
    return {
      text: 'List every registration for this host and the ID each one allows. HKCU wins over '
        + 'HKLM, so a per-user value here is what the browser is actually reading (in PowerShell):',
      // String.raw only on the segment that carries a backslash (\SOFTWARE); the other two are
      // plain templates. PowerShell's own $_ and $( ... ) are not JS interpolation, so they pass
      // through untouched -- but a future edit must not introduce a literal ${ here.
      code: String.raw`'HKCU:','HKLM:' | %{ gci "$_\SOFTWARE" -Recurse -Include NativeMessagingHosts `
        + `-EA 0 } | gci -EA 0 | ?{ $_.PSChildName -eq 'com.pdfeditor.host' } | %{ `
        + `$p = (gp $_.PSPath).'(default)'; "$($_.Name) -> $p"; gc $p -EA 0 | sls chrome-extension }`,
    };
  }
  return {
    text: 'List every manifest for this host and the ID each one allows. The per-user copies are '
      + 'read in preference to the system-wide one, so the first hit is what the browser sees:',
    // /etc alone, not "/etc /etc/opt": /etc/opt is inside /etc, and naming both makes find walk
    // it twice and print every system-wide manifest twice — in a listing whose whole point is
    // which manifest wins, a duplicate reads like a second, conflicting registration.
    code: 'find ~/.config ~/snap ~/.var/app ~/Library/Application\\ Support /etc \\\n'
      + '     -name com.pdfeditor.host.json 2>/dev/null \\\n'
      + '  | while read -r f; do echo "== $f"; grep -o "chrome-extension://[a-p]*" "$f"; done',
  };
}

// Removes the per-user registrations, so a stale one stops shadowing the package's system-wide
// manifest. Deliberately separate from re-registering: someone running the published extension
// against a correctly built package wants the stale file *gone*, not overwritten with the same ID.
function dropUserRegistrationStep(platform) {
  if (platform === 'windows') {
    return {
      text: 'Remove the per-user (HKCU) registrations, leaving the installer’s machine-wide ones '
        + 'in place (in PowerShell):',
      code: WINDOWS_HOST_DIR
        + 'powershell -NoProfile -ExecutionPolicy Bypass -File '
        + String.raw`"$hostDir\register-host.ps1" -Uninstall`,
    };
  }
  if (platform === 'linux') {
    return {
      text: 'Remove the per-user manifests, leaving the package’s system-wide one in place:',
      code: 'pdf-editor-host-register --uninstall',
    };
  }
  return {
    text: 'Remove the per-user manifests, leaving any system-wide one in place:',
    code: './scripts/register-host.sh --uninstall',
  };
}

// Where the host lives, so "run it yourself and see the error" is an instruction someone can follow.
function diagnoseStep(platform) {
  if (platform === 'windows') {
    return {
      text: 'Run the host yourself to see why it fails (in PowerShell):',
      code: WINDOWS_HOST_DIR
        + String.raw`& "$hostDir\PdfEditor.NativeHost.exe" --diagnostics`,
    };
  }
  if (platform === 'linux') {
    return {
      text: 'Run the host yourself to see why it fails:',
      code: 'pdf-editor-host --diagnostics',
    };
  }
  return {
    text: 'Run the host yourself to see why it fails:',
    code: '~/.local/share/pdf-editor-host/PdfEditor.NativeHost --diagnostics',
  };
}

/**
 * Turns a connection state into something a user can act on: a headline, an explanation of what
 * the state actually means, and ordered steps (each with an optional command to copy).
 *
 * @param {object} o
 * @param {string} o.state       one of {@link HOST_STATE}
 * @param {string} o.platform    from {@link detectPlatform}
 * @param {string} o.extensionId this extension's ID (`chrome.runtime.id`)
 * @param {string} [o.error]     the raw browser error, shown verbatim so bug reports carry it
 */
export function hostInstallGuide({ state, platform, extensionId = '', error = '' } = {}) {
  const guide = {
    state,
    headline: hostStateSummary(state),
    detail: '',
    steps: [],
    error,
    releasesUrl: RELEASES_URL,
  };

  switch (state) {
    case HOST_STATE.CONNECTED:
      guide.detail = 'Document processing is running locally. Nothing to do.';
      return guide;

    case HOST_STATE.MISSING:
      guide.detail = 'The browser found no native-messaging manifest for com.pdfeditor.host, so '
        + 'there is nothing to launch. Editing needs the host: it does the PDF work on your '
        + 'machine, and no document ever leaves it.';
      guide.steps = installSteps(platform);
      break;

    case HOST_STATE.FORBIDDEN:
      // Two different faults produce the identical browser message, and the usual advice is wrong
      // for one of them. If this extension is the pinned Web Store build, the OS package's own
      // manifest *does* list it -- so the manifest the browser actually read is a different,
      // staler one. Chromium reads the per-user directory in preference to the system-wide one,
      // so an earlier `install-host.sh <some-other-id>` keeps winning long after a correct
      // package is installed, and telling someone to "re-register, your ID is not the pinned
      // one" misdescribes their machine.
      if (extensionId && extensionId === PINNED_EXTENSION_ID) {
        guide.detail = 'This is the published build, and the OS packages pin their manifest to '
          + `exactly this ID (${extensionId}) — so the manifest the browser read is not the `
          + 'package’s. A per-user manifest from an earlier install takes precedence over the '
          + 'system-wide one and keeps being read instead, even after the package is reinstalled.';
        guide.steps = [
          listManifestsStep(platform),
          dropUserRegistrationStep(platform),
          { text: 'Quit this browser completely and start it again.' },
        ];
        break;
      }
      guide.detail = 'A host is registered, but the manifest’s allowed_origins does not list '
        + `this extension (${extensionId || 'unknown ID'}). The OS packages pin the published Web `
        + 'Store ID, so a developer-mode or self-built extension needs its own registration.';
      guide.steps = [
        reRegisterStep(platform, extensionId || '<your-extension-id>'),
        { text: 'Quit this browser completely and start it again.' },
      ];
      break;

    case HOST_STATE.CRASHED:
      guide.detail = 'The browser found the host and launched it, but the process exited before it '
        + 'said anything. That is usually a missing system library rather than a bad install — a '
        + 'self-contained .NET build still needs ICU and OpenSSL from the system.';
      guide.steps = [diagnoseStep(platform)];
      if (platform === 'linux') {
        guide.steps.push({
          text: 'If it reports a missing ICU package, install the runtime libraries:',
          code: 'sudo apt install libicu-dev libssl3      # Debian / Ubuntu\n'
            + 'sudo dnf install libicu openssl-libs     # Fedora / RHEL\n'
            + 'sudo pacman -S --needed icu openssl     # Arch',
        });
      }
      guide.steps.push({ text: 'Reinstall the host package, then restart this browser.' });
      break;

    default:
      guide.detail = 'The browser did not say why the connection failed. Reinstalling the host and '
        + 'restarting the browser fixes most cases.';
      guide.steps = installSteps(platform);
      break;
  }

  return guide;
}

/** The guide as plain text, for the copyable diagnostics blob and downloaded logs. */
export function hostInstallGuideLines(guide) {
  if (!guide) return [];
  const lines = [guide.headline];
  if (guide.detail) lines.push(guide.detail);
  guide.steps.forEach((step, i) => {
    lines.push(`${i + 1}. ${step.text}`);
    if (step.code) step.code.split('\n').forEach((l) => lines.push(`     ${l}`));
  });
  if (guide.error) lines.push(`Browser error: ${guide.error}`);
  return lines;
}
