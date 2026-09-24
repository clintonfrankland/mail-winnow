#!/usr/bin/env python3
"""Fail closed unless GHCR tags are absent or a complete verified rerun state."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from ghcr_release import COMPONENTS, DIGEST_PATTERN, REVISION_PATTERN, VERSION_PATTERN


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate the six expected GHCR tags before or after publication."
    )
    parser.add_argument("--state-directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument(
        "--expected-digest-directory",
        type=Path,
        help="directory containing one expected manifest digest per component",
    )
    parser.add_argument(
        "--write-expected-digests",
        type=Path,
        help="record complete preflight manifest digests for a no-op rerun",
    )
    return parser.parse_args()


def read_inspection(path: Path) -> tuple[str, dict[str, str]]:
    try:
        inspection = json.loads(path.read_text(encoding="utf-8"))
        digest = inspection["manifest"]["digest"]
        labels = inspection["image"]["linux/amd64"]["config"].get("Labels", {})
    except (KeyError, TypeError, json.JSONDecodeError) as error:
        raise ValueError(f"{path.name} is not a usable linux/amd64 imagetools inspection") from error

    if not isinstance(digest, str) or not digest.startswith("sha256:"):
        raise ValueError(f"{path.name} has no manifest digest")
    if not isinstance(labels, dict) or not all(
        isinstance(key, str) and isinstance(value, str) for key, value in labels.items()
    ):
        raise ValueError(f"{path.name} has invalid OCI labels")

    return digest, labels


def fail(message: str) -> None:
    print(f"GHCR tag-state validation failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def read_expected_digest(expected_digest_directory: Path, component: str) -> str:
    expected_path = expected_digest_directory / f"{component}.digest"
    try:
        digest = expected_path.read_text(encoding="utf-8").strip()
    except OSError as error:
        raise ValueError(f"missing expected digest file {expected_path}") from error
    if DIGEST_PATTERN.fullmatch(digest) is None:
        raise ValueError(f"{expected_path} has an invalid expected manifest digest")
    return digest


def main() -> None:
    arguments = parse_arguments()
    if not VERSION_PATTERN.fullmatch(arguments.version):
        fail(f"version {arguments.version!r} is not strict MAJOR.MINOR.PATCH")
    if not REVISION_PATTERN.fullmatch(arguments.revision):
        fail("revision is not a lowercase 40-character Git SHA")

    tags = (arguments.version, arguments.revision)
    inspection_paths = {
        (component, tag): arguments.state_directory / f"{component}-{tag}.json"
        for component in COMPONENTS
        for tag in tags
    }
    present = {
        key: path.exists()
        for key, path in inspection_paths.items()
    }

    if not any(present.values()):
        print("publish=true")
        print("All six expected GHCR tags are absent; publication may proceed.", file=sys.stderr)
        return

    if not all(present.values()):
        missing = ", ".join(
            f"{component}:{tag}"
            for (component, tag), exists in present.items()
            if not exists
        )
        fail(f"partial tag state; missing {missing}")

    expected_labels = {
        "org.opencontainers.image.source": arguments.source,
        "org.opencontainers.image.version": arguments.version,
        "org.opencontainers.image.revision": arguments.revision,
    }
    verified_digests: dict[str, str] = {}
    for component in COMPONENTS:
        semantic_digest, semantic_labels = read_inspection(
            inspection_paths[(component, arguments.version)]
        )
        revision_digest, revision_labels = read_inspection(
            inspection_paths[(component, arguments.revision)]
        )
        if semantic_digest != revision_digest:
            fail(f"{component} semantic and revision tags resolve to different digests")
        if arguments.expected_digest_directory is not None:
            expected_digest: str | None = None
            try:
                expected_digest = read_expected_digest(
                    arguments.expected_digest_directory, component
                )
            except ValueError as error:
                fail(str(error))
            if expected_digest is None:
                fail(f"missing expected digest for {component}")
            if semantic_digest != expected_digest:
                fail(
                    f"{component} tags resolve to {semantic_digest}; "
                    f"expected independently captured digest {expected_digest}"
                )
        for tag, labels in (
            (arguments.version, semantic_labels),
            (arguments.revision, revision_labels),
        ):
            for label, expected_value in expected_labels.items():
                actual_value = labels.get(label)
                if actual_value != expected_value:
                    fail(
                        f"{component}:{tag} has {label}={actual_value!r}; "
                        f"expected {expected_value!r}"
                    )
        verified_digests[component] = semantic_digest
        print(f"Verified {component}: {semantic_digest}", file=sys.stderr)

    if arguments.write_expected_digests is not None:
        arguments.write_expected_digests.mkdir(parents=True, exist_ok=True)
        for component, digest in verified_digests.items():
            (arguments.write_expected_digests / f"{component}.digest").write_text(
                f"{digest}\n", encoding="utf-8"
            )

    print("publish=false")
    print(
        "All six expected GHCR tags already resolve consistently with required OCI labels.",
        file=sys.stderr,
    )


if __name__ == "__main__":
    main()
