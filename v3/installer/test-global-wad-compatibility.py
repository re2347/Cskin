"""Regression checks for hash-gated global WAD compatibility filtering."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import struct
import tempfile
import zipfile
from pathlib import Path


def load_engine():
    engine_path = Path(__file__).resolve().parents[1] / "Runtime" / "Cskin" / "cskin_engine.py"
    spec = importlib.util.spec_from_file_location("cskin_engine_global_wad_test", engine_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load engine source: {engine_path}")
    engine = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(engine)
    return engine


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--skin-id", type=int, default=234043)
    args = parser.parse_args()

    engine = load_engine()
    engine.log = lambda message: print(f"engine-log: {message}")
    archive = args.archive.resolve()
    with tempfile.TemporaryDirectory(prefix="cskin-global-wad-test-") as temporary:
        root = Path(temporary)
        mod = root / "mod"
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(mod)

        common = mod / "WAD" / "Common.wad.client"
        original_hash = hashlib.sha256(common.read_bytes()).hexdigest()
        ui = mod / "WAD" / "UI.wad.client"
        ui_hash = hashlib.sha256(ui.read_bytes()).hexdigest()
        removed = engine.OverlayApplier._filter_incompatible_global_wads(args.skin_id, mod)
        if removed != ["common.wad.client"] or common.exists() or not ui.exists():
            raise AssertionError(f"known incompatible global WADs were not removed: {removed}")
        for required in ("Viego.wad.client",):
            if not (mod / "WAD" / required).is_file():
                raise AssertionError(f"filter removed required current resource: {required}")
        repaired = engine.OverlayApplier._repair_incompatible_wad_entries(args.skin_id, mod)
        if repaired != ["ui.wad.client", "viego.wad.client"]:
            raise AssertionError(f"known incompatible WAD entries were not repaired: {repaired}")
        repaired_ui = (mod / "WAD" / "UI.wad.client").read_bytes()
        ui_count = struct.unpack_from("<I", repaired_ui, 0x10C)[0]
        ui_path_hash = struct.unpack_from("<Q", repaired_ui, 0x110)[0]
        if ui_count != 1 or ui_path_hash != 0xF221D11BC2E0CAE6:
            raise AssertionError(
                f"UI repair retained wrong entries: count={ui_count} hash={ui_path_hash:016x}")
        repaired_viego = (mod / "WAD" / "Viego.wad.client").read_bytes()
        count = struct.unpack_from("<I", repaired_viego, 0x10C)[0]
        path_hashes = {
            struct.unpack_from("<Q", repaired_viego, 0x110 + index * 0x20)[0]
            for index in range(count)
        }
        expected_hashes = {
            0x26162BD67C7EB042,
            0x50AA7AC5B646F513,
            0x4EDF655BF6F3D880,
        }
        if count != 3 or path_hashes != expected_hashes:
            actual = ",".join(f"{value:016x}" for value in sorted(path_hashes))
            raise AssertionError(f"Viego repair retained wrong entries: count={count} hashes={actual}")
        if not engine.OverlayApplier._wad_is_valid(repaired_viego):
            raise AssertionError("repaired Viego WAD is invalid")

        updated = root / "updated"
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(updated)
        for name in ("Common.wad.client", "UI.wad.client"):
            updated_wad = updated / "WAD" / name
            payload = bytearray(updated_wad.read_bytes())
            payload[-1] ^= 1
            updated_wad.write_bytes(payload)
        if engine.OverlayApplier._filter_incompatible_global_wads(args.skin_id, updated):
            raise AssertionError("updated global WAD hashes were incorrectly removed")
        if not all((updated / "WAD" / name).is_file() for name in ("Common.wad.client", "UI.wad.client")):
            raise AssertionError("updated global WAD was not preserved")

    print(f"PASS archive={archive}")
    print(f"skinId={args.skin_id} incompatibleCommonSha256={original_hash} incompatibleUiSha256={ui_hash}")
    print("bad Common removed; sword UI retained; Skin43 animation retained; stale Skin43 main config removed")


if __name__ == "__main__":
    main()
