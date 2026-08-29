"""Regression check for expanded Fantome HUD aliases and path normalization."""

from __future__ import annotations

import argparse
import importlib.util
import struct
import subprocess
import tempfile
import zipfile
from pathlib import Path


def load_engine():
    engine_path = Path(__file__).resolve().parents[1] / "Runtime" / "Cskin" / "cskin_engine.py"
    spec = importlib.util.spec_from_file_location("cskin_engine_hud_test", engine_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load engine source: {engine_path}")
    engine = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(engine)
    return engine


def read_wad_entry(engine, wad_path: Path, asset_path: str) -> bytes:
    wad = wad_path.read_bytes()
    layout = engine.OverlayApplier._wad_layout(wad)
    if layout is None:
        raise AssertionError(f"invalid WAD: {wad_path}")
    count, _ = layout
    expected_hash = engine.xxhash.xxh64(asset_path.casefold().encode("utf-8")).intdigest()
    for index in range(count):
        offset = engine.OverlayApplier._TOC_OFFSET + index * engine.OverlayApplier._TOC_ENTRY_SIZE
        path_hash = struct.unpack_from("<Q", wad, offset)[0]
        if path_hash != expected_hash:
            continue
        data_offset, compressed_size, decompressed_size = struct.unpack_from("<III", wad, offset + 8)
        entry_type = wad[offset + 20] & 0x0F
        compressed = wad[data_offset:data_offset + compressed_size]
        if entry_type == engine.OverlayApplier._ZSTD_ENTRY_TYPE:
            return engine.zstandard.ZstdDecompressor().decompress(
                compressed, max_output_size=decompressed_size)
        if entry_type == engine.OverlayApplier._RAW_ENTRY_TYPE:
            return compressed
        raise AssertionError(f"unsupported WAD entry type: {entry_type}")
    raise AssertionError(f"WAD entry not found: {asset_path}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--source-index", type=int, default=87)
    parser.add_argument("--target-index", type=int, default=0)
    args = parser.parse_args()

    engine = load_engine()
    archive = args.archive.resolve()
    game_dir = args.game_dir.resolve()
    source_name = "WAD/Yasuo.wad.client/assets/hematite/Characters/Yasuo/HUD/Yasuo_Circle_87.tex"
    normalized_target = "WAD/Yasuo.wad.client/assets/hematite/Characters/Yasuo/HUD/Yasuo_Circle.tex"
    forbidden_square_target = "WAD/Yasuo.wad.client/assets/Characters/Yasuo/HUD/Yasuo_Circle.tex"
    stale_circle_target = "WAD/Yasuo.wad.client/assets/Characters/Yasuo/HUD/Yasuo_Circle_87.tex"
    original_square = "WAD/Yasuo.wad.client/assets/hematite/Characters/Yasuo/HUD/Yasuo_Square.tex"

    with zipfile.ZipFile(archive) as bundle:
        expected = bundle.read(source_name)

    with tempfile.TemporaryDirectory(prefix="cskin-expanded-hud-test-") as temporary:
        root = Path(temporary)
        mapped = root / "mapped.fantome"
        aliases = engine.OverlayApplier._alias_expanded_hud_archive(
            archive, mapped, [args.source_index], args.target_index)
        if not aliases:
            raise AssertionError("expanded HUD alias was not generated")
        with zipfile.ZipFile(mapped) as bundle:
            names = set(bundle.namelist())
            if normalized_target not in names or bundle.read(normalized_target) != expected:
                raise AssertionError("normalized Circle HUD target does not contain the source skin portrait")
            if stale_circle_target in names:
                raise AssertionError(f"canonical source HUD target was retained: {stale_circle_target}")
            if forbidden_square_target in names:
                raise AssertionError("Circle portrait was incorrectly copied to the Square HUD path")
            if original_square not in names:
                raise AssertionError("the package's independent Square resource was unexpectedly removed")

        mod_dir = root / "mod"
        mod_tools = Path(engine.__file__).resolve().parent / "tools" / "mod-tools.exe"
        result = subprocess.run(
            [str(mod_tools), "import", str(mapped), str(mod_dir),
             f"--game:{game_dir}", "--noTFT"],
            cwd=mod_tools.parent, capture_output=True, text=True, timeout=180, check=False)
        if result.returncode:
            raise AssertionError(f"mod-tools import failed: {(result.stdout + result.stderr)[-1200:]}")
        imported = read_wad_entry(
            engine, mod_dir / "WAD" / "Yasuo.wad.client",
            "assets/hematite/characters/yasuo/hud/yasuo_circle.tex")
        if imported != expected:
            raise AssertionError("imported Circle HUD entry does not contain the Skin87 portrait")
        try:
            imported_square = read_wad_entry(
                engine, mod_dir / "WAD" / "Yasuo.wad.client",
                "assets/hematite/characters/yasuo/hud/yasuo_square.tex")
        except AssertionError:
            imported_square = None
        if imported_square == expected:
            raise AssertionError("Circle portrait was incorrectly imported into the Square HUD entry")

    print(f"PASS archive={archive}")
    print(f"aliases={aliases}")
    print("Skin87 HUD portrait stays in the hematite namespace; Circle is replaced and Square remains independent")


if __name__ == "__main__":
    main()
