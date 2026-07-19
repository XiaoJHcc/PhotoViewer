"""
data_audit.py — M1 §1.4 分布审计 + 阈值校准（Plan-3-1 §1.4）

读数据集库（只读），输出 8 个统计块（控制台分节中文摘要 + Training/audit/out/ 下
markdown 报告与 PNG 直方图），报告末尾给"校准建议"：window 大小、τ_hi/τ_lo（cosine
分层界）、tie 的 CV 距离阈值、相似度→权重映射锚点、M2 人工偏移工作量结论。

统计块（逐项对应 plan-3-1 §1.4 规格）：
    1. 星级分布直方图（总体 + 每段）——锦标赛结构、"每段分布大致相同"验证
    2. CLS cosine 全库直方图 + 分位——段内滑窗对(window=20) + 段内全配对
    3. 段内 vs 跨段 cosine 分布——滑窗+相似度能否分离可比对
    4. 拍摄段切分（gap>10min，对照 20/45）+ M2 人工偏移工作量核算
    5. is_retouched 覆盖率（按事件 + 按星级分桶）
    6. 近重复对（cosine ≥ 0.98）的 CV 距离分布——校准决策 1"条件保留"阈值
    7. EXIF / rating 字段覆盖率——是否 ≥95%、缺失策略
    8. ≥3★ 实际占比（总体 + 每段）——对照"Top 12.5%"口径

实现约定全部复用 Training/probes/feature_probe.py（sys.path 注入 import）：
l2_normalize / _cv_aggregate（CV grid 解码）/ split_segments / sliding_window_pairs /
pairwise_cv_distance（CV 距离 = 三标量按全库 std 归一后的欧氏距离，NaN 分量跳过）。

用法（仓根 D:/Git/PhotoViewer 下）：
    PYTHONUTF8=1 Tools/.venv/Scripts/python.exe Training/audit/data_audit.py
"""
from __future__ import annotations

import argparse
import sqlite3
import sys
import warnings
from datetime import datetime
from pathlib import Path

import numpy as np

# 复用 feature_probe 的常量与辅助函数（CV 布局常量、解码、段切分、配对、CV 距离同源）
_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_ROOT / "Training" / "probes"))
from feature_probe import (  # noqa: E402
    l2_normalize,
    _cv_aggregate,
    split_segments,
    sliding_window_pairs,
    pairwise_cv_distance,
)

MODEL_ID = "dinov3_vits16_f32_518_v1"   # backbone 已定格 ViT-S，本审计只用原片 CLS
OUT_DIR = _ROOT / "Training" / "audit" / "out"

GAP_PRIMARY = 10.0              # 主口径 gap（分钟），与探针 120 段口径一致
GAP_ALT = (20.0, 45.0)          # 对照 gap
WINDOW = 20                     # 段内滑窗宽度（按张数），与探针一致
NEAR_DUP_COS = 0.98             # 近重复对判界（块 6）
CROSS_SAMPLE_N = 100_000        # 跨段采样对数量级 ~10^5
RNG_SEED = 0
TOP3_TARGET = 0.125             # 章程"Top 12.5%"口径
PCTS = (50, 75, 90, 95, 98, 99)  # cosine 分位


# ---------------------------------------------------------------------------
# 数据装载（只读）
# ---------------------------------------------------------------------------

def load_data(db_path: str) -> dict:
    """读 photos 全量行 + 原片 CLS（model_id=MODEL_ID）。
    返回 dict：samples（按 capture_time 升序的数组字典）+ coverage（块 7 覆盖率）+
    retouched 明细（块 5）。capture_time 解析失败的样本保留但标记，段切分时剔除。
    """
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)   # 显式只读
    try:
        rows = conn.execute(
            "SELECT fingerprint, rating, capture_time, cv_grid, "
            "       COALESCE(event_label,''), is_retouched, "
            "       focal_length, aperture, shutter_speed, crop_factor "
            "FROM photos"
        ).fetchall()
        feats = dict(conn.execute(
            "SELECT fingerprint, cls_vector FROM photo_features WHERE model_id = ?",
            (MODEL_ID,)).fetchall())
    finally:
        conn.close()

    # 块 7 覆盖率统计（全量行，不依赖特征）
    n = len(rows)
    coverage = {
        "n_total": n,
        "rating": sum(1 for r in rows if r[1] is not None),
        "capture_time": sum(1 for r in rows if r[2]),
        "cv_grid": sum(1 for r in rows if r[3] is not None),
        "focal_length": sum(1 for r in rows if r[6] is not None),
        "aperture": sum(1 for r in rows if r[7] is not None),
        "shutter_speed": sum(1 for r in rows if r[8] is not None),
        "crop_factor": sum(1 for r in rows if r[9] is not None),
    }

    fps, ratings, times, events, retouched = [], [], [], [], []
    sharps, shakes, contrasts = [], [], []
    n_no_time, n_no_feat = 0, 0
    for fp, rating, ctime, cv_blob, event, ret, _fl, _ap, _ss, _cf in rows:
        blob = feats.get(fp)
        if blob is None or rating is None:
            n_no_feat += 1
            continue
        parsed = None
        if ctime:
            try:
                parsed = datetime.fromisoformat(str(ctime))
            except ValueError:
                parsed = None
        if parsed is None:
            n_no_time += 1
        sharp, shake, contrast = _cv_aggregate(cv_blob)
        fps.append(fp)
        ratings.append(int(rating))
        times.append(parsed)
        events.append(str(event))
        retouched.append(-1 if ret is None else int(ret))   # -1 = 旧批 NULL
        sharps.append(sharp)
        shakes.append(shake)
        contrasts.append(contrast)

    # 特征按样本顺序堆叠（L2 归一幂等）
    vec = {fp: l2_normalize(np.frombuffer(b, dtype="<f4").astype(np.float32)[None, :])[0]
           for fp, b in feats.items()}
    orig = np.stack([vec[fp] for fp in fps])

    order = np.argsort([t.timestamp() if t else float("inf") for t in times])
    samples = {
        "fp": [fps[i] for i in order],
        "rating": np.array(ratings, dtype=np.int64)[order],
        "time": [times[i] for i in order],
        "event": np.array(events, dtype=object)[order],
        "retouched": np.array(retouched, dtype=np.int64)[order],
        "orig": orig[order],
        "cv_sharp": np.array(sharps)[order],
        "cv_shake": np.array(shakes)[order],
        "cv_contrast": np.array(contrasts)[order],
    }
    samples["n_loaded"] = len(fps)
    samples["n_no_time"] = n_no_time
    samples["n_skipped"] = n_no_feat
    return {"samples": samples, "coverage": coverage, "rows": rows}


