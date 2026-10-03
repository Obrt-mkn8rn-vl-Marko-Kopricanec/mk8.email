#!/usr/bin/env python3
"""Check the native release archive without extracting or activating it."""

import argparse
import tarfile


COMPONENTS = {
    "cli": "mk8.email.Application.CLI",
    "admin": "mk8.email.Gateway",
    "worker": "mk8.email.Application.Worker",
    "wake": "mk8.email.Wake",
}
REQUIRED_FILES = frozenset(
    f"{directory}/{assembly}.{suffix}"
    for directory, assembly in COMPONENTS.items()
    for suffix in ("dll", "runtimeconfig.json")
)


def validate_archive(path):
    files = set()
    entries = set()
    with tarfile.open(path, "r:gz") as archive:
        for member in archive:
            name = member.name.rstrip("/")
            parts = name.split("/")
            if any(part in ("", ".", "..") for part in parts):
                raise ValueError("The release contains an unsafe path.")
            if parts[0] not in COMPONENTS:
                raise ValueError("The release contains an unexpected component.")
            if name in entries:
                raise ValueError("The release contains duplicate entries.")
            entries.add(name)
            if member.isfile():
                if len(parts) < 2:
                    raise ValueError("A component root must be a directory.")
                if name in REQUIRED_FILES and member.size <= 0:
                    raise ValueError("A required runtime file is empty.")
                files.add(name)
            elif not member.isdir():
                raise ValueError("The release contains a link or special file.")
    if REQUIRED_FILES - files:
        raise ValueError("The release is missing required runtime files: "
                         + ", ".join(sorted(REQUIRED_FILES - files)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive")
    arguments = parser.parse_args()
    validate_archive(arguments.archive)
    print("Native release archive includes CLI, Gateway, Worker and Wake.")


if __name__ == "__main__":
    main()
