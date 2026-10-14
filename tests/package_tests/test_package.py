"""Package validation uses synthetic PE headers only inside these isolated tests."""
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

MODULE = Path(__file__).resolve().parents[2] / "scripts/package.py"
SPEC = importlib.util.spec_from_file_location("pcanalyzer_packaging", MODULE)
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="pcanalyzer-package-test-")
        self.root = Path(self.temporary.name)
        self.release = self.root / "release"
        self.release.mkdir()
        for name in package.REQUIRED:
            path = self.release / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("test fixture", encoding="utf-8")
        header = bytearray(70)
        header[:2] = b"MZ"
        struct.pack_into("<I", header, 0x3c, 64)
        header[64:68] = b"PE\0\0"
        struct.pack_into("<H", header, 68, 0x8664)
        for name in package.EXECUTABLES:
            (self.release / name).write_bytes(header)
        for name in package.RUNTIMES:
            frameworks = [{"name": "Microsoft.NETCore.App", "version": "test"}]
            if name == package.RUNTIMES[0]:
                frameworks.append({"name": "Microsoft.WindowsDesktop.App", "version": "test"})
            (self.release / name).write_text(json.dumps({"runtimeOptions": {"includedFrameworks": frameworks}}))

    def tearDown(self):
        self.temporary.cleanup()

    def test_manifest_is_refreshed_and_matches_the_actual_zip(self):
        archive = self.root / "windows.zip"
        package.package_runtime(self.release, archive)
        previous = package.sha256(archive)
        (self.release / "README.md").write_text("updated fixture", encoding="utf-8")
        package.package_runtime(self.release, archive)
        with zipfile.ZipFile(archive) as result:
            self.assertIsNone(result.testzip())
            manifest = json.loads(result.read("SHA256SUMS.json"))
            self.assertNotIn("SHA256SUMS.json", manifest)
            for name, digest in manifest.items():
                self.assertEqual(package.sha256(self.release / name), digest)
            self.assertEqual(result.read("README.md"), b"updated fixture")
        self.assertNotEqual(package.sha256(archive), previous)
        self.assertTrue(archive.with_suffix(".zip.sha256").read_text().startswith(package.sha256(archive)))

    def test_missing_worker_is_rejected_before_packaging(self):
        (self.release / "worker/PCAnalyzer.Worker.exe").unlink()
        with self.assertRaisesRegex(ValueError, "obrigatório ausente"):
            package.package_runtime(self.release, self.root / "invalid.zip")
        self.assertFalse((self.root / "invalid.zip").exists())

    def test_wrong_architecture_is_rejected(self):
        path = self.release / package.EXECUTABLES[0]
        header = bytearray(path.read_bytes())
        struct.pack_into("<H", header, 68, 0x14c)
        path.write_bytes(header)
        with self.assertRaisesRegex(ValueError, "Windows x64"):
            package.validate_runtime(self.release)

    def test_framework_dependent_runtime_is_rejected(self):
        (self.release / package.RUNTIMES[1]).write_text(json.dumps({"runtimeOptions": {"framework": {"name": "Microsoft.NETCore.App"}}}))
        with self.assertRaisesRegex(ValueError, "autossuficiente"):
            package.validate_runtime(self.release)

    def test_zip_cannot_include_itself(self):
        with self.assertRaisesRegex(ValueError, "fora da pasta"):
            package.package_runtime(self.release, self.release / "recursive.zip")


if __name__ == "__main__":
    unittest.main()
