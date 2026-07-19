"""
abs_pair_sampler.py — M1 §1.5 绝对性探针：跨段候选对采样器（Plan-3-1 §1.5）

目的：从库内采 ~110 对"跨段/跨事件"照片对，交人工标注"哪张绝对更好"（盲掉库内星级，
防锚定），供绝对性探针测"CLS 特征差异能否线性可分人工明确可判的绝对优劣对"。

分层（固定随机种子 42，共 ~110 对；每对带 stratum 标签）：
    a) 同星跨事件（rating 相同、event 不同）：1★/2★/3★/4★ 各 12 对 = 48（宪法挑战 3 构型）
    b) 跨星跨事件：|Δ|=1 约 18 对 + |Δ|≥2 约 12 对 = 30
    c) 事件内跨段（同 event、不同拍摄段[gap>10min]、同 rating）= 20（优先大事件）
    d) 极值对照（4-5★ vs 0-1★，跨事件）= 10
约束：同一照片最多出现在 2 对里；每对两照片 capture_time 不同日或不同段；
     抽出的每一对两边文件都经 os.path.exists 验证，不存在则换样重抽。

路径还原：source_rel_path 是库内相对路径。按"事件过滤 + 后缀匹配"还原——候选根 =
manifest folders[] 中 eventLabel 等于该照片 event_label 的文件夹（旧批 20240212
event_label 为 ''，根 = D:/PhotoDB/20240212）；拼不出再回退全量根。已验证该口径
9418/9418 命中且事件内无歧义（跨事件存在同名文件，不过滤会误配）。
可查看文件选择：formats 含 jpg 且同目录存在同基名 .JPG → 标注用 JPG 路径（好打开）；
否则用代表文件原路径（HIF/ARW 由用户查看器读）。

产出（写 D:/PhotoDB/dataset/）：
    abs_pairs_label.csv — 用户标注用：pair_id,stratum,photo_a,photo_b,winner
        （winner 留空；只含绝对路径，无 rating/无事件名——盲标；头部 # 注释写标注规则；
        stratum 只给 a/b/c/d 大类，不泄露星级构型）
    abs_pairs_key.csv — 答案键（仅内部分析用）：pair_id,stratum,event_a,event_b,
        rating_a,rating_b,seg_a,seg_b,fp_a,fp_b
    控制台摘要：各层对数、事件覆盖、文件存在率（须 100%）。

段切分复用 Training/probes/feature_probe.py 的 split_segments（sys.path 注入 import，
与 data_audit.py 同一约定），gap=10min 全局按 capture_time 升序切；不同事件天然落
在不同段，故 a/b/d 跨事件即满足"不同日或不同段"。

用法（仓根 D:/Git/PhotoViewer 下）：
    PYTHONUTF8=1 Tools/.venv/Scripts/python.exe Training/audit/abs_pair_sampler.py
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import random
import sqlite3
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

# 复用 feature_probe 的段切分（与 data_audit.py 同一约定）
_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_ROOT / "Training" / "probes"))
from feature_probe import split_segments  # noqa: E402

DB_DEFAULT = "D:/PhotoDB/dataset/photos_dataset.db"
MANIFEST_DEFAULT = "D:/PhotoDB/dataset/manifest.2026-07-19.json"
OUT_DIR_DEFAULT = "D:/PhotoDB/dataset"
OLD_BATCH_ROOT = "D:/PhotoDB/20240212"   # 旧批 20240212 快速模式入库根
OLD_BATCH_TAG = "20240212"               # key CSV 里 '' 事件的可读标记
SEED = 42
GAP_MIN = 10.0                           # 段切分 gap（分钟），与探针主口径一致
MAX_USES = 2                             # 同一照片最多出现的对数
TARGETS = {"a": (1, 2, 3, 4), "a_per_star": 12, "b_d1": 18, "b_d2": 12, "c": 20, "d": 10}
ATTEMPT_FACTOR = 500                     # 每层尝试上限 = 目标 × ATTEMPT_FACTOR

LABEL_HEADER = [
    "# 绝对优劣标注：每行一对照片（绝对路径）。并排打开两张图，判断哪张『绝对更好』",
    "# （画质/曝光/构图/瞬间等综合第一观感，不对比任何星级信息）。",
    "# winner 列填: A=左(photo_a)好  B=右(photo_b)好  tie=判不出/相当  skip=文件打不开",
    "# stratum 为内部分层标记（a/b/c/d 大类），标注时无需关注。",
    "# 一行一对，逗号分隔；路径含中文，请用 UTF-8 打开本文件。",
]


@dataclass
class Photo:
    """一个指纹组的采样单元：身份 + 星级 + 时间 + 段号 + 事件 + 相对路径 + 格式。"""
    fp: str
    name: str
    rating: int
    event: str            # '' = 旧批 20240212
    time: datetime
    rel: str
    formats: str
    seg: int = -1
    path: str | None = field(default=None)   # resolve 缓存（标注用绝对路径）


@dataclass
class Pair:
    fine: str             # 细分 stratum（key CSV 用，可含构型信息）
    coarse: str           # 大类 stratum（label CSV 用：a/b/c/d）
    a: Photo
    b: Photo


# ---------------------------------------------------------------------------
# 数据装载（只读）+ 段切分
# ---------------------------------------------------------------------------

def load_photos(db_path: str) -> list[Photo]:
    """读 photos 全量行（只读）。capture_time 解析失败的剔除（打印计数）。"""
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)   # 显式只读
    try:
        rows = conn.execute(
            "SELECT fingerprint, filename_noext, capture_time, rating, "
            "       COALESCE(event_label,''), source_rel_path, formats "
            "FROM photos"
        ).fetchall()
    finally:
        conn.close()

    photos: list[Photo] = []
    n_bad_time = 0
    for fp, name, ctime, rating, event, rel, formats in rows:
        try:
            t = datetime.fromisoformat(str(ctime))
        except (TypeError, ValueError):
            n_bad_time += 1
            continue
        photos.append(Photo(fp=str(fp), name=str(name), rating=int(rating),
                            event=str(event), time=t, rel=str(rel),
                            formats=str(formats or "")))
    if n_bad_time:
        print(f"[WARN] {n_bad_time} 组 capture_time 解析失败，已剔除")
    photos.sort(key=lambda p: p.time)
    seg = split_segments([p.time for p in photos], GAP_MIN)
    for p, s in zip(photos, seg):
        p.seg = int(s)
    print(f"样本 {len(photos)} 组（gap>{GAP_MIN:.0f}min 切得 {int(seg.max()) + 1} 段）")
    return photos


# ---------------------------------------------------------------------------
# 绝对路径还原（事件过滤 + 后缀匹配 + JPG 优先）
# ---------------------------------------------------------------------------

class Resolver:
    """按事件过滤 manifest 根还原绝对路径；formats 含 jpg 时优先同基名 .JPG。"""

    def __init__(self, manifest_path: str):
        with open(manifest_path, encoding="utf-8") as f:
            mf = json.load(f)
        self.ev_roots: dict[str, list[str]] = defaultdict(list)
        for folder in mf["folders"]:
            self.ev_roots[folder["eventLabel"]].append(folder["path"])
        self.all_roots = [folder["path"] for folder in mf["folders"]]
        self.n_resolved = 0
        self.n_jpg_preferred = 0
        self.n_fallback = 0

    def _prefer_jpg(self, path: str, formats: str) -> str:
        """formats 含 jpg 且同目录存在同基名 .JPG/.jpg → 用 JPG 路径（好打开）。"""
        if "jpg" not in formats.lower().split("|"):
            return path
        base = os.path.splitext(path)[0]
        for ext in (".JPG", ".jpg"):
            cand = base + ext
            if os.path.exists(cand):
                if cand != path:
                    self.n_jpg_preferred += 1
                return cand
        return path

    def resolve(self, p: Photo) -> str | None:
        if p.path is not None:
            return p.path
        cands = list(self.ev_roots.get(p.event, []))
        if not p.event:
            cands.append(OLD_BATCH_ROOT)
        for root in cands:
            cand = os.path.join(root, p.rel)
            if os.path.exists(cand):
                p.path = self._prefer_jpg(cand, p.formats)
                self.n_resolved += 1
                return p.path
        # 回退：全量根 + 旧批根（事件过滤拼不出时）
        for root in [*self.all_roots, OLD_BATCH_ROOT]:
            cand = os.path.join(root, p.rel)
            if os.path.exists(cand):
                p.path = self._prefer_jpg(cand, p.formats)
                self.n_resolved += 1
                self.n_fallback += 1
                return p.path
        return None


# ---------------------------------------------------------------------------
# 采样器
# ---------------------------------------------------------------------------

class Sampler:
    def __init__(self, rng: random.Random, resolver: Resolver):
        self.rng = rng
        self.resolver = resolver
        self.uses: dict[str, int] = defaultdict(int)
        self.used_pairs: set[tuple[str, str]] = set()
        self.pairs: list[Pair] = []

    def commit(self, fine: str, coarse: str, a: Photo, b: Photo) -> bool:
        """约束检查（同照片≤2 对、对不重复、不同日或不同段、两边文件存在）后入列。
        左右顺序随机化，避免某构型系统性地固定在左/右。"""
        if a.fp == b.fp:
            return False
        key = (a.fp, b.fp) if a.fp < b.fp else (b.fp, a.fp)
        if key in self.used_pairs:
            return False
        if self.uses[a.fp] >= MAX_USES or self.uses[b.fp] >= MAX_USES:
            return False
        if a.time.date() == b.time.date() and a.seg == b.seg:
            return False
        if self.resolver.resolve(a) is None or self.resolver.resolve(b) is None:
            return False
        if self.rng.random() < 0.5:
            a, b = b, a
        self.used_pairs.add(key)
        self.uses[a.fp] += 1
        self.uses[b.fp] += 1
        self.pairs.append(Pair(fine, coarse, a, b))
        return True


def _index_by_rating_event(photos: list[Photo]) -> dict[int, dict[str, list[Photo]]]:
    by: dict[int, dict[str, list[Photo]]] = defaultdict(lambda: defaultdict(list))
    for p in photos:
        by[p.rating][p.event].append(p)
    return by


def sample_a(sam: Sampler, by: dict, per_star: int, stars=(1, 2, 3, 4)) -> list[str]:
    """a) 同星跨事件：stars 各 per_star 对。"""
    gaps = []
    for r in stars:
        got, att = 0, 0
        evs = [e for e, ps in by[r].items() if ps]
        while got < per_star and att < per_star * ATTEMPT_FACTOR:
            att += 1
            if len(evs) < 2:
                break
            e1, e2 = sam.rng.sample(evs, 2)
            a = sam.rng.choice(by[r][e1])
            b = sam.rng.choice(by[r][e2])
            if sam.commit(f"a_same_star_r{r}", "a", a, b):
                got += 1
        if got < per_star:
            gaps.append(f"a) {r}★ 同星跨事件只采到 {got}/{per_star}（候选事件数 {len(evs)}）")
    return gaps


def _sample_cross_star(sam: Sampler, by: dict, target: int, min_d: int, max_d: int,
                       fine: str) -> str | None:
    """b) 通用：跨星跨事件，rating 差落在 [min_d, max_d]。"""
    combos = [(r1, r2) for r1 in range(6) for r2 in range(6)
              if min_d <= abs(r1 - r2) <= max_d]
    got, att = 0, 0
    while got < target and att < target * ATTEMPT_FACTOR:
        att += 1
        r1, r2 = combos[sam.rng.randrange(len(combos))]
        e1 = sam.rng.choice(list(by[r1].keys()))
        e2 = sam.rng.choice(list(by[r2].keys()))
        if e1 == e2:
            continue
        a = sam.rng.choice(by[r1][e1])
        b = sam.rng.choice(by[r2][e2])
        if sam.commit(fine, "b", a, b):
            got += 1
    if got < target:
        return f"b) {fine} 只采到 {got}/{target}"
    return None


def sample_c(sam: Sampler, photos: list[Photo], target: int) -> str | None:
    """c) 事件内跨段：同 event、不同段（gap>10min）、同 rating。事件按大小降序轮询。"""
    by_ev: dict[str, list[Photo]] = defaultdict(list)
    for p in photos:
        by_ev[p.event].append(p)
    events = sorted(by_ev, key=lambda e: -len(by_ev[e]))
    # 每事件按 (rating, seg) 分组
    groups = {}
    for e in events:
        g: dict[tuple[int, int], list[Photo]] = defaultdict(list)
        for p in by_ev[e]:
            g[(p.rating, p.seg)].append(p)
        groups[e] = g

    got, att, ei = 0, 0, 0
    while got < target and att < target * ATTEMPT_FACTOR:
        att += 1
        e = events[ei % len(events)]
        ei += 1
        g = groups[e]
        # 该事件内至少跨 2 段的 rating 集合（均匀选 rating，避免被 0★ 主导）
        r_segs: dict[int, list[int]] = defaultdict(list)
        for (r, s), ps in g.items():
            if ps:
                r_segs[r].append(s)
        ok = [r for r, ss in r_segs.items() if len(ss) >= 2]
        if not ok:
            continue
        r = sam.rng.choice(ok)
        s1, s2 = sam.rng.sample(r_segs[r], 2)
        a = sam.rng.choice(g[(r, s1)])
        b = sam.rng.choice(g[(r, s2)])
        if sam.commit("c_within_event_cross_seg", "c", a, b):
            got += 1
    if got < target:
        return f"c) 事件内跨段只采到 {got}/{target}"
    return None


def sample_d(sam: Sampler, by: dict, target: int) -> str | None:
    """d) 极值对照：4-5★ vs 0-1★，跨事件。"""
    hi = defaultdict(list)
    lo = defaultdict(list)
    for r in (4, 5):
        for e, ps in by[r].items():
            hi[e].extend(ps)
    for r in (0, 1):
        for e, ps in by[r].items():
            lo[e].extend(ps)
    got, att = 0, 0
    while got < target and att < target * ATTEMPT_FACTOR:
        att += 1
        e1 = sam.rng.choice(list(hi.keys()))
        e2 = sam.rng.choice(list(lo.keys()))
        if e1 == e2:
            continue
        a = sam.rng.choice(hi[e1])
        b = sam.rng.choice(lo[e2])
        if sam.commit("d_extreme_hi_vs_lo", "d", a, b):
            got += 1
    if got < target:
        return f"d) 极值对照只采到 {got}/{target}"
    return None


# ---------------------------------------------------------------------------
# 产出 CSV + 摘要
# ---------------------------------------------------------------------------

def write_csvs(pairs: list[Pair], out_dir: str) -> tuple[Path, Path]:
    label_path = Path(out_dir) / "abs_pairs_label.csv"
    key_path = Path(out_dir) / "abs_pairs_key.csv"
    with open(label_path, "w", newline="", encoding="utf-8") as f:
        for line in LABEL_HEADER:
            f.write(line + "\n")
        w = csv.writer(f)
        w.writerow(["pair_id", "stratum", "photo_a", "photo_b", "winner"])
        for i, pr in enumerate(pairs, 1):
            w.writerow([f"P{i:03d}", pr.coarse, pr.a.path, pr.b.path, ""])
    with open(key_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["pair_id", "stratum", "event_a", "event_b", "rating_a", "rating_b",
                    "seg_a", "seg_b", "fp_a", "fp_b"])
        for i, pr in enumerate(pairs, 1):
            ea = pr.a.event or OLD_BATCH_TAG
            eb = pr.b.event or OLD_BATCH_TAG
            w.writerow([f"P{i:03d}", pr.fine, ea, eb, pr.a.rating, pr.b.rating,
                        pr.a.seg, pr.b.seg, pr.a.fp, pr.b.fp])
    return label_path, key_path


def print_summary(pairs: list[Pair], resolver: Resolver, gaps: list[str]) -> None:
    print("\n========== 采样摘要 ==========")
    n = len(pairs)
    by_coarse: dict[str, int] = defaultdict(int)
    by_fine: dict[str, int] = defaultdict(int)
    for pr in pairs:
        by_coarse[pr.coarse] += 1
        by_fine[pr.fine] += 1
    print(f"总对数: {n}")
    for fine in sorted(by_fine):
        print(f"  {fine:<28} {by_fine[fine]:>3} 对")

    # 事件覆盖（一张算一次参与）
    ev_count: dict[str, int] = defaultdict(int)
    for pr in pairs:
        ev_count[pr.a.event or OLD_BATCH_TAG] += 1
        ev_count[pr.b.event or OLD_BATCH_TAG] += 1
    print(f"\n事件覆盖（{len(ev_count)}/{len(ev_count)} 个事件有照片参与，按参与次数降序）:")
    for e, c in sorted(ev_count.items(), key=lambda kv: -kv[1]):
        print(f"  {e:<28} {c:>3} 次")

    # 文件存在率复查（resolve 已验证，这里对最终路径再 exists 一遍）
    n_exist = sum(1 for pr in pairs
                  if os.path.exists(pr.a.path) and os.path.exists(pr.b.path))
    print(f"\n文件存在率: {n_exist}/{n} = {n_exist / n * 100:.1f}%"
          f"（JPG 优先命中 {resolver.n_jpg_preferred} 张，回退根还原 {resolver.n_fallback} 张）")

    # 约束复查
    n_diff = sum(1 for pr in pairs
                 if pr.a.time.date() != pr.b.time.date() or pr.a.seg != pr.b.seg)
    uses: dict[str, int] = defaultdict(int)
    for pr in pairs:
        uses[pr.a.fp] += 1
        uses[pr.b.fp] += 1
    print(f"不同日或不同段: {n_diff}/{n}；单照片出现次数: "
          f"1 次={sum(1 for v in uses.values() if v == 1)} 张 · "
          f"2 次={sum(1 for v in uses.values() if v == 2)} 张 · "
          f">2 次={sum(1 for v in uses.values() if v > 2)} 张（上限 {MAX_USES}）")

    if gaps:
        print("\n[WARN] 采样缺口:")
        for g in gaps:
            print("  " + g)
    else:
        print("\n无采样缺口，各层均采满。")


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description="M1 §1.5 绝对性探针：跨段候选对采样器")
    ap.add_argument("--db", default=DB_DEFAULT)
    ap.add_argument("--manifest", default=MANIFEST_DEFAULT)
    ap.add_argument("--out", default=OUT_DIR_DEFAULT)
    ap.add_argument("--seed", type=int, default=SEED)
    args = ap.parse_args()

    if not Path(args.db).exists():
        print(f"[ERROR] 库不存在: {args.db}", file=sys.stderr)
        return 1
    if not Path(args.manifest).exists():
        print(f"[ERROR] manifest 不存在: {args.manifest}", file=sys.stderr)
        return 1

    photos = load_photos(args.db)
    resolver = Resolver(args.manifest)
    rng = random.Random(args.seed)
    sam = Sampler(rng, resolver)
    by = _index_by_rating_event(photos)

    gaps: list[str] = []
    gaps += sample_a(sam, by, TARGETS["a_per_star"], TARGETS["a"])
    for fine, target, lo_d, hi_d in (("b_cross_star_d1", TARGETS["b_d1"], 1, 1),
                                     ("b_cross_star_d2plus", TARGETS["b_d2"], 2, 5)):
        g = _sample_cross_star(sam, by, target, lo_d, hi_d, fine)
        if g:
            gaps.append(g)
    g = sample_c(sam, photos, TARGETS["c"])
    if g:
        gaps.append(g)
    g = sample_d(sam, by, TARGETS["d"])
    if g:
        gaps.append(g)

    # 打乱顺序后编号（相邻对不总同构型；pair_id 不泄露分层顺序）
    rng.shuffle(sam.pairs)

    label_path, key_path = write_csvs(sam.pairs, args.out)
    print_summary(sam.pairs, resolver, gaps)
    print(f"\n标注清单: {label_path}")
    print(f"答案键:   {key_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