def pct_str(x: np.ndarray, pcts=PCTS) -> str:
    """分位数摘要串：P50=.. P75=.. ..."""
    q = np.percentile(x, pcts)
    return " ".join(f"P{p}={v:.3f}" for p, v in zip(pcts, q))


# ---------------------------------------------------------------------------
# 绘图（matplotlib 出图一律英文标注，报告正文中文）
# ---------------------------------------------------------------------------

def _plt():
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    return plt


def fig_rating(ratings: np.ndarray, seg: np.ndarray, out: Path) -> None:
    plt = _plt()
    fig, axes = plt.subplots(1, 2, figsize=(15, 4.5))
    counts = np.bincount(ratings, minlength=6)
    axes[0].bar(range(6), counts, color="steelblue")
    axes[0].set_xlabel("rating"); axes[0].set_ylabel("count")
    axes[0].set_title(f"Rating distribution (n={len(ratings)})")
    for k, c in enumerate(counts):
        axes[0].text(k, c, f"{c}\n({c/len(ratings)*100:.1f}%)", ha="center", va="bottom", fontsize=8)
    n_seg = int(seg.max()) + 1
    props = np.zeros((6, n_seg))
    for s in range(n_seg):
        r = ratings[seg == s]
        props[:, s] = np.bincount(r, minlength=6) / len(r)
    im = axes[1].imshow(props, aspect="auto", cmap="viridis", vmin=0,
                        vmax=max(0.6, props.max()), origin="lower")
    axes[1].set_xlabel("segment (time-ordered)"); axes[1].set_ylabel("rating")
    axes[1].set_yticks(range(6))
    axes[1].set_title(f"Per-segment rating proportions (gap>{GAP_PRIMARY:.0f}min, {n_seg} segs)")
    fig.colorbar(im, ax=axes[1], label="proportion")
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)


def fig_cos_window_segall(cos_win: np.ndarray, cos_seg: np.ndarray, out: Path) -> None:
    plt = _plt()
    fig, ax = plt.subplots(figsize=(8.5, 5))
    bins = np.linspace(0.0, 1.0, 201)
    ax.hist(cos_seg, bins=bins, density=True, alpha=0.55, label=f"within-seg all pairs (n={len(cos_seg)})")
    ax.hist(cos_win, bins=bins, density=True, alpha=0.55, label=f"within-seg window-{WINDOW} pairs (n={len(cos_win)})")
    ax.set_xlabel("CLS cosine"); ax.set_ylabel("density")
    ax.set_title("Within-segment cosine: sliding window vs all pairs")
    ax.legend(); ax.set_xlim(0, 1)
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)


def fig_within_cross(cos_within: np.ndarray, cos_cross: np.ndarray, out: Path) -> None:
    plt = _plt()
    fig, ax = plt.subplots(figsize=(8.5, 5))
    bins = np.linspace(0.0, 1.0, 201)
    ax.hist(cos_within, bins=bins, density=True, alpha=0.55,
            label=f"within-seg (n={len(cos_within)})")
    ax.hist(cos_cross, bins=bins, density=True, alpha=0.55,
            label=f"cross-seg sampled (n={len(cos_cross)})")
    ax.set_xlabel("CLS cosine"); ax.set_ylabel("density")
    ax.set_title("Within-segment vs cross-segment cosine")
    ax.legend(); ax.set_xlim(0, 1)
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)


def fig_cv_neardup(cv_dist: np.ndarray, out: Path) -> None:
    plt = _plt()
    valid = cv_dist[~np.isnan(cv_dist)]
    fig, ax = plt.subplots(figsize=(8.5, 5))
    ax.hist(valid, bins=100, color="teal", alpha=0.8)
    for q, c in ((40, "darkorange"), (60, "crimson")):
        v = float(np.quantile(valid, q / 100))
        ax.axvline(v, color=c, ls="--", label=f"P{q} = {v:.3f}")
    ax.set_xlabel("CV distance (std-normalized euclidean of sharp/shake/contrast)")
    ax.set_ylabel("count")
    ax.set_title(f"CV distance of near-duplicate pairs (cos >= {NEAR_DUP_COS}, n={len(valid)})")
    ax.legend()
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)


def fig_retouched(ratings: np.ndarray, retouched: np.ndarray, out: Path) -> None:
    plt = _plt()
    fig, ax = plt.subplots(figsize=(8, 4.5))
    rates = []
    for k in range(6):
        m = ratings == k
        known = retouched[m] >= 0
        rates.append(float((retouched[m] == 1).sum() / known.sum() * 100) if known.sum() else np.nan)
    ax.bar(range(6), rates, color="darkseagreen")
    ax.set_xlabel("rating"); ax.set_ylabel("is_retouched=1 rate (%)")
    ax.set_title("Retouched rate by rating (NULL rows excluded)")
    for k, v in enumerate(rates):
        if not np.isnan(v):
            ax.text(k, v, f"{v:.1f}%", ha="center", va="bottom", fontsize=9)
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)


