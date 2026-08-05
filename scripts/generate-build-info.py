#!/usr/bin/env python3
"""Writes extension/build-info.json with the commit and build time for the About dialog (#103).

Run from package-extension.sh at package time. The file is intentionally not committed
(see .gitignore) — an unpacked development build has none, and the viewer degrades to
showing just the manifest version. The extension version itself is NOT recorded here:
manifest.json is the single source of truth for it and is read at runtime.
"""
import datetime
import json
import os
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_PATH = os.path.join(REPO_ROOT, "extension", "build-info.json")


def resolve_commit():
    # Prefer the SHA GitHub Actions provides; fall back to asking git directly (local builds).
    sha = os.environ.get("GITHUB_SHA", "").strip()
    if sha:
        return sha
    try:
        return subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=REPO_ROOT, stderr=subprocess.DEVNULL
        ).decode().strip()
    except (subprocess.CalledProcessError, OSError):
        return ""


def main():
    commit = resolve_commit()
    built_at = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    with open(OUT_PATH, "w") as f:
        json.dump({"commit": commit, "builtAt": built_at}, f, indent=2)
        f.write("\n")
    print(f"Wrote {OUT_PATH}: commit={commit[:12] or 'unknown'} builtAt={built_at}")
    if not commit:
        print("warning: no commit SHA available (not a git checkout and GITHUB_SHA unset)", file=sys.stderr)


if __name__ == "__main__":
    main()
