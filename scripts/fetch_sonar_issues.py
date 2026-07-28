#!/usr/bin/env python3
"""Wait for a SonarQube Cloud analysis to finish, then download its issues as JSON.

The scanner's `end` step only *submits* the analysis -- SonarQube Cloud processes it
asynchronously on a Compute Engine task, so querying issues straight afterwards returns the
previous run's results (or none at all). The scanner leaves the task's URL in report-task.txt;
this polls that until the task settles, then pages through api/issues/search.

Standard library only, and every request is an explicit HTTPS GET with a bearer token -- no shell
interpolation, no `curl | sh`, nothing unpinned, in keeping with the supply-chain hardening in
issue #56.

Usage:
    SONAR_TOKEN=... fetch_sonar_issues.py --output issues.json [--pull-request 68]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from sonar_paths import safe_path

DEFAULT_REPORT_TASK = Path(".sonarqube/out/.sonar/report-task.txt")
PAGE_SIZE = 500          # api/issues/search maximum
MAX_ISSUES = 10_000      # the API refuses paging past this
SETTLED = {"SUCCESS", "FAILED", "CANCELED", "CANCELLED"}


def read_report_task(path: Path) -> dict[str, str]:
    """Parse the scanner's report-task.txt (key=value per line), searching if it moved."""
    if not path.exists():
        found = sorted(Path(".").rglob("report-task.txt"))
        if not found:
            raise SystemExit(f"error: {path} not found — did the scanner's `end` step run?")
        path = found[0]
        print(f"note: using {path}", file=sys.stderr)
    values: dict[str, str] = {}
    for line in safe_path(str(path), must_exist=True).read_text(encoding="utf-8").splitlines():
        key, sep, value = line.partition("=")
        if sep:
            values[key.strip()] = value.strip()
    return values


def get_json(url: str, token: str, timeout: int = 60) -> dict[str, Any]:
    request = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
    })
    if not url.lower().startswith("https://"):
        raise SystemExit(f"error: refusing to call a non-HTTPS URL: {url}")
    with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - https enforced
        return json.loads(response.read().decode("utf-8"))


def wait_for_task(task_url: str, token: str, timeout_seconds: int = 600) -> str:
    """Block until the Compute Engine task settles; returns its final status."""
    deadline = time.monotonic() + timeout_seconds
    delay = 3
    while True:
        status = str(get_json(task_url, token).get("task", {}).get("status", "")).upper()
        if status in SETTLED:
            return status
        if time.monotonic() >= deadline:
            raise SystemExit(f"error: analysis did not finish within {timeout_seconds}s "
                             f"(last status: {status or 'unknown'})")
        time.sleep(delay)
        delay = min(delay * 2, 30)  # back off rather than hammering the API


def fetch_issues(base_url: str, project_key: str, token: str,
                 pull_request: str | None, branch: str | None) -> list[dict[str, Any]]:
    """Page through every unresolved issue for the analysed PR or branch."""
    issues: list[dict[str, Any]] = []
    page = 1
    while True:
        query = {
            "componentKeys": project_key,
            "resolved": "false",
            "ps": str(PAGE_SIZE),
            "p": str(page),
        }
        if pull_request:
            query["pullRequest"] = pull_request
        elif branch:
            query["branch"] = branch
        url = f"{base_url.rstrip('/')}/api/issues/search?{urllib.parse.urlencode(query)}"
        payload = get_json(url, token)
        batch = payload.get("issues") or []
        issues.extend(batch)
        total = int(payload.get("total") or 0)
        if len(batch) < PAGE_SIZE or len(issues) >= min(total, MAX_ISSUES):
            return issues
        page += 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report-task", type=Path, default=DEFAULT_REPORT_TASK)
    parser.add_argument("--output", default="sonar-issues.json")
    parser.add_argument("--pull-request", default=None)
    parser.add_argument("--branch", default=None)
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args(argv)

    token = os.environ.get("SONAR_TOKEN")
    if not token:
        raise SystemExit("error: SONAR_TOKEN is not set")

    report = read_report_task(args.report_task)
    task_url = report.get("ceTaskUrl")
    base_url = report.get("serverUrl", "https://sonarcloud.io")
    project_key = report.get("projectKey")
    if not task_url or not project_key:
        raise SystemExit("error: report-task.txt has no ceTaskUrl/projectKey")

    print(f"Waiting for analysis of {project_key}…", file=sys.stderr)
    status = wait_for_task(task_url, token, args.timeout)
    if status != "SUCCESS":
        raise SystemExit(f"error: analysis finished with status {status}")

    issues = fetch_issues(base_url, project_key, token, args.pull_request, args.branch)
    safe_path(args.output).write_text(
        json.dumps({"issues": issues}, indent=2), encoding="utf-8")
    print(f"Fetched {len(issues)} unresolved issue(s) -> {args.output}", file=sys.stderr)

    # Hand the project key on so the converter can strip it off component keys.
    if step_output := os.environ.get("GITHUB_OUTPUT"):
        with open(step_output, "a", encoding="utf-8") as handle:
            handle.write(f"project-key={project_key}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