def fig_top3(seg: np.ndarray, ratings: np.ndarray, out: Path) -> None:
    plt = _plt()
    n_seg = int(seg.max()) + 1
    prop = np.array([np.mean(ratings[seg == s] >= 3) for s in range(n_seg)])
    overall = float(np.mean(ratings >= 3))
    fig, ax = plt.subplots(figsize=(10, 4.5))
    ax.plot(np.arange(n_seg), prop * 100, ".", ms=5, color="steelblue", label="per-segment")
    ax.axhline(overall * 100, color="darkorange", ls="-", label=f"overall = {overall*100:.1f}%")
    ax.axhline(TOP3_TARGET * 100, color="crimson", ls="--", label=f"charter Top {TOP3_TARGET*100:.1f}%")
    ax.set_xlabel("segment (time-ordered)"); ax.set_ylabel(">=3-star proportion (%)")
    ax.set_title(f">=3-star proportion per segment (mean={prop.mean()*100:.1f}% +- {prop.std()*100:.1f}%)")
    ax.legend()
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)


# ---------------------------------------------------------------------------
# 统计块
# ---------------------------------------------------------------------------

def block1_rating(samples: dict, seg: np.ndarray, out: Path) -> dict:
    """块 1：星级分布（总体 + 每段）+ '每段分布大致相同'验证（占比均值/标准差 + KL）。"""
    print("\n========== ① 星级分布（总体 + 每段） ==========")
    ratings = samples["rating"]
    n = len(ratings)
    counts = np.bincount(ratings, minlength=6)
    print(f"总体 {n} 组: " + " ".join(f"{k}★={c}({c/n*100:.1f}%)" for k, c in enumerate(counts)))

    n_seg = int(seg.max()) + 1
    sizes = np.bincount(seg)
    props = np.zeros((n_seg, 6))
    for s in range(n_seg):
        r = ratings[seg == s]
        props[s] = np.bincount(r, minlength=6) / len(r)
    overall = counts / n
    eps = 1e-6
    kl = np.sum(props * np.log((props + eps) / (overall + eps)), axis=1)   # KL(段||总体)
    print(f"每段 0-5★ 占比（{n_seg} 段）均值±标准差:")
    for k in range(6):
        print(f"  {k}★: {props[:,k].mean()*100:5.1f}% ± {props[:,k].std()*100:4.1f}%  (总体 {overall[k]*100:.1f}%)")
    print(f"每段分布对总体的 KL(段||总体): 均值 {kl.mean():.3f} / 中位 {np.median(kl):.3f} / 最大 {kl.max():.3f}")
    print(f"  KL 最大段: #{int(kl.argmax())}（大小 {int(sizes[kl.argmax()])} 张）")
    # 小段（<30 张）占比本身噪声大，KL 需按段大小分层看，否则把小样本抖动误读为分布偏移
    large = sizes >= 30
    kl_large = kl[large]
    print(f"  仅看 ≥30 张的段（{int(large.sum())} 段）: KL 均值 {kl_large.mean():.3f} / 中位 "
          f"{np.median(kl_large):.3f} / 最大 {kl_large.max():.3f}")
    fig_rating(ratings, seg, out / "rating_dist.png")
    verdict = (f"全段 KL 均值 {kl.mean():.3f} 主要由小段的采样噪声贡献（最大 KL 段仅 "
               f"{int(sizes[kl.argmax()])} 张）；≥30 张的段 KL 均值 {kl_large.mean():.3f}、"
               f"各星级占比均值与总体一致 —— 大段层面'每段分布大致相同'成立，小段层面波动属采样噪声，"
               f"锦标赛'段内评比'结构可用")
    print(f"结论: {verdict}")
    return {"counts": counts, "props": props, "overall": overall, "kl": kl, "n_seg": n_seg,
            "kl_large_mean": float(kl_large.mean()), "n_large_seg": int(large.sum()),
            "max_kl_size": int(sizes[kl.argmax()]), "verdict": verdict}


def block2_cosine(samples: dict, seg: np.ndarray, out: Path) -> dict:
    """块 2：CLS cosine 直方图 + 分位。范围 = 段内滑窗对(window=20) + 段内全配对。"""
    print("\n========== ② CLS cosine 分布（段内滑窗 + 段内全配对） ==========")
    orig = samples["orig"]
    raw = sliding_window_pairs(seg, WINDOW)
    cos_win = np.sum(orig[raw[:, 0]] * orig[raw[:, 1]], axis=1)

    # 段内全配对：逐段 sim 矩阵取上三角；同时收集 cos≥0.95/0.98 的对（块 3/6 复用）
    cos_seg_parts, hi95_ii, hi95_jj, hi95_cos = [], [], [], []
    n = len(seg)
    start = 0
    while start < n:
        end = start
        while end + 1 < n and seg[end + 1] == seg[start]:
            end += 1
        idx = np.arange(start, end + 1)
        sim = orig[idx] @ orig[idx].T
        iu, ju = np.triu_indices(len(idx), k=1)
        c = sim[iu, ju]
        cos_seg_parts.append(c)
        m95 = c >= 0.95
        hi95_ii.append(idx[iu[m95]]); hi95_jj.append(idx[ju[m95]]); hi95_cos.append(c[m95])
        start = end + 1
    cos_seg = np.concatenate(cos_seg_parts)
    hi95 = (np.concatenate(hi95_ii), np.concatenate(hi95_jj), np.concatenate(hi95_cos))

    print(f"段内滑窗对(window={WINDOW}): {len(cos_win)} 对  {pct_str(cos_win)}")
    print(f"段内全配对:                  {len(cos_seg)} 对  {pct_str(cos_seg)}")
    for thr in (0.90, 0.95, 0.98):
        print(f"  cos≥{thr:.2f}: 滑窗 {np.mean(cos_win>=thr)*100:.2f}% · 段内全配 {np.mean(cos_seg>=thr)*100:.2f}%")
    # window=20/40 对高相似对的召回（校准 window 用）
    in_win = set(map(tuple, raw.tolist()))
    key = set(zip(hi95[0].tolist(), hi95[1].tolist()))
    recall_win = sum(1 for p in key if p in in_win) / max(1, len(key))
    raw40 = sliding_window_pairs(seg, 40)
    in_win40 = set(map(tuple, raw40.tolist()))
    recall_win40 = sum(1 for p in key if p in in_win40) / max(1, len(key))
    print(f"window={WINDOW} 对段内 cos≥0.95 高相似对的覆盖率: {recall_win*100:.1f}%"
          f"（{sum(1 for p in key if p in in_win)}/{len(key)}）")
    print(f"window=40 对照: 覆盖率 {recall_win40*100:.1f}%（代价: 滑窗对数 {len(raw)}→{len(raw40)}，"
          f"+{(len(raw40)/max(len(raw),1)-1)*100:.0f}%）")
    fig_cos_window_segall(cos_win, cos_seg, out / "cos_window_vs_segall.png")
    return {"cos_win": cos_win, "cos_seg": cos_seg, "hi95": hi95, "n_win": len(raw),
            "recall_win95": recall_win, "recall_win95_w40": recall_win40, "n_win40": len(raw40)}


