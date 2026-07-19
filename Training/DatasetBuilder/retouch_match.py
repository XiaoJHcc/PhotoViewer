#!/usr/bin/env python3
"""Training/DatasetBuilder/retouch_match.py — 精修回溯匹配（Plan-3-1 §1.1 前置确认项）。

扫各事件文件夹下的 OUT-JPG* 目录（ACR 精修导出件，命名 = 原片名 + "@JPG" 后缀），
按 **文件名（剥 @JPG）+ 事件** 匹配库内直出原片指纹。

产出（--out 目录，默认 D:/PhotoDB/dataset）：
  - retouched.txt   平铺安全子集（跨事件无同名），兼容 DatasetBuilder manifest 的 retouchedList
  - collisions.txt  跨事件同名但事件域内可唯一确指的记录（由 --apply 事件域落库，不进 retouched.txt）
  - unmatched.txt   仍未命中的 OUT-JPG 文件（手动补清单）

默认只扫描+产清单；加 --apply 才把结果写库：
  - event_label 非空的组（manifest 10 事件）先置 is_retouched=0，再对全部事件域命中置 1；
  - event_label 为空的组（20240212 旧批）不动、留 NULL（无精修信息）；
  - 冲突记录按事件域落库（OUT-JPG 文件夹的事件归属即证据），重庆春天等同名异片不误标。

命名变体：`@JPG_1` 等 ACR 重复导出序号自动剥除；个别手工改名走 MANUAL_ALIAS（用户裁定后登记）。
精修像素是 OOD 绝不入模——本脚本只产出"是否精修"顶层标记（plan-3-0 §0.4 / §1 决策 13）。

用法:
    python Training/DatasetBuilder/retouch_match.py [--db ...] [--out ...] [--apply]
"""

from __future__ import annotations

import argparse
import re
import sqlite3
from collections import defaultdict
from pathlib import Path

# 事件目录 → 库内 event_label。目录命名规则 = eventLabel + " P2"（F:\照片2026-P2 现行结构）。
P2_SUFFIX = " P2"
EVENT_ROOTS = [Path("F:/照片2026-P2")]

# 手工别名：OUT-JPG 文件名基（剥 @JPG 后）→ 库内原片基名（用户逐案裁定后登记）。
# 2026-07-19：A7C01320-Mix15 = 绍兴 A7C01320 的 15 张混合导出件，挂同事件基片（重庆春天同名 5★ 片为异片，不标）。
MANUAL_ALIAS: dict[str, str] = {
    "A7C01320-Mix15": "A7C01320",
}

# ACR 导出后缀：@JPG 可带重复导出序号（@JPG_1 / @JPG_2 ...）。
EXPORT_SUFFIX = re.compile(r"@JPG(_\d+)?$", re.IGNORECASE)


def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(description="精修回溯匹配：OUT-JPG 文件名 → 库内原片 is_retouched")
    ap.add_argument("--db", default="D:/PhotoDB/dataset/photos_dataset.db")
    ap.add_argument("--out", default="D:/PhotoDB/dataset", help="retouched/unmatched/collisions 输出目录")
    ap.add_argument("--apply", action="store_true", help="把匹配结果写库（事件域；缺省只产清单不写库）")
    return ap.parse_args()


