"""Regression coverage for the GHCR release helpers used by GitHub Actions."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS_DIRECTORY = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = SCRIPTS_DIRECTORY.parent
sys.path.insert(0, str(SCRIPTS_DIRECTORY))

from ghcr_release import COMPONENTS, record_build_digests  # noqa: E402

VERIFY_SCRIPT = SCRIPTS_DIRECTORY / "verify-ghcr-tag-state.py"
RELEASE_SCRIPT = SCRIPTS_DIRECTORY / "ghcr_release.py"
FIXTURES_DIRECTORY = Path(__file__).resolve().parent / "fixtures"
VERSION = "1.19.0"
REVISION = "a" * 40
SOURCE = "https://github.com/clintonfrankland/mail-winnow"
REAL_FLAT_REVISION = "9cfd75beaee655267f29224201f6d749c9203eff"


def digest(seed: str) -> str:
    return f"sha256:{seed * 64}"


def inspection(manifest_digest: str, revision: str = REVISION) -> dict[str, object]:
    return {
        "manifest": {"digest": manifest_digest},
        "image": {
            "linux/amd64": {
                "config": {
                    "Labels": {
                        "org.opencontainers.image.source": SOURCE,
                        "org.opencontainers.image.version": VERSION,
                        "org.opencontainers.image.revision": revision,
                    }
                }
            }
        },
    }


def read_fixture(name: str) -> dict[str, object]:
    return json.loads((FIXTURES_DIRECTORY / name).read_text(encoding="utf-8"))


class GhcrPublicationTests(unittest.TestCase):
    def run_version_reader(self, project_path: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(RELEASE_SCRIPT), "read-version", "--project", str(project_path)],
            capture_output=True,
            text=True,
            check=False,
        )

    def run_validator(
        self,
        state_directory: Path,
        *extra_arguments: str,
        version: str = VERSION,
        revision: str = REVISION,
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                sys.executable,
                str(VERIFY_SCRIPT),
                "--state-directory",
                str(state_directory),
                "--version",
                version,
                "--revision",
                revision,
                "--source",
                SOURCE,
                *extra_arguments,
            ],
            capture_output=True,
            text=True,
            check=False,
        )

    def write_project(self, directory: Path, version: str) -> Path:
        project_path = directory / "MailWinnow.Web.csproj"
        project_path.write_text(
            f"<Project><PropertyGroup><VersionPrefix>{version}</VersionPrefix>"
            "</PropertyGroup></Project>",
            encoding="utf-8",
        )
        return project_path

    def write_complete_state(
        self, state_directory: Path, state_digest: str, revision: str = REVISION
    ) -> None:
        state_directory.mkdir()
        for component in COMPONENTS:
            for tag in (VERSION, REVISION):
                (state_directory / f"{component}-{tag}.json").write_text(
                    json.dumps(inspection(state_digest, revision)), encoding="utf-8"
                )

    def write_complete_fixture_state(
        self, state_directory: Path, fixture_name: str, version: str, revision: str
    ) -> None:
        fixture = read_fixture(fixture_name)
        state_directory.mkdir()
        for component in COMPONENTS:
            for tag in (version, revision):
                (state_directory / f"{component}-{tag}.json").write_text(
                    json.dumps(fixture), encoding="utf-8"
                )

    def assert_fixture_supports_preflight_and_post_push_verification(
        self, fixture_name: str, version: str = VERSION, revision: str = REVISION
    ) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            state_directory = temporary_path / "state"
            expected_directory = temporary_path / "expected"
            self.write_complete_fixture_state(
                state_directory, fixture_name, version, revision
            )

            preflight = self.run_validator(
                state_directory,
                "--write-expected-digests",
                str(expected_directory),
                version=version,
                revision=revision,
            )
            post_push = self.run_validator(
                state_directory,
                "--expected-digest-directory",
                str(expected_directory),
                version=version,
                revision=revision,
            )

        self.assertEqual(preflight.returncode, 0, preflight.stderr)
        self.assertIn("publish=false", preflight.stdout)
        self.assertEqual(post_push.returncode, 0, post_push.stderr)
        self.assertIn("publish=false", post_push.stdout)

    def test_workflow_uses_the_shared_version_reader(self) -> None:
        workflow = (REPOSITORY_ROOT / ".github/workflows/publish-ghcr.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("python3 scripts/ghcr_release.py read-version", workflow)

    def test_version_reader_accepts_strict_semantic_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = self.run_version_reader(
                self.write_project(Path(temporary_directory), VERSION)
            )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), VERSION)

    def test_version_reader_rejects_malformed_versions(self) -> None:
        malformed_versions = ("01.19.0", "1.19.0-beta", "1.19.0.124", "1.19", "1.19.00")
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            for malformed_version in malformed_versions:
                with self.subTest(version=malformed_version):
                    result = self.run_version_reader(
                        self.write_project(temporary_path, malformed_version)
                    )
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("strict MAJOR.MINOR.PATCH", result.stderr)

    def test_version_reader_rejects_unicode_digits_in_each_segment(self) -> None:
        malformed_versions = ("1\u0661.19.0", "1.1\u0661.0", "1.19.0\u0661")
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            for malformed_version in malformed_versions:
                with self.subTest(version=malformed_version):
                    result = self.run_version_reader(
                        self.write_project(temporary_path, malformed_version)
                    )
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("strict MAJOR.MINOR.PATCH", result.stderr)

    def test_version_reader_accepts_multi_digit_ascii_version(self) -> None:
        ascii_version = "12.34.56"
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = self.run_version_reader(
                self.write_project(Path(temporary_directory), ascii_version)
            )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), ascii_version)

    def test_all_absent_tags_allow_publication(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = self.run_validator(Path(temporary_directory))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("publish=true", result.stdout)

    def test_complete_rerun_records_and_accepts_preflight_digests(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            state_directory = temporary_path / "state"
            expected_directory = temporary_path / "expected"
            expected_digest = digest("b")
            self.write_complete_state(state_directory, expected_digest)

            preflight = self.run_validator(
                state_directory,
                "--write-expected-digests",
                str(expected_directory),
            )
            rerun = self.run_validator(
                state_directory,
                "--expected-digest-directory",
                str(expected_directory),
            )

        self.assertEqual(preflight.returncode, 0, preflight.stderr)
        self.assertIn("publish=false", preflight.stdout)
        self.assertEqual(rerun.returncode, 0, rerun.stderr)
        self.assertIn("publish=false", rerun.stdout)

    def test_real_flat_linux_amd64_fixture_supports_preflight_and_post_push_verification(
        self,
    ) -> None:
        self.assert_fixture_supports_preflight_and_post_push_verification(
            "ghcr-imagetools-flat-linux-amd64.json",
            revision=REAL_FLAT_REVISION,
        )

    def test_indexed_linux_amd64_fixture_remains_supported(self) -> None:
        self.assert_fixture_supports_preflight_and_post_push_verification(
            "ghcr-imagetools-indexed-linux-amd64.json"
        )

    def test_flat_inspection_rejects_non_linux_amd64_platform(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            state_directory = temporary_path / "state"
            fixture = read_fixture("ghcr-imagetools-flat-linux-amd64.json")
            fixture["image"]["architecture"] = "arm64"  # type: ignore[index]
            state_directory.mkdir()
            for component in COMPONENTS:
                for tag in (VERSION, REAL_FLAT_REVISION):
                    (state_directory / f"{component}-{tag}.json").write_text(
                        json.dumps(fixture), encoding="utf-8"
                    )
            result = self.run_validator(
                state_directory, revision=REAL_FLAT_REVISION
            )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("usable linux/amd64 imagetools inspection", result.stderr)

    def test_agreeing_tags_with_an_unexpected_digest_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            state_directory = temporary_path / "state"
            expected_directory = temporary_path / "expected"
            expected_directory.mkdir()
            for component in COMPONENTS:
                (expected_directory / f"{component}.digest").write_text(
                    f"{digest('b')}\n", encoding="utf-8"
                )
            self.write_complete_state(state_directory, digest("c"))
            result = self.run_validator(
                state_directory,
                "--expected-digest-directory",
                str(expected_directory),
            )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("expected independently captured digest", result.stderr)

    def test_partial_tag_state_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            state_directory = Path(temporary_directory) / "state"
            state_directory.mkdir()
            (state_directory / f"web-{VERSION}.json").write_text(
                json.dumps(inspection(digest("b"))), encoding="utf-8"
            )
            result = self.run_validator(state_directory)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("partial tag state", result.stderr)

    def test_semantic_tag_collision_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            state_directory = Path(temporary_directory) / "state"
            self.write_complete_state(state_directory, digest("b"))
            for component in COMPONENTS:
                (state_directory / f"{component}-{VERSION}.json").write_text(
                    json.dumps(inspection(digest("b"), revision="d" * 40)),
                    encoding="utf-8",
                )
            result = self.run_validator(state_directory)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("org.opencontainers.image.revision", result.stderr)

    def test_validator_rejects_malformed_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = subprocess.run(
                [
                    sys.executable,
                    str(VERIFY_SCRIPT),
                    "--state-directory",
                    temporary_directory,
                    "--version",
                    "1.19.0.124",
                    "--revision",
                    REVISION,
                    "--source",
                    SOURCE,
                ],
                capture_output=True,
                text=True,
                check=False,
            )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("strict MAJOR.MINOR.PATCH", result.stderr)

    def test_validator_rejects_unicode_digits_in_each_segment(self) -> None:
        malformed_versions = ("1\u0661.19.0", "1.1\u0661.0", "1.19.0\u0661")
        with tempfile.TemporaryDirectory() as temporary_directory:
            state_directory = Path(temporary_directory)
            for malformed_version in malformed_versions:
                with self.subTest(version=malformed_version):
                    result = self.run_validator(
                        state_directory, version=malformed_version
                    )
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("strict MAJOR.MINOR.PATCH", result.stderr)

    def test_validator_accepts_multi_digit_ascii_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = self.run_validator(Path(temporary_directory), version="12.34.56")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("publish=true", result.stdout)

    def test_buildx_metadata_digests_become_expected_digests(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            metadata_directory = temporary_path / "metadata"
            expected_directory = temporary_path / "expected"
            metadata_directory.mkdir()
            for component in COMPONENTS:
                (metadata_directory / f"{component}.json").write_text(
                    json.dumps({"containerimage.digest": digest("b")}), encoding="utf-8"
                )

            record_build_digests(metadata_directory, expected_directory)
            recorded_digests = {
                (expected_directory / f"{component}.digest").read_text(
                    encoding="utf-8"
                ).strip()
                for component in COMPONENTS
            }

        self.assertEqual(recorded_digests, {digest("b")})


if __name__ == "__main__":
    unittest.main()
