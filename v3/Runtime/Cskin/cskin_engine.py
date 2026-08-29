"""Local API-only skin overlay engine used by Cskin Native."""

from __future__ import annotations

import argparse
import base64
import copy
import hashlib
import json
import os
import re
import shutil
import ssl
import struct
import subprocess
import sys
import tempfile
import threading
import time
import zipfile
from http import HTTPStatus
from http.client import HTTPSConnection
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

import xxhash
import zstandard


FROZEN = bool(getattr(sys, "frozen", False))
if FROZEN:
    # The one-dir release keeps mutable catalogs and mod-tools beside Cskin.exe.
    # _MEIPASS is an internal loader directory and does not contain those files.
    ROOT = Path(sys.executable).resolve().parent
else:
    ROOT = Path(__file__).resolve().parent
SKINS = ROOT / "skins"
DATA = ROOT / "data"
ASSETS = ROOT / "selector_assets"
LOG_PATH = DATA / "selector.log"
MOD_TOOLS_TIMEOUT_SECONDS = 180
OVERLAY_DIAGNOSTIC_SECONDS = 300
OVERLAY_DIAGNOSTIC_INTERVAL_SECONDS = 2
ENGINE_BUILD = "0.2.16-path-staging"


def log(message: str) -> None:
    try:
        message = re.sub(
            r'(--remoting-auth-token(?:=|\s+))(?:"[^"]*"|\'[^\']*\'|\S+)',
            r'\1<redacted>',
            message,
            flags=re.IGNORECASE)
        DATA.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} [engine] {message}\n")
    except OSError:
        pass


class ApplyError(RuntimeError):
    pass


class SkinCatalog:
    def __init__(self) -> None:
        self.champions: list[dict] = []
        self.by_skin: dict[int, dict] = {}
        self.by_champion: dict[int, dict] = {}
        self.lock = threading.RLock()
        self.load()

    def load(self) -> None:
        source = ASSETS / "skin_index.json"
        try:
            payload = json.loads(source.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError) as error:
            log(f"Catalog source unavailable: {source}: {error}")
            payload = {}
        available: dict[int, dict] = {}
        for key, item in (payload.get("bySkin") or {}).items():
            record = dict(item)
            skin_id = int(record.get("id") or key)
            relative = str(record.get("relativePath") or "")
            if relative and (SKINS / relative).is_file():
                available[skin_id] = record

        champions: list[dict] = []
        for raw in payload.get("champions") or []:
            champion = dict(raw)
            skins = [available[int(item.get("id", 0))] for item in champion.get("skins", [])
                     if int(item.get("id", 0)) in available]
            if not skins:
                continue
            champion["skins"] = skins
            champion["skinCount"] = len(skins)
            champions.append(champion)

        with self.lock:
            self.champions = champions
            self.by_skin = available
            self.by_champion = {int(item.get("id", 0)): item for item in champions}
        log(f"Catalog loaded: {len(available)} skins")
        if not available:
            log(f"Catalog has no local .fantome files under {SKINS}; apply requires GitCode download")

    def champion(self, champion_id: int) -> dict | None:
        with self.lock:
            return self.by_champion.get(champion_id)


