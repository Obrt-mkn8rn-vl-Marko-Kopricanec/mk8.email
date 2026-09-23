#!/usr/bin/env python3
"""Extract only the regular files allowed by a verified distributed snapshot."""

import os
import re
import sys
import tarfile
from pathlib import Path


CORE_FILES = {"database.dump", "references.jsonl", "backup.json", "SHA256SUMS"}
BLOB_NAME = re.compile(r"blobs/[0-9a-f]{64}\Z")
MAX_ENTRIES = 1_000_000
MAX_BYTES = 1 << 50


def extract(destination: Path) -> None:
    if destination.is_symlink() or not destination.is_dir():
        raise ValueError("The extraction destination is unsafe.")
    seen: set[str] = set()
    total_size = 0
    with tarfile.open(fileobj=sys.stdin.buffer, mode="r|") as archive:
        for entry in archive:
            name = entry.name
            if name.startswith("./"):
                name = name[2:]
            if name in ("", "."):
                if not entry.isdir():
                    raise ValueError("The archive root is not a directory.")
                continue
            if name.endswith("/"):
                name = name[:-1]
            if name in seen or len(seen) >= MAX_ENTRIES:
                raise ValueError("The archive has duplicate or excessive entries.")
            seen.add(name)
            if name == "blobs":
                if not entry.isdir():
                    raise ValueError("The Blob path is not a directory.")
                (destination / "blobs").mkdir(mode=0o700)
                continue
            if not (name in CORE_FILES or BLOB_NAME.fullmatch(name)):
                raise ValueError("The archive contains an unexpected path.")
            if not entry.isfile() or entry.size < 0:
                raise ValueError("The archive contains a non-regular payload.")
            total_size += entry.size
            if total_size > MAX_BYTES:
                raise ValueError("The archive exceeds the extraction size limit.")
            if name.startswith("blobs/") and "blobs" not in seen:
                raise ValueError("A Blob appeared before its directory.")
            input_file = archive.extractfile(entry)
            if input_file is None:
                raise ValueError("The archive payload is missing.")
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW
            descriptor = os.open(destination / name, flags, 0o600)
            with os.fdopen(descriptor, "wb") as output_file, input_file:
                remaining = entry.size
                while remaining:
                    chunk = input_file.read(min(1 << 20, remaining))
                    if not chunk:
                        raise ValueError("The archive payload is truncated.")
                    output_file.write(chunk)
                    remaining -= len(chunk)
                output_file.flush()
                os.fsync(output_file.fileno())
    if not CORE_FILES.issubset(seen) or "blobs" not in seen:
        raise ValueError("The archive is incomplete.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: extract-distributed-snapshot-tar.py PRIVATE_DIRECTORY")
    try:
        extract(Path(sys.argv[1]))
    except (OSError, tarfile.TarError, ValueError) as error:
        raise SystemExit(f"Unsafe distributed snapshot archive: {error}") from None
