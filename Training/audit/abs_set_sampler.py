"""
abs_set_sampler.py — M1 §1.5 绝对性探针（重标集方案，替代 108 对标注方案）

从库内抽 ~200 张 rating≥3★ 的照片，复制成中性文件名副本并抹掉内嵌 XMP 星级，
交用户按锦标赛习惯从 3 往上重新标 3-5★。本工具只做：抽样 + 复制 + 抹星 + 验证。

流程（seed 42 可复现）：
    1. 抽样 ~200 张 ≥3★：每事件至少 8 张，其余按事件 ≥3★ 占比最大余数法分配，合计 200；
       事件内先按 3/4/5 = 53:27:20（全库 489:249:184）最大余数法定 rating 配额
       （池不够时赤字顺移 3→4→5 再循环），每个 (事件,rating) 池内按拍摄段 round-robin
       摊匀抽取；每张只用一次。
    2. 复制到 D:/PhotoDB/dataset/abs_set/：中性顺序名 P0001..P0200，保留原扩展名
       （formats 含 jpg 的组复制 .JPG，其余复制代表 .HIF；不复制 ARW、不复制 .xmp
       sidecar——sidecar 会带星级，FingerprintGrouper.ResolveRating 会读它）。
       编号前先整体 shuffle，编号不泄露事件/星级分组。
    3. 抹星级（关键）：HIF/JPG 内嵌 XMP 的 xmp:Rating 有元素形式 <xmp:Rating>4</xmp:Rating>
       与属性形式 xmp:Rating="4"（两种实测均存在）。对副本做等长字节替换：
       xmp:Rating>[0-5]< → xmp:Rating>0< 、xmp:Rating="[0-5]" → xmp:Rating="0"
       （长度不变，容器偏移不受影响）。逐文件报告命中与替换；替换后对该文件再 grep
       校验 xmp:Rating>[1-5] / xmp:Rating="[1-5]" 必须 0 命中，否则报错退出。
    4. 答案键 D:/PhotoDB/dataset/abs_set_key.csv（内部用，不进 abs_set）：
       new_name,fingerprint,event_label,old_rating,seg_id,orig_path。
    5. 验证：跑 dotnet run --project Training/DatasetBuilder -- <abs_set> --scan-only，
       应扫出 200 文件 → 200 指纹组、1★-5★ 全 0（任何非 0 星级 = 抹除失败，报错）。

路径还原沿用 abs_pair_sampler.py 的做法：manifest folders[] 中 eventLabel 等于该照片
event_label 的文件夹为候选根（跨事件存在同名文件，不过滤会误配），旧批 20240212
（event_label NULL→''）根 = D:/PhotoDB/20240212；全部 os.path.exists 验证。
段切分复用 feature_probe.split_segments（gap=10min 全局 120 段，与探针主口径一致）。

用法（仓根 D:/Git/PhotoViewer 下）：
    PYTHONUTF8=1 Tools/.venv/Scripts/python.exe Training/audit/abs_set_sampler.py
    （--no-scan 跳过 dotnet scan-only 验证；--total/--seed/--out 可改默认）
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import random
import re
import shutil
import sqlite3
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

# 复用 feature_probe 的段切分（与 data_audit.py / abs_pair_sampler.py 同一约定）
_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_ROOT / "Training" / "probes"))
from feature_probe import split_segments  # noqa: E402

DB_DEFAULT = "D:/PhotoDB/dataset/photos_dataset.db"
MANIFEST_DEFAULT = "D:/PhotoDB/dataset/manifest.2026-07-19.json"
OUT_DIR_DEFAULT = "D:/PhotoDB/dataset"
ABS_SET_SUBDIR = "abs_set"
KEY_CSV_NAME = "abs_set_key.csv"
OLD_BATCH_ROOT = "D:/PhotoDB/20240212"   # 旧批 20240212 快速模式入库根
OLD_BATCH_TAG = "20240212"               # key CSV 里 '' 事件的可读标记
SEED = 42
GAP_MIN = 10.0                           # 段切分 gap（分钟），与探针主口径一致
TOTAL = 200                              # 目标抽样总数（±5 可接受）
MIN_PER_EVENT = 8                        # 每事件最少抽样数
RATING_W = {3: 0.53, 4: 0.27, 5: 0.20}   # 旧 rating 目标比例（全库 489:249:184）

# 抹星：元素形式与属性形式（等长替换）；POST 为替换后不得再命中的校验式
RE_ELEM = re.compile(rb"xmp:Rating>[0-5]<")
RE_ATTR = re.compile(rb'xmp:Rating="[0-5]"')
RE_ELEM_LEFT = re.compile(rb"xmp:Rating>[1-5]")
RE_ATTR_LEFT = re.compile(rb'xmp:Rating="[1-5]"')


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
    path: str | None = field(default=None)   # resolve 缓存（复制源绝对路径）


# ---------------------------------------------------------------------------
# 数据装载（只读）+ 段切分
# ---------------------------------------------------------------------------

def load_photos(db_path: str) -> list[Photo]:
    """读 photos 全量行（只读，按 fingerprint 排序保证可复现）。capture_time 解析失败的剔除。"""
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)   # 显式只读
    try:
        rows = conn.execute(
            "SELECT fingerprint, filename_noext, capture_time, rating, "
            "       COALESCE(event_label,''), source_rel_path, formats "
            "FROM photos ORDER BY fingerprint"
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
# 绝对路径还原（事件过滤 + 后缀匹配 + JPG 优先），与 abs_pair_sampler.py 同口径
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
        self.n_fallback = 0

    def resolve(self, p: Photo) -> str | None:
        """返回复制源绝对路径（jpg 组返回 .JPG，其余返回代表文件）；不存在返回 None。"""
        if p.path is not None:
            return p.path
        cands = list(self.ev_roots.get(p.event, []))
        if not p.event:
            cands.append(OLD_BATCH_ROOT)
        rep = None
        for root in cands:
            cand = os.path.join(root, p.rel)
            if os.path.exists(cand):
                rep = cand
                break
        if rep is None:   # 回退：全量根 + 旧批根（事件过滤拼不出时）
            for root in [*self.all_roots, OLD_BATCH_ROOT]:
                cand = os.path.join(root, p.rel)
                if os.path.exists(cand):
                    rep = cand
                    self.n_fallback += 1
                    break
        if rep is None:
            return None
        if "jpg" in p.formats.lower().split("|"):   # jpg 组：优先同基名 .JPG
            base = os.path.splitext(rep)[0]
            for ext in (".JPG", ".jpg"):
                cand = base + ext
                if os.path.exists(cand):
                    rep = cand
                    break
        p.path = rep
        return p.path


# ---------------------------------------------------------------------------
# 抽样
# ---------------------------------------------------------------------------

def largest_remainder(weights: dict, total: int) -> dict:
    """按权重把 total 整数分配到各键（最大余数法），返回 {键: 数量}，合计 == total。"""
    raw = {k: total * w for k, w in weights.items()}
    base = {k: int(v) for k, v in raw.items()}
    rem = total - sum(base.values())
    for k in sorted(weights, key=lambda k: -(raw[k] - base[k])):
        if rem <= 0:
            break
        base[k] += 1
        rem -= 1
    return base


def allocate_quotas(counts: dict[str, int], total: int, min_per: int) -> dict[str, int]:
    """每事件至少 min_per 张（不够则全取），其余按事件 ≥3★ 超出部分占比最大余数分配。"""
    quota = {e: min(min_per, c) for e, c in counts.items()}
    left = total - sum(quota.values())
    excess = {e: counts[e] - quota[e] for e in counts}
    tot_ex = sum(excess.values())
    if left > 0 and tot_ex > 0:
        shares = {e: excess[e] / tot_ex for e in counts}   # 归一化成占比再分配
        extra = largest_remainder(shares, left)
        for e in counts:
            quota[e] += extra[e]
    return quota


def rr_pick(rng: random.Random, pool: list[Photo], take: int) -> list[Photo]:
    """池内按拍摄段 round-robin 摊匀取 take 张（段内先 shuffle，大段先轮）。"""
    buckets: dict[int, list[Photo]] = defaultdict(list)
    for p in pool:
        buckets[p.seg].append(p)
    for b in buckets.values():
        rng.shuffle(b)
    segs = sorted(buckets, key=lambda s: (-len(buckets[s]), s))
    picked: list[Photo] = []
    while len(picked) < take:
        progressed = False
        for s in segs:
            if buckets[s] and len(picked) < take:
                picked.append(buckets[s].pop())
                progressed = True
        if not progressed:
            break
    return picked


def sample(rng: random.Random, photos: list[Photo], total: int) -> tuple[list[Photo], dict[str, int]]:
    """分层抽样：≥3★ 池 → 事件配额 → 事件内 rating 配额（池不够赤字顺移）→ 段 round-robin。"""
    pool = [p for p in photos if p.rating >= 3]
    counts: dict[str, int] = defaultdict(int)
    for p in pool:
        counts[p.event] += 1
    quota = allocate_quotas(counts, total, MIN_PER_EVENT)

    by_ev_r: dict[str, dict[int, list[Photo]]] = defaultdict(lambda: defaultdict(list))
    for p in pool:
        by_ev_r[p.event][p.rating].append(p)

    picks: list[Photo] = []
    for e in sorted(counts, key=lambda e: -counts[e]):   # 大事件先抽（rng 消耗顺序固定）
        q = quota[e]
        desired = largest_remainder(RATING_W, q)
        takes: dict[int, int] = {}
        left = q
        for r in (3, 4, 5):   # 先按配额取，池不够记赤字
            takes[r] = min(desired[r], len(by_ev_r[e][r]), left)
            left -= takes[r]
        for r in (3, 4, 5):   # 赤字顺移 3→4→5 补足
            if left <= 0:
                break
            extra = min(len(by_ev_r[e][r]) - takes[r], left)
            takes[r] += extra
            left -= extra
        for r in (3, 4, 5):
            if takes[r] > 0:
                picks.extend(rr_pick(rng, by_ev_r[e][r], takes[r]))
        if left > 0:
            print(f"[WARN] 事件 {e or OLD_BATCH_TAG} ≥3★ 池不足，配额 {q} 实取 {q - left}")
    return picks, quota


# ---------------------------------------------------------------------------
# 复制 + 抹星
# ---------------------------------------------------------------------------

def scrub_file(path: Path) -> tuple[int, int, int]:
    """对副本做等长字节替换抹星。返回 (元素命中, 属性命中, 替换后残留[1-5]命中)。"""
    data = path.read_bytes()
    n_elem = len(RE_ELEM.findall(data))
    n_attr = len(RE_ATTR.findall(data))
    new = data
    if n_elem or n_attr:
        new = RE_ELEM.sub(b"xmp:Rating>0<", data)
        new = RE_ATTR.sub(b'xmp:Rating="0"', new)
        assert len(new) == len(data), "等长替换不变式被破坏"
        if new != data:
            path.write_bytes(new)
    n_left = len(RE_ELEM_LEFT.findall(new)) + len(RE_ATTR_LEFT.findall(new))
    return n_elem, n_attr, n_left


def copy_and_scrub(picks: list[Photo], resolver: Resolver, abs_set: Path,
                   rng: random.Random) -> tuple[list[tuple[Photo, str]], list[str]]:
    """shuffle 后编 P0001.. 中性名，复制（jpg 组复制 .JPG），逐文件抹星 + grep 校验。
    返回 [(Photo, new_name)] 与错误列表（非空 = 有抹除失败）。"""
    abs_set.mkdir(parents=True, exist_ok=True)
    for stale in abs_set.iterdir():   # 清理本工具自命名的旧副本（P####.*），保证重跑幂等
        if re.fullmatch(r"P\d{4}\..+", stale.name):
            stale.unlink()

    order = list(picks)
    rng.shuffle(order)   # 编号不泄露事件/星级分组
    named: list[tuple[Photo, str]] = []
    errors: list[str] = []
    n_elem_files = n_attr_files = n_none_files = n_repl = 0

    print(f"\n========== 复制 + 抹星（{len(order)} 张 → {abs_set}） ==========")
    for i, p in enumerate(order, 1):
        src = resolver.resolve(p)
        if src is None:
            errors.append(f"{p.name}: 源文件不存在（{p.rel} @ {p.event or OLD_BATCH_TAG}）")
            continue
        ext = os.path.splitext(src)[1]
        new_name = f"P{i:04d}{ext}"
        dst = abs_set / new_name
        shutil.copyfile(src, dst)
        n_e, n_a, n_left = scrub_file(dst)
        n_repl += n_e + n_a
        if n_left > 0:
            errors.append(f"{new_name}: 抹除后仍有 {n_left} 处 xmp:Rating[1-5] 残留")
        if n_e:
            n_elem_files += 1
            tag = f"元素×{n_e}"
        elif n_a:
            n_attr_files += 1
            tag = f"属性×{n_a}"
        else:
            n_none_files += 1
            tag = "未找到xmp:Rating"
        print(f"  {new_name:<12} 旧{p.rating}★ {tag:<14} → 已抹为0"
              f"{'  [残留!]' if n_left else ''}")
        named.append((p, new_name))

    print(f"\n抹除统计: 元素形式 {n_elem_files} 张 · 属性形式 {n_attr_files} 张 · "
          f"未找到标签 {n_none_files} 张 · 总替换 {n_repl} 处")
    return named, errors


# ---------------------------------------------------------------------------
# scan-only 验证
# ---------------------------------------------------------------------------

def run_scan_only(abs_set: Path, expect_n: int) -> bool:
    """跑 DatasetBuilder --scan-only，解析报告：文件数/指纹组数 == expect_n，1★-5★ 全 0。"""
    cmd = ["dotnet", "run", "--project", "Training/DatasetBuilder", "--",
           str(abs_set), "--scan-only"]
    print(f"\n========== scan-only 验证 ==========\n$ {' '.join(cmd)}")
    proc = subprocess.run(cmd, cwd=_ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    out = proc.stdout + proc.stderr
    print(out)
    if proc.returncode != 0:
        print(f"[ERROR] scan-only 退出码 {proc.returncode}")
        return False
    m = re.search(r"文件\s*(\d+)\s*·\s*指纹组\s*(\d+)", out)
    if not m:
        print("[ERROR] 未能从 scan-only 输出解析 文件/指纹组 计数")
        return False
    n_files, n_groups = int(m.group(1)), int(m.group(2))

    stars: dict[int, int] = {}
    in_star = False
    for line in out.splitlines():
        if "星级（按指纹组）" in line:
            in_star = True
            continue
        if in_star:
            if line.strip().startswith("—"):
                break
            mm = re.match(r"\s*(\d)★\s*#*\s*(\d+)\s*$", line)
            if mm:
                stars[int(mm.group(1))] = int(mm.group(2))

    n_warn = len(re.findall(r"\[(?:WARN|ERROR)\]", out))
    ok = (n_files == expect_n and n_groups == expect_n
          and all(stars.get(k, 0) == 0 for k in range(1, 6))
          and stars.get(0, 0) == expect_n)
    print(f"scan-only 结论: 文件 {n_files} · 指纹组 {n_groups}（期望各 {expect_n}）· "
          f"星级分布 {dict(sorted(stars.items()))} · WARN/ERROR 行 {n_warn}")
    if ok:
        print(f"验证通过：{expect_n} 文件 → {expect_n} 指纹组，星级全 0/未评，抹除生效。")
    else:
        print("[ERROR] 验证失败：计数不符或存在非 0 星级（抹除失败）。")
    return ok


# ---------------------------------------------------------------------------
# 摘要 + main
# ---------------------------------------------------------------------------

def print_summary(named: list[tuple[Photo, str]], quota: dict[str, int],
                  counts: dict[str, int]) -> None:
    print("\n========== 抽样分布摘要 ==========")
    cross: dict[str, dict[int, int]] = defaultdict(lambda: defaultdict(int))
    tot_r: dict[int, int] = defaultdict(int)
    segs: set[int] = set()
    for p, _ in named:
        cross[p.event][p.rating] += 1
        tot_r[p.rating] += 1
        segs.add(p.seg)
    n = len(named)
    print(f"总抽样 {n} 张 · 覆盖段 {len(segs)}/120 · 事件 {len(cross)}/{len(counts)}")
    print(f"{'事件':<26}{'配额':>4}{'3★':>5}{'4★':>5}{'5★':>5}{'合计':>6}")
    for e in sorted(cross, key=lambda e: -quota.get(e, 0)):
        row = cross[e]
        s = sum(row.values())
        print(f"{(e or OLD_BATCH_TAG):<26}{quota.get(e, 0):>4}{row[3]:>5}{row[4]:>5}"
              f"{row[5]:>5}{s:>6}")
    print(f"{'合计':<26}{sum(quota.values()):>4}{tot_r[3]:>5}{tot_r[4]:>5}{tot_r[5]:>5}{n:>6}")
    print(f"旧 rating 比例: 3★ {tot_r[3]/n*100:.1f}% · 4★ {tot_r[4]/n*100:.1f}% · "
          f"5★ {tot_r[5]/n*100:.1f}%（目标 ≈53:27:20）")


def main() -> int:
    ap = argparse.ArgumentParser(description="M1 §1.5 绝对性探针（重标集）：抽样+复制+抹星+验证")
    ap.add_argument("--db", default=DB_DEFAULT)
    ap.add_argument("--manifest", default=MANIFEST_DEFAULT)
    ap.add_argument("--out", default=OUT_DIR_DEFAULT, help="输出根目录（abs_set/ 与 key CSV 所在）")
    ap.add_argument("--total", type=int, default=TOTAL)
    ap.add_argument("--seed", type=int, default=SEED)
    ap.add_argument("--no-scan", action="store_true", help="跳过 dotnet scan-only 验证")
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

    picks, quota = sample(rng, photos, args.total)
    counts: dict[str, int] = defaultdict(int)
    for p in photos:
        if p.rating >= 3:
            counts[p.event] += 1
    print(f"≥3★ 池 {sum(counts.values())} 组 / {len(counts)} 事件；配额合计 {sum(quota.values())}，"
          f"实抽 {len(picks)}（每张只用一次: {len({p.fp for p in picks}) == len(picks)}）")

    abs_set = Path(args.out) / ABS_SET_SUBDIR
    named, errors = copy_and_scrub(picks, resolver, abs_set, rng)
    if errors:
        print("\n[ERROR] 复制/抹除存在问题:")
        for e in errors:
            print("  " + e)
        return 1

    key_path = Path(args.out) / KEY_CSV_NAME
    with open(key_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["new_name", "fingerprint", "event_label", "old_rating", "seg_id", "orig_path"])
        for p, new_name in named:
            w.writerow([new_name, p.fp, p.event or OLD_BATCH_TAG, p.rating, p.seg, p.path])
    print(f"\n答案键: {key_path}（{len(named)} 行）")

    print_summary(named, quota, counts)

    if args.no_scan:
        print("\n（--no-scan：跳过 scan-only 验证）")
        return 0
    return 0 if run_scan_only(abs_set, len(named)) else 1


if __name__ == "__main__":
    sys.exit(main())
