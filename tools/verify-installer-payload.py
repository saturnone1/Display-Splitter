"""Verify embedded setup files without executing the installer or requesting elevation."""
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import re
import sys


def file_hash(path):
    result = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def main():
    setup = Path(sys.argv[1]).resolve(strict=True)
    repo = Path(__file__).resolve().parent.parent
    resources = repo / "installer" / "setup"
    manifest = json.loads((resources / "payload" / "payload.json").read_text(encoding="utf-8-sig"))
    staged = {item["Name"]: item for item in manifest["Files"]}
    ids = dict(re.findall(r"^#define\s+(IDR_\w+)\s+(\d+)", (resources / "resource.h").read_text(), re.M))
    entries = re.findall(r'^\s*(IDR_\w+)\s+RCDATA\s+"payload\\\\([^"\\]+)"',
                         (resources / "DesktopSplitterSetup.rc").read_text(), re.M)
    if len(entries) != len(staged) or {name for _, name in entries} != set(staged):
        raise ValueError("Installer resources and staged manifest disagree")

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
    kernel.LoadLibraryExW.restype = wintypes.HMODULE
    kernel.FindResourceW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p]
    kernel.FindResourceW.restype = wintypes.HANDLE
    kernel.SizeofResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
    kernel.SizeofResource.restype = wintypes.DWORD
    kernel.LoadResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
    kernel.LoadResource.restype = wintypes.HANDLE
    kernel.LockResource.argtypes = [wintypes.HANDLE]
    kernel.LockResource.restype = ctypes.c_void_p
    kernel.FreeLibrary.argtypes = [wintypes.HMODULE]
    kernel.FreeLibrary.restype = wintypes.BOOL
    # DATAFILE | IMAGE_RESOURCE: map PE resources as data. Never run an entry point.
    module = kernel.LoadLibraryExW(str(setup), None, 0x22)
    if not module:
        raise ctypes.WinError(ctypes.get_last_error())
    checked = []
    try:
        for symbol, name in entries:
            resource = kernel.FindResourceW(module, int(ids[symbol]), 10)
            if not resource:
                raise ctypes.WinError(ctypes.get_last_error())
            size = kernel.SizeofResource(module, resource)
            pointer = kernel.LockResource(kernel.LoadResource(module, resource))
            if not pointer or not size:
                raise ValueError(f"Empty embedded resource: {name}")
            digest = hashlib.sha256((ctypes.c_ubyte * size).from_address(pointer)).hexdigest()
            expected = staged[name]
            if size != expected["Bytes"] or digest.upper() != expected["Sha256"].upper():
                raise ValueError(f"Embedded payload hash/size mismatch: {name}")
            if digest != file_hash(resources / "payload" / name):
                raise ValueError(f"Staged file changed since manifest generation: {name}")
            checked.append({"name": name, "bytes": size, "sha256": digest})
    finally:
        kernel.FreeLibrary(module)
    report = {"installer": str(setup), "bytes": setup.stat().st_size, "sha256": file_hash(setup),
              "verification": "PE resources mapped as data; installer not executed", "files": checked}
    setup.with_suffix(".verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"PASS: {len(checked)} embedded files match staged SHA-256 hashes; installer not executed")
    print(f"Installer: {setup} ({setup.stat().st_size / 1024 / 1024:.1f} MiB)")
    print(f"SHA256: {report['sha256']}")


if __name__ == "__main__":
    main()