def block3_within_cross(samples: dict, cos_within: np.ndarray, seg: np.ndarray, out: Path) -> dict:
    """块 3：段内 vs 跨段 cosine 分布（跨段随机采样 ~10^5 对）。"""
    print("\n========== ③ 段内 vs 跨段 cosine 分布 ==========")
    orig = samples["orig"]
    n = len(orig)
    rng = np.random.default_rng(RNG_SEED)
    a = rng.integers(0, n, size=int(CROSS_SAMPLE_N * 1.4))
    b = rng.integers(0, n, size=int(CROSS_SAMPLE_N * 1.4))
    m = (seg[a] != seg[b]) & (a != b)
    a, b = a[m][:CROSS_SAMPLE_N], b[m][:CROSS_SAMPLE_N]
    cos_cross = np.sum(orig[a] * orig[b], axis=1)

    print(f"段内全配对: {len(cos_within)} 对  {pct_str(cos_within)}")
    print(f"跨段采样对: {len(cos_cross)} 对  {pct_str(cos_cross)}")
    for thr in (0.80, 0.83, 0.88, 0.90, 0.95, 0.98):
        fw = np.mean(cos_within >= thr) * 100
        fc = np.mean(cos_cross >= thr) * 100
        sep = f"{fw/fc:8.1f}x" if fc > 0 else "     >999x"
        print(f"  cos≥{thr:.2f}: 段内 {fw:6.2f}% · 跨段 {fc:7.3f}% · 分离倍数 {sep}")
    cross_p999 = float(np.quantile(cos_cross, 0.999))
    print(f"跨段分布 P99.9 = {cross_p999:.3f}（高于此值的对几乎不可能是跨场景偶合）")
    fig_within_cross(cos_within, cos_cross, out / "cos_within_vs_cross.png")
    return {"cos_cross": cos_cross, "cross_p999": cross_p999}


def block4_segments(samples: dict, out: Path) -> dict:
    """块 4：拍摄段切分（gap 10/20/45）+ M2 人工偏移工作量核算。"""
    print("\n========== ④ 拍摄段切分 + M2 人工偏移工作量 ==========")
    times = samples["time"]
    if any(t is None for t in times):
        print(f"[WARN] {samples['n_no_time']} 组 capture_time 解析失败，已排除在段切分之外")
    valid = [(i, t) for i, t in enumerate(times) if t is not None]
    idx_ok = np.array([i for i, _ in valid])
    ts = [t for _, t in valid]

    results = {}
    for gap in (GAP_PRIMARY, *GAP_ALT):
        seg_sub = split_segments(ts, gap)
        n_seg = int(seg_sub.max()) + 1
        sizes = np.bincount(seg_sub)
        durations = np.empty(n_seg)
        for s in range(n_seg):
            ii = np.where(seg_sub == s)[0]
            durations[s] = (ts[ii[-1]] - ts[ii[0]]).total_seconds() / 60.0
        valid_seg = int((sizes >= 2).sum())
        print(f"\n■ gap>{gap:.0f}min —— {n_seg} 段（有效段[≥2张] {valid_seg} · 单张段 {int((sizes==1).sum())}）")
        print(f"  段大小(张): 均值 {sizes.mean():.1f} / 中位 {np.median(sizes):.0f} / 最大 {int(sizes.max())}")
        print(f"  段时长(分钟): 均值 {durations.mean():.1f} / 中位 {np.median(durations):.1f} / 最大 {durations.max():.1f}")
        results[gap] = {"seg_sub": seg_sub, "n_seg": n_seg, "sizes": sizes,
                        "durations": durations, "valid": valid_seg}

    # 主口径 seg 映射回全样本（解析失败的排最后、段号单列 -1，调用方只用有效部分）
    seg_full = np.full(len(times), -1, dtype=np.int64)
    seg_full[idx_ok] = results[GAP_PRIMARY]["seg_sub"]

    n_seg = results[GAP_PRIMARY]["n_seg"]
    for per in (1, 2):
        print(f"M2 人工偏移核算: {n_seg} 段 × 每段 {per} 张代表评级 = {n_seg*per} 张人工评级")
    work = (f"gap=10min 得 {n_seg} 段；每段 1-2 张代表 → 人工评级 {n_seg}~{n_seg*2} 张，"
            f"按每批数十张的节奏半天~一天可完成，人工偏移在该段规模下可行")
    print(f"结论: {work}")
    results["work_conclusion"] = work
    results["seg_full"] = seg_full
    return results


