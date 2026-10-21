#!/usr/bin/env python3
"""Cross-check HeifContainer's Annex-B tiles against heif_tile_extract.py.

Usage: python cross_check_annexb.py <outdir written by HeifFastDecodeTest extract>

For every <outdir>/<stem>_<hash>/info.json the script re-runs the reference
Python parser on info["source"] and compares
  * tile count / order, per-tile ispe size,
  * tile Annex-B bytes (exact),
  * grid rows/columns/tile dims, primary item id.

The reference reimplements only the container walk, so an exact byte match on
every tile proves the managed parser produces the same elementary streams.
"""

from __future__ import annotations

import glob
import json
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "HeifTileBench")))
import heif_tile_extract as hx  # noqa: E402


def reference_tiles(ref):
    """(tile ids, hvcc by item, ispe by item) in dimg order; single item for non-grid."""
    ids = list(ref["tile_ids"])
    hvccs = dict(ref["_hvcc_by_item"])
    ispes = dict(ref["_ispe_by_item"])
    if ids:
        return ids, hvccs, ispes

    # 非网格主图:唯一瓦片就是主图项自身
    primary = ref["primary_id"]
    buf = ref["_buf"]
    hvcc = None
    ispe = None
    for ptype, box in ref["_item_props"](primary):
        if ptype == b"hvcC":
            hvcc = hx.parse_hvcc(buf, box)
        elif ptype == b"ispe":
            ispe = hx.parse_ispe(buf, box)
    if hvcc is None:
        return [], {}, {}
    return [primary], {primary: hvcc}, {primary: ispe}


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    outdir = os.path.abspath(sys.argv[1])
    dirs = sorted(glob.glob(os.path.join(outdir, "*", "info.json")))
    if not dirs:
        print(f"no info.json under {outdir}")
        return 2

    checked = 0
    failures = 0
    for info_path in dirs:
        with open(info_path, encoding="utf-8") as f:
            info = json.load(f)
        source = info["source"]
        stem = os.path.basename(os.path.dirname(info_path))
        ref = hx.analyze(source)
        item_data = ref["_item_data"]

        problems = []
        if info["primaryItemId"] != ref["primary_id"]:
            problems.append(f"primary id {info['primaryItemId']} != {ref['primary_id']}")

        grid = ref["grid"]
        if grid is not None:
            if (info["rows"], info["columns"]) != (grid["rows"], grid["cols"]):
                problems.append(
                    f"grid {info['rows']}x{info['columns']} != {grid['rows']}x{grid['cols']}")
            # Sony payloads carry only the output dims; tile dims live in each tile's ispe
            if not grid["fields_truncated"] and grid["tiles"]:
                w, h = grid["tiles"][0]
                if (info["tileWidth"], info["tileHeight"]) != (w, h):
                    problems.append(f"tile size {info['tileWidth']}x{info['tileHeight']} != {w}x{h}")

        ids, hvccs, ispes = reference_tiles(ref)
        if len(ids) != len(info["tiles"]):
            problems.append(f"tile count {len(info['tiles'])} != {len(ids)}")

        for tile in info["tiles"]:
            index = tile["index"]
            if index >= len(ids):
                problems.append(f"tile {index} has no reference counterpart")
                continue
            item_id = ids[index]
            hvcc = hvccs.get(item_id)
            if hvcc is None:
                problems.append(f"tile {index}: reference has no hvcC")
                continue
            expected = hx.annexb_from_hvcc(hvcc, item_data(item_id))
            with open(os.path.join(os.path.dirname(info_path), f"tile{index}.hevc"), "rb") as f:
                actual = f.read()
            if expected != actual:
                problems.append(
                    f"tile {index}: annexb differs (expected {len(expected)} B, got {len(actual)} B)")
            if ispes.get(item_id) and (tile["width"], tile["height"]) != ispes[item_id]:
                problems.append(
                    f"tile {index}: ispe {tile['width']}x{tile['height']} != {ispes[item_id]}")

        checked += 1
        if problems:
            failures += 1
            print(f"FAIL {stem} ({os.path.basename(source)})")
            for p in problems:
                print("   - " + p)
        else:
            print(f"ok   {stem} ({os.path.basename(source)}) {len(ids)} tiles")

    print(f"cross-check: {checked} files, {failures} mismatches")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
