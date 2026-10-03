import io
from pathlib import Path
import tarfile
import tempfile
import unittest

from release_archive_smoke import REQUIRED_FILES, validate_archive


class ReleaseArchiveTests(unittest.TestCase):
    def assert_archive(self, missing=(), extra=None, valid=False, empty=None):
        with tempfile.TemporaryDirectory(prefix="mk8-release-check-") as directory:
            path = Path(directory) / "release.tar.gz"
            with tarfile.open(path, "w:gz") as archive:
                for name in sorted(REQUIRED_FILES - set(missing)):
                    content = b"" if name == empty else b"runtime fixture"
                    member = tarfile.TarInfo(name)
                    member.size = len(content)
                    archive.addfile(member, io.BytesIO(content))
                if extra is not None:
                    archive.addfile(extra, io.BytesIO(b""))
            if valid:
                validate_archive(path)
            else:
                with self.assertRaises(ValueError):
                    validate_archive(path)

    def test_complete_release(self):
        self.assert_archive(valid=True)

    def test_missing_wake(self):
        self.assert_archive(missing=[name for name in REQUIRED_FILES if name.startswith("wake/")])

    def test_missing_worker(self):
        self.assert_archive(missing=[name for name in REQUIRED_FILES if name.startswith("worker/")])

    def test_missing_runtime_configuration(self):
        self.assert_archive(missing=["wake/mk8.email.Wake.runtimeconfig.json"])

    def test_empty_runtime(self):
        self.assert_archive(empty="wake/mk8.email.Wake.dll")

    def test_duplicate(self):
        self.assert_archive(extra=tarfile.TarInfo("wake/mk8.email.Wake.dll"))

    def test_traversal_and_absolute_paths(self):
        for name in ("../outside", "/wake/outside", "wake/../outside", "wake/./outside", "wake//outside"):
            with self.subTest(name=name):
                self.assert_archive(extra=tarfile.TarInfo(name))

    def test_unexpected_component(self):
        self.assert_archive(extra=tarfile.TarInfo("other/unexpected"))

    def test_component_root_file(self):
        self.assert_archive(extra=tarfile.TarInfo("wake"))

    def test_links_and_special_files(self):
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE):
            with self.subTest(kind=kind):
                member = tarfile.TarInfo("wake/unsafe")
                member.type = kind
                member.linkname = "outside"
                self.assert_archive(extra=member)


if __name__ == "__main__":
    unittest.main()