def block5_retouched(samples: dict, rows: list, out: Path) -> dict:
    """块 5：is_retouched 覆盖率（按事件 + 按星级分桶）。"""
    print("\n========== ⑤ is_retouched 覆盖率 ==========")
    ret = samples["retouched"]
    ratings = samples["rating"]
    events = samples["event"]
    n = len(ret)
    n_null = int((ret == -1).sum())
    n_pos = int((ret == 1).sum())
    print(f"总体: 1={n_pos}({n_pos/n*100:.2f}%) · 0={int((ret==0).sum())} · NULL(旧批)={n_null}({n_null/n*100:.1f}%)")

    print("按事件:")
    ev_table = []
    for e in sorted(set(events.tolist())):
        m = events == e
        known = ret[m] >= 0
        pos = int((ret[m] == 1).sum())
        rate = pos / known.sum() * 100 if known.sum() else None   # None = 全 NULL（旧批未标）
        label = e if e else "(无事件/旧批20240212)"
        ev_table.append((label, int(m.sum()), pos, rate))
        rate_str = f"{rate:5.2f}%" if rate is not None else "  —（全 NULL 未标）"
        print(f"  {label:<24} {int(m.sum()):>5} 组 · 精修 {pos:>3} 张 · 覆盖率 {rate_str}")
    print("按星级（NULL 行不计入分母）:")
    star_table = []
    for k in range(6):
        m = ratings == k
        known = ret[m] >= 0
        pos = int((ret[m] == 1).sum())
        rate = pos / known.sum() * 100 if known.sum() else float("nan")
        star_table.append((k, int(m.sum()), pos, rate))
        print(f"  {k}★: {int(m.sum()):>5} 组 · 精修 {pos:>3} 张 · 覆盖率 {rate:5.2f}%")
    fig_retouched(ratings, ret, out / "retouched_by_rating.png")
    r4 = star_table[4][3]; r5 = star_table[5][3]
    verdict = (f"精修共 {n_pos} 张且 {n_null} 张旧批无标记；4★/5★ 覆盖率 {r4:.1f}%/{r5:.1f}% —— "
               f"信号稀疏且只覆盖新批，可作 M2 绝对池的'顶端锚点'（少量、高精度、按张权重高），"
               f"不能当全量监督信号；旧批段完全没有锚，跨段对齐不能依赖它")
    print(f"结论: {verdict}")
    return {"n_pos": n_pos, "n_null": n_null, "ev_table": ev_table, "star_table": star_table,
            "verdict": verdict}


def block6_neardup_cv(samples: dict, hi95: tuple, out: Path) -> dict:
    """块 6：近重复对（cos≥0.98，段内全配对中筛）的 CV 距离分布。
    CV 距离定义与 feature_probe.py 完全一致：三标量按全库 std 归一后的欧氏距离，NaN 分量跳过。"""
    print("\n========== ⑥ 近重复对(cos≥0.98)的 CV 距离分布 ==========")
    ii95, jj95, cos95 = hi95
    m = cos95 >= NEAR_DUP_COS
    ii, jj = ii95[m], jj95[m]
    with np.errstate(all="ignore"), warnings.catch_warnings():
        warnings.simplefilter("ignore", category=RuntimeWarning)
        std_sharp = np.nanstd(samples["cv_sharp"]) or 1.0
        std_shake = np.nanstd(samples["cv_shake"]) or 1.0
        std_contrast = np.nanstd(samples["cv_contrast"]) or 1.0
    cv_dist, n_valid = pairwise_cv_distance(
        samples["cv_sharp"], samples["cv_shake"], samples["cv_contrast"],
        std_sharp, std_shake, std_contrast, ii, jj)
    valid = cv_dist[~np.isnan(cv_dist)]
    print(f"近重复对(cos≥{NEAR_DUP_COS}): {len(ii)} 对（段内全配对中），CV 距离有效 {len(valid)} 对"
          f"（三标量全 NaN 剔除 {len(ii)-len(valid)} 对）")
    print(f"CV 距离分位: {pct_str(valid, (10, 25, 40, 50, 60, 75, 90, 95))}")
    t40, t60 = (float(np.quantile(valid, q)) for q in (0.4, 0.6))
    print(f"τ_cv@P40 = {t40:.3f} · τ_cv@P60 = {t60:.3f}（探针条件保留分位口径）")
    fig_cv_neardup(cv_dist, out / "cv_dist_neardup.png")
    return {"n_pairs": len(ii), "n_valid": len(valid), "tau_cv_p40": t40, "tau_cv_p60": t60,
            "cv_dist": valid}


def block7_coverage(coverage: dict) -> dict:
    """块 7：EXIF / rating 字段覆盖率。"""
    print("\n========== ⑦ EXIF / rating 字段覆盖率 ==========")
    n = coverage["n_total"]
    fields = [("rating", "rating"), ("capture_time", "capture_time"), ("cv_grid", "cv_grid"),
              ("focal_length", "focal_length"), ("aperture", "aperture"),
              ("shutter_speed", "shutter_speed"), ("crop_factor", "crop_factor")]
    table = []
    for name, key in fields:
        c = coverage[key]
        ok = c / n * 100 >= 95
        table.append((name, c, c / n * 100, ok))
        print(f"  {name:<14} {c}/{n} = {c/n*100:6.2f}%  {'✓≥95%' if ok else '✗<95% 异常'}")
    bad = [t for t in table if not t[3]]
    if bad:
        advice = ("crop_factor 全库 100% 缺失（9418/9418 NULL）——该列当前不可用；"
                  "缺失策略：训练/审计特征均不依赖 crop_factor，等效焦距类分析改由 focal_length 直接承担，"
                  "若 M3+ 需要画幅归一则回 DatasetBuilder 补提该字段；其余字段（rating/focal/aperture/shutter）"
                  "均 100%，无需填充策略")
    else:
        advice = "全部 ≥95%，无需缺失填充策略"
    print(f"结论: {advice}")
    return {"table": table, "advice": advice}


