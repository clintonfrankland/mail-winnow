#!/usr/bin/env python3
"""Shared validation and Buildx digest handling for MailWinnow GHCR releases."""

from __future__ import annotations

import argparse
import json
import re
import xml.etree.ElementTree as element_tree
from pathlib import Path

COMPONENTS = ("web", "worker", "db-migrator")
VERSION_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
REVISION_PATTERN = re.compile(r"^[0-9a-f]{40}$")
DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")


def read_version_prefix(project_path: Path) -> str:
    """Read and strictly validate VersionPrefix from the shipped Web project."""
    try:
        version = element_tree.parse(project_path).findtext(".//VersionPrefix")
    except (OSError, element_tree.ParseError) as error:
        raise ValueError(f"could not read VersionPrefix from {project_path}") from error

    if version is None or VERSION_PATTERN.fullmatch(version) is None:
        raise ValueError("VersionPrefix must be strict MAJOR.MINOR.PATCH")
    return version


def record_build_digests(metadata_directory: Path, expected_digest_directory: Path) -> None:
    """Persist the manifest digests emitted by the three successful Buildx pushes."""
    expected_digest_directory.mkdir(parents=True, exist_ok=True)

    for component in COMPONENTS:
        metadata_path = metadata_directory / f"{component}.json"
        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            digest = metadata["containerimage.digest"]
        except (OSError, KeyError, TypeError, json.JSONDecodeError) as error:
            raise ValueError(
                f"{metadata_path} has no usable Buildx containerimage.digest"
            ) from error

        if not isinstance(digest, str) or DIGEST_PATTERN.fullmatch(digest) is None:
            raise ValueError(f"{metadata_path} has an invalid Buildx manifest digest")
        (expected_digest_directory / f"{component}.digest").write_text(
            f"{digest}\n", encoding="utf-8"
        )


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    read_version = subparsers.add_parser("read-version", help="read a strict VersionPrefix")
    read_version.add_argument("--project", type=Path, required=True)

    record_digests = subparsers.add_parser(
        "record-build-digests", help="record expected digests from Buildx metadata"
    )
    record_digests.add_argument("--metadata-directory", type=Path, required=True)
    record_digests.add_argument("--expected-digest-directory", type=Path, required=True)

    return parser.parse_args()


def main() -> None:
    arguments = parse_arguments()
    try:
        if arguments.command == "read-version":
            print(read_version_prefix(arguments.project))
        else:
            record_build_digests(
                arguments.metadata_directory, arguments.expected_digest_directory
            )
    except ValueError as error:
        raise SystemExit(str(error)) from error


if __name__ == "__main__":
    main()
