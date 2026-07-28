#!/usr/bin/env python3
"""Tests for the Sonar -> SARIF conversion (run: python3 scripts/test_sonar_to_sarif.py).

The end-to-end path (real SonarQube Cloud API -> upload-sarif) can only be exercised in CI with a
token, so these pin down the translation itself: the parts that are easy to get quietly wrong are
the component-key prefix, the 0-based -> 1-based column shift, and the severity mapping.
"""

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sonar_to_sarif import (convert, load_issues, region, relative_path,  # noqa: E402
                            safe_path, sarif_level)

PROJECT = "ZanattaMichael_Chromium-PDF-Editor"


class RelativePathTests(unittest.TestCase):
    def test_strips_the_project_key_prefix(self):
        component = f"{PROJECT}:src/PdfEditor.Core/OcrTool.cs"
        self.assertEqual(relative_path(component, PROJECT), "src/PdfEditor.Core/OcrTool.cs")

    def test_leaves_an_already_relative_path_alone(self):
        self.assertEqual(relative_path("src/Foo.cs", PROJECT), "src/Foo.cs")

    def test_falls_back_to_the_last_segment_for_an_unexpected_prefix(self):
        self.assertEqual(relative_path("other:src/Foo.cs", PROJECT), "src/Foo.cs")


class RegionTests(unittest.TestCase):
    def test_converts_zero_based_offsets_to_one_based_columns(self):
        # Sonar's startOffset 4 is SARIF's startColumn 5 — off-by-one here silently mis-anchors
        # every annotation by one character.
        issue = {"textRange": {"startLine": 42, "endLine": 42, "startOffset": 4, "endOffset": 10}}
        self.assertEqual(region(issue),
                         {"startLine": 42, "endLine": 42, "startColumn": 5, "endColumn": 11})

    def test_falls_back_to_the_bare_line_when_there_is_no_text_range(self):
        self.assertEqual(region({"line": 7}), {"startLine": 7})

    def test_returns_none_when_there_is_no_position_at_all(self):
        self.assertIsNone(region({}))

    def test_drops_an_end_column_that_would_precede_the_start(self):
        issue = {"textRange": {"startLine": 3, "endLine": 3, "startOffset": 20, "endOffset": 2}}
        self.assertNotIn("endColumn", region(issue))

    def test_keeps_an_earlier_end_column_when_the_range_spans_lines(self):
        issue = {"textRange": {"startLine": 3, "endLine": 5, "startOffset": 20, "endOffset": 2}}
        self.assertEqual(region(issue)["endColumn"], 3)


class LevelTests(unittest.TestCase):
    def test_maps_legacy_severities(self):
        for severity, expected in [("BLOCKER", "error"), ("CRITICAL", "error"),
                                   ("MAJOR", "warning"), ("MINOR", "note"), ("INFO", "note")]:
            self.assertEqual(sarif_level({"severity": severity}), expected, severity)

    def test_prefers_the_newer_impacts_shape(self):
        issue = {"severity": "INFO", "impacts": [{"severity": "HIGH"}]}
        self.assertEqual(sarif_level(issue), "error")

    def test_defaults_to_warning_for_an_unknown_severity(self):
        self.assertEqual(sarif_level({"severity": "WEIRD"}), "warning")