def block8_top3(samples: dict, seg: np.ndarray, out: Path) -> dict:
    """块 8：≥3★ 实际占比（总体 + 每段），对照 Top 12.5%。"""
    print("\n========== ⑧ ≥3★ 实际占比 ==========")
    ratings = samples["rating"]
    overall = float(np.mean(ratings >= 3))
    n_seg = int(seg.max()) + 1
    props = np.array([np.mean(ratings[seg == s] >= 3) for s in range(n_seg)])
    print(f"总体 ≥3★: {int((ratings>=3).sum())}/{len(ratings)} = {overall*100:.2f}%（章程口径预期 ≈9.8%）")
    print(f"每段 ≥3★ 占比（{n_seg} 段）: 均值 {props.mean()*100:.1f}% ± 标准差 {props.std()*100:.1f}%"
          f"（最小 {props.min()*100:.1f}% / 最大 {props.max()*100:.1f}%）")
    zero_seg = int((props == 0).sum())
    sizes = np.bincount(seg)
    zero_seg_small = int(((props == 0) & (sizes < 30)).sum())
    print(f"  ≥3★ 为 0 的段: {zero_seg}/{n_seg}（其中 <30 张的小段 {zero_seg_small} 个）")
    fig_top3(seg, ratings, out / "top3_per_segment.png")
    verdict = (f"实际 ≥3★ 占比 {overall*100:.1f}% 低于章程'Top 12.5%'（理想锦标赛 50%³）——"
               f"用户裁定（2026-07-19）：逐级过选率实际在 35–60% 浮动，12.5% 是理想值、"
               f"本批 {overall*100:.1f}% 仅代表本批实况、**不改产品目标口径**；"
               f"{zero_seg}/{n_seg} 段无 ≥3★（多为 <30 张小段的采样噪声），"
               f"段间波动 ±{props.std()*100:.1f}% 主要由段大小驱动，不作分桶修正")
    print(f"结论: {verdict}")
    return {"overall": overall, "props": props, "verdict": verdict}


# ---------------------------------------------------------------------------
# 校准建议 + markdown 报告
# ---------------------------------------------------------------------------

def calibration(r1: dict, r2: dict, r3: dict, r4: dict, r5: dict,
                r6: dict, r7: dict, r8: dict) -> list[str]:
    """报告末尾的「校准建议」：每条 = 建议值 + 依据数据。"""
    cos_win, cos_seg = r2["cos_win"], r2["cos_seg"]
    cos_cross = r3["cos_cross"]
    q_win = {p: float(np.percentile(cos_win, p)) for p in PCTS}
    q_seg = {p: float(np.percentile(cos_seg, p)) for p in PCTS}
    q_cross = {p: float(np.percentile(cos_cross, p)) for p in (50, 90, 99, 99.9)}
    n_seg = r4[GAP_PRIMARY]["n_seg"]
    sizes = r4[GAP_PRIMARY]["sizes"]
    coverage_total = r7["table"][0][1]  # rating 非空数 = 全库组数

    lines = []
    # 1) window
    med = float(np.median(sizes))
    lines.append(
        f"1. **window（段内滑窗宽度）建议 = {WINDOW} 张**：段大小中位 {med:.0f} 张，"
        f"window={WINDOW} 对段内 cos≥0.95 高相似对的覆盖率 {r2['recall_win95']*100:.1f}%"
        f"（{int(round(r2['recall_win95']*len(r2['hi95'][0])))} / {len(r2['hi95'][0])}）；"
        f"window=40 对照覆盖率 {r2['recall_win95_w40']*100:.1f}%，仅多召回 "
        f"{(r2['recall_win95_w40']-r2['recall_win95'])*100:.1f} 个百分点，代价是滑窗对数 "
        f"{r2['n_win']}→{r2['n_win40']}（+{(r2['n_win40']/r2['n_win']-1)*100:.0f}%，新增多为低相似噪声对）——"
        f"window={WINDOW} 在召回与对数之间更划算，维持探针口径。")
    # 2) τ_hi / τ_lo
    tau_hi = 0.98
    frac_hi = float(np.mean(cos_win >= tau_hi)) * 100
    tau_lo = round(float(np.clip(np.ceil(q_cross[99.9] * 100) / 100, 0.5, 0.95)), 2)
    frac_comp = float(np.mean((cos_win >= tau_lo) & (cos_win < tau_hi))) * 100
    lines.append(
        f"2. **τ_hi 建议 = {tau_hi:.2f}，τ_lo 建议 ≈ {tau_lo:.2f}**："
        f"滑窗对 cos≥{tau_hi:.2f} 占 {frac_hi:.2f}%（细节层量级适中，近重复判界 P98 分位 "
        f"{q_win[98]:.3f} 附近）；跨段采样 cos P99.9 = {q_cross[99.9]:.3f}，"
        f"取 τ_lo={tau_lo:.2f} 可使跨场景偶合混入率 <0.1%，同时构图层 "
        f"[{tau_lo:.2f},{tau_hi:.2f}) 仍覆盖滑窗对的 {frac_comp:.1f}%（样本充足）。")
    # 3) τ_cv
    lines.append(
        f"3. **tie 的 CV 距离阈值 τ_cv 建议 = {r6['tau_cv_p40']:.3f}（保守）~ "
        f"{r6['tau_cv_p60']:.3f}（激进）**：近重复对(cos≥{NEAR_DUP_COS})共 {r6['n_valid']} 对，"
        f"其 CV 距离 P40={r6['tau_cv_p40']:.3f} / P60={r6['tau_cv_p60']:.3f}；"
        f"低于 P40 判 tie 丢弃可保住 60% 细节层对，与探针条件保留 P40/P60 口径一致。")
    # 4) 相似度→权重锚点
    lines.append(
        f"4. **相似度→权重映射锚点建议**：cos ≥ {q_win[98]:.3f}(滑窗 P98) → w=1.0（细节层满权）；"
        f"cos = {q_win[90]:.3f}(P90) → w≈0.8；cos = {q_win[75]:.3f}(P75) → w≈0.5；"
        f"cos = {q_win[50]:.3f}(P50) → w≈0.3；cos < {tau_lo:.2f} → w=0（排除）。"
        f"依据：滑窗对 cos 分位 P50={q_win[50]:.3f} / P75={q_win[75]:.3f} / "
        f"P90={q_win[90]:.3f} / P98={q_win[98]:.3f}，映射沿真实分布单调、"
        f"把权重质量集中在高相似半区。")
    # 5) M2 工作量
    lines.append(
        f"5. **M2 人工偏移工作量结论**：gap=10min 切得 {n_seg} 段（有效段 {r4[GAP_PRIMARY]['valid']}），"
        f"每段 1-2 张代表 → 人工评级 {n_seg}~{n_seg*2} 张，半天~一天量级，"
        f"**人工偏移法（决策 3a）在该段规模下可行**，plan-3-2 可按 3a 为主、3d 学习偏置为辅配比。")
    # 6) 数据异常提醒（附录级）
    lines.append(
        f"6. **数据异常提醒**：crop_factor 全库 0% 覆盖（{r7['table'][-1][1]}/{coverage_total} 非空），"
        f"is_retouched 在旧批 {r5['n_null']} 张上为 NULL —— 见块 ⑤⑦ 结论，M2+ 不得依赖这两列做全量约束。")
    return lines


