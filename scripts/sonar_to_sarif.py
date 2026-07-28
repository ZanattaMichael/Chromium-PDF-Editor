#!/usr/bin/env python3
"""Convert SonarQube Cloud issues into a SARIF 2.1.0 report for GitHub code scanning.

SonarQube Cloud has no SARIF export of its own (`sonar.sarifReportPaths` is the opposite
direction -- importing external SARIF *into* Sonar), so findings are read back from its web API
and translated here. The result is uploaded with github/codeql-action/upload-sarif, which puts
each finding in the repository's Security tab and annotates the lines it touches on a pull
request -- so reviewers no longer have to leave GitHub to see what Sonar flagged.

Written as a script in this repository rather than pulled in as a marketplace action on purpose:
every action in these workflows is pinned by commit SHA, and issue #56 was largely about removing
unpinned `npx`/`curl` supply-chain exposure. A third-party converter would reintroduce exactly
that.

Usage:
    sonar_to_sarif.py --issues issues.json --project-key KEY --output sonar.sarif
    sonar_to_sarif.py --issues - --project-key KEY --output -      # stdin/stdout
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any, Iterable

SARIF_VERSION = "2.1.0"
SARIF_SCHEMA = "https://json.schemastore.org/sarif-2.1.0.json"

# Sonar severities -> SARIF levels. SARIF only has error/warning/note/none, so the two
# "must fix" Sonar bands collapse onto error and the two advisory ones onto note.
_LEVEL_BY_SEVERITY = {
    "BLOCKER": "error",
    "CRITICAL": "error",
    "MAJOR": "warning",
    "MINOR": "note",
    "INFO": "note",
}

# Newer Sonar payloads carry `impacts[].severity` instead of the legacy top-level `severity`.
_LEVEL_BY_IMPACT = {
    "BLOCKER": "error",
    "HIGH": "error",
    "MEDIUM": "warning",
    "LOW": "note",
    "INFO": "note",
}


def sarif_level(issue: dict[str, Any]) -> str:
    """The SARIF level for an issue, preferring the newer `impacts` shape when present."""
    for impact in issue.get("impacts") or []:
        level = _LEVEL_BY_IMPACT.get(str(impact.get("severity", "")).upper())
        if level:
            return level
    return _LEVEL_BY_SEVERITY.get(str(issue.get("severity", "")).upper(), "warning")


def relative_path(component: str, project_key: str) -> str:
    """Strip Sonar's `projectKey:` (or `projectKey:branch:`) prefix off a component key.

    Sonar identifies a file as "ZanattaMichael_Chromium-PDF-Editor:src/Foo.cs"; SARIF wants the
    repository-relative "src/Foo.cs" so GitHub can map the finding onto the diff.
    """
    prefix = f"{project_key}:"
    if component.startswith(prefix):
        return component[len(prefix):]
    # Fall back to the last colon-separated segment, which is the path for every shape Sonar
    # currently emits. Paths themselves never contain a colon in this repository.
    return component.rsplit(":", 1)[-1] if ":" in component else component


def region(issue: dict[str, Any]) -> dict[str, Any] | None:
    """SARIF region for an issue, or None when Sonar reported no position.

    SARIF columns are 1-based while Sonar's textRange offsets are 0-based, so every offset is
    shifted by one. An `endColumn` that would land before `startColumn` is dropped rather than
    emitted invalid.
    """
    text_range = issue.get("textRange") or {}
    start_line = text_range.get("startLine") or issue.get("line")
    if not start_line:
        return None

    result: dict[str, Any] = {"startLine": int(start_line)}
    end_line = text_range.get("endLine")
    if end_line:
        result["endLine"] = max(int(end_line), result["startLine"])

    start_offset = text_range.get("startOffset")
    if start_offset is not None:
        result["startColumn"] = int(start_offset) + 1
    end_offset = text_range.get("endOffset")
    if end_offset is not None:
        end_column = int(end_offset) + 1
        # Only meaningful when the range is on one line, or the end is on a later line.
        if result.get("endLine", result["startLine"]) > result["startLine"] \
                or end_column >= result.get("startColumn", 1):
            result["endColumn"] = end_column
    return result


def rule_help_uri(rule_key: str, organization: str | None) -> str:
    """Deep link to the rule's description."""
    if organization:
        return (f"https://sonarcloud.io/organizations/{organization}/rules"
                f"?open={rule_key}&rule_key={rule_key}")
    return f"https://rules.sonarsource.com/?search={rule_key}"


def convert(issues: Iterable[dict[str, Any]], project_key: str,
            organization: str | None = None) -> dict[str, Any]:
    """Build a SARIF 2.1.0 document from Sonar issues."""
    rule_index: dict[str, int] = {}
    rules: list[dict[str, Any]] = []
    results: list[dict[str, Any]] = []

    for issue in issues:
        rule_key = issue.get("rule")
        component = issue.get("component") or ""
        if not rule_key or not component:
            continue  # project-level finding with nothing to anchor to in the diff

        path = relative_path(component, project_key)
        if not path or path.endswith("/"):
            continue  # a directory/module component, not a file

        if rule_key not in rule_index:
            rule_index[rule_key] = len(rules)
            rules.append({
                "id": rule_key,
                "name": rule_key,
                "shortDescription": {"text": issue.get("message") or rule_key},
                "helpUri": rule_help_uri(rule_key, organization),
                "properties": {
                    "tags": [t for t in [issue.get("type")] if t],
                },
            })

        physical: dict[str, Any] = {"artifactLocation": {"uri": path}}
        issue_region = region(issue)
        if issue_region:
            physical["region"] = issue_region

        result: dict[str, Any] = {
            "ruleId": rule_key,
            "ruleIndex": rule_index[rule_key],
            "level": sarif_level(issue),
            "message": {"text": issue.get("message") or rule_key},
            "locations": [{"physicalLocation": physical}],
        }
        # Stable identity so GitHub can track a finding across runs rather than re-raising it.
        if issue.get("hash"):
            result["partialFingerprints"] = {"sonarIssueHash": issue["hash"]}
        results.append(result)

    return {
        "$schema": SARIF_SCHEMA,
        "version": SARIF_VERSION,
        "runs": [{
            "tool": {"driver": {
                "name": "SonarQube Cloud",
                "informationUri": "https://sonarcloud.io",
                "rules": rules,
            }},
            "results": results,
        }],
    }


def load_issues(raw: str) -> list[dict[str, Any]]:
    """Accept either a raw api/issues/search response or a bare list of issues."""
    data = json.loads(raw)
    if isinstance(data, dict):
        return list(data.get("issues") or [])
    return list(data)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--issues", required=True,
                        help="JSON file of Sonar issues, or - for stdin")
    parser.add_argument("--project-key", required=True)
    parser.add_argument("--organization", default=None)
    parser.add_argument("--output", default="-", help="SARIF output path, or - for stdout")
    args = parser.parse_args(argv)

    raw = sys.stdin.read() if args.issues == "-" else open(args.issues, encoding="utf-8").read()
    sarif = convert(load_issues(raw), args.project_key, args.organization)
    rendered = json.dumps(sarif, indent=2)

    if args.output == "-":
        sys.stdout.write(rendered + "\n")
    else:
        with open(args.output, "w", encoding="utf-8") as handle:
            handle.write(rendered + "\n")
    count = len(sarif["runs"][0]["results"])
    print(f"Wrote {count} finding{'' if count == 1 else 's'} to {args.output}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
