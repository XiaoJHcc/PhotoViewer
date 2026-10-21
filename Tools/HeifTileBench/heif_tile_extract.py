#!/usr/bin/env python3
"""
Extract Sony HEIF (ISOBMFF) grid tile items as Annex-B HEVC elementary streams.

Why this exists: FFmpeg 7.1's HEIF demuxer cannot split Sony .HIF NAL units
("Error splitting the input into NAL units"), so benchmarks need raw .hevc
streams pulled straight out of the container.

What it parses:
  ftyp
  meta                (FullBox container)
    hdlr pitm iinf/infe iloc iref(dimg) iprp/ipco(hvcC,ispe,colr)/ipma

Output per file:
  <stem>/tile<N>.hevc   Annex-B stream: VPS/SPS/PPS from that tile's hvcC
                        followed by the item payload with length prefixes
                        rewritten to 4-byte start codes
  <stem>/grid.json      grid layout (rows/cols/tile dims/output dims) plus
                        item ids, so the shell/Python driver can build an
                        ffmpeg xstack/vstack command

Usage:
  python heif_tile_extract.py FILE.HIF --outdir DIR [--all-items] [--dump]
"""

from __future__ import annotations

import argparse
import json
import os
import struct
import sys
from dataclasses import dataclass, field
from typing import Any

CONTAINER_BOXES = {b"moov", b"trak", b"mdia", b"minf", b"stbl", b"dinf", b"edts", b"iprp", b"ipco"}
FULLBOX_CONTAINERS = {b"meta", b"iinf", b"iref", b"meco", b"mere"}

# --------------------------------------------------------------------------
# box walking
# --------------------------------------------------------------------------
@dataclass
class Box:
    type: bytes
    offset: int  # start of box (including header)
    size: int
    header: int  # header length
    payload_off: int
    payload_len: int
    children: list["Box"] = field(default_factory=list)


def read_boxes(buf: bytes, start: int, end: int) -> list[Box]:
    out: list[Box] = []
    off = start
    while off + 8 <= end:
        size = struct.unpack_from(">I", buf, off)[0]
        btype = buf[off + 4 : off + 8]
        header = 8
        if size == 1:
            if off + 16 > end:
                break
            size = struct.unpack_from(">Q", buf, off + 8)[0]
            header = 16
        elif size == 0:
            size = end - off
        if size < header or off + size > end:
            break
        out.append(Box(btype, off, size, header, off + header, size - header))
        off += size
    return out


def parse_tree(buf: bytes, start: int, end: int) -> list[Box]:
    boxes = read_boxes(buf, start, end)
    for b in boxes:
        if b.type in CONTAINER_BOXES:
            b.children = parse_tree(buf, b.payload_off, b.payload_off + b.payload_len)
        elif b.type == b"iinf":
            # FullBox + entry_count (u16 for v0, u32 for v1+) precede the infe boxes
            ver = buf[b.payload_off]
            skip = b.payload_off + 4 + (2 if ver == 0 else 4)
            b.children = parse_tree(buf, skip, b.payload_off + b.payload_len)
        elif b.type in FULLBOX_CONTAINERS:
            # FullBox: 1 byte version + 3 bytes flags precede children
            b.children = parse_tree(buf, b.payload_off + 4, b.payload_off + b.payload_len)
    return boxes


def find(boxes: list[Box], btype: bytes) -> Box | None:
    for b in boxes:
        if b.type == btype:
            return b
    return None


def find_all(boxes: list[Box], btype: bytes) -> list[Box]:
    return [b for b in boxes if b.type == btype]


def walk(boxes: list[Box], depth: int = 0):
    for b in boxes:
        yield b, depth
        yield from walk(b.children, depth + 1)


# --------------------------------------------------------------------------
# specific box parsers
# --------------------------------------------------------------------------
@dataclass
class ItemInfo:
    item_id: int
    item_type: str
    name: str
    content_type: str = ""
    content_encoding: str = ""


def parse_infe(buf: bytes, box: Box) -> ItemInfo:
    p = box.payload_off
    version = buf[p]
    q = p + 4
    if version >= 3:
        item_id = struct.unpack_from(">I", buf, q)[0]
        q += 4
    else:
        item_id = struct.unpack_from(">H", buf, q)[0]
        q += 2
    q += 2  # item_protection_index
    item_type = buf[q : q + 4].decode("latin-1")
    q += 4
    # item_name then content_type / content_encoding, all null-terminated
    def cstr(pos: int) -> tuple[str, int]:
        e = buf.index(b"\x00", pos)
        return buf[pos:e].decode("utf-8", "replace"), e + 1

    name, q = cstr(q)
    ct = ce = ""
    if item_type == "mime":
        ct, q = cstr(q)
        ce, q = cstr(q)
    return ItemInfo(item_id, item_type, name, ct, ce)


