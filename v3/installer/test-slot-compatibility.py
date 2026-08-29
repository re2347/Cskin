"""Regression check for WAD slot mapping with an existing target entry."""

from __future__ import annotations

import argparse
import importlib.util
import struct
import tempfile
import zipfile
from pathlib import Path


def toc_hashes(wad: bytes) -> list[int]:
    count = struct.unpack_from("<I", wad, 0x10C)[0]
    return [struct.unpack_from("<Q", wad, 0x110 + index * 0x20)[0] for index in range(count)]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--source-index", type=int, required=True)
    parser.add_argument("--target-index", type=int, default=0)
    parser.add_argument("--wad", default="WAD/Viego.wad.client")
    args = parser.parse_args()

    engine_path = Path(__file__).resolve().parents[1] / "Runtime" / "Cskin" / "cskin_engine.py"
    spec = importlib.util.spec_from_file_location("cskin_engine_test", engine_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load engine source: {engine_path}")
    engine = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(engine)

    archive = args.archive.resolve()
    with zipfile.ZipFile(archive) as bundle:
        original_wad = bundle.read(args.wad)

    with tempfile.TemporaryDirectory(prefix="cskin-slot-test-") as temporary:
        mapped_archive = Path(temporary) / "mapped.fantome"
        mapped = engine.OverlayApplier._alias_archive(
            archive, mapped_archive, args.source_index, args.target_index
        )
        if not mapped:
            raise AssertionError("engine did not recognize the package slot entries")
        with zipfile.ZipFile(mapped_archive) as bundle:
            mapped_wad = bundle.read(args.wad)

    hashes = toc_hashes(mapped_wad)
    if len(hashes) != len(set(hashes)):
        raise AssertionError("slot mapping created duplicate WAD path hashes")
    if mapped_wad != original_wad:
        raise AssertionError("package with an existing target entry was unexpectedly rewritten")
    if not engine.OverlayApplier._wad_is_valid(mapped_wad):
        raise AssertionError("mapped WAD failed layout or checksum validation")

    print(f"PASS archive={archive}")
    print(f"wad={args.wad} entries={len(hashes)} uniqueHashes={len(set(hashes))}")
    print("existing target entry preserved; champion WAD unchanged and valid")


if __name__ == "__main__":
    main()
