"""Contract and local behavioral checks for the GHCR workflow's entry gate.

The expression probe deliberately supports only conjunctions of string equality
checks. It is not a general GitHub expression engine; actionlint validates the
workflow syntax, and GitHub remains the authority for job scheduling.
"""

from __future__ import annotations

import itertools
import os
import re
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

WORKFLOW_PATH = Path(__file__).resolve().parents[2] / ".github/workflows/publish-ghcr.yml"


class GhcrWorkflowGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = WORKFLOW_PATH.read_text(encoding="utf-8")
        self.validation_job = self.workflow.split("  validate-tested-push:\n", 1)[1].split(
            "\n  publish:\n", 1
        )[0]

    def test_only_successful_main_pushes_enter_validation(self) -> None:
        condition = re.search(
            r"^    if: >-\n((?:      .*\n)+)", self.validation_job, re.MULTILINE
        )
        if condition is None:
            self.fail("Validation needs a job-level eligibility condition")
        expression = " ".join(condition.group(1).split())
        self.assertTrue(expression.startswith("${{ ") and expression.endswith(" }}"))
        comparisons = []
        for clause in expression[4:-3].split("&&"):
            comparison = re.fullmatch(
                r"\s*github\.event\.workflow_run\.(event|head_branch|conclusion)"
                r"\s*==\s*'([^']*)'\s*",
                clause,
            )
            if comparison is None:
                self.fail(f"Unsupported eligibility clause: {clause}")
            comparisons.append(comparison.groups())

        for event, branch, conclusion in itertools.product(
            ("push", "pull_request", "workflow_dispatch", None),
            ("main", "docs/github-delivery-default", None),
            ("success", "failure", "cancelled", "skipped", "timed_out", None),
        ):
            with self.subTest(event=event, branch=branch, conclusion=conclusion):
                workflow_run = {"event": event, "head_branch": branch, "conclusion": conclusion}
                eligible = all(workflow_run[field] == expected for field, expected in comparisons)
                self.assertEqual(
                    eligible, event == "push" and branch == "main" and conclusion == "success"
                )

    def test_validation_still_fails_closed(self) -> None:
        script = textwrap.dedent(self.validation_job.split("        run: |\n", 1)[1])
        canonical = "clintonfrankland/mail-winnow"
        valid_environment = {
            "REPOSITORY": canonical,
            "RUN_CONCLUSION": "success",
            "RUN_EVENT": "push",
            "RUN_HEAD_BRANCH": "main",
            "RUN_HEAD_REPOSITORY": canonical,
            "RUN_HEAD_SHA": "a" * 40,
        }
        cases = (
            ({}, True),
            ({"REPOSITORY": "other/mail-winnow"}, False),
            ({"RUN_CONCLUSION": "failure"}, False),
            ({"RUN_EVENT": "pull_request"}, False),
            ({"RUN_HEAD_BRANCH": "feature"}, False),
            ({"RUN_HEAD_REPOSITORY": "other/mail-winnow"}, False),
            ({"RUN_HEAD_SHA": "invalid"}, False),
            ({"RUN_HEAD_SHA": "A" * 40}, False),
        )
        for overrides, expected_success in cases:
            with self.subTest(overrides=overrides), tempfile.TemporaryDirectory() as directory:
                output_path = Path(directory) / "output"
                result = subprocess.run(
                    ["bash", "-c", script],
                    env={**os.environ, **valid_environment, **overrides, "GITHUB_OUTPUT": str(output_path)},
                    capture_output=True,
                    text=True,
                    timeout=10,
                    check=False,
                )
                self.assertEqual(result.returncode == 0, expected_success, result.stderr)
                if expected_success:
                    self.assertEqual(output_path.read_text(), f"tested-revision={'a' * 40}\n")
                else:
                    self.assertIn("Refusing", result.stderr)
                    self.assertFalse(output_path.exists(), "Rejected runs must not emit a revision")

    def test_publishing_requires_successful_validation(self) -> None:
        publish_job = self.workflow.split("\n  publish:\n", 1)[1]
        self.assertRegex(publish_job, r"(?m)^    needs: validate-tested-push$")
        self.assertNotRegex(publish_job, r"(?m)^    if:")
        self.assertNotIn("continue-on-error:", self.workflow)
        self.assertIn("ref: ${{ needs.validate-tested-push.outputs.tested-revision }}", publish_job)


if __name__ == "__main__":
    unittest.main()