@dataclass
class Extent:
    offset: int
    length: int


@dataclass
class ItemLoc:
    item_id: int
    construction_method: int
    base_offset: int
    extents: list[Extent]


def parse_iloc(buf: bytes, box: Box) -> list[ItemLoc]:
    p = box.payload_off
    version = buf[p]
    q = p + 4
    b = buf[q]
    offset_size, length_size = b >> 4, b & 0xF
    b = buf[q + 1]
    base_offset_size, index_size = b >> 4, b & 0xF
    q += 2
    if version < 2:
        item_count = struct.unpack_from(">H", buf, q)[0]
        q += 2
    else:
        item_count = struct.unpack_from(">I", buf, q)[0]
        q += 4

    def vsize(pos: int, n: int) -> int:
        return int.from_bytes(buf[pos : pos + n], "big") if n else 0

    items: list[ItemLoc] = []
    for _ in range(item_count):
        if version < 2:
            item_id = struct.unpack_from(">H", buf, q)[0]
            q += 2
        else:
            item_id = struct.unpack_from(">I", buf, q)[0]
            q += 4
        cm = 0
        if version in (1, 2):
            cm = struct.unpack_from(">H", buf, q)[0] & 0xF
            q += 2
        q += 2  # data_reference_index
        base = vsize(q, base_offset_size)
        q += base_offset_size
        ec = struct.unpack_from(">H", buf, q)[0]
        q += 2
        extents = []
        for _ in range(ec):
            if index_size:
                q += index_size
            eo = vsize(q, offset_size)
            q += offset_size
            el = vsize(q, length_size)
            q += length_size
            extents.append(Extent(eo, el))
        items.append(ItemLoc(item_id, cm, base, extents))
    return items


def parse_iref(buf: bytes, box: Box) -> dict[str, list[tuple[int, list[int]]]]:
    """Returns {ref_type: [(from_id, [to_ids...]), ...]} (version 0: 16-bit ids)."""
    p = box.payload_off
    version = buf[p]
    out: dict[str, list[tuple[int, list[int]]]] = {}
    q = p + 4
    end = p + box.payload_len
    id_size = 4 if version else 2
    fmt = ">I" if version else ">H"
    while q + 8 <= end:
        bsize = struct.unpack_from(">I", buf, q)[0]
        if bsize < 8:
            break
        rtype = buf[q + 4 : q + 8].decode("latin-1")
        q += 8
        bend = q + bsize - 8
        frm = struct.unpack_from(fmt, buf, q)[0]
        q += id_size
        cnt = struct.unpack_from(">H", buf, q)[0]
        q += 2
        targets = []
        for _ in range(cnt):
            targets.append(struct.unpack_from(fmt, buf, q)[0])
            q += id_size
        out.setdefault(rtype, []).append((frm, targets))
        q = bend
    return out


def parse_ipma(buf: bytes, box: Box) -> dict[int, list[tuple[int, bool]]]:
    p = box.payload_off
    version = buf[p]
    flags = int.from_bytes(buf[p + 1 : p + 4], "big")
    q = p + 4
    entry_count = struct.unpack_from(">I", buf, q)[0]
    q += 4
    out: dict[int, list[tuple[int, bool]]] = {}
    for _ in range(entry_count):
        if version < 1:
            item_id = struct.unpack_from(">H", buf, q)[0]
            q += 2
        else:
            item_id = struct.unpack_from(">I", buf, q)[0]
            q += 4
        ac = buf[q]
        q += 1
        props = []
        for _ in range(ac):
            if flags & 1:
                v = struct.unpack_from(">H", buf, q)[0]
                q += 2
                essential = bool(v & 0x8000)
                idx = v & 0x7FFF
            else:
                v = buf[q]
                q += 1
                essential = bool(v & 0x80)
                idx = v & 0x7F
            props.append((idx, essential))
        out[item_id] = props
    return out


@dataclass
class HvcC:
    length_size_minus_one: int
    nal_arrays: list[tuple[int, list[bytes]]]  # (nal_unit_type, [nal bytes])


def parse_hvcc(buf: bytes, box: Box) -> HvcC:
    p = box.payload_off
    length_size_minus_one = buf[p + 21] & 0x3
    num_arrays = buf[p + 22]
    q = p + 23
    arrays: list[tuple[int, list[bytes]]] = []
    for _ in range(num_arrays):
        hdr = buf[q]
        nal_type = hdr & 0x3F
        q += 1
        num_nalus = struct.unpack_from(">H", buf, q)[0]
        q += 2
        nalus = []
        for _ in range(num_nalus):
            ln = struct.unpack_from(">H", buf, q)[0]
            q += 2
            nalus.append(buf[q : q + ln])
            q += ln
        arrays.append((nal_type, nalus))
    return HvcC(length_size_minus_one, arrays)


