#!/usr/bin/env bash
# Runs once after the dev container is created. Picks whichever bootstrap script
# is more natural in a fresh container (PowerShell is installed via a Feature; falls
# back to Python if it's ever unavailable) -- both do the same setup.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -File ./scripts/bootstrap.ps1
else
  python3 ./scripts/bootstrap.py
fi
