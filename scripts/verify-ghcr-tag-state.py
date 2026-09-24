#!/usr/bin/env python3
"""Fail closed unless GHCR tags are absent or a complete verified rerun state."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

COMPONENTS = ("web", "worker", "db-migrator")
VERSION_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
REVISION_PATTERN = re.compile(r"^[0-9a-f]{40}$")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate the six expected GHCR tags before or after publication."
    )
    parser.add_argument("--state-directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--source", required=True)
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
    for component in COMPONENTS:
        semantic_digest, semantic_labels = read_inspection(
            inspection_paths[(component, arguments.version)]
        )
        revision_digest, revision_labels = read_inspection(
            inspection_paths[(component, arguments.revision)]
        )
        if semantic_digest != revision_digest:
            fail(f"{component} semantic and revision tags resolve to different digests")
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
        print(f"Verified {component}: {semantic_digest}", file=sys.stderr)

    print("publish=false")
    print(
        "All six expected GHCR tags already resolve consistently with required OCI labels.",
        file=sys.stderr,
    )


if __name__ == "__main__":
    main()