def parse_ispe(buf: bytes, box: Box) -> tuple[int, int]:
    p = box.payload_off + 4
    w, h = struct.unpack_from(">II", buf, p)
    return w, h


def parse_grid_payload(data: bytes) -> dict[str, Any]:
    """ISO/IEC 23008-12 ImageGrid.

    Note: Sony .HIF writes rows_minus_one/cols_minus_one for a 3x2 grid but only
    one 32-bit field (the canvas size) instead of rows*cols tile fields, so the
    field list is parsed best-effort and flagged when short.
    """
    version = data[0]
    flags = data[1]
    rows_minus_one = data[2]
    cols_minus_one = data[3]
    rows = rows_minus_one + 1
    cols = cols_minus_one + 1
    tiles = []
    q = 4
    while q + 4 <= len(data) and len(tiles) < rows * cols:
        v = struct.unpack_from(">I", data, q)[0]
        q += 4
        w = (v >> 16) & 0xFFFF
        h = v & 0xFFFF
        tiles.append((w, h))
    return {
        "version": version,
        "flags": flags,
        "rows": rows,
        "cols": cols,
        "tiles": tiles,
        "expected_fields": rows * cols,
        "payload_bytes": len(data),
        "payload_hex": data.hex(),
        "fields_truncated": len(tiles) < rows * cols,
    }


# --------------------------------------------------------------------------
# top level
# --------------------------------------------------------------------------
def annexb_from_hvcc(hvcc: HvcC, payload: bytes) -> bytes:
    out = bytearray()
    for _nal_type, nalus in hvcc.nal_arrays:
        for nal in nalus:
            out += b"\x00\x00\x00\x01" + nal
    n = hvcc.length_size_minus_one + 1
    q = 0
    while q + n <= len(payload):
        ln = int.from_bytes(payload[q : q + n], "big")
        q += n
        if ln == 0 or q + ln > len(payload):
            break
        out += b"\x00\x00\x00\x01" + payload[q : q + ln]
        q += ln
    return bytes(out)


def analyze(path: str) -> dict[str, Any]:
    with open(path, "rb") as f:
        buf = f.read()
    top = parse_tree(buf, 0, len(buf))
    meta = find(top, b"meta")
    if meta is None:
        raise SystemExit(f"{path}: no meta box")
    kids = meta.children

    pitm = find(kids, b"pitm")
    primary_id = None
    if pitm is not None:
        p = pitm.payload_off
        ver = buf[p]
        primary_id = struct.unpack_from(">H" if ver == 0 else ">I", buf, p + 4)[0]

    iinf = find(kids, b"iinf")
    items: dict[int, ItemInfo] = {}
    if iinf is not None:
        for infe in find_all(iinf.children, b"infe"):
            info = parse_infe(buf, infe)
            items[info.item_id] = info

    iloc = find(kids, b"iloc")
    locs = {l.item_id: l for l in (parse_iloc(buf, iloc) if iloc else [])}

    iref = find(kids, b"iref")
    refs = parse_iref(buf, iref) if iref else {}

    iprp = find(kids, b"iprp")
    props: list[Box] = []
    assoc: dict[int, list[tuple[int, bool]]] = {}
    if iprp is not None:
        ipco = find(iprp.children, b"ipco")
        if ipco is not None:
            props = ipco.children  # 1-based indices
        ipma = find(iprp.children, b"ipma")
        if ipma is not None:
            assoc = parse_ipma(buf, ipma)

    def item_props(item_id: int) -> list[tuple[bytes, Box]]:
        return [(props[i - 1].type, props[i - 1]) for i, _e in assoc.get(item_id, []) if 0 < i <= len(props)]

    idat = find(kids, b"idat")
    idat_start = idat.payload_off if idat is not None else 0

    def item_data(item_id: int) -> bytes:
        loc = locs[item_id]
        base = idat_start if loc.construction_method == 1 else 0
        chunks = []
        for e in loc.extents:
            start = base + loc.base_offset + e.offset
            chunks.append(buf[start : start + e.length])
        return b"".join(chunks)

    tiles: list[int] = []
    for frm, targets in refs.get("dimg", []):
        if frm == primary_id:
            tiles = targets

    grid = None
    if primary_id is not None and items.get(primary_id, ItemInfo(0, "", "")).item_type == "grid":
        grid = parse_grid_payload(item_data(primary_id))

    result: dict[str, Any] = {
        "file": os.path.basename(path),
        "size_bytes": len(buf),
        "primary_id": primary_id,
        "grid": grid,
        "tile_ids": tiles,
        "items": [],
        "_buf": buf,
        "_locs": locs,
        "_hvcc_by_item": {},
        "_ispe_by_item": {},
        "_item_data": item_data,
        "_items": items,
        "_item_props": item_props,
    }

    for iid, info in sorted(items.items()):
        props_list = item_props(iid)
        ptypes = [t.decode("latin-1") for t, _ in props_list]
        ispe = None
        hvcc = None
        for t, b in props_list:
            if t == b"ispe":
                ispe = parse_ispe(buf, b)
            elif t == b"hvcC":
                hvcc = parse_hvcc(buf, b)
        loc = locs.get(iid)
        total = sum(e.length for e in loc.extents) if loc else 0
        result["items"].append(
            {
                "id": iid,
                "type": info.item_type,
                "name": info.name,
                "ispe": ispe,
                "props": ptypes,
                "extents": len(loc.extents) if loc else 0,
                "bytes": total,
                "construction_method": loc.construction_method if loc else None,
            }
        )
        if iid in tiles:
            result["_hvcc_by_item"][iid] = hvcc
            result["_ispe_by_item"][iid] = ispe
    return result


