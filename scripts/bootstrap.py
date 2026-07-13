#!/usr/bin/env python3
"""Bootstraps a local development environment for PDF Editor.

Restores .NET dependencies and pinned tools, generates the extension icons, and
(optionally) installs the Playwright end-to-end test dependencies.

Usage:
    python3 scripts/bootstrap.py [--skip-e2e]

Equivalent to scripts/bootstrap.ps1 for contributors who prefer PowerShell — run
whichever matches your platform/taste, they do the same thing.
"""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


def section(title: str) -> None:
    print(f"\n== {title} ==")


def run(command, cwd=None, required_tool=None) -> bool:
    """Runs a command, exiting the script on failure. Returns False without
    running anything if required_tool isn't on PATH (used for optional steps)."""
    if required_tool and shutil.which(required_tool) is None:
        print(f"  skipped ({required_tool} not found on PATH)")
        return False
    print(f"$ {' '.join(str(c) for c in command)}" + (f"   (in {cwd})" if cwd else ""))
    result = subprocess.run(command, cwd=cwd or REPO_ROOT)
    if result.returncode != 0:
        print(f"error: command failed with exit code {result.returncode}: "
              f"{' '.join(str(c) for c in command)}", file=sys.stderr)
        sys.exit(result.returncode)
    return True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__,
                                      formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip-e2e", action="store_true",
                         help="Skip installing e2e (Playwright) dependencies.")
    args = parser.parse_args()

    print("PDF Editor -- development environment bootstrap")

    section("Checking prerequisites")
    if shutil.which("dotnet") is None:
        print("error: 'dotnet' was not found on PATH.", file=sys.stderr)
        print("Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0",
              file=sys.stderr)
        sys.exit(1)
    dotnet_version = subprocess.run(["dotnet", "--version"], cwd=REPO_ROOT,
                                     capture_output=True, text=True).stdout.strip()
    print(f".NET SDK {dotnet_version} found.")

    section("Restoring .NET packages")
    run(["dotnet", "restore"])

    section("Restoring pinned .NET tools (reportgenerator)")
    run(["dotnet", "tool", "restore"])

    section("Generating extension icons")
    # sys.executable, not a hardcoded "python3"/"python": this script is already
    # running under some Python interpreter, so reuse exactly that one.
    run([sys.executable, "scripts/generate-icons.py"])

    if args.skip_e2e:
        section("Skipping e2e (Playwright) setup (--skip-e2e)")
    else:
        section("Installing e2e test dependencies")
        installed = run(["npm", "install"], cwd=REPO_ROOT / "e2e", required_tool="npm")
        if installed:
            section("Installing Playwright's Chromium build")
            run(["npx", "playwright", "install", "--with-deps", "chromium"], cwd=REPO_ROOT / "e2e")
        else:
            print("Node.js/npm not found -- skipping e2e setup. Install Node 22+ to run "
                  "the Playwright suite (see the e2e/ section of the README).")

    section("Done")
    print("""
Next steps:
  dotnet test                     # run the .NET unit + integration suite
  ./scripts/coverage.sh           # run tests with coverage, print a summary
  cd e2e && npx playwright test   # run the browser end-to-end suite
  ./scripts/package-extension.sh  # build a Chrome Web Store-ready zip

See CONTRIBUTING.md for the full development workflow.""")


if __name__ == "__main__":
    main()