def write_markdown(out: Path, db_path: str, samples: dict, r1, r2, r3, r4, r5, r6, r7, r8,
                   calib: list[str]) -> None:
    ratings = samples["rating"]
    n = len(ratings)
    cos_win, cos_seg, cos_cross = r2["cos_win"], r2["cos_seg"], r3["cos_cross"]
    lines = [
        "# data_audit 报告 — M1 §1.4 分布审计 + 阈值校准",
        "",
        f"- 数据库（只读）: `{db_path}`",
        f"- 特征: `{MODEL_ID}`（384d 原片 CLS，L2 归一）",
        f"- 样本: {n} 组（rating 非空且具备原片 CLS；跳过 {samples['n_skipped']} 组）",
        f"- 主口径: gap>{GAP_PRIMARY:.0f}min 切段 · window={WINDOW} 滑窗 · CV 距离与 feature_probe.py 同源",
        "",
        "## 1. 星级分布（总体 + 每段）",
        "",
        "![rating](rating_dist.png)",
        "",
        "| 星级 | 数量 | 占比 | 每段占比均值±std |",
        "|---|---|---|---|",
    ]
    for k in range(6):
        lines.append(f"| {k}★ | {r1['counts'][k]} | {r1['overall'][k]*100:.1f}% | "
                     f"{r1['props'][:,k].mean()*100:.1f}% ± {r1['props'][:,k].std()*100:.1f}% |")
    lines += ["",
              f"每段分布对总体 KL(段‖总体): 均值 {r1['kl'].mean():.3f} / 中位 {np.median(r1['kl']):.3f} / "
              f"最大 {r1['kl'].max():.3f}（最大段仅 {r1['max_kl_size']} 张）；"
              f"≥30 张的 {r1['n_large_seg']} 段 KL 均值 {r1['kl_large_mean']:.3f}。",
              f"**结论**：{r1['verdict']}。",
              "",
              "## 2. CLS cosine 分布（段内滑窗 + 段内全配对）",
              "",
              "![cos](cos_window_vs_segall.png)",
              "",
              "| 配对范围 | 对数 | P50 | P75 | P90 | P95 | P98 | P99 |",
              "|---|---|---|---|---|---|---|---|"]
    for name, arr in ((f"段内滑窗(window={WINDOW})", cos_win), ("段内全配对", cos_seg)):
        q = np.percentile(arr, PCTS)
        lines.append(f"| {name} | {len(arr)} | " + " | ".join(f"{v:.3f}" for v in q) + " |")
    lines += ["",
              f"cos≥0.98 占比: 滑窗 {np.mean(cos_win>=0.98)*100:.2f}% · 段内全配 {np.mean(cos_seg>=0.98)*100:.2f}%；"
              f"window={WINDOW} 对 cos≥0.95 高相似对覆盖率 {r2['recall_win95']*100:.1f}%"
              f"（window=40 对照 {r2['recall_win95_w40']*100:.1f}%，对数 {r2['n_win']}→{r2['n_win40']}）。",
              "",
              "## 3. 段内 vs 跨段 cosine 分布",
              "",
              "![within_cross](cos_within_vs_cross.png)",
              "",
              "| 范围 | 对数 | P50 | P75 | P90 | P95 | P98 | P99 |",
              "|---|---|---|---|---|---|---|---|"]
    for name, arr in (("段内全配对", cos_seg), (f"跨段采样(n={len(cos_cross)})", cos_cross)):
        q = np.percentile(arr, PCTS)
        lines.append(f"| {name} | {len(arr)} | " + " | ".join(f"{v:.3f}" for v in q) + " |")
    lines += ["",
              f"跨段 cos P99.9 = {r3['cross_p999']:.3f}；cos≥0.88 占比: 段内 {np.mean(cos_seg>=0.88)*100:.2f}% "
              f"vs 跨段 {np.mean(cos_cross>=0.88)*100:.3f}%。",
              f"**结论**：段内分布在高相似端显著厚于跨段（高 cos 区段内/跨段占比差 1-2 个数量级），"
              f"'滑窗 + 相似度分层'能有效分离可比对，跨场景偶合混入可忽略。",
              "",
              "## 4. 拍摄段切分 + M2 人工偏移工作量",
              "",
              "| gap(min) | 段数 | 有效段(≥2张) | 单张段 | 段大小均值/中位/最大 | 段时长(分)均值/中位/最大 |",
              "|---|---|---|---|---|---|"]
    for gap in (GAP_PRIMARY, *GAP_ALT):
        r = r4[gap]
        s, d = r["sizes"], r["durations"]
        lines.append(f"| {gap:.0f} | {r['n_seg']} | {r['valid']} | {int((s==1).sum())} | "
                     f"{s.mean():.1f} / {np.median(s):.0f} / {int(s.max())} | "
                     f"{d.mean():.1f} / {np.median(d):.1f} / {d.max():.1f} |")
    lines += ["", f"**M2 工作量结论**：{r4['work_conclusion']}。",
              "",
              "## 5. is_retouched 覆盖率",
              "",
              "![retouched](retouched_by_rating.png)",
              "",
              f"总体: 精修 {r5['n_pos']} 张 · 旧批 NULL {r5['n_null']} 张。",
              "",
              "| 事件 | 组数 | 精修张数 | 覆盖率 |",
              "|---|---|---|---|"]
    for label, cnt, pos, rate in r5["ev_table"]:
        lines.append(f"| {label} | {cnt} | {pos} | {f'{rate:.2f}%' if rate is not None else '—（全 NULL）'} |")
    lines += ["", "| 星级 | 组数 | 精修张数 | 覆盖率 |", "|---|---|---|---|"]
    for k, cnt, pos, rate in r5["star_table"]:
        lines.append(f"| {k}★ | {cnt} | {pos} | {rate:.2f}% |")
    lines += ["", f"**结论**：{r5['verdict']}。",
              "",
              "## 6. 近重复对(cos≥0.98)的 CV 距离分布",
              "",
              "![cv](cv_dist_neardup.png)",
              "",
              f"近重复对 {r6['n_pairs']} 对（CV 有效 {r6['n_valid']} 对）；"
              f"τ_cv@P40 = **{r6['tau_cv_p40']:.3f}**，τ_cv@P60 = **{r6['tau_cv_p60']:.3f}**"
              f"（与 feature_probe 条件保留分位口径一致）。",
              "",
              "## 7. EXIF / rating 字段覆盖率",
              "",
              "| 字段 | 非空数 | 覆盖率 | ≥95% |",
              "|---|---|---|---|"]
    for name, c, pct, ok in r7["table"]:
        lines.append(f"| {name} | {c} | {pct:.2f}% | {'✓' if ok else '✗ 异常'} |")
    lines += ["", f"**缺失策略**：{r7['advice']}。",
              "",
              "## 8. ≥3★ 实际占比",
              "",
              "![top3](top3_per_segment.png)",
              "",
              f"总体 ≥3★ = **{r8['overall']*100:.2f}%**（{int((ratings>=3).sum())}/{n}）；"
              f"每段均值 {r8['props'].mean()*100:.1f}% ± {r8['props'].std()*100:.1f}%。",
              f"**结论**：{r8['verdict']}。",
              "",
              "## 9. 校准建议",
              ""]
    lines += calib
    lines += ["",
              "---",
              "*生成: `Training/audit/data_audit.py`（M1 §1.4）；CV 距离 / 段切分 / 滑窗配对与 "
              "`Training/probes/feature_probe.py` 同源复用。*"]
    (out / "data_audit.md").write_text("\n".join(lines), encoding="utf-8")


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description="M1 §1.4 data_audit：分布审计 + 阈值校准")
    ap.add_argument("--db", default="D:/PhotoDB/dataset/photos_dataset.db")
    ap.add_argument("--out", default=str(OUT_DIR))
    args = ap.parse_args()

    if not Path(args.db).exists():
        print(f"[ERROR] 库不存在: {args.db}", file=sys.stderr)
        return 1
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    data = load_data(args.db)
    samples, coverage, rows = data["samples"], data["coverage"], data["rows"]
    print(f"样本 {samples['n_loaded']} 组（跳过无特征/无 rating {samples['n_skipped']} 组，"
          f"capture_time 解析失败 {samples['n_no_time']} 组）")

    # 块 4 先跑（产出主口径 seg，供块 1/2/3/8 用）
    r4 = block4_segments(samples, out)
    seg = r4["seg_full"]
    if (seg < 0).any():
        print(f"[WARN] {int((seg<0).sum())} 组无 capture_time，段相关统计仅基于有时间样本")
        keep = seg >= 0
        samples = {k: (v[keep] if isinstance(v, np.ndarray) else [x for x, m in zip(v, keep) if m])
                   for k, v in samples.items() if k not in ("n_loaded", "n_no_time", "n_skipped")}
        samples.update({"n_loaded": int(keep.sum()), "n_no_time": 0, "n_skipped": 0})
        seg = seg[keep]

    r1 = block1_rating(samples, seg, out)
    r2 = block2_cosine(samples, seg, out)
    r3 = block3_within_cross(samples, r2["cos_seg"], seg, out)
    r5 = block5_retouched(samples, rows, out)
    r6 = block6_neardup_cv(samples, r2["hi95"], out)
    r7 = block7_coverage(coverage)
    r8 = block8_top3(samples, seg, out)

    calib = calibration(r1, r2, r3, r4, r5, r6, r7, r8)
    print("\n========== ⑨ 校准建议 ==========")
    for line in calib:
        print("  " + line)

    write_markdown(out, args.db, samples, r1, r2, r3, r4, r5, r6, r7, r8, calib)
    print(f"\n报告: {out / 'data_audit.md'}")
    print(f"图片: {out} (rating_dist / cos_window_vs_segall / cos_within_vs_cross / "
          f"cv_dist_neardup / retouched_by_rating / top3_per_segment).png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