def dump_boxes(path: str) -> None:
    with open(path, "rb") as f:
        buf = f.read()
    top = parse_tree(buf, 0, len(buf))
    for b, d in walk(top):
        extra = ""
        if b.type == b"ftyp":
            extra = " major=" + buf[b.payload_off : b.payload_off + 4].decode("latin-1")
        print(f"{'  ' * d}{b.type.decode('latin-1')} off={b.offset} size={b.size}{extra}")


def extract(path: str, outdir: str, want_all: bool) -> dict[str, Any]:
    r = analyze(path)
    os.makedirs(outdir, exist_ok=True)
    g = r["grid"]
    hvccs = r["_hvcc_by_item"]
    item_data = r["_item_data"]
    ispes = r["_ispe_by_item"]

    written = []
    for idx, iid in enumerate(r["tile_ids"]):
        hvcc = hvccs.get(iid)
        if hvcc is None:
            print(f"  ! tile item {iid} has no hvcC, skipping", file=sys.stderr)
            continue
        stream = annexb_from_hvcc(hvcc, item_data(iid))
        fn = os.path.join(outdir, f"tile{idx}.hevc")
        with open(fn, "wb") as f:
            f.write(stream)
        written.append(
            {
                "index": idx,
                "item_id": iid,
                "file": os.path.basename(fn),
                "bytes": len(stream),
                "ispe": ispes.get(iid),
                "length_size": hvcc.length_size_minus_one + 1,
                "nal_types": sorted({t for t, _ in hvcc.nal_arrays}),
                "nal_counts": {t: len(n) for t, n in hvcc.nal_arrays},
            }
        )

    meta = {
        "source": path,
        "source_bytes": r["size_bytes"],
        "primary_item": r["primary_id"],
        "grid": g,
        "tiles": written,
        "items": r["items"],
    }

    if want_all:
        # also dump every hvc1/jpeg item for reference
        for it in r["items"]:
            iid = it["id"]
            if it["type"] == "hvc1" and iid not in r["tile_ids"]:
                hvcc = None
                for t, b in r["_item_props"](iid):
                    if t == b"hvcC":
                        hvcc = parse_hvcc(r["_buf"], b)
                if hvcc:
                    with open(os.path.join(outdir, f"item{iid}.hevc"), "wb") as f:
                        f.write(annexb_from_hvcc(hvcc, item_data(iid)))
            elif it["type"] == "jpeg":
                with open(os.path.join(outdir, f"item{iid}.jpg"), "wb") as f:
                    f.write(item_data(iid))

    with open(os.path.join(outdir, "grid.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
    return meta


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--outdir", "-o")
    ap.add_argument("--all-items", action="store_true")
    ap.add_argument("--dump", action="store_true")
    ap.add_argument("--json", action="store_true", help="print item summary")
    a = ap.parse_args()

    if a.dump:
        dump_boxes(a.file)
        return
    if a.json:
        r = analyze(a.file)
        r.pop("_buf", None)
        for k in [k for k in r if k.startswith("_")]:
            r.pop(k)
        print(json.dumps(r, indent=2, ensure_ascii=False))
        return
    if not a.outdir:
        ap.error("--outdir required")
    m = extract(a.file, a.outdir, a.all_items)
    print(json.dumps({"grid": m["grid"], "tiles": [(t["file"], t["bytes"], t["ispe"]) for t in m["tiles"]]}, indent=2))


if __name__ == "__main__":
    main()
