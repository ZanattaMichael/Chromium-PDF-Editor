#!/usr/bin/env bash
# Runs the .NET test suites with code coverage collection and generates an HTML report.
# Usage: ./scripts/coverage.sh [minimum-line-coverage-percent] [-- extra dotnet-test args]
# Example: ./scripts/coverage.sh 90 --configuration Release
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_DIR="$REPO_ROOT/coverage-results"
REPORT_DIR="$REPO_ROOT/coverage-report"
MIN_COVERAGE="${1:-0}"
shift || true
EXTRA_ARGS=("$@")

rm -rf "$RESULTS_DIR" "$REPORT_DIR"

echo "Running tests with coverage collection..."
dotnet test "$REPO_ROOT/tests/PdfEditor.Core.Tests" \
  --collect:"XPlat Code Coverage" --results-directory "$RESULTS_DIR" --nologo -v q "${EXTRA_ARGS[@]}"
dotnet test "$REPO_ROOT/tests/PdfEditor.NativeHost.Tests" \
  --collect:"XPlat Code Coverage" --results-directory "$RESULTS_DIR" --nologo -v q "${EXTRA_ARGS[@]}"
dotnet test "$REPO_ROOT/tests/PdfEditor.IntegrationTests" \
  --collect:"XPlat Code Coverage" --results-directory "$RESULTS_DIR" --nologo -v q "${EXTRA_ARGS[@]}"

echo "Generating report..."
cd "$REPO_ROOT"
dotnet tool restore --tool-manifest .config/dotnet-tools.json >/dev/null
dotnet tool run reportgenerator \
  -reports:"$RESULTS_DIR/**/coverage.cobertura.xml" \
  -targetdir:"$REPORT_DIR" \
  -reporttypes:"Html;TextSummary" \
  -classfilters:"-PdfEditor.NativeHost.Program"

cat "$REPORT_DIR/Summary.txt"
echo
echo "Full HTML report: $REPORT_DIR/index.html"

if [[ "$MIN_COVERAGE" != "0" ]]; then
  actual=$(grep -oP 'Line coverage:\s*\K[0-9.]+' "$REPORT_DIR/Summary.txt")
  if (( $(echo "$actual < $MIN_COVERAGE" | bc -l) )); then
    echo "FAIL: line coverage ${actual}% is below the required ${MIN_COVERAGE}%" >&2
    exit 1
  fi
  echo "OK: line coverage ${actual}% meets the required ${MIN_COVERAGE}%"
fi
