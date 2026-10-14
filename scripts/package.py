#!/usr/bin/env python3
"""Validate and archive the already published Windows build; starts no application."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import zipfile

EXECUTABLES = ("PCAnalyzer.exe", "worker/PCAnalyzer.Worker.exe", "verify/PCAnalyzer.Verify.exe")
RUNTIMES = ("PCAnalyzer.runtimeconfig.json", "worker/PCAnalyzer.Worker.runtimeconfig.json",
            "verify/PCAnalyzer.Verify.runtimeconfig.json")
REQUIRED = EXECUTABLES + RUNTIMES + ("e_sqlite3.dll", "verify/e_sqlite3.dll", "README.md",
                                   "THIRD-PARTY-NOTICES.md", "docs/VALIDATION.md")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_runtime(release):
    release = Path(release).resolve()
    for name in REQUIRED:
        if not (release / name).is_file():
            raise ValueError("Arquivo obrigatório ausente: " + name)
    for path in release.rglob("*"):
        if path.is_symlink():
            raise ValueError("O pacote não aceita links simbólicos: " + str(path))
    for name in EXECUTABLES:
        with (release / name).open("rb") as stream:
            header = stream.read(64)
            if len(header) != 64 or header[:2] != b"MZ":
                raise ValueError("Executável PE inválido: " + name)
            stream.seek(struct.unpack_from("<I", header, 0x3c)[0])
            pe = stream.read(6)
            if len(pe) != 6 or pe[:4] != b"PE\0\0" or struct.unpack_from("<H", pe, 4)[0] != 0x8664:
                raise ValueError("Executável não é Windows x64: " + name)
    for name in RUNTIMES:
        options = json.loads((release / name).read_text(encoding="utf-8"))["runtimeOptions"]
        frameworks = {entry["name"] for entry in options.get("includedFrameworks", [])}
        if "Microsoft.NETCore.App" not in frameworks:
            raise ValueError("Runtime autossuficiente ausente: " + name)
        if name == RUNTIMES[0] and "Microsoft.WindowsDesktop.App" not in frameworks:
            raise ValueError("Runtime WPF ausente")
    return release


def archive_files(files, root, destination):
    destination = Path(destination).resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=destination.parent, prefix=destination.name, suffix=".tmp", delete=False) as stream:
        temporary = Path(stream.name)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(files):
                archive.write(file, file.relative_to(root).as_posix())
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise ValueError("Verificação CRC do ZIP falhou")
        temporary.replace(destination)
        digest = sha256(destination)
        destination.with_suffix(destination.suffix + ".sha256").write_text(
            digest + "  " + destination.name + "\n", encoding="utf-8")
    finally:
        temporary.unlink(missing_ok=True)


def package_runtime(release, destination):
    release = validate_runtime(release)
    if Path(destination).resolve().is_relative_to(release):
        raise ValueError("O ZIP deve ficar fora da pasta publicada")
    manifest_path = release / "SHA256SUMS.json"
    files = [file for file in release.rglob("*") if file.is_file() and file != manifest_path]
    manifest = {file.relative_to(release).as_posix(): sha256(file) for file in sorted(files)}
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    archive_files(files + [manifest_path], release, destination)


def package_source(repo, destination):
    repo = Path(repo).resolve()
    names = subprocess.check_output(["git", "-C", str(repo), "ls-files", "--cached", "--others",
                                     "--exclude-standard", "-z"]).decode("utf-8").split("\0")
    files = []
    for name in sorted(set(names)):
        if not name:
            continue
        file = repo / name
        if file.is_symlink():
            raise ValueError("Arquivo-fonte é link simbólico: " + name)
        if file.is_file():
            files.append(file)
    archive_files(files, repo, destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release", type=Path)
    parser.add_argument("--zip", type=Path)
    parser.add_argument("--source", action="store_true")
    arguments = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    target = arguments.zip or repo / "artifacts/PCAnalyzer-windows-x64.zip"
    package_runtime(arguments.release, target)
    if arguments.source:
        package_source(repo, repo / "artifacts/PCAnalyzer-source.zip")
    print("Pacote verificado:", target)
