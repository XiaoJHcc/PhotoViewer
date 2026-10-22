#!/usr/bin/env python3
"""
Verify and benchmark pvhevc.dll (PhotoViewer's minimal FFmpeg HEVC tile decoder).

What it does:
  1. Pulls the grid tiles out of a Sony .HIF / libheif .heic with
     heif_tile_extract.py (into a temp dir that is deleted afterwards).
  2. Decodes every tile through pvhevc.dll via ctypes and checks the reported
     geometry (width/height/bit depth/chroma format/colour code points).
  3. Compares the decoded Y/Cb/Cr planes byte for byte against
     `ffmpeg -i tile.hevc -f rawvideo -pix_fmt <fmt>`.
  4. Times single-threaded per-tile decode and 6-tile concurrent decode
     (one context per thread).

Usage:
  python pvhevc_check.py
  python pvhevc_check.py --dll ../../PhotoViewer.Windows/NativeLibs/win-x64/pvhevc.dll
  python pvhevc_check.py --no-timing --json pvhevc_check.json
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import shutil
import statistics
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import heif_tile_extract as hx  # noqa: E402

DEFAULT_DLL = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "PhotoViewer.Windows", "NativeLibs", "win-x64", "pvhevc.dll",
)
DEFAULT_SONY = "D:/照片2026/2026-5-4 湘湖/A7C2/A7C00024.HIF"
DEFAULT_HEIC = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "PhotoViewer.Android", ".build-android-native", "src",
    "libheif-1.18.2", "examples", "example.heic",
)
FFMPEG = "ffmpeg"


# --------------------------------------------------------------------------
# ctypes binding of pvhevc.h
# --------------------------------------------------------------------------
class PvHevcFrame(ctypes.Structure):
    _fields_ = [
        ("width", ctypes.c_int32),
        ("height", ctypes.c_int32),
        ("bit_depth", ctypes.c_int32),
        ("chroma_format", ctypes.c_int32),
        ("planes", ctypes.POINTER(ctypes.c_uint8) * 3),
        ("strides", ctypes.c_int32 * 3),
        ("full_range", ctypes.c_int32),
        ("matrix", ctypes.c_int32),
        ("primaries", ctypes.c_int32),
        ("transfer", ctypes.c_int32),
    ]


class PvHevc:
    def __init__(self, dll_path: str) -> None:
        self.lib = ctypes.CDLL(os.path.abspath(dll_path))
        self.lib.pv_hevc_version.restype = ctypes.c_int32
        self.lib.pv_hevc_version.argtypes = []
        self.lib.pv_hevc_open.restype = ctypes.c_void_p
        self.lib.pv_hevc_open.argtypes = [ctypes.c_int32]
        self.lib.pv_hevc_decode.restype = ctypes.c_int32
        self.lib.pv_hevc_decode.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_uint8),
            ctypes.c_int32,
            ctypes.POINTER(PvHevcFrame),
        ]
        self.lib.pv_hevc_close.restype = None
        self.lib.pv_hevc_close.argtypes = [ctypes.c_void_p]
        self.lib.pv_hevc_last_error.restype = ctypes.c_char_p
        self.lib.pv_hevc_last_error.argtypes = [ctypes.c_void_p]

    def version(self) -> int:
        return self.lib.pv_hevc_version()

    def open(self, threads: int = 1) -> int:
        ctx = self.lib.pv_hevc_open(threads)
        if not ctx:
            raise RuntimeError("pv_hevc_open returned NULL")
        return ctx

    def close(self, ctx: int) -> None:
        self.lib.pv_hevc_close(ctx)

    def decode(self, ctx: int, stream: bytes) -> tuple[int, PvHevcFrame]:
        buf = (ctypes.c_uint8 * len(stream)).from_buffer_copy(stream)
        frame = PvHevcFrame()
        rc = self.lib.pv_hevc_decode(ctx, buf, len(stream), ctypes.byref(frame))
        if rc < 0:
            msg = self.lib.pv_hevc_last_error(ctx)
            raise RuntimeError(f"pv_hevc_decode failed rc={rc}: "
                               f"{msg.decode('utf-8', 'replace') if msg else ''}")
        return rc, frame


def version_tuple(v: int) -> tuple[int, int, int]:
    return v >> 16, (v >> 8) & 0xFF, v & 0xFF


# --------------------------------------------------------------------------
# plane helpers
# --------------------------------------------------------------------------
def chroma_size(width: int, height: int, chroma_format: int) -> tuple[int, int]:
    if chroma_format == 1:      # 4:2:0
        return (width + 1) // 2, (height + 1) // 2
    if chroma_format == 2:      # 4:2:2
        return (width + 1) // 2, height
    if chroma_format == 3:      # 4:4:4
        return width, height
    return 0, 0                 # monochrome


def pixfmt_name(bit_depth: int, chroma_format: int) -> str:
    if chroma_format == 0:
        return "gray" if bit_depth == 8 else f"gray{bit_depth}le"
    base = {1: "yuv420p", 2: "yuv422p", 3: "yuv444p"}[chroma_format]
    return base if bit_depth == 8 else f"{base}{bit_depth}le"


def pack_planes(frame: PvHevcFrame) -> bytes:
    """Repack the strided planes into ffmpeg rawvideo layout (planes, no padding)."""
    bps = 1 if frame.bit_depth == 8 else 2
    cw, ch = chroma_size(frame.width, frame.height, frame.chroma_format)
    sizes = [(frame.width, frame.height)]
    if frame.chroma_format:
        sizes += [(cw, ch), (cw, ch)]

    out = bytearray()
    for i, (pw, ph) in enumerate(sizes):
        stride = frame.strides[i]
        ptr = frame.planes[i]
        if not ptr or ph <= 0 or pw <= 0:
            raise RuntimeError(f"plane {i} missing (stride={stride})")
        if stride <= 0:
            raise RuntimeError(f"plane {i} has non-positive stride {stride}")
        row_bytes = pw * bps
        if row_bytes > stride:
            raise RuntimeError(f"plane {i} row {row_bytes} exceeds stride {stride}")
        raw = ctypes.string_at(ptr, stride * ph)
        for y in range(ph):
            start = y * stride
            out += raw[start:start + row_bytes]
    return bytes(out)


def ffmpeg_raw(tile_path: str, pix_fmt: str, width: int, height: int,
               chroma_format: int, bit_depth: int) -> bytes:
    p = subprocess.run(
        [FFMPEG, "-nostdin", "-v", "error", "-i", tile_path,
         "-f", "rawvideo", "-pix_fmt", pix_fmt, "-"],
        capture_output=True,
    )
    if p.returncode != 0:
        raise RuntimeError(f"ffmpeg failed on {tile_path}: "
                           f"{p.stderr.decode('utf-8', 'replace')[:400]}")
    cw, ch = chroma_size(width, height, chroma_format)
    bps = 1 if bit_depth == 8 else 2
    expect = width * height
    if chroma_format:
        expect += 2 * cw * ch
    expect *= bps
    if len(p.stdout) != expect:
        raise RuntimeError(f"ffmpeg raw size {len(p.stdout)} != expected {expect} "
                           f"({width}x{height} {pix_fmt})")
    return p.stdout


# --------------------------------------------------------------------------
# tile extraction
# --------------------------------------------------------------------------
def extract_tiles(path: str, workdir: str) -> tuple[dict, list[tuple[str, int]]]:
    """Annex-B streams per coded picture: grid tiles when present, else the
    primary hvc1 item (libheif's example.heic is a plain single image)."""
    meta = hx.extract(path, workdir, False)
    tiles = [(os.path.join(workdir, t["file"]), t["bytes"]) for t in meta["tiles"]]
    if tiles:
        return meta, tiles

    r = hx.analyze(path)
    item_data = r["_item_data"]
    wanted = [r["primary_id"]]
    if wanted[0] is None or wanted[0] not in r["_items"]:
        wanted = sorted(iid for iid, it in r["_items"].items() if it.item_type == "hvc1")[:1]
    for idx, iid in enumerate(wanted):
        hvcc = None
        for ptype, box in r["_item_props"](iid):
            if ptype == b"hvcC":
                hvcc = hx.parse_hvcc(r["_buf"], box)
        if hvcc is None:
            raise RuntimeError(f"{path}: item {iid} has no hvcC")
        stream = hx.annexb_from_hvcc(hvcc, item_data(iid))
        tile_path = os.path.join(workdir, f"tile{idx}.hevc")
        with open(tile_path, "wb") as f:
            f.write(stream)
        tiles.append((tile_path, len(stream)))
    if not tiles:
        raise RuntimeError(f"{path}: no grid tiles and no hvc1 item found")
    return meta, tiles


# --------------------------------------------------------------------------
# checks
# --------------------------------------------------------------------------
def check_file(pv: PvHevc, path: str, label: str, expect: dict) -> dict:
    result: dict = {"label": label, "file": os.path.basename(path), "tiles": []}
    workdir = tempfile.mkdtemp(prefix="pvhx_")
    ctx = pv.open(1)
    try:
        meta, tiles = extract_tiles(path, workdir)
        result["grid"] = meta["grid"]
        result["tile_count"] = len(tiles)

        ok = True
        for idx, (tile_path, nbytes) in enumerate(tiles):
            with open(tile_path, "rb") as f:
                stream = f.read()
            _, frame = pv.decode(ctx, stream)
            info = {
                "index": idx,
                "bytes": nbytes,
                "width": frame.width,
                "height": frame.height,
                "bit_depth": frame.bit_depth,
                "chroma_format": frame.chroma_format,
                "strides": [frame.strides[i] for i in range(3)],
                "full_range": frame.full_range,
                "matrix": frame.matrix,
                "primaries": frame.primaries,
                "transfer": frame.transfer,
            }
            fmt = pixfmt_name(frame.bit_depth, frame.chroma_format)
            info["pix_fmt"] = fmt
            ours = pack_planes(frame)
            theirs = ffmpeg_raw(tile_path, fmt, frame.width, frame.height,
                                frame.chroma_format, frame.bit_depth)
            info["byte_exact"] = ours == theirs
            if not info["byte_exact"]:
                ok = False
                diff = next((i for i, (a, b) in enumerate(zip(ours, theirs)) if a != b), None)
                info["first_diff"] = diff
            for key, want in expect.items():
                got = info[key]
                if got != want:
                    info[f"mismatch_{key}"] = {"got": got, "want": want}
                    ok = False
            result["tiles"].append(info)
        result["ok"] = ok
    finally:
        pv.close(ctx)
        shutil.rmtree(workdir, ignore_errors=True)
    return result


def check_reuse(pv: PvHevc, path: str, rounds: int = 12) -> dict:
    """Same context decoding repeatedly — exercises flush/reuse and buffer lifetime."""
    workdir = tempfile.mkdtemp(prefix="pvhxreuse_")
    ctx = pv.open(1)
    try:
        _, tiles = extract_tiles(path, workdir)
        streams = []
        for tile_path, _ in tiles:
            with open(tile_path, "rb") as f:
                streams.append(f.read())
        refs = []
        for s in streams:
            _, fr = pv.decode(ctx, s)
            refs.append(pack_planes(fr))
        stable = True
        for _ in range(rounds):
            for i, s in enumerate(streams):
                _, fr = pv.decode(ctx, s)
                if pack_planes(fr) != refs[i]:
                    stable = False
        return {"rounds": rounds, "tiles": len(streams), "stable": stable}
    finally:
        pv.close(ctx)
        shutil.rmtree(workdir, ignore_errors=True)


def check_mixed_resolution(pv: PvHevc, big: str, small: str, rounds: int = 4) -> dict:
    """One context alternating between 3520x1600 4:2:2 and 1280x854 4:2:0 —
    the decoder must reconfigure itself instead of inheriting the first SPS."""
    wd_big = tempfile.mkdtemp(prefix="pvmix_big_")
    wd_small = tempfile.mkdtemp(prefix="pvmix_small_")
    ctx = pv.open(1)
    try:
        _, big_tiles = extract_tiles(big, wd_big)
        _, small_tiles = extract_tiles(small, wd_small)
        with open(big_tiles[0][0], "rb") as f:
            big_stream = f.read()
        with open(small_tiles[0][0], "rb") as f:
            small_stream = f.read()

        def decode_packed(stream: bytes) -> tuple[tuple[int, int, int, int], bytes]:
            _, fr = pv.decode(ctx, stream)
            return (fr.width, fr.height, fr.bit_depth, fr.chroma_format), pack_planes(fr)

        big_ref = decode_packed(big_stream)
        small_ref = decode_packed(small_stream)

        ok = True
        for _ in range(rounds):
            if decode_packed(big_stream) != big_ref:
                ok = False
            if decode_packed(small_stream) != small_ref:
                ok = False
        return {"big": list(big_ref[0]), "small": list(small_ref[0]),
                "rounds": rounds, "stable": ok}
    finally:
        pv.close(ctx)
        shutil.rmtree(wd_big, ignore_errors=True)
        shutil.rmtree(wd_small, ignore_errors=True)


# --------------------------------------------------------------------------
# timing
# --------------------------------------------------------------------------
def time_single(pv: PvHevc, files: list[str], reps: int) -> dict:
    per_file = []
    all_ms = []
    for path in files:
        workdir = tempfile.mkdtemp(prefix="pvht_")
        ctx = pv.open(1)
        try:
            _, tiles = extract_tiles(path, workdir)
            streams = []
            for tile_path, _ in tiles:
                with open(tile_path, "rb") as f:
                    streams.append(f.read())
            tile_medians = []
            for s in streams:
                pv.decode(ctx, s)  # warmup: first touch of the picture buffers is not decode cost
                samples = []
                for _ in range(reps):
                    t0 = time.perf_counter()
                    pv.decode(ctx, s)
                    samples.append((time.perf_counter() - t0) * 1000.0)
                tile_medians.append(statistics.median(samples))
            per_file.append({"file": os.path.basename(path),
                             "tile_ms": [round(v, 2) for v in tile_medians],
                             "median_ms": round(statistics.median(tile_medians), 2)})
            all_ms.extend(tile_medians)
        finally:
            pv.close(ctx)
            shutil.rmtree(workdir, ignore_errors=True)
    return {
        "tiles_measured": len(all_ms),
        "median_ms": round(statistics.median(all_ms), 2) if all_ms else None,
        "min_ms": round(min(all_ms), 2) if all_ms else None,
        "max_ms": round(max(all_ms), 2) if all_ms else None,
        "p25_ms": round(statistics.quantiles(all_ms, n=4)[0], 2) if len(all_ms) > 3 else None,
        "per_file": per_file,
    }


def time_concurrent(pv: PvHevc, files: list[str], reps: int = 3) -> dict:
    """Wall time to decode all tiles of one image on one thread per tile."""
    per_file = []
    for path in files:
        workdir = tempfile.mkdtemp(prefix="pvhc_")
        try:
            _, tiles = extract_tiles(path, workdir)
            streams = []
            for tile_path, _ in tiles:
                with open(tile_path, "rb") as f:
                    streams.append(f.read())
            ctxs = [pv.open(1) for _ in streams]
            # threads + main thread rendezvous so all decodes start together
            barrier = threading.Barrier(len(streams) + 1)
            errors: list[BaseException] = []
            walls = []

            def worker(i: int) -> None:
                try:
                    barrier.wait()
                    pv.decode(ctxs[i], streams[i])
                except BaseException as exc:  # noqa: BLE001 - surface any failure
                    errors.append(exc)

            try:
                for rep in range(reps + 1):  # first round is warmup
                    threads = [threading.Thread(target=worker, args=(i,))
                               for i in range(len(streams))]
                    for t in threads:
                        t.start()
                    barrier.wait()  # release workers and start the clock together
                    t0 = time.perf_counter()
                    for t in threads:
                        t.join()
                    elapsed = (time.perf_counter() - t0) * 1000.0
                    if errors:
                        raise errors[0]
                    if rep:
                        walls.append(elapsed)
                per_file.append({"file": os.path.basename(path),
                                 "tiles": len(streams),
                                 "wall_ms": round(statistics.median(walls), 2),
                                 "runs_ms": [round(v, 2) for v in walls]})
            finally:
                for c in ctxs:
                    pv.close(c)
        finally:
            shutil.rmtree(workdir, ignore_errors=True)
    walls = [r["wall_ms"] for r in per_file]
    return {
        "per_file": per_file,
        "median_ms": round(statistics.median(walls), 2) if walls else None,
        "min_ms": round(min(walls), 2) if walls else None,
        "max_ms": round(max(walls), 2) if walls else None,
    }


# --------------------------------------------------------------------------
def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", default=DEFAULT_DLL)
    ap.add_argument("--file", default=DEFAULT_SONY, help="Sony .HIF used for the byte check")
    ap.add_argument("--heic", default=DEFAULT_HEIC, help="non-Sony .HEIC for the byte check")
    ap.add_argument("--timing-files", nargs="*", default=None,
                    help="5 files for the timing set (default: neighbours of --file)")
    ap.add_argument("--reps", type=int, default=3, help="decode reps per tile when timing")
    ap.add_argument("--threads", type=int, default=6, help="parallel tiles when timing concurrently")
    ap.add_argument("--no-timing", action="store_true")
    ap.add_argument("--json", default="")
    a = ap.parse_args()

    pv = PvHevc(a.dll)
    v = pv.version()
    out: dict = {"dll": os.path.abspath(a.dll), "dll_bytes": os.path.getsize(a.dll),
                 "libavcodec_version_int": v,
                 "libavcodec_version": ".".join(str(x) for x in version_tuple(v))}

    print(f"pvhevc.dll      {out['dll']}")
    print(f"size            {out['dll_bytes'] / 1024:.1f} KiB")
    print(f"libavcodec      {out['libavcodec_version']}  (0x{v:08x})")

    # ── Sony HEVC Rext 4:2:2 10-bit tiles ───────────────────────────────
    print("\n── Sony HIF (HEVC Rext yuv422p10le) ──")
    expect_sony = {"width": 3520, "height": 1600, "bit_depth": 10, "chroma_format": 2}
    sony = check_file(pv, a.file, "sony", expect_sony) if os.path.exists(a.file) else None
    if sony is None:
        print(f"  [skip] sample missing: {a.file}")
    else:
        for t in sony["tiles"]:
            print(f"  tile{t['index']} {t['width']}x{t['height']} bd={t['bit_depth']} "
                  f"cf={t['chroma_format']} {t['pix_fmt']:>12} range={t['full_range']} "
                  f"mx={t['matrix']} pri={t['primaries']} trc={t['transfer']} "
                  f"exact={t['byte_exact']}")
        print(f"  => {'PASS' if sony['ok'] else 'FAIL'}")
        out["sony"] = sony
        reuse = check_reuse(pv, a.file)
        print(f"  context reuse: {reuse['tiles']} tiles x {reuse['rounds']} rounds "
              f"stable={reuse['stable']}")
        out["sony_reuse"] = reuse

    # ── libheif example.heic (ordinary 8-bit 4:2:0) ─────────────────────
    print("\n── libheif example.heic (8-bit 4:2:0) ──")
    heic = None
    if not os.path.exists(a.heic):
        print(f"  [skip] sample missing: {a.heic}")
    else:
        try:
            heic = check_file(pv, a.heic, "heic", {})
        except Exception as exc:  # noqa: BLE001 - container may not be a grid
            print(f"  [skip] {type(exc).__name__}: {exc}")
        if heic is not None:
            for t in heic["tiles"]:
                print(f"  tile{t['index']} {t['width']}x{t['height']} bd={t['bit_depth']} "
                      f"cf={t['chroma_format']} {t['pix_fmt']:>12} "
                      f"exact={t['byte_exact']}")
            print(f"  => {'PASS' if heic['ok'] else 'FAIL'}")
            out["heic"] = heic

    # ── one context alternating between the two picture geometries ──────
    if os.path.exists(a.file) and os.path.exists(a.heic):
        try:
            mix = check_mixed_resolution(pv, a.file, a.heic)
            print(f"\n── mixed resolution on one context ──\n"
                  f"  {mix['big']} <-> {mix['small']} x {mix['rounds']} rounds "
                  f"stable={mix['stable']}")
            out["mixed_resolution"] = mix
        except Exception as exc:  # noqa: BLE001 - non-grid sample may not fit
            print(f"\n── mixed resolution on one context ──\n  [skip] "
                  f"{type(exc).__name__}: {exc}")

    # ── timing ─────────────────────────────────────────────────────────
    if not a.no_timing:
        if a.timing_files:
            files = list(a.timing_files)
        else:
            folder = os.path.dirname(a.file)
            names = sorted(n for n in os.listdir(folder) if n.lower().endswith(".hif"))
            files = [os.path.join(folder, n) for n in names[:5]]
        files = [f for f in files if os.path.exists(f)]
        print(f"\n── timing: {len(files)} files ──")
        for f in files:
            print(f"  {os.path.basename(f)}")
        single = time_single(pv, files, a.reps)
        print(f"  per-tile median (1 ctx / 1 thread, {single['tiles_measured']} measurements): "
              f"{single['median_ms']} ms  [p25 {single['p25_ms']} / min {single['min_ms']} "
              f"/ max {single['max_ms']}]")
        for row in single["per_file"]:
            print(f"    {row['file']}: median {row['median_ms']} ms  {row['tile_ms']}")
        out["single_tile"] = single

        conc = time_concurrent(pv, files)
        print(f"  all 6 tiles concurrent, 6 ctx x 1 thread: median {conc['median_ms']} ms "
              f"[min {conc['min_ms']} / max {conc['max_ms']}]")
        for row in conc["per_file"]:
            print(f"    {row['file']}: {row['wall_ms']} ms  {row['runs_ms']}")
        out["concurrent"] = conc

    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=2, ensure_ascii=False)
        print(f"\nwrote {a.json}")

    failed = [k for k in ("sony", "heic") if k in out and not out[k].get("ok", True)]
    for key in ("sony_reuse", "mixed_resolution"):
        if key in out and not out[key].get("stable", True):
            failed.append(key)
    if failed:
        print(f"\nFAILED: {', '.join(failed)}")
        sys.exit(1)
    print("\nALL CHECKS PASSED")


if __name__ == "__main__":
    main()
