#!/usr/bin/env python3
"""Path validation shared by the Sonar helper scripts.

Lives in its own module because both fetch_sonar_issues.py and sonar_to_sarif.py need it, and
carrying a second copy tripped Sonar's duplication gate on the very pull request that introduced
the scan. Both scripts are run as `python3 scripts/<name>.py`, which puts this directory on
sys.path, so a plain `from sonar_paths import safe_path` resolves.
"""

from __future__ import annotations

from pathlib import Path


def safe_path(value: str, *, must_exist: bool = False) -> Path:
    """Resolve a caller-supplied path, refusing anything outside the working tree.

    These scripts only ever read and write artefacts inside the checkout, so a path that escapes
    it (`../../etc/passwd`) is always a mistake or an attack rather than a legitimate use. Anchor
    to the working directory and reject the rest, instead of trusting whatever the CLI was given.
    """
    base = Path.cwd().resolve()
    candidate = Path(value)
    candidate = candidate.resolve() if candidate.is_absolute() else (base / candidate).resolve()
    if not candidate.is_relative_to(base):
        raise SystemExit(f"error: refusing a path outside {base}: {value}")
    if must_exist and not candidate.is_file():
        raise SystemExit(f"error: no such file: {value}")
    return candidate