class OverlayApplier:
    _TOC_OFFSET = 0x110
    _TOC_ENTRY_SIZE = 0x20
    _RAW_ENTRY_TYPE = 0
    _ZSTD_ENTRY_TYPE = 3
    _INCOMPATIBLE_GLOBAL_WADS = {
        (234043, "common.wad.client"):
            ("dc07002681762fbda866c429588dd0ac3f56bc0d2c058dcc5984eec99d95cc2b",
             "16.17-global-audio-bank"),
    }
    _INCOMPATIBLE_WAD_ENTRIES = {
        (234043, "ui.wad.client"): (
            "9353430e53712cbde0497d7c6af103e7a0082d8dc6ee4f45820adcc4d508f37f",
            {0xF221D11BC2E0CAE6},
            "16.17-outdated-global-ui-registry",
        ),
        (234043, "viego.wad.client"): (
            "76c9b6d83e092c7724567e1ae8209204a35186fbc965cc9f6dce8b66f2965ca7",
            {0x26162BD67C7EB042, 0x50AA7AC5B646F513, 0x4EDF655BF6F3D880},
            "16.17-incompatible-skin43-main-config; keep-animation-for-sword-swap",
        ),
    }
    _WAD_DIAGNOSTIC_ENTRIES = {
        "yasuo.wad.client": {
            0x8C6ADE71B994180B: "assets/characters/yasuo/hud/yasuo_circle.tex",
            0x08A94D50FB5AD2FB: "assets/characters/yasuo/hud/yasuo_circle_9.tex",
            0xFC7C2BE8D0FB51FA: "assets/characters/yasuo/hud/yasuo_circle_87.tex",
            0x9C6FE325C898BD72: "assets/characters/yasuo/hud/yasuo_circle_87.skins_yasuo_skin87.tex",
            0x60D8E5A988805854: "assets/characters/yasuo/hud/yasuo_square.tex",
            0x3C8821E566A4EF4C: "assets/hematite/characters/yasuo/hud/yasuo_circle.tex",
            0xA76C31572A46AA22: "assets/hematite/characters/yasuo/hud/yasuo_circle_87.tex",
            0x1D62563B5D3253F6: "assets/hematite/characters/yasuo/hud/yasuo_square.tex",
        },
        "viego.wad.client": {
            0x26162BD67C7EB042: "data/characters/viego/animations/skin0.bin",
            0x50AA7AC5B646F513: "data/characters/viego/animations/skin43.bin",
            0x4EDF655BF6F3D880: "data/characters/viego/skins/skin0.bin",
            0x12BCB8373C9B2D16: "data/characters/viego/skins/skin43.bin",
        },
        "ui.wad.client": {
            0x704B0292633A00A7: "gameplay.bin",
            0xF221D11BC2E0CAE6: "gameplay.viegoskin43viewcontroller.bin",
        },
    }

    def __init__(self) -> None:
        self.cache_root = DATA / "cache" / "skins"
        self.injection_root = DATA / "injection"
        self.mods_dir = self.injection_root / "mods"
        self.overlay_dir = self.injection_root / "overlay"
        self.overlay_log = self.injection_root / "overlay.log"
        self.process: subprocess.Popen | None = None
        self.lock = threading.Lock()

    @property
    def tools_dir(self) -> Path:
        return ROOT / "tools"

    @property
    def mod_tools(self) -> Path:
        return self.tools_dir / "mod-tools.exe"

    @staticmethod
    def game_dir() -> Path | None:
        configured = os.environ.get("AATROX_GAME_DIR", "").strip()
        candidates = [Path(configured)] if configured else []
        for drive in "CDEFGHIJKLMNOPQRSTUVWXYZ":
            root = Path(f"{drive}:/")
            candidates.extend([
                root / "wegameapps" / "英雄联盟" / "Game",
                root / "Program Files" / "腾讯游戏" / "英雄联盟" / "Game",
                root / "Program Files (x86)" / "腾讯游戏" / "英雄联盟" / "Game",
                root / "Riot Games" / "League of Legends" / "Game",
            ])
        for path in candidates:
            try:
                if (path / "League of Legends.exe").is_file():
                    return path
            except OSError:
                pass
        return None

    @staticmethod
    def _flags() -> int:
        return int(getattr(subprocess, "CREATE_NO_WINDOW", 0))

    @staticmethod
    def _clear(path: Path) -> None:
        shutil.rmtree(path, ignore_errors=True)
        path.mkdir(parents=True, exist_ok=True)

    def _stop(self) -> None:
        process = self.process
        if process is not None and process.poll() is None:
            try:
                process.terminate()
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                try:
                    process.kill()
                    process.wait(timeout=2)
                except (OSError, subprocess.TimeoutExpired):
                    pass
            except OSError:
                pass
        self.process = None
        if process is not None:
            try:
                subprocess.run(["taskkill", "/F", "/T", "/PID", str(process.pid)],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               creationflags=self._flags(), check=False, timeout=5)
            except (OSError, subprocess.TimeoutExpired):
                pass

    def _extract(self, archive: Path, destination: Path) -> None:
        self._clear(destination)
        root = destination.resolve()
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                target = (destination / member.filename).resolve()
                if target != root and root not in target.parents:
                    raise ApplyError("skin package contains an invalid path")
                bundle.extract(member, destination)

    @staticmethod
    def _archive_layout(archive: Path) -> str:
        packed_wads = 0
        expanded_files = 0
        try:
            with zipfile.ZipFile(archive) as bundle:
                for member in bundle.infolist():
                    normalized = member.filename.replace("\\", "/")
                    parts = [part for part in normalized.split("/") if part]
                    if (normalized.startswith("/") or ".." in parts
                            or (len(normalized) >= 2 and normalized[0].isalpha() and normalized[1] == ":")):
                        raise ApplyError(f"skin package contains an invalid path: {member.filename}")
                    lowered = normalized.casefold()
                    if not lowered.startswith("wad/") or member.is_dir():
                        continue
                    if lowered.endswith(".wad.client"):
                        packed_wads += 1
                    elif ".wad.client/" in lowered:
                        expanded_files += 1
        except (OSError, zipfile.BadZipFile, zipfile.LargeZipFile) as error:
            raise ApplyError(f"skin package is not a valid fantome archive: {error}") from error
        if expanded_files:
            return "expanded"
        if packed_wads:
            return "packed"
        raise ApplyError("skin package contains no packed or expanded WAD entries")

    @staticmethod
    def _alias_expanded_hud_archive(archive: Path, destination: Path,
                                    source_indices: list[int], target_index: int) -> list[str]:
        temporary = destination.with_name(f".{destination.name}.{os.getpid()}.hud.partial")
        try:
            with zipfile.ZipFile(archive, "r") as reader:
                entries = reader.infolist()
                by_name = {info.filename.replace("\\", "/").casefold(): info for info in entries}
                replacements: dict[str, zipfile.ZipInfo] = {}
                output_names: dict[str, str] = {}
                aliases: list[str] = []
                for source_index in source_indices:
                    suffix = re.compile(rf"_{source_index}(\.[^/]+)$", flags=re.IGNORECASE)
                    for info in entries:
                        normalized = info.filename.replace("\\", "/")
                        lowered = normalized.casefold()
                        if "/hud/" not in lowered or info.is_dir() or not suffix.search(normalized):
                            continue
                        target_name = suffix.sub(r"\1", normalized) if target_index == 0 \
                            else suffix.sub(rf"_{target_index}\1", normalized)
                        # Circle and Square are different HUD resources. Copying
                        # Circle bytes into Square made every expanded skin render
                        # an empty HUD portrait on the Tencent client.
                        for candidate_name in [target_name]:
                            target_info = by_name.get(candidate_name.casefold())
                            if target_info is None or target_info.is_dir():
                                continue
                            target_key = target_info.filename.casefold()
                            # Keep the package's original resource namespace.
                            # Skin PROP records reference the hematite paths by
                            # hash; renaming only the texture leaves dangling
                            # references and renders the HUD portrait blank.
                            output_name = target_info.filename
                            replacements[target_key] = info
                            output_names[target_key] = output_name
                            aliases.append(f"{info.filename}->{output_name}")
                    if aliases:
                        break
                if not aliases:
                    return []
                with zipfile.ZipFile(temporary, "w") as writer:
                    writer.comment = reader.comment
                    for info in entries:
                        key = info.filename.casefold()
                        source_info = replacements.get(key, info)
                        output_info = copy.copy(info)
                        output_info.filename = output_names.get(key, info.filename)
                        writer.writestr(output_info, reader.read(source_info))
            os.replace(temporary, destination)
            return aliases
        except (OSError, ValueError, zipfile.BadZipFile, zipfile.LargeZipFile) as error:
            temporary.unlink(missing_ok=True)
            raise ApplyError(f"failed to map expanded HUD resources: {error}") from error

    @staticmethod
    def _strip_invalid_hud_archive(archive: Path, destination: Path, remove_all: bool = False) -> int:
        """Remove empty/corrupt expanded HUD textures so the game keeps its avatar.

        A missing HUD entry is safe: the client falls back to the original WAD.
        An empty or malformed ``.tex`` entry is not safe because it overrides
        that fallback with a blank resource.
        """
        temporary = destination.with_name(f".{destination.name}.{os.getpid()}.hud-clean.partial")
        removed = 0
        try:
            with zipfile.ZipFile(archive, "r") as reader:
                with zipfile.ZipFile(temporary, "w") as writer:
                    writer.comment = reader.comment
                    for info in reader.infolist():
                        normalized = info.filename.replace("\\", "/")
                        lowered = normalized.casefold()
                        drop = False
                        if not info.is_dir() and "/hud/" in lowered and lowered.endswith(".tex"):
                            payload = reader.read(info)
                            drop = remove_all or len(payload) == 0 or not payload.startswith(b"TEX\0")
                            if drop:
                                removed += 1
                                reason = "known-incompatible-skin-hud" if remove_all else "invalid-texture"
                                log(f"HUD resource fallback: removed texture={info.filename} "
                                    f"bytes={len(payload)} reason={reason} fallback=original-avatar")
                        if not drop:
                            writer.writestr(info, reader.read(info))
            os.replace(temporary, destination)
            return removed
        except (OSError, ValueError, zipfile.BadZipFile, zipfile.LargeZipFile) as error:
            temporary.unlink(missing_ok=True)
            raise ApplyError(f"failed to clean invalid HUD resources: {error}") from error

    @classmethod
    def _filter_incompatible_global_wads(cls, skin_id: int, mod_dir: Path) -> list[str]:
        removed: list[str] = []
        for (known_skin_id, file_name), (expected_sha256, reason) in cls._INCOMPATIBLE_GLOBAL_WADS.items():
            if skin_id != known_skin_id:
                continue
            candidate = mod_dir / "WAD" / file_name
            try:
                if not candidate.is_file():
                    continue
                actual_sha256 = hashlib.sha256(candidate.read_bytes()).hexdigest()
                if actual_sha256 != expected_sha256:
                    log(f"Compatibility filter kept updated global WAD: skinId={skin_id} "
                        f"file={candidate} sha256={actual_sha256}")
                    continue
                candidate.unlink()
                removed.append(file_name)
                log(f"Compatibility filter removed incompatible global WAD: skinId={skin_id} "
                    f"file={candidate} sha256={actual_sha256} reason={reason}")
            except OSError as error:
                raise ApplyError(f"failed to filter incompatible global WAD: {candidate}: {error}") from error
        return removed

    @classmethod
    def _retain_wad_hashes(cls, wad: bytes, allowed_hashes: set[int]) -> tuple[bytes, int] | None:
        layout = cls._wad_layout(wad)
        if layout is None:
            return None
        count, _ = layout
        records: list[tuple[bytearray, bytes]] = []
        for index in range(count):
            offset = cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE
            try:
                path_hash = struct.unpack_from("<Q", wad, offset)[0]
                data_offset, compressed_size = struct.unpack_from("<II", wad, offset + 8)
            except struct.error:
                return None
            data_end = data_offset + compressed_size
            if data_offset < cls._TOC_OFFSET or data_end > len(wad):
                return None
            if path_hash in allowed_hashes:
                records.append((bytearray(wad[offset:offset + cls._TOC_ENTRY_SIZE]),
                                wad[data_offset:data_end]))
        if not records:
            return None

        header = bytearray(wad[:cls._TOC_OFFSET])
        struct.pack_into("<I", header, 0x10C, len(records))
        rebuilt = bytearray(header)
        rebuilt.extend(b"\0" * (len(records) * cls._TOC_ENTRY_SIZE))
        cursor = len(rebuilt)
        for index, (record, data) in enumerate(records):
            struct.pack_into("<I", record, 8, cursor)
            offset = cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE
            rebuilt[offset:offset + cls._TOC_ENTRY_SIZE] = record
            rebuilt.extend(data)
            cursor += len(data)
        result = bytes(rebuilt)
        return (result, count - len(records)) if cls._wad_is_valid(result) else None

    @classmethod
    def _repair_incompatible_wad_entries(cls, skin_id: int, mod_dir: Path) -> list[str]:
        repaired: list[str] = []
        for (known_skin_id, file_name), (expected_sha256, allowed_hashes, reason) \
                in cls._INCOMPATIBLE_WAD_ENTRIES.items():
            if skin_id != known_skin_id:
                continue
            candidate = mod_dir / "WAD" / file_name
            try:
                if not candidate.is_file():
                    continue
                original = candidate.read_bytes()
                actual_sha256 = hashlib.sha256(original).hexdigest()
                if actual_sha256 != expected_sha256:
                    log(f"Compatibility repair kept updated WAD: skinId={skin_id} "
                        f"file={candidate} sha256={actual_sha256}")
                    continue
                result = cls._retain_wad_hashes(original, allowed_hashes)
                if result is None:
                    raise ApplyError(f"failed to retain compatible WAD entries: {candidate}")
                rebuilt, removed_count = result
                candidate.write_bytes(rebuilt)
                repaired.append(file_name)
                kept = ",".join(f"{value:016x}" for value in sorted(allowed_hashes))
                log(f"Compatibility repair pruned incompatible WAD entries: skinId={skin_id} "
                    f"file={candidate} sha256={actual_sha256} removedEntries={removed_count} "
                    f"keptHashes={kept} reason={reason}")
            except OSError as error:
                raise ApplyError(f"failed to repair incompatible WAD: {candidate}: {error}") from error
        return repaired

    @staticmethod
    def _slot_hash(champion: bytes, skin_index: int) -> int:
        return xxhash.xxh64(
            b"data/characters/" + champion.lower() + b"/skins/skin" +
            str(skin_index).encode("ascii") + b".bin").intdigest()

    @classmethod
    def _wad_layout(cls, wad: bytes) -> tuple[int, int] | None:
        if wad[:3] != b"RW\x03" or wad[3:4] not in {b"\x03", b"\x04"} or len(wad) < cls._TOC_OFFSET:
            return None
        try:
            count = struct.unpack_from("<I", wad, 0x10C)[0]
            toc_end = cls._TOC_OFFSET + count * cls._TOC_ENTRY_SIZE
        except struct.error:
            return None
        return (count, toc_end) if toc_end <= len(wad) else None

    @classmethod
    def _wad_is_valid(cls, wad: bytes) -> bool:
        layout = cls._wad_layout(wad)
        if layout is None:
            return False
        count, toc_end = layout
        for index in range(count):
            offset = cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE
            try:
                data_offset, compressed_size, decompressed_size = struct.unpack_from("<III", wad, offset + 8)
                entry_type = wad[offset + 20] & 0x0F
                checksum = struct.unpack_from("<Q", wad, offset + 24)[0]
            except (IndexError, struct.error):
                return False
            data_end = data_offset + compressed_size
            if data_offset < toc_end or data_end > len(wad):
                return False
            compressed = wad[data_offset:data_end]
            if xxhash.xxh3_64(compressed).intdigest() != checksum:
                return False
            if entry_type == cls._RAW_ENTRY_TYPE and compressed_size != decompressed_size:
                return False
        return True

    @classmethod
    def _diagnose_wad_file(cls, stage: str, path: Path) -> dict[int, str]:
        watched = cls._WAD_DIAGNOSTIC_ENTRIES.get(path.name.casefold())
        if not watched:
            return {}
        result: dict[int, str] = {}
        try:
            if not path.is_file():
                log(f"WAD diagnostic: stage={stage} file={path} exists=False")
                return {path_hash: "<missing>" for path_hash in watched}
            file_size = path.stat().st_size
            with path.open("rb") as handle:
                header = handle.read(cls._TOC_OFFSET)
                if (len(header) < cls._TOC_OFFSET or header[:3] != b"RW\x03"
                        or header[3:4] not in {b"\x03", b"\x04"}):
                    log(f"WAD diagnostic: stage={stage} file={path} exists=True "
                        f"bytes={file_size} validHeader=False")
                    return {path_hash: "<invalid-wad>" for path_hash in watched}
                count = struct.unpack_from("<I", header, 0x10C)[0]
                toc = handle.read(count * cls._TOC_ENTRY_SIZE)
                if len(toc) != count * cls._TOC_ENTRY_SIZE:
                    log(f"WAD diagnostic: stage={stage} file={path} exists=True "
                        f"bytes={file_size} entryCount={count} validToc=False")
                    return {path_hash: "<invalid-toc>" for path_hash in watched}
                records: dict[int, tuple[int, int, int, int]] = {}
                for index in range(count):
                    offset = index * cls._TOC_ENTRY_SIZE
                    path_hash = struct.unpack_from("<Q", toc, offset)[0]
                    if path_hash not in watched:
                        continue
                    data_offset, compressed_size, decompressed_size = struct.unpack_from(
                        "<III", toc, offset + 8)
                    entry_type = toc[offset + 20] & 0x0F
                    records[path_hash] = (data_offset, compressed_size, decompressed_size, entry_type)
                log(f"WAD diagnostic: stage={stage} file={path} exists=True bytes={file_size} "
                    f"entryCount={count} indexSha256={hashlib.sha256(header + toc).hexdigest()}")
                for path_hash, asset_name in watched.items():
                    record = records.get(path_hash)
                    if record is None:
                        result[path_hash] = "<missing>"
                        log(f"WAD entry diagnostic: stage={stage} wad={path.name} "
                            f"path={asset_name} hash={path_hash:016x} present=False")
                        continue
                    data_offset, compressed_size, decompressed_size, entry_type = record
                    handle.seek(data_offset)
                    compressed = handle.read(compressed_size)
                    raw: bytes | None = None
                    error = ""
                    try:
                        if entry_type == cls._RAW_ENTRY_TYPE:
                            raw = compressed
                        elif entry_type == cls._ZSTD_ENTRY_TYPE:
                            raw = zstandard.ZstdDecompressor().decompress(
                                compressed, max_output_size=decompressed_size)
                        else:
                            error = f"unsupported-type-{entry_type}"
                    except zstandard.ZstdError as exception:
                        error = f"zstd-error:{exception}"
                    if raw is None:
                        content_sha = hashlib.sha256(compressed).hexdigest()
                        head = compressed[:24].hex()
                    else:
                        content_sha = hashlib.sha256(raw).hexdigest()
                        head = raw[:24].hex()
                    result[path_hash] = content_sha
                    log(f"WAD entry diagnostic: stage={stage} wad={path.name} path={asset_name} "
                        f"hash={path_hash:016x} present=True type={entry_type} "
                        f"compressedBytes={compressed_size} decompressedBytes={decompressed_size} "
                        f"contentSha256={content_sha} head={head or '<empty>'} "
                        f"decode={error or 'ok'}")
        except (OSError, struct.error, ValueError) as error:
            log(f"WAD diagnostic failed: stage={stage} file={path} error={error}")
            return {path_hash: "<read-error>" for path_hash in watched}
        return result

    @classmethod
    def _diagnose_prop_references(cls, stage: str, path: Path) -> None:
        """Log whether PROP records point at the HUD hashes present in the WAD.

        Expanded skin packages may carry a PROP hash for the ``hematite``
        namespace while ``mod-tools`` rebases the texture entry to the normal
        game namespace.  This diagnostic makes that mismatch visible without
        changing either resource silently.
        """
        if path.name.casefold() != "yasuo.wad.client" or not path.is_file():
            return
        aliases = {
            0x3C8821E566A4EF4C: 0x8C6ADE71B994180B,
            0xA76C31572A46AA22: 0xFC7C2BE8D0FB51FA,
            0x1D62563B5D3253F6: 0x60D8E5A988805854,
        }
        labels = {
            0x3C8821E566A4EF4C: "hematite-circle",
            0xA76C31572A46AA22: "hematite-circle-87",
            0x1D62563B5D3253F6: "hematite-square",
        }
        try:
            wad = path.read_bytes()
            layout = cls._wad_layout(wad)
            if layout is None:
                log(f"PROP reference diagnostic: stage={stage} file={path} validWad=False")
                return
            count, _ = layout
            present = {
                struct.unpack_from("<Q", wad, cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE)[0]
                for index in range(count)
            }
            references = {source_hash: 0 for source_hash in aliases}
            for index in range(count):
                offset = cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE
                data_offset, compressed_size, decompressed_size = struct.unpack_from(
                    "<III", wad, offset + 8)
                entry_type = wad[offset + 20] & 0x0F
                compressed = wad[data_offset:data_offset + compressed_size]
                if entry_type == cls._RAW_ENTRY_TYPE:
                    raw = compressed
                elif entry_type == cls._ZSTD_ENTRY_TYPE:
                    raw = zstandard.ZstdDecompressor().decompress(
                        compressed, max_output_size=decompressed_size)
                else:
                    continue
                if not raw.startswith(b"PROP"):
                    continue
                for source_hash in references:
                    references[source_hash] += raw.count(struct.pack("<Q", source_hash))
            for source_hash, reference_count in references.items():
                if not reference_count:
                    continue
                target_hash = aliases[source_hash]
                log(f"PROP reference diagnostic: stage={stage} file={path} "
                    f"alias={labels[source_hash]} sourceHash={source_hash:016x} "
                    f"sourcePresent={source_hash in present} references={reference_count} "
                    f"targetHash={target_hash:016x} targetPresent={target_hash in present}")
        except (OSError, struct.error, ValueError, zstandard.ZstdError) as error:
            log(f"PROP reference diagnostic failed: stage={stage} file={path} error={error}")

    @classmethod
    def _diagnose_apply_wads(cls, skin_id: int, stage: str, root: Path,
                             game_layout: bool = False) -> dict[str, dict[int, str]]:
        names: list[str] = []
        if skin_id // 1000 == 157:
            names.append("Yasuo.wad.client")
        if skin_id == 234043:
            names.extend(["Viego.wad.client", "UI.wad.client"])
        diagnostics: dict[str, dict[int, str]] = {}
        for name in names:
            if game_layout:
                path = (root / "DATA" / "FINAL" / name if name.casefold() == "ui.wad.client"
                        else root / "DATA" / "FINAL" / "Champions" / name)
            else:
                try:
                    path = next((candidate for candidate in root.rglob("*.wad.client")
                                 if candidate.name.casefold() == name.casefold()), root / name)
                except OSError:
                    path = root / name
            diagnostics[name.casefold()] = cls._diagnose_wad_file(stage, path)
            cls._diagnose_prop_references(stage, path)
        return diagnostics

    @classmethod
    def _alias_wad(cls, wad: bytes, source_index: int, target_index: int, champion_hint: str) -> bytes | None:
        layout = cls._wad_layout(wad)
        if layout is None:
            return None
        count, toc_end = layout

        try:
            wad_hashes = {
                struct.unpack_from("<Q", wad, cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE)[0]
                for index in range(count)
            }
        except struct.error:
            return None

        rebuilt = bytearray(wad)
        # Track hashes introduced during this pass as well as hashes that were
        # already present.  Complex packages such as Garen 86044 contain both
        # a base-slot PROP (skin0) and a source-slot PROP (skin44).  When the
        # selected target is a third slot (for example skin13), treating the
        # original TOC as immutable rewrites both records to the same target
        # hash and creates a duplicate WAD entry.  The first mapped record owns
        # the target; later records remain at their original hash.
        mapped_hashes = set(wad_hashes)
        recognized = False
        for index in range(count):
            offset = cls._TOC_OFFSET + index * cls._TOC_ENTRY_SIZE
            try:
                path_hash = struct.unpack_from("<Q", wad, offset)[0]
                data_offset, compressed_size, decompressed_size = struct.unpack_from("<III", wad, offset + 8)
                entry_type = wad[offset + 20] & 0x0F
            except (IndexError, struct.error):
                return None
            data_end = data_offset + compressed_size
            if data_offset < toc_end or data_end > len(wad):
                return None
            compressed = wad[data_offset:data_end]
            try:
                raw = zstandard.ZstdDecompressor().decompress(compressed, max_output_size=decompressed_size) \
                    if entry_type == cls._ZSTD_ENTRY_TYPE else compressed if entry_type == cls._RAW_ENTRY_TYPE else None
            except zstandard.ZstdError:
                return None
            if raw is None or not raw.startswith(b"PROP"):
                continue

            match = re.search(rb"data/characters/([^/]+)/skins/skin(\d+)\.bin", raw, flags=re.IGNORECASE)
            champion = match.group(1) if match else re.sub(rb"[^a-z0-9]", b"", champion_hint.lower().encode("utf-8"))
            if not champion:
                continue
            if match and int(match.group(2)) != source_index:
                continue
            if not match and re.search(
                re.escape(champion) + rb"skin0*" + str(source_index).encode("ascii") + rb"(?!\d)",
                raw, flags=re.IGNORECASE) is None:
                continue

            # Most packages keep their PROP record at the champion's base
            # slot (skin0) even though the embedded object name identifies
            # the source skin. A source-slot-only lookup silently rejects
            # those valid packages and makes non-base applications no-op.
            base_hash = cls._slot_hash(champion, 0)
            source_hash = cls._slot_hash(champion, source_index)
            target_hash = cls._slot_hash(champion, target_index)
            if path_hash not in {base_hash, source_hash, target_hash}:
                continue
            recognized = True
            if path_hash != target_hash:
                # Some complex skins intentionally ship both a base-slot PROP
                # and a source-slot PROP. Rewriting the latter to an existing
                # target hash creates duplicate WAD entries; League then
                # crashes while loading the redirected archive. In that case
                # the package already supports the target slot, so preserve
                # both original entries exactly as the legacy engine did.
                if target_hash in mapped_hashes:
                    log(
                        f"Slot mapping preserved existing target entry: wad={champion_hint} "
                        f"sourceIndex={source_index} targetIndex={target_index} "
                        f"sourceHash={source_hash:016x} targetHash={target_hash:016x}"
                    )
                    continue
                struct.pack_into("<Q", rebuilt, offset, target_hash)
                mapped_hashes.add(target_hash)

        if not recognized:
            return None
        result = bytes(rebuilt)
        return result if cls._wad_is_valid(result) else None

    @staticmethod
    def _source_indices(skin_id: int, record: dict) -> list[int]:
        indices = [skin_id % 1000]
        try:
            base_skin_id = int(record.get("baseSkinId") or 0)
        except (TypeError, ValueError):
            base_skin_id = 0
        if base_skin_id // 1000 == skin_id // 1000 and base_skin_id % 1000 not in indices:
            indices.append(base_skin_id % 1000)
        return indices

    @classmethod
    def _alias_archive(cls, archive: Path, destination: Path, source_index: int, target_index: int) -> bool:
        temporary = destination.with_name(f".{destination.name}.{os.getpid()}.partial")
        try:
            with zipfile.ZipFile(archive, "r") as reader, zipfile.ZipFile(temporary, "w") as writer:
                recognized = False
                for info in reader.infolist():
                    content = reader.read(info.filename)
                    if info.filename.lower().startswith("wad/") and info.filename.lower().endswith(".wad.client"):
                        name = Path(info.filename).name[: -len(".wad.client")]
                        rewritten = cls._alias_wad(content, source_index, target_index, name)
                        if rewritten is not None:
                            content = rewritten
                            recognized = True
                    writer.writestr(info, content)
            if not recognized:
                temporary.unlink(missing_ok=True)
                return False
            os.replace(temporary, destination)
            return True
        except (OSError, ValueError, zipfile.BadZipFile, zipfile.LargeZipFile):
            temporary.unlink(missing_ok=True)
            return False

    @classmethod
    def _alias_archive_for_sources(cls, archive: Path, destination: Path,
                                   source_indices: list[int], target_index: int) -> bool:
        for source_index in source_indices:
            if cls._alias_archive(archive, destination, source_index, target_index):
                return True
        return False

    @classmethod
    def _alias_mod_for_sources(cls, mod_dir: Path, source_indices: list[int],
                               target_index: int) -> bool:
        wad_root = mod_dir / "WAD"
        for source_index in source_indices:
            mapped_paths: list[str] = []
            try:
                wad_paths = sorted(path for path in wad_root.rglob("*.wad.client") if path.is_file())
            except OSError as error:
                raise ApplyError(f"failed to enumerate imported WAD files: {error}") from error
            for wad_path in wad_paths:
                try:
                    original = wad_path.read_bytes()
                    champion_hint = wad_path.name[: -len(".wad.client")]
                    rewritten = cls._alias_wad(original, source_index, target_index, champion_hint)
                    if rewritten is None:
                        continue
                    if rewritten != original:
                        temporary = wad_path.with_name(f".{wad_path.name}.{os.getpid()}.partial")
                        temporary.write_bytes(rewritten)
                        os.replace(temporary, wad_path)
                    mapped_paths.append(str(wad_path.relative_to(mod_dir)))
                except OSError as error:
                    raise ApplyError(f"failed to map imported WAD slot: {wad_path}: {error}") from error
            if mapped_paths:
                log(f"Imported package slot mapping completed: sourceIndex={source_index} "
                    f"targetIndex={target_index} wads={mapped_paths}")
                return True
        return False

    def _run(self, arguments: list[str], background: bool = False):
        command = [str(self.mod_tools), *arguments]
        environment = os.environ.copy()
        # mod-tools loads cslol-dll.dll by name.  Keep its directory first in
        # PATH as well as the working directory so a copied portable folder
        # cannot accidentally resolve a DLL from another installation.
        tool_path = str(self.tools_dir)
        environment["PATH"] = tool_path + os.pathsep + environment.get("PATH", "")
        try:
            tool_exists = self.mod_tools.is_file()
            tool_bytes = self.mod_tools.stat().st_size if tool_exists else 0
            tool_sha256 = hashlib.sha256(self.mod_tools.read_bytes()).hexdigest() if tool_exists else "<missing>"
        except OSError as error:
            tool_exists = False
            tool_bytes = 0
            tool_sha256 = f"<error:{error}>"
        log(f"mod-tools invocation: path={self.mod_tools} exists={tool_exists} "
            f"bytes={tool_bytes} sha256={tool_sha256} cwd={self.tools_dir} "
            f"argLengths={[len(str(value)) for value in arguments]}")
        if background:
            dll = self.tools_dir / "cslol-dll.dll"
            log(f"Running mod-tools background: {' '.join(arguments)} cwd={self.tools_dir} "
                f"runnerExists={self.mod_tools.is_file()} dllExists={dll.is_file()} "
                f"dllBytes={dll.stat().st_size if dll.is_file() else 0}")
            self.overlay_log.parent.mkdir(parents=True, exist_ok=True)
            output = self.overlay_log.open("w", encoding="utf-8", errors="replace")
            try:
                return subprocess.Popen(command, stdin=subprocess.PIPE, stdout=output,
                                        stderr=subprocess.STDOUT, cwd=str(self.tools_dir),
                                        creationflags=self._flags(), env=environment)
            finally:
                output.close()
        try:
            log(f"Running mod-tools: {' '.join(arguments)} timeout={MOD_TOOLS_TIMEOUT_SECONDS}s")
            result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    text=True, cwd=str(self.tools_dir),
                                    creationflags=self._flags(), check=False,
                                    timeout=MOD_TOOLS_TIMEOUT_SECONDS, env=environment)
        except subprocess.TimeoutExpired as error:
            log(f"mod-tools timed out after {MOD_TOOLS_TIMEOUT_SECONDS} seconds")
            raise ApplyError(f"mod-tools timed out after {MOD_TOOLS_TIMEOUT_SECONDS} seconds") from error
        if result.returncode:
            stdout = (result.stdout or "").strip()
            stderr = (result.stderr or "").strip()
            detail = "\n".join(part for part in (stdout, stderr) if part)
            log(f"mod-tools failed: command={arguments[0] if arguments else ''} "
                f"exitCode={result.returncode} stdout={stdout[-2000:] if stdout else '<empty>'} "
                f"stderr={stderr[-2000:] if stderr else '<empty>'}")
            raise ApplyError(detail or "mod-tools failed")
        stdout = (result.stdout or "").strip()
        stderr = (result.stderr or "").strip()
        log(f"mod-tools completed: command={arguments[0] if arguments else ''} exitCode=0 "
            f"stdoutBytes={len(result.stdout or '')} stderrBytes={len(result.stderr or '')}")
        if stdout:
            log(f"mod-tools stdout: command={arguments[0] if arguments else ''} "
                f"detail={stdout[-2000:]}")
        if stderr:
            log(f"mod-tools stderr: command={arguments[0] if arguments else ''} "
                f"detail={stderr[-2000:]}")
        return result

    def _overlay_wads(self) -> tuple[list[Path], int]:
        if not self.overlay_dir.is_dir():
            return [], 0
        try:
            paths = sorted(self.overlay_dir.rglob("*.wad.client"))
            total = 0
            for path in paths:
                try:
                    total += path.stat().st_size
                except OSError as error:
                    log(f"Overlay WAD stat failed: path={path}: {error}")
            return paths, total
        except OSError as error:
            log(f"Overlay WAD enumeration failed: root={self.overlay_dir}: {error}")
            return [], 0

    def _overlay_log_tail(self, limit: int = 2400) -> str:
        try:
            if not self.overlay_log.is_file():
                return "<missing>"
            return self.overlay_log.read_text(encoding="utf-8", errors="replace")[-limit:].replace("\r", " ").replace("\n", " ").strip() or "<empty>"
        except OSError as error:
            return f"<read-error:{error}>"

    def _overlay_state(self) -> tuple[str, str]:
        """Return the strongest state visible in the runner's log."""
        tail = self._overlay_log_tail(12000)
        normalized = tail.casefold()
        if "redirected wad" in normalized:
            return "redirected", tail
        if "init done" in normalized:
            return "initialized", tail
        if "init in process" in normalized or "patching module" in normalized:
            return "dll-initializing", tail
        if "found league" in normalized:
            return "found-game", tail
        if ("failed to" in normalized or "error:" in normalized or "error " in normalized
                or "access denied" in normalized or "cannot open process" in normalized
                or "unable to inject" in normalized or "inject failed" in normalized):
            return "error", tail
        if "waiting for league" in normalized:
            return "waiting-game", tail
        return "unknown", tail

    @staticmethod
    def _game_process_running() -> bool:
        try:
            result = subprocess.run(
                ["tasklist.exe", "/FI", "IMAGENAME eq League of Legends.exe", "/FO", "CSV", "/NH"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                creationflags=int(getattr(subprocess, "CREATE_NO_WINDOW", 0)),
                check=False, timeout=2)
            return "league of legends.exe" in (result.stdout or "").casefold()
        except (OSError, subprocess.TimeoutExpired):
            return False

    @staticmethod
    def _game_process_snapshot() -> str:
        try:
            result = subprocess.run(
                ["tasklist.exe", "/FI", "IMAGENAME eq League of Legends.exe", "/FO", "CSV", "/NH"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                creationflags=int(getattr(subprocess, "CREATE_NO_WINDOW", 0)),
                check=False, timeout=2)
            output = " ".join((result.stdout or "").split())
            running = "league of legends.exe" in output.casefold()
            detail = output[-500:] if output else (result.stderr or "<empty>").strip()[-500:]
            # tasklist confirms presence but not which executable instance was
            # found.  A short WMI query adds the absolute path/PID when the
            # bundled WMIC is available; failures remain diagnostic only.
            identity = ""
            try:
                wmic = ROOT / "wmic.exe"
                if wmic.is_file():
                    identity_result = subprocess.run(
                        [str(wmic), "process", "where", "name='League of Legends.exe'",
                         "get", "ProcessId,ExecutablePath", "/format:list"],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                        creationflags=int(getattr(subprocess, "CREATE_NO_WINDOW", 0)),
                        check=False, timeout=2)
                    identity = " ".join((identity_result.stdout or "").split())[-700:]
            except (OSError, subprocess.TimeoutExpired):
                identity = "<identity-unavailable>"
            return f"running={running} detail={detail or '<empty>'} identity={identity or '<none>'}"
        except subprocess.TimeoutExpired:
            return "running=<unknown> error=tasklist timeout"
        except OSError as error:
            return f"running=<unknown> error={error}"

    @staticmethod
    def _analyze_latest_game_log(game_dir: Path, skin_id: int, apply_started_at: float) -> None:
        logs_root = game_dir / "Logs" / "GameLogs"
        try:
            candidates = [
                path for path in logs_root.rglob("*_r3dlog.txt")
                if path.is_file() and path.stat().st_mtime >= apply_started_at - 15
            ]
            if not candidates:
                log(f"Game result diagnostic: skinId={skin_id} logFound=False "
                    f"root={logs_root} applyStarted={time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(apply_started_at))}")
                return
            latest = max(candidates, key=lambda path: path.stat().st_mtime)
            champion = "<unknown>"
            client_skin_id = -1
            entered_game_loop = False
            fatal_message = ""
            last_load_stage = "<none>"
            with latest.open("r", encoding="utf-8", errors="replace") as handle:
                for line in handle:
                    roster = re.search(r"Champion\(([^)]+)\)\s+SkinID\((\d+)\)", line)
                    if roster:
                        champion = roster.group(1)
                        client_skin_id = int(roster.group(2))
                    if "GAMESTATE_GAMELOOP" in line:
                        entered_game_loop = True
                    if "FATAL ERROR" in line:
                        fatal_message = line.strip()[-500:]
                    if ("|  LOAD|" in line or "LoadGlobalEffects" in line
                            or "Loading Ended" in line or "Data Load Complete" in line):
                        last_load_stage = line.strip()[-500:]
            result = "fatal" if fatal_message else "entered-game" if entered_game_loop else "incomplete"
            log(f"Game result diagnostic: skinId={skin_id} logFound=True result={result} "
                f"champion={champion} clientSkinId={client_skin_id} "
                f"enteredGameLoop={entered_game_loop} fatal={bool(fatal_message)} "
                f"lastLoadStage={last_load_stage} fatalMessage={fatal_message or '<none>'} "
                f"log={latest} bytes={latest.stat().st_size}")
        except (OSError, ValueError) as error:
            log(f"Game result diagnostic failed: skinId={skin_id} root={logs_root} error={error}")

    def _monitor_overlay(self, process: subprocess.Popen, skin_id: int,
                         game_dir: Path, apply_started_at: float) -> None:
        deadline = time.monotonic() + OVERLAY_DIAGNOSTIC_SECONDS
        previous_signature: tuple[int | None, str] | None = None
        game_seen = False
        log(f"runoverlay background monitor started: pid={process.pid} "
            f"skinId={skin_id} timeout={OVERLAY_DIAGNOSTIC_SECONDS}s")
        while time.monotonic() < deadline:
            if self.process is not process:
                return
            exit_code = process.poll()
            state, tail = self._overlay_state()
            game_running = self._game_process_running()
            game_seen = game_seen or game_running
            signature = (exit_code, state)
            if signature != previous_signature:
                previous_signature = signature
                log(f"runoverlay progress: pid={process.pid} state={state} "
                    f"exitCode={exit_code if exit_code is not None else '<running>'} "
                    f"gameProcess={self._game_process_snapshot()} overlayLog={tail}")
                if state == "redirected":
                    log(f"runoverlay injection confirmed: pid={process.pid} state=redirected")
                elif state == "error":
                    log(f"runoverlay injection error: pid={process.pid} overlayLog={tail}")
            if exit_code is not None:
                if self.process is process:
                    self.process = None
                time.sleep(0.5)
                self._analyze_latest_game_log(game_dir, skin_id, apply_started_at)
                return
            if game_seen and not game_running:
                time.sleep(0.5)
                self._analyze_latest_game_log(game_dir, skin_id, apply_started_at)
                return
            time.sleep(OVERLAY_DIAGNOSTIC_INTERVAL_SECONDS)
        if self.process is process:
            log(f"runoverlay diagnostics window expired: pid={process.pid} "
                f"gameProcess={self._game_process_snapshot()} overlayLog={self._overlay_log_tail()}")
            self._analyze_latest_game_log(game_dir, skin_id, apply_started_at)

    def apply(self, skin_id: int, record: dict, target_skin_id: int = 0) -> dict:
        apply_started_at = time.time()
        archive = SKINS / str(record.get("relativePath") or "")
        if not archive.is_file():
            log(f"Apply rejected: skin package missing: skinId={skin_id} archive={archive} skinsRoot={SKINS}")
            raise ApplyError("skin package is not cached")
        if not self.mod_tools.is_file():
            log(f"Apply rejected: mod-tools missing: path={self.mod_tools} toolsRoot={self.tools_dir}")
            raise ApplyError("mod-tools is missing")
        game_dir = self.game_dir()
        if game_dir is None:
            log(f"Apply rejected: game directory missing: configured={os.environ.get('AATROX_GAME_DIR', '') or '<empty>'}")
            raise ApplyError("League of Legends game directory was not found")
        game_executable = game_dir / "League of Legends.exe"
        try:
            log(f"Game executable check: path={game_executable} exists={game_executable.is_file()} "
                f"bytes={game_executable.stat().st_size if game_executable.is_file() else 0} "
                f"process={self._game_process_snapshot()}")
        except OSError as error:
            log(f"Game executable check failed: path={game_executable} error={error}")

        with self.lock:
            if self._game_process_running():
                snapshot = self._game_process_snapshot()
                log(f"Apply rejected while game is running: skinId={skin_id} process={snapshot}")
                raise ApplyError("游戏进程正在运行，请结束本局后再应用其他皮肤")
            self._stop()
            cache = self.cache_root / str(skin_id)
            cache.mkdir(parents=True, exist_ok=True)
            cached = cache / "skin.fantome"
            source_indices = self._source_indices(skin_id, record)
            same_champion_target = target_skin_id > 0 and target_skin_id // 1000 == skin_id // 1000
            target_index = target_skin_id % 1000 if same_champion_target else 0
            package_layout = self._archive_layout(archive)
            log(f"Apply requested: skinId={skin_id} targetSkinId={target_skin_id or 0} "
                f"sourceIndices={source_indices} targetIndex={target_index} "
                f"layout={package_layout} archive={archive} game={game_dir}")
            diagnostic_stages: dict[str, dict[str, dict[int, str]]] = {
                "game-original": self._diagnose_apply_wads(
                    skin_id, "game-original", game_dir, game_layout=True)
            }
            mapped = False
            hud_aliases: list[str] = []
            hud_invalid_removed = 0
            if same_champion_target and package_layout == "packed":
                mapped = self._alias_archive_for_sources(archive, cached, source_indices, target_index)
                if not mapped and target_index != 0:
                    raise ApplyError("skin package cannot be mapped to the client-selected slot")
            if same_champion_target and package_layout == "expanded":
                if skin_id // 1000 == 157:
                    # HUD mapping was verified as syntactically valid but the
                    # Tencent client still rendered it blank. Preserve the
                    # original game avatar instead of injecting a broken HUD.
                    log(f"Expanded HUD slot mapping skipped: skinId={skin_id} "
                        "fallback=original-yasuo-avatar")
                else:
                    hud_aliases = self._alias_expanded_hud_archive(
                        archive, cached, source_indices, target_index)
                if hud_aliases:
                    log(f"Expanded HUD slot mapping completed: skinId={skin_id} "
                        f"sourceIndices={source_indices} targetIndex={target_index} aliases={hud_aliases}")
            if not mapped and not hud_aliases:
                shutil.copy2(archive, cached)
                log(f"Apply package copied without slot rewrite: cached={cached}")
            elif hud_aliases:
                log(f"Apply package HUD mapping completed: cached={cached} targetIndex={target_index}")
            else:
                log(f"Apply package slot mapping completed: cached={cached} targetIndex={target_index}")
            if package_layout == "expanded":
                hud_invalid_removed = self._strip_invalid_hud_archive(
                    cached, cached, remove_all=(skin_id // 1000 == 157))
                if hud_invalid_removed:
                    log(f"Expanded HUD invalid resources removed: skinId={skin_id} "
                        f"count={hud_invalid_removed} fallback=original-avatar")
            (cache / "meta.json").write_text(json.dumps({"skinId": skin_id,
                "localPath": str(archive), "layout": package_layout}, ensure_ascii=False, indent=2), encoding="utf-8")

            self._clear(self.mods_dir)
            mod_name = f"skin_{skin_id}"
            mod_dir = self.mods_dir / mod_name
            if package_layout == "expanded":
                # mod-tools has a legacy MAX_PATH limitation while creating
                # deeply nested files from expanded Fantome packages. A
                # portable app can itself live under a long path, so import
                # into a short system-temp directory first, then copy the
                # completed mod tree beside the engine.
                import_stage: Path | None = None
                try:
                    temp_root = Path(tempfile.gettempdir()) / "CskinImport"
                    temp_root.mkdir(parents=True, exist_ok=True)
                    import_stage = Path(tempfile.mkdtemp(prefix=f"{skin_id}-", dir=str(temp_root)))
                    staged_mod_dir = import_stage / "mod"
                    log(f"Expanded package import staging: skinId={skin_id} "
                        f"archive={cached} archiveLength={len(str(cached))} "
                        f"stagedMod={staged_mod_dir} stagedLength={len(str(staged_mod_dir))} "
                        f"finalMod={mod_dir} finalLength={len(str(mod_dir))}")
                    import_result = self._run(["import", str(cached), str(staged_mod_dir),
                                               f"--game:{game_dir}", "--noTFT"])
                    if not staged_mod_dir.is_dir():
                        raise ApplyError(f"mod-tools import completed without output: {staged_mod_dir}")
                    self._clear(mod_dir)
                    shutil.copytree(staged_mod_dir, mod_dir, dirs_exist_ok=True)
                    import_output = (import_result.stdout or "").strip()
                    log(f"Expanded package imported: skinId={skin_id} stagedMod={staged_mod_dir} "
                        f"finalMod={mod_dir} output={import_output[-1000:] if import_output else '<empty>'}")
                finally:
                    if import_stage is not None:
                        shutil.rmtree(import_stage, ignore_errors=True)
                if same_champion_target:
                    mapped = self._alias_mod_for_sources(mod_dir, source_indices, target_index)
                    if not mapped and target_index != 0:
                        raise ApplyError("imported skin package cannot be mapped to the client-selected slot")
            else:
                self._extract(cached, mod_dir)
            diagnostic_stages["mod-before-compat"] = self._diagnose_apply_wads(
                skin_id, "mod-before-compat", mod_dir)
            removed_global_wads = self._filter_incompatible_global_wads(skin_id, mod_dir)
            repaired_wads = self._repair_incompatible_wad_entries(skin_id, mod_dir)
            diagnostic_stages["mod-after-compat"] = self._diagnose_apply_wads(
                skin_id, "mod-after-compat", mod_dir)
            try:
                extracted_files = list(mod_dir.rglob("*"))
                extracted_count = sum(1 for path in extracted_files if path.is_file())
            except OSError:
                extracted_count = 0
            log(f"Apply package extracted: mod={mod_dir} fileCount={extracted_count} "
                f"removedGlobalWads={removed_global_wads or '<none>'} "
                f"repairedWads={repaired_wads or '<none>'} "
                f"hudAliases={hud_aliases or '<none>'} "
                f"hudInvalidRemoved={hud_invalid_removed}")
            self._clear(self.overlay_dir)
            mk_result = self._run(["mkoverlay", str(self.mods_dir), str(self.overlay_dir),
                                   f"--game:{game_dir}", f"--mods:{mod_name}", "--noTFT", "--ignoreConflict"])
            config = self.overlay_dir / "cslol-config.json"
            mk_output = (mk_result.stdout or "").strip()
            if mk_output:
                log(f"mkoverlay output: {mk_output[-1000:]}")
            # configless mode intentionally accepts a missing cslol-config;
            # mod-tools writes the merged WADs under DATA/FINAL and the
            # runner can attach to an already-running League process without
            # a persisted config file.
            wad_paths, wad_bytes = self._overlay_wads()
            diagnostic_stages["overlay-final"] = self._diagnose_apply_wads(
                skin_id, "overlay-final", self.overlay_dir)
            for wad_name in sorted({name for stage in diagnostic_stages.values() for name in stage}):
                watched = self._WAD_DIAGNOSTIC_ENTRIES.get(wad_name, {})
                for path_hash, asset_name in watched.items():
                    values = []
                    for stage_name in ("game-original", "mod-before-compat", "mod-after-compat", "overlay-final"):
                        value = diagnostic_stages.get(stage_name, {}).get(wad_name, {}).get(path_hash, "<not-checked>")
                        values.append(f"{stage_name}={value}")
                    log(f"WAD evidence comparison: skinId={skin_id} wad={wad_name} "
                        f"path={asset_name} hash={path_hash:016x} {' '.join(values)}")
            wad_names = ",".join(str(path.relative_to(self.overlay_dir)) for path in wad_paths[:12]) or "<none>"
            log(f"mkoverlay completed: config={config} configExists={config.is_file()} "
                f"overlayExists={self.overlay_dir.is_dir()} wadCount={len(wad_paths)} "
                f"wadBytes={wad_bytes} wadFiles={wad_names}")
            if not self.overlay_dir.is_dir():
                raise ApplyError(f"mkoverlay 未生成覆盖层目录：{self.overlay_dir}")
            if not wad_paths:
                detail = mk_output[-600:] if mk_output else self._overlay_log_tail(600)
                raise ApplyError(f"mkoverlay 未生成 .wad.client 文件：{detail}")
            self.process = self._run(["runoverlay", str(self.overlay_dir), str(config),
                                      f"--game:{game_dir}", "--opts:configless"], background=True)
            time.sleep(0.5)
            exit_code = self.process.poll() if self.process is not None else None
            log(f"runoverlay status: pid={self.process.pid if self.process else 0} "
                f"exitCode={exit_code if exit_code is not None else '<running>'} "
                f"overlayLog={self._overlay_log_tail()}")
            if exit_code is not None:
                detail = self._overlay_log_tail(1200)
                self.process = None
                raise ApplyError(detail or "overlay did not start")

            injection_status, injection_detail = self._overlay_state()
            if injection_status == "error":
                detail = self._overlay_log_tail(1600)
                log(f"runoverlay injection failed: pid={self.process.pid if self.process else 0} "
                    f"status={injection_status} gameProcess={self._game_process_snapshot()} "
                    f"overlayLog={detail}")
                self._stop()
                raise ApplyError(f"覆盖层注入失败（{injection_status}）：{detail}")

            overlay_process = self.process
            if overlay_process is not None:
                threading.Thread(target=self._monitor_overlay,
                                 args=(overlay_process, skin_id, game_dir, apply_started_at),
                                 name="overlay-diagnostics", daemon=True).start()
            log(f"Overlay armed: skinId={skin_id} targetIndex={target_index} pid={self.process.pid if self.process else 0} "
                f"overlay={self.overlay_dir} config={config} game={game_dir} "
                f"injectionStatus={injection_status} overlayLog={injection_detail}")
            return {"ok": True, "skinId": skin_id, "mode": "manual-overlay", "overlayStatus": "armed",
                    "injectionStatus": injection_status,
                    "overlayWadCount": len(wad_paths), "overlayWadBytes": wad_bytes}


_lcu_lock = threading.Lock()
_lcu_connection: tuple[int, str] | None = None
_lcu_next_probe = 0.0
_last_selection: dict | None = None
_last_selection_at = 0.0
_last_selection_log_key: tuple | None = None
_last_lcu_error_at = 0.0


def _log_lcu_error(message: str) -> None:
    global _last_lcu_error_at
    now = time.monotonic()
    if now - _last_lcu_error_at >= 10:
        _last_lcu_error_at = now
        log(message)


def _publish_selection(value: dict) -> dict:
    global _last_selection_log_key
    key = (bool(value.get("available")), int(value.get("championId") or 0),
           int(value.get("selectedSkinId") or 0), str(value.get("phase") or ""))
    if key != _last_selection_log_key:
        _last_selection_log_key = key
        log(f"LCU selection: available={key[0]} championId={key[1]} "
            f"selectedSkinId={key[2]} phase={key[3] or '-'}")
    return value


def _read_lockfile(path: Path) -> tuple[int, str] | None:
    try:
        fields = path.read_text(encoding="utf-8", errors="replace").strip().split(":", 4)
        if len(fields) >= 5 and int(fields[2]) > 0 and fields[3].strip():
            return int(fields[2]), fields[3].strip()
    except (OSError, ValueError):
        pass
    return None


def _lcu_lockfiles() -> list[Path]:
    result: list[Path] = []
    configured = os.environ.get("AATROX_GAME_DIR", "").strip()
    if configured:
        game = Path(configured)
        result.append(game.parent / "LeagueClient" / "lockfile")
        result.append(game.parent / "lockfile")
    for drive in "CDEFGHIJKLMNOPQRSTUVWXYZ":
        root = Path(f"{drive}:/")
        result.extend([
            root / "wegameapps" / "英雄联盟" / "LeagueClient" / "lockfile",
            root / "Program Files" / "腾讯游戏" / "英雄联盟" / "LeagueClient" / "lockfile",
            root / "Program Files (x86)" / "腾讯游戏" / "英雄联盟" / "LeagueClient" / "lockfile",
            root / "Riot Games" / "League of Legends" / "lockfile",
        ])
    return list(dict.fromkeys(result))


def _command_line_connection() -> tuple[int, str] | None:
    command = ROOT / "wmic.exe"
    if not command.is_file():
        command = ROOT.parent / "wmic.exe"
    commands: list[list[str]] = []
    if command.is_file():
        commands.append([str(command), "process", "where",
                         "name='LeagueClientUx.exe' OR name='LeagueClient.exe' OR name='LeagueClientUxRender.exe'",
                         "get", "CommandLine", "/value"])
    script = ("Get-CimInstance Win32_Process -Filter \"Name='LeagueClientUx.exe' OR "
              "Name='LeagueClient.exe' OR Name='LeagueClientUxRender.exe'\" | "
              "ForEach-Object { \"$($_.ProcessId)|$($_.CommandLine)\" }")
    commands.append(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script])
    for command_args in commands:
        try:
            result = subprocess.run(command_args, capture_output=True, text=True, timeout=2.5,
                                    creationflags=int(getattr(subprocess, "CREATE_NO_WINDOW", 0)), check=False)
        except (OSError, subprocess.TimeoutExpired):
            continue
        for line in result.stdout.splitlines():
            command_line = line.split("|", 1)[-1]
            if command_line.lower().startswith("commandline="):
                command_line = command_line[len("CommandLine="):]
            port_match = re.search(r"--app-port(?:=|\s+)(\d+)", command_line)
            token_match = re.search(r'--remoting-auth-token(?:=|\s+)(?:"([^"]+)"|([^\s]+))', command_line)
            if not port_match or not token_match:
                continue
            token = token_match.group(1) or token_match.group(2)
            if token:
                return int(port_match.group(1)), token.strip('"')
    return None


def _discover_lcu() -> tuple[int, str] | None:
    global _lcu_connection, _lcu_next_probe
    now = time.monotonic()
    with _lcu_lock:
        if now < _lcu_next_probe:
            return _lcu_connection
        for path in _lcu_lockfiles():
            connection = _read_lockfile(path)
            if connection:
                _lcu_connection = connection
                _lcu_next_probe = now + 4
                return connection
        connection = _command_line_connection()
        _lcu_connection = connection
        _lcu_next_probe = now + 4
        return connection


def _parse_selection(session: dict) -> dict:
    def number(value) -> int:
        try:
            return int(value or 0)
        except (TypeError, ValueError):
            return 0

    local_cell = number(session.get("localPlayerCellId"))
    champion_id = number(session.get("championId"))
    champion_pick_intent = number(session.get("championPickIntent"))
    selected_skin_id = number(session.get("selectedSkinId"))
    for player in session.get("myTeam") or []:
        if local_cell and number(player.get("cellId")) == local_cell:
            champion_id = number(player.get("championId")) or champion_id
            champion_pick_intent = (number(player.get("championPickIntent")) or
                                    champion_pick_intent)
            selected_skin_id = number(player.get("selectedSkinId")) or selected_skin_id
            break
    for round_actions in session.get("actions") or []:
        if not isinstance(round_actions, list):
            continue
        for action in round_actions:
                if (str(action.get("type", "")).lower() == "pick" and
                        number(action.get("actorCellId")) == local_cell):
                    champion_id = number(action.get("championId")) or champion_id
                    champion_pick_intent = (number(action.get("championPickIntent")) or
                                            champion_pick_intent)
                    selected_skin_id = number(action.get("selectedSkinId")) or selected_skin_id
    if champion_id <= 0:
        champion_id = champion_pick_intent
    if champion_id > 0 and selected_skin_id // 1000 != champion_id:
        selected_skin_id = champion_id * 1000
    return {"available": True, "championId": champion_id,
            "selectedSkinId": selected_skin_id, "phase": str(session.get("phase") or "")}


def read_lcu_selection() -> dict:
    global _lcu_connection, _lcu_next_probe, _last_selection, _last_selection_at
    connection = _discover_lcu()
    if connection:
        port, token = connection
        client = HTTPSConnection("127.0.0.1", port,
                                 context=ssl._create_unverified_context(), timeout=1.5)
        try:
            client.request("GET", "/lol-champ-select/v1/session", headers={
                "Authorization": "Basic " + base64.b64encode(f"riot:{token}".encode()).decode()})
            response = client.getresponse()
            if response.status == HTTPStatus.NOT_FOUND:
                return _publish_selection({"available": True, "championId": 0,
                                           "selectedSkinId": 0, "phase": ""})
            if response.status < 200 or response.status >= 300:
                _log_lcu_error(f"LCU selection endpoint returned HTTP {response.status}")
                raise OSError(f"LCU returned HTTP {response.status}")
            value = _parse_selection(json.loads(response.read().decode("utf-8")))
            if value["championId"] > 0:
                _last_selection = value
                _last_selection_at = time.monotonic()
            return _publish_selection(value)
        except (OSError, ValueError, json.JSONDecodeError) as error:
            _log_lcu_error(f"LCU selection request failed: {error}")
            with _lcu_lock:
                _lcu_connection = None
                _lcu_next_probe = time.monotonic() + 2
        finally:
            client.close()
    if _last_selection is not None and time.monotonic() - _last_selection_at < 8:
        return _publish_selection(dict(_last_selection))
    _log_lcu_error("LCU unavailable: no valid lockfile or client process credentials")
    return _publish_selection({"available": False, "championId": 0,
                               "selectedSkinId": 0, "phase": "manual"})


class Handler(BaseHTTPRequestHandler):
    catalog: SkinCatalog
    applier: OverlayApplier

    def log_message(self, format: str, *args) -> None:
        return

    def send_json(self, payload: dict | list, status: int = HTTPStatus.OK) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        path = urlparse(self.path).path
        if path == "/api/health":
            self.send_json({"ok": True, "roseEngine": True, "engine": True})
        elif path == "/api/champions":
            with self.catalog.lock:
                self.send_json({"champions": self.catalog.champions})
        elif path.startswith("/api/champions/"):
            try:
                champion = self.catalog.champion(int(path.rsplit("/", 1)[1]))
            except ValueError:
                champion = None
            self.send_json(champion or {"error": "Champion not found"},
                           HTTPStatus.OK if champion else HTTPStatus.NOT_FOUND)
        elif path == "/api/champion-selection":
            self.send_json(read_lcu_selection())
        else:
            self.send_json({"error": "Not found"}, HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        path = urlparse(self.path).path
        if path == "/api/rebuild":
            try:
                self.catalog.load()
                with self.catalog.lock:
                    self.send_json({"ok": True, "skins": len(self.catalog.by_skin)})
            except (OSError, ValueError, json.JSONDecodeError) as error:
                log(f"Catalog rebuild failed: {error}")
                self.send_json({"error": "catalog rebuild failed"}, HTTPStatus.INTERNAL_SERVER_ERROR)
            return
        if path != "/api/apply":
            self.send_json({"error": "Not found"}, HTTPStatus.NOT_FOUND)
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(size).decode("utf-8"))
            skin_id = int(payload.get("skinId") or 0)
            target_skin_id = int(payload.get("targetSkinId") or 0)
            with self.catalog.lock:
                record = self.catalog.by_skin.get(skin_id)
            if not record:
                # Packages are cached after process startup. Reload once on a
                # miss so the first apply does not require a process restart.
                self.catalog.load()
                with self.catalog.lock:
                    record = self.catalog.by_skin.get(skin_id)
            if not record:
                raise ApplyError("skin is not in the local catalog")
            self.send_json(self.applier.apply(skin_id, record, target_skin_id))
        except (ApplyError, ValueError, json.JSONDecodeError) as error:
            log(f"Apply rejected for skin {locals().get('skin_id', 0)}: {error}")
            self.send_json({"error": str(error)}, HTTPStatus.BAD_REQUEST)
        except Exception as error:
            log(f"Apply failed: {error}")
            self.send_json({"error": f"apply failed: {error}"}, HTTPStatus.INTERNAL_SERVER_ERROR)


def main() -> None:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--no-browser", action="store_true")
    parser.parse_args()
    try:
        Handler.catalog = SkinCatalog()
    except Exception as error:
        log(f"Catalog initialization failed: {error}")
        Handler.catalog = object.__new__(SkinCatalog)
        Handler.catalog.champions = []
        Handler.catalog.by_skin = {}
        Handler.catalog.by_champion = {}
        Handler.catalog.lock = threading.RLock()
    Handler.applier = OverlayApplier()
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    log(f"Engine build: {ENGINE_BUILD} root={ROOT} frozen={FROZEN}")
    log(f"Selector ready at http://127.0.0.1:{server.server_port}/")
    try:
        server.serve_forever()
    finally:
        Handler.applier._stop()


if __name__ == "__main__":
    main()