class ConvertTests(unittest.TestCase):
    def sample(self):
        return [
            {
                "rule": "csharpsquid:S1118", "severity": "MAJOR", "type": "CODE_SMELL",
                "component": f"{PROJECT}:src/PdfEditor.Core/OcrTool.cs",
                "line": 42, "hash": "abc123",
                "textRange": {"startLine": 42, "endLine": 42, "startOffset": 4, "endOffset": 10},
                "message": "Add a private constructor.",
            },
            {
                "rule": "javascript:S3776", "severity": "CRITICAL", "type": "CODE_SMELL",
                "component": f"{PROJECT}:extension/src/viewer.js",
                "line": 10, "message": "Refactor this function.",
            },
            {  # same rule again — must reuse the rule entry, not duplicate it
                "rule": "csharpsquid:S1118", "severity": "MAJOR",
                "component": f"{PROJECT}:src/PdfEditor.Core/FormTools.cs",
                "line": 9, "message": "Add a private constructor.",
            },
        ]

    def test_produces_a_well_formed_sarif_document(self):
        sarif = convert(self.sample(), PROJECT, organization="zanattamichael")
        self.assertEqual(sarif["version"], "2.1.0")
        self.assertEqual(len(sarif["runs"]), 1)
        run = sarif["runs"][0]
        self.assertEqual(run["tool"]["driver"]["name"], "SonarQube Cloud")
        self.assertEqual(len(run["results"]), 3)

    def test_deduplicates_rules_and_keeps_rule_index_consistent(self):
        run = convert(self.sample(), PROJECT)["runs"][0]
        rules = run["tool"]["driver"]["rules"]
        self.assertEqual([r["id"] for r in rules], ["csharpsquid:S1118", "javascript:S3776"])
        for result in run["results"]:
            self.assertEqual(rules[result["ruleIndex"]]["id"], result["ruleId"])

    def test_maps_locations_onto_repository_relative_paths(self):
        run = convert(self.sample(), PROJECT)["runs"][0]
        uris = [r["locations"][0]["physicalLocation"]["artifactLocation"]["uri"]
                for r in run["results"]]
        self.assertEqual(uris, ["src/PdfEditor.Core/OcrTool.cs", "extension/src/viewer.js",
                                "src/PdfEditor.Core/FormTools.cs"])

    def test_carries_a_fingerprint_when_sonar_supplies_a_hash(self):
        run = convert(self.sample(), PROJECT)["runs"][0]
        self.assertEqual(run["results"][0]["partialFingerprints"],
                         {"sonarIssueHash": "abc123"})
        self.assertNotIn("partialFingerprints", run["results"][1])

    def test_skips_findings_with_nothing_to_anchor_to(self):
        issues = [
            {"rule": "csharpsquid:S1", "component": ""},            # project-level
            {"rule": "", "component": f"{PROJECT}:src/Foo.cs"},      # no rule
            {"rule": "csharpsquid:S2", "component": f"{PROJECT}:src/"},  # a directory
        ]
        self.assertEqual(convert(issues, PROJECT)["runs"][0]["results"], [])

    def test_every_result_has_a_location_github_can_place(self):
        # upload-sarif rejects a result with no physical location, so this must hold for all.
        run = convert(self.sample(), PROJECT)["runs"][0]
        for result in run["results"]:
            location = result["locations"][0]["physicalLocation"]
            self.assertTrue(location["artifactLocation"]["uri"])
            self.assertGreaterEqual(location["region"]["startLine"], 1)
            self.assertGreaterEqual(location["region"].get("startColumn", 1), 1)

    def test_is_json_serialisable(self):
        json.dumps(convert(self.sample(), PROJECT))


class SafePathTests(unittest.TestCase):
    def test_accepts_a_path_inside_the_working_tree(self):
        self.assertEqual(safe_path("sonar.sarif"), Path.cwd().resolve() / "sonar.sarif")

    def test_rejects_a_traversal_out_of_the_working_tree(self):
        with self.assertRaises(SystemExit):
            safe_path("../../etc/passwd")

    def test_rejects_an_absolute_path_elsewhere(self):
        with self.assertRaises(SystemExit):
            safe_path("/etc/passwd")

    def test_rejects_a_missing_file_when_existence_is_required(self):
        with self.assertRaises(SystemExit):
            safe_path("definitely-not-here.json", must_exist=True)

    def test_accepts_a_real_file_when_existence_is_required(self):
        self.assertTrue(safe_path(__file__, must_exist=True).is_file())


class LoadIssuesTests(unittest.TestCase):
    def test_accepts_a_full_api_response(self):
        raw = json.dumps({"total": 1, "issues": [{"rule": "r", "component": "c"}]})
        self.assertEqual(len(load_issues(raw)), 1)

    def test_accepts_a_bare_list(self):
        self.assertEqual(len(load_issues('[{"rule": "r"}]')), 1)

    def test_handles_a_response_with_no_issues(self):
        self.assertEqual(load_issues('{"total": 0}'), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