def main() -> int:
    args = parse_args()
    out_dir = Path(args.out)

    # 1) 发现全部 OUT-JPG* 目录并解析事件标签
    targets: list[tuple[Path, str]] = []  # (out_jpg_dir, event_label)
    for root in EVENT_ROOTS:
        for event_dir in sorted(p for p in root.iterdir() if p.is_dir()):
            for sub in sorted(p for p in event_dir.iterdir() if p.is_dir()):
                if sub.name.upper().startswith("OUT-JPG"):
                    label = event_dir.name
                    if label.endswith(P2_SUFFIX):
                        label = label[: -len(P2_SUFFIX)]
                    targets.append((sub, label))
    if not targets:
        print("[ERROR] 未发现任何 OUT-JPG* 目录")
        return 1

    # 2) 库内索引：event_label → filename_noext → [(fingerprint, rating)]；跨事件同名检测
    conn = sqlite3.connect(args.db, timeout=60)
    conn.execute("PRAGMA busy_timeout=60000")
    by_event: dict[str, dict[str, list[tuple[str, int]]]] = defaultdict(lambda: defaultdict(list))
    name_events: dict[str, set[str]] = defaultdict(set)
    for fp, name, event, rating in conn.execute(
        "SELECT fingerprint, filename_noext, COALESCE(event_label,''), rating FROM photos"
    ):
        by_event[event][name].append((fp, rating))
        name_events[name].add(event)
    db_events = {e for e in by_event if e}

    flat_safe: list[str] = []           # 平铺安全命中（基名跨事件唯一）
    resolved_hits: list[tuple[str, str]] = []  # 全部事件域命中（event, base），--apply 用
    collision_notes: list[str] = []     # 跨事件同名（事件域已确指，防平铺故不进 retouched.txt）
    unmatched: list[str] = []
    n_files = 0

    print(f"{'事件':<22}{'OUT-JPG文件':>10}{'命中':>6}{'未命中':>7}{'跨事件同名':>9}")
    for out_jpg, event in targets:
        files = [
            f for f in out_jpg.rglob("*")
            if f.is_file() and f.suffix.upper() == ".JPG" and not f.name.startswith("._")
        ]
        n_files += len(files)
        n_hit = n_miss = n_col = 0
        if event not in db_events:
            print(f"[WARN] 事件标签不在库内: {event}（{out_jpg}）")
        index = by_event.get(event, {})
        for f in files:
            base = EXPORT_SUFFIX.sub("", f.stem)
            base = MANUAL_ALIAS.get(base, base)
            hits = index.get(base, [])
            if not hits:
                unmatched.append(f"{event}: {f.name}")
                n_miss += 1
                continue
            if len(hits) > 1:
                unmatched.append(f"{event}: {f.name}（事件内同名 {len(hits)} 组，需手动）")
                n_miss += 1
                continue
            resolved_hits.append((event, base))
            n_hit += 1
            if len(name_events[base]) > 1:
                others = "、".join(sorted(name_events[base] - {event})) or "?"
                collision_notes.append(f"{event}: {base}（同名亦见于 {others}；事件域已确指={event}，由 --apply 落库）")
                n_col += 1
            else:
                flat_safe.append(base)
        print(f"{event:<22}{len(files):>10}{n_hit:>6}{n_miss:>7}{n_col:>9}")

    # 3) 清单落盘
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "retouched.txt").write_text("\n".join(sorted(set(flat_safe))) + "\n", encoding="utf-8")
    (out_dir / "unmatched.txt").write_text("\n".join(unmatched) + ("\n" if unmatched else ""), encoding="utf-8")
    (out_dir / "collisions.txt").write_text("\n".join(collision_notes) + ("\n" if collision_notes else ""), encoding="utf-8")
    print(f"\n合计 OUT-JPG {n_files} 件 → 事件域命中 {len(resolved_hits)}（平铺安全 {len(set(flat_safe))}）· 未命中 {len(unmatched)} · 跨事件同名 {len(collision_notes)}")
    print(f"输出: {out_dir / 'retouched.txt'} | unmatched.txt | collisions.txt")

    if not args.apply:
        print("（未写库；加 --apply 落库 is_retouched）")
        return 0

    # 4) 落库：in-scope 事件先置 0，再对事件域命中置 1；event_label 空（旧批）不动
    with conn:
        conn.execute("UPDATE photos SET is_retouched=0 WHERE event_label IS NOT NULL AND event_label != ''")
        conn.executemany(
            "UPDATE photos SET is_retouched=1 WHERE event_label=? AND filename_noext=?",
            resolved_hits,
        )
    n1 = conn.execute("SELECT COUNT(*) FROM photos WHERE is_retouched=1").fetchone()[0]
    n0 = conn.execute("SELECT COUNT(*) FROM photos WHERE is_retouched=0").fetchone()[0]
    nnull = conn.execute("SELECT COUNT(*) FROM photos WHERE is_retouched IS NULL").fetchone()[0]
    dist_rows = conn.execute(
        "SELECT rating, COUNT(*) FROM photos WHERE is_retouched=1 GROUP BY rating ORDER BY rating"
    ).fetchall()
    print(f"\n[apply] is_retouched: 1={n1} · 0={n0} · NULL={nnull}（旧批留 NULL）")
    print(f"[apply] 精修原片星级分布: " + " · ".join(f"{r}★={c}" for r, c in dist_rows))
    per_event = conn.execute(
        "SELECT event_label, COUNT(*) FROM photos WHERE is_retouched=1 GROUP BY event_label ORDER BY event_label"
    ).fetchall()
    for e, c in per_event:
        print(f"  {e}: {c}")
    conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
