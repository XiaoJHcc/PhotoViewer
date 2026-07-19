"""
feature_probe.py — DINOv3 增强制式线性探针（Plan-3-1 §1.2 / §1.5，段级 split 版）

方法论（本版与上游对齐，见任务书）：
    1. 按 capture_time 完整时间戳把整批切成"拍摄段"（相邻间隔 > gap 阈值处切段），gap 阈值扫几档。
    2. 段内按张数滑动窗口（不跨段）两两配对，只保留 rating 不同的对。
    3. 每对算原片 CLS cosine（语义相似度）+ CV 距离（锐度/抖动/对比度三标量归一欧氏距离，tie 判据）。
    4. 按 cosine 分三层：细节层（≥τ_hi，条件保留——CV 距离不够大则判 tie 丢弃）／构图层
       （τ_lo~τ_hi，GATE 测试集）／跨场景（<τ_lo，排除）。
    5. 主口径 = 段级 split（train/test 拍摄段不重叠，杀近重复+场景泄漏）；段数不足或某折样本
       不够时回退连拍组（union-find cosine≥τ_hi）级 split；对级 CV（乐观上界）保留作对照。
    6. 探针矩阵：{CLS原片, CLS增强, CLS多视图}（--with-patch 加 patch 视图）× {构图层:全部/Δ=1/Δ≥2,
       细节层(条件保留后):全部}，段级 split 准确率 ± std。

阈值（gap/window/τ_hi/τ_lo）全部按 CLI 列表扫描；耗时的探针矩阵（§4 tier 计数 + §6 概率矩阵）
以列表第一个值组成的"主口径组合"为完整报告对象，其余组合只汇总"构图层 Δ=1 最佳制式准确率"
一行，避免把矩阵撑到不可读（这是报告粒度上的实现取舍，不改变算法本身；见对话记录）。

用法：
    Training/.venv/Scripts/python.exe Training/probes/feature_probe.py \
        --db D:/PhotoDB/dataset/photos_dataset.db --no-tsne
"""
from __future__ import annotations

import argparse
import sqlite3
import sys
import warnings
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import numpy as np

MODEL_ID_ORIG = "dinov3_vits16_f32_518_v1"
FEATURE_DIM = 384      # 仅 patch 池化路径用（ViT-S）；CLS 维度按库内实测（梯2 ViT-L=1024）
PATCH_TOKENS = 1024  # ViT-S/16@518 patch 网格 32×32

# CV 网格布局常量，须与 PhotoViewer/Core/AI/CvGridResult.cs 保持一致。
CV_SCALAR_COUNT = 7
CV_GRID_SIZE = 32
CV_PLANE_LEN = CV_GRID_SIZE * CV_GRID_SIZE  # 1024
CV_IDX_EDGE_WIDTH_P20 = 1   # 锐度（越小越锐）
CV_IDX_DRAG_WIDTH = 3       # 抖动量级
CV_IDX_BLOCK_CONTRAST = 6   # 曝光对比 0-255

FOLDS = 5           # 段级/组级/对级 split 统一折数
FOLD_SEED = 0        # 折分配随机种子（用于打散同权重单元的顺序）


@dataclass
class Sample:
    """一个指纹组的探针样本：身份 + 星级 + 拍摄时间 + 两路 CLS + CV 全图聚合标量 + 事件标签。"""
    fingerprint: str
    rating: int
    capture_time: datetime | None
    orig: np.ndarray               # 原片 CLS（L2 归一化）
    enh: np.ndarray                # 增强 CLS（L2 归一化）
    cv_sharp: float                 # nanmean(edge_width_p20)，可能为 NaN
    cv_shake: float                 # nanmean(drag_width)，可能为 NaN
    cv_contrast: float              # nanmean(block_contrast)，可能为 NaN
    event: str = ""                 # 事件标签（photos.event_label，空串=无）
    patch_mean: np.ndarray | None = None     # 原片 patch mean 池化（384，L2）；--with-patch 时填
    patch_meanstd: np.ndarray | None = None  # 原片 patch mean+std 池化（768）；--with-patch 时填


def l2_normalize(v: np.ndarray) -> np.ndarray:
    """按行 L2 归一化（幂等：库里可能已归一，再归一无副作用）。"""
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    n[n == 0] = 1.0
    return v / n


def discover_enhanced_model_id(db_path: str, model_orig: str) -> str:
    """
    自动发现增强 model_id：库里 photo_features 中非原片的那个（增强算法的参数已冻结进后缀，
    脚本不重复硬编码，后缀参数变了也不用改脚本）。恰好一个则用之，否则报错列出让用户 --enh-model 指定。
    """
    conn = sqlite3.connect(db_path)
    try:
        others = [m for (m,) in conn.execute(
            "SELECT DISTINCT model_id FROM photo_features WHERE model_id != ?", (model_orig,))]
    finally:
        conn.close()
    if len(others) == 1:
        return others[0]
    raise SystemExit(
        f"[ERROR] 无法自动确定增强 model_id（候选 {others}）；请用 --enh-model 显式指定")


def _cv_aggregate(blob: bytes | None) -> tuple[float, float, float]:
    """把 cv_grid BLOB（7×32×32 小端 float32）聚合成三个全图标量：
    (nanmean(edge_width_p20), nanmean(drag_width), nanmean(block_contrast))。
    整块全 NaN（该标量在全图都无有效读数）或 BLOB 缺失时返回 NaN，由调用方在距离计算里跳过。
    """
    if blob is None:
        return float("nan"), float("nan"), float("nan")
    arr = np.frombuffer(blob, dtype="<f4")
    if arr.size != CV_SCALAR_COUNT * CV_PLANE_LEN:
        return float("nan"), float("nan"), float("nan")
    grid = arr.reshape(CV_SCALAR_COUNT, CV_PLANE_LEN)
    with np.errstate(all="ignore"), warnings.catch_warnings():
        warnings.simplefilter("ignore", category=RuntimeWarning)
        sharp = np.nanmean(grid[CV_IDX_EDGE_WIDTH_P20])
        shake = np.nanmean(grid[CV_IDX_DRAG_WIDTH])
        contrast = np.nanmean(grid[CV_IDX_BLOCK_CONTRAST])
    return float(sharp), float(shake), float(contrast)


def load_samples(db_path: str, model_orig: str, model_enh: str,
                 with_patch: bool = False) -> tuple[list[Sample], int]:
    """从数据集库读取同时具备原片+增强 CLS 的指纹样本，附带拍摄时间与 CV 聚合标量。
    返回 (samples, n_missing_time)：n_missing_time 为 capture_time 解析失败/为空、
    因而无法参与拍摄段切分的样本数（本函数仍会尝试为其保留 capture_time=None，调用方决定如何处理）。
    """
    conn = sqlite3.connect(db_path)
    try:
        meta: dict[str, tuple] = {}
        n_missing_time = 0
        for fp, rating, ctime, cv_blob, event in conn.execute(
            # 未评(NULL)不参与探针——NULL ≠ 0★（2026-07-19 修正：原 COALESCE(rating,0) 会把
            # 2732 个未评组错当 0★ 标签注入配对；0★ 只来自 NO 层回填，NULL 一律剔除）
            "SELECT fingerprint, rating, capture_time, cv_grid, COALESCE(event_label,'') FROM photos WHERE rating IS NOT NULL"
        ):
            parsed = None
            if ctime:
                try:
                    parsed = datetime.fromisoformat(str(ctime))
                except ValueError:
                    parsed = None
            if parsed is None:
                n_missing_time += 1
            sharp, shake, contrast = _cv_aggregate(cv_blob)
            meta[fp] = (int(rating), parsed, sharp, shake, contrast, str(event))

        feats: dict[str, dict[str, np.ndarray]] = {}
        for fp, model_id, blob in conn.execute(
            "SELECT fingerprint, model_id, cls_vector FROM photo_features WHERE model_id IN (?,?)",
            (model_orig, model_enh),
        ):
            vec = np.frombuffer(blob, dtype="<f4")
            # CLS 维度按库内实测（ViT-S=384 / 梯2 ViT-L=1024），不按 FEATURE_DIM 硬过滤；
            # 同指纹原片/增强同维在样本组装处校验。
            feats.setdefault(fp, {})[model_id] = vec.astype(np.float32)

        # 原片 patch token（1024×384）→ mean / mean+std 池化（逐行处理，不整体驻留 ~2GB raw）
        patch_pooled: dict[str, tuple[np.ndarray, np.ndarray]] = {}
        if with_patch:
            for fp, blob in conn.execute(
                "SELECT fingerprint, patch_tokens FROM photo_patches WHERE model_id = ?", (model_orig,)
            ):
                t = np.frombuffer(blob, dtype="<f4")
                if t.size != PATCH_TOKENS * FEATURE_DIM:
                    continue
                t = t.reshape(PATCH_TOKENS, FEATURE_DIM)
                m = l2_normalize(t.mean(axis=0))          # 均值：整体语义（≈CLS 的另一种 pooling）
                s = l2_normalize(t.std(axis=0))           # 标准差：空间离散度（锐度/对比分布的载体）
                patch_pooled[fp] = (m, np.concatenate([m, s]))
    finally:
        conn.close()

    samples: list[Sample] = []
    for fp, (rating, ctime, sharp, shake, contrast, event) in meta.items():
        pair = feats.get(fp)
        if not pair or model_orig not in pair or model_enh not in pair:
            continue
        if pair[model_orig].shape != pair[model_enh].shape:
            continue                                   # 原片/增强维度不一致（异常行），剔除
        pm = ps = None
        if with_patch:
            pooled = patch_pooled.get(fp)
            if pooled is None:
                continue                                   # patch 缺失的样本不进，保证各视图样本一致
            pm, ps = pooled
        samples.append(Sample(
            fingerprint=fp, rating=rating, capture_time=ctime,
            orig=l2_normalize(pair[model_orig]), enh=l2_normalize(pair[model_enh]),
            cv_sharp=sharp, cv_shake=shake, cv_contrast=contrast,
            patch_mean=pm, patch_meanstd=ps, event=event,
        ))
    n_missing = sum(1 for s in samples if s.capture_time is None)
    return samples, n_missing


def _make_clf(kind: str):
    """探针分类器：linear=对数几率（过原点，配 pairwise 对称化）；mlp=单隐层非线性头。"""
    if kind == "mlp":
        from sklearn.neural_network import MLPClassifier
        return MLPClassifier(hidden_layer_sizes=(64,), alpha=1e-2, max_iter=200,
                             early_stopping=True, n_iter_no_change=8, random_state=0)
    from sklearn.linear_model import LogisticRegression
    return LogisticRegression(fit_intercept=False, C=1.0, max_iter=2000)


def cluster_bursts(orig: np.ndarray, cos_thr: float) -> np.ndarray:
    """
    连拍组聚类：原片 CLS cosine ≥ 阈值的连通分量（union-find）。
    同一连拍/同构图（cosine 极高）归一组；返回每样本的组标签（0..G-1）。
    本函数除了旧的 --group-report 诊断模式外，也是新流程"段级 split 不可用时"的
    回退分组依据（用 τ_hi 当聚类阈值，语义上与"细节层"边界一致）。
    """
    n = orig.shape[0]
    parent = np.arange(n)

    def find(x: int) -> int:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return int(x)

    sim = orig @ orig.T
    iu, ju = np.triu_indices(n, k=1)
    mask = sim[iu, ju] >= cos_thr
    for a, b in zip(iu[mask].tolist(), ju[mask].tolist()):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    labels = np.array([find(i) for i in range(n)])
    _, inv = np.unique(labels, return_inverse=True)   # 重编号为 0..G-1
    return inv


def report_groups(samples: list[Sample], thresholds: list[float]) -> None:
    """（legacy `--group-report` 诊断）扫描若干 cosine 阈值，报告连拍组结构，供人工核验聚类阈值。"""
    orig = np.stack([s.orig for s in samples])
    ratings = np.array([s.rating for s in samples])
    for thr in thresholds:
        g = cluster_bursts(orig, thr)
        sizes = np.bincount(g)
        multi = sizes[sizes >= 2]
        spans, with_diff = [], 0
        for gid in range(g.max() + 1):
            rs = ratings[g == gid]
            if len(rs) >= 2:
                span = int(rs.max() - rs.min())
                spans.append(span)
                if span > 0:
                    with_diff += 1
        spans_arr = np.array(spans) if spans else np.array([0])
        print(f"\n■ cosine≥{thr:.2f} —— {g.max()+1} 组 · 单张组 {(sizes==1).sum()} · 多张组 {(sizes>=2).sum()}")
        if len(multi):
            print(f"  多张组: 均值 {multi.mean():.1f} 张 / 最大 {multi.max()} 张 · "
                  f"组内有星级差的组 {with_diff}（占多张组 {with_diff/len(multi)*100:.0f}%）")
            print(f"  组内星级跨度: 均值 {spans_arr.mean():.1f} / 最大 {spans_arr.max()}")
            for lo, hi, lbl in [(2, 2, "2 张"), (3, 5, "3-5 张"), (6, 10, "6-10 张"), (11, 10**9, ">10 张")]:
                c = int(((sizes >= lo) & (sizes <= hi)).sum())
                if c:
                    print(f"    {lbl}: {c} 组")


def probe_accuracy(feat: np.ndarray, ii: np.ndarray, jj: np.ndarray, ratings: np.ndarray,
                   unit_fold: np.ndarray | None = None, folds: int = FOLDS, seed: int = 0,
                   clf_kind: str = "linear") -> tuple[float, float, int]:
    """
    线性探针：在差异向量 feat[ii]-feat[jj] 上预测 sign(rating 差)。
    对称化（同时喂 ±diff/±label，fit_intercept=False）消除方向偏置。
    unit_fold=None → 对级 StratifiedKFold（同一"单元"——指纹/段/组——可能跨 train/test，乐观上界）。
    unit_fold 给定 → 单元级 split：每个样本的折号（同段/同连拍组样本折号相同，由调用方预先分配），
      只用两端同折的对、留一折出，train/test 单元不重叠（无泄漏、真实泛化）。
    返回 (平均准确率, 标准差, 实际参与评估的对数)。
    """
    from sklearn.model_selection import StratifiedKFold

    diff = feat[ii] - feat[jj]
    label = np.where(ratings[ii] > ratings[jj], 1, -1)

    if unit_fold is None:
        X = np.concatenate([diff, -diff]); y = np.concatenate([label, -label])
        skf = StratifiedKFold(n_splits=folds, shuffle=True, random_state=seed)
        accs = [_make_clf(clf_kind).fit(X[tr], y[tr]).score(X[te], y[te])
                for tr, te in skf.split(X, y)]
        return float(np.mean(accs)), float(np.std(accs)), len(diff)

    same = unit_fold[ii] == unit_fold[jj]          # 只保留两端同折的对（其 fold = unit_fold[ii]）
    pf = unit_fold[ii]
    accs = []
    for f in range(folds):
        te = same & (pf == f); tr = same & (pf != f)
        if te.sum() < 10 or tr.sum() < 10:
            continue
        Xtr = np.concatenate([diff[tr], -diff[tr]]); ytr = np.concatenate([label[tr], -label[tr]])
        Xte = np.concatenate([diff[te], -diff[te]]); yte = np.concatenate([label[te], -label[te]])
        clf = _make_clf(clf_kind).fit(Xtr, ytr)
        accs.append(clf.score(Xte, yte))
    if not accs:
        return float("nan"), float("nan"), int(same.sum())
    return float(np.mean(accs)), float(np.std(accs)), int(same.sum())


def probe_oof_correct(feat: np.ndarray, ii: np.ndarray, jj: np.ndarray, ratings: np.ndarray,
                      unit_fold: np.ndarray, folds: int = FOLDS,
                      clf_kind: str = "linear") -> tuple[np.ndarray, np.ndarray]:
    """单元级 split 的逐对 OOF 诊断版：与 probe_accuracy 同折同对称化训练，
    返回 (correct, evaluated)——correct[i] 表示第 i 对在留出折上是否判对，evaluated[i] 表示
    该对是否参与评估（只评两端同折的对）。用于按星级边界 / 按事件拆解准确率。"""
    diff = feat[ii] - feat[jj]
    label = np.where(ratings[ii] > ratings[jj], 1, -1)
    same = unit_fold[ii] == unit_fold[jj]
    pf = unit_fold[ii]
    correct = np.zeros(len(ii), bool)
    for f in range(folds):
        te = same & (pf == f); tr = same & (pf != f)
        if te.sum() < 10 or tr.sum() < 10:
            continue
        Xtr = np.concatenate([diff[tr], -diff[tr]]); ytr = np.concatenate([label[tr], -label[tr]])
        clf = _make_clf(clf_kind).fit(Xtr, ytr)
        correct[te] = (clf.decision_function(diff[te]) * label[te] > 0)
    return correct, same


# ---------------------------------------------------------------------------
# 拍摄段切分
# ---------------------------------------------------------------------------

def split_segments(times: list[datetime], gap_minutes: float) -> np.ndarray:
    """按时间间隔切拍摄段：samples 须已按 capture_time 升序排列。
    相邻两张时间差 > gap_minutes 处切一刀；返回每个样本的段号（0..S-1，随时间单调递增）。
    """
    seg = np.zeros(len(times), dtype=np.int64)
    cur = 0
    for k in range(1, len(times)):
        delta_min = (times[k] - times[k - 1]).total_seconds() / 60.0
        if delta_min > gap_minutes:
            cur += 1
        seg[k] = cur
    return seg


def report_segment_structure(seg_ids: np.ndarray, times: list[datetime], gap_minutes: float) -> None:
    """打印某 gap 阈值下的段结构：段数、有效段（≥2张）数、段大小分布、段时长分布。"""
    n_seg = int(seg_ids.max()) + 1
    sizes = np.bincount(seg_ids)
    durations = np.empty(n_seg)
    for s in range(n_seg):
        idx = np.where(seg_ids == s)[0]
        durations[s] = (times[idx[-1]] - times[idx[0]]).total_seconds() / 60.0
    valid = int((sizes >= 2).sum())
    print(f"\n■ gap>{gap_minutes:.0f}min —— {n_seg} 段（有效段[≥2张] {valid} · 单张段 {int((sizes==1).sum())}）")
    print(f"  段大小(张): 均值 {sizes.mean():.1f} / 中位 {np.median(sizes):.0f} / 最大 {int(sizes.max())}")
    print(f"  段时长(分钟): 均值 {durations.mean():.1f} / 中位 {np.median(durations):.1f} / 最大 {durations.max():.1f}")


# ---------------------------------------------------------------------------
# 段内滑动窗口配对 + CV 距离
# ---------------------------------------------------------------------------

def sliding_window_pairs(seg_ids: np.ndarray, window: int) -> np.ndarray:
    """段内按张数滑动窗口生成配对（位置索引，即排序后数组下标，不跨段）。
    宽度为 W 的滑窗覆盖位置 [k, k+W-1]；窗口内两两成对的并集，等价于"同段内位置差 ≤ W-1"的
    所有 (a,b) 对——用位置差直接生成即可，无需真的逐窗口枚举再去重。
    返回 (M,2) 数组，值为排序后数组下标（a<b）。
    """
    pairs: list[tuple[int, int]] = []
    n = len(seg_ids)
    start = 0
    while start < n:
        end = start
        while end + 1 < n and seg_ids[end + 1] == seg_ids[start]:
            end += 1
        for a in range(start, end + 1):
            b_max = min(a + window - 1, end)
            for b in range(a + 1, b_max + 1):
                pairs.append((a, b))
        start = end + 1
    if not pairs:
        return np.empty((0, 2), dtype=np.int64)
    return np.array(pairs, dtype=np.int64)


def pairwise_cv_distance(cv_sharp: np.ndarray, cv_shake: np.ndarray, cv_contrast: np.ndarray,
                         std_sharp: float, std_shake: float, std_contrast: float,
                         ii: np.ndarray, jj: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """一对的 CV 距离 = 三个全图聚合标量差、各自按全库该标量 std 归一化后的欧氏距离。
    任一图该标量为 NaN（全图无有效读数）时该分量跳过，只用剩余有效分量算距离；
    三个分量全 NaN 时距离记 NaN（由调用方剔除，不参与三层分层与条件保留判断）。
    返回 (dist, n_valid_components)。
    """
    d_sharp = (cv_sharp[ii] - cv_sharp[jj]) / std_sharp
    d_shake = (cv_shake[ii] - cv_shake[jj]) / std_shake
    d_contrast = (cv_contrast[ii] - cv_contrast[jj]) / std_contrast
    comps = np.stack([d_sharp, d_shake, d_contrast], axis=1)  # (M,3)，可能含 NaN
    valid = ~np.isnan(comps)
    n_valid = valid.sum(axis=1)
    comps_zeroed = np.where(valid, comps, 0.0)
    dist = np.sqrt((comps_zeroed ** 2).sum(axis=1))
    dist = np.where(n_valid > 0, dist, np.nan)
    return dist, n_valid


def compute_window_pairs(samples: list[Sample], seg_ids: np.ndarray, window: int) -> dict:
    """给定段号与窗口宽度，生成段内滑窗配对，只保留 rating 不同的对，并算好 cosine / CV 距离。
    返回字典：ii, jj（排序后数组下标）、cos（原片 CLS 余弦）、dr（|Δrating|）、cv_dist、
    n_raw（过滤 rating 前的原始对数，供 §2 结构报告）。
    """
    orig = np.stack([s.orig for s in samples])
    ratings = np.array([s.rating for s in samples])
    sharp = np.array([s.cv_sharp for s in samples])
    shake = np.array([s.cv_shake for s in samples])
    contrast = np.array([s.cv_contrast for s in samples])
    with np.errstate(all="ignore"), warnings.catch_warnings():
        warnings.simplefilter("ignore", category=RuntimeWarning)
        std_sharp = np.nanstd(sharp) or 1.0
        std_shake = np.nanstd(shake) or 1.0
        std_contrast = np.nanstd(contrast) or 1.0

    raw = sliding_window_pairs(seg_ids, window)
    n_raw = len(raw)
    if n_raw == 0:
        empty = np.array([], dtype=np.int64)
        return dict(ii=empty, jj=empty, cos=np.array([]), dr=np.array([]),
                   cv_dist=np.array([]), n_raw=0)
    ii, jj = raw[:, 0], raw[:, 1]
    dr = np.abs(ratings[ii] - ratings[jj])
    keep = dr > 0
    ii, jj, dr = ii[keep], jj[keep], dr[keep]
    cos = np.sum(orig[ii] * orig[jj], axis=1)  # 已 L2 归一，点积=cosine
    cv_dist, _ = pairwise_cv_distance(sharp, shake, contrast, std_sharp, std_shake, std_contrast, ii, jj)
    return dict(ii=ii, jj=jj, cos=cos, dr=dr, cv_dist=cv_dist, n_raw=n_raw)


# ---------------------------------------------------------------------------
# 三层分层 + 细节层条件保留
# ---------------------------------------------------------------------------

def three_tier_masks(cos: np.ndarray, tau_hi: float, tau_lo: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """按原片 CLS cosine 把配对切三层：细节层(≥τ_hi) / 构图层(τ_lo~τ_hi) / 跨场景(<τ_lo)。"""
    detail = cos >= tau_hi
    comp = (cos >= tau_lo) & (cos < tau_hi)
    cross = cos < tau_lo
    return detail, comp, cross


def conditional_retain_detail(cv_dist: np.ndarray, quantile: float) -> tuple[np.ndarray, float]:
    """细节层条件保留：CV 距离 > 该层分布的 quantile 分位数才留，否则判 tie 丢弃。
    CV 距离为 NaN 的对（三标量全无效）视为不可判定，一并丢弃（计入 tie）。
    返回 (keep_mask, tau_cv 取值)。
    """
    valid = ~np.isnan(cv_dist)
    if valid.sum() == 0:
        return np.zeros_like(cv_dist, dtype=bool), float("nan")
    tau_cv = float(np.quantile(cv_dist[valid], quantile))
    keep = valid & (cv_dist > tau_cv)
    return keep, tau_cv


# ---------------------------------------------------------------------------
# 单元级 split（段级 / 连拍组级）：折分配 + 段级不可用时的回退
# ---------------------------------------------------------------------------

def assign_unit_folds_balanced(unit_ids: np.ndarray, unit_size: dict[int, int],
                               n_folds: int, seed: int) -> np.ndarray:
    """把"单元"（拍摄段或连拍组）分配到 n_folds 个折，贪心装箱使折间样本量大致均衡
    （按单元大小降序、优先塞进当前样本量最小的折；相同大小的单元顺序先随机打散避免确定性偏置）。
    返回每个样本对应的折号（0..n_folds-1），同一单元内样本折号必然相同。
    """
    units = list(unit_size.keys())
    rng = np.random.default_rng(seed)
    rng.shuffle(units)
    units.sort(key=lambda u: -unit_size[u])  # Python sort 稳定，打散后的相对顺序在同权重时保留
    load = [0] * n_folds
    unit_to_fold: dict[int, int] = {}
    for u in units:
        f = int(np.argmin(load))
        unit_to_fold[u] = f
        load[f] += unit_size[u]
    return np.array([unit_to_fold[u] for u in unit_ids])


_burst_group_cache: dict[float, np.ndarray] = {}


def build_unit_fold_with_fallback(orig_all: np.ndarray, seg_ids: np.ndarray, tau_hi: float,
                                  probe_ii: np.ndarray, probe_jj: np.ndarray,
                                  folds: int = FOLDS, seed: int = FOLD_SEED) -> tuple[np.ndarray, str]:
    """主口径 = 段级 split；若有效段数(≥2张的段) < 5，或按段折分配后没有一折同时满足
    test/train 对数 ≥10（用传入的 probe_ii/probe_jj 试算，因为不同层/不同 τ 组合下可比对
    集合不同，可用性要按当前这批对判断），回退到连拍组级（union-find, cosine≥τ_hi）split。
    返回 (unit_fold, 使用的模式说明 "段级" / "连拍组级(回退)")。
    """
    seg_size = {int(s): int(c) for s, c in zip(*np.unique(seg_ids, return_counts=True))}
    valid_segs = sum(1 for c in seg_size.values() if c >= 2)
    if valid_segs >= 5:
        unit_fold = assign_unit_folds_balanced(seg_ids, seg_size, folds, seed)
        if len(probe_ii) > 0:
            same = unit_fold[probe_ii] == unit_fold[probe_jj]
            pf = unit_fold[probe_ii]
            usable = any(((pf == f) & same).sum() >= 10 and ((pf != f) & same).sum() >= 10
                        for f in range(folds))
            if usable:
                return unit_fold, "段级"
        else:
            return unit_fold, "段级"  # 无对可判定可用性时，仍按段级返回（上层会因 0 对而跳过）

    if tau_hi not in _burst_group_cache:
        _burst_group_cache[tau_hi] = cluster_bursts(orig_all, tau_hi)
    group_ids = _burst_group_cache[tau_hi]
    group_size = {int(g): int(c) for g, c in zip(*np.unique(group_ids, return_counts=True))}
    unit_fold = assign_unit_folds_balanced(group_ids, group_size, folds, seed)
    return unit_fold, "连拍组级(回退)"


# ---------------------------------------------------------------------------
# 探针矩阵
# ---------------------------------------------------------------------------

def _views_for(samples: list[Sample]) -> dict[str, np.ndarray]:
    """三/五种输入制式：CLS原片 / CLS增强 / CLS多视图（拼接）+ 可选 patch 均值 / 均+std。"""
    orig = np.stack([s.orig for s in samples])
    enh = np.stack([s.enh for s in samples])
    multi = np.concatenate([orig, enh], axis=1)
    views = {"CLS原片": orig, "CLS增强": enh, "CLS多视图": multi}
    if samples and samples[0].patch_mean is not None:
        views["patch均值"] = np.stack([s.patch_mean for s in samples])
        views["patch均+std"] = np.stack([s.patch_meanstd for s in samples])
    return views


def probe_stratum(views: dict[str, np.ndarray], ii: np.ndarray, jj: np.ndarray,
                  ratings: np.ndarray, unit_fold: np.ndarray | None, clf_kind: str) -> dict[str, tuple]:
    """对一层/一个分层的可比对，逐视图跑 probe_accuracy，返回 {视图名: (acc, sd, neval)}。"""
    out = {}
    for name, feat in views.items():
        out[name] = probe_accuracy(feat, ii, jj, ratings, unit_fold, clf_kind=clf_kind)
    return out


def print_stratum_row(label: str, results: dict[str, tuple], view_names: list[str]) -> int:
    """打印一行分层结果；返回该行的评估对数（用于 <300 告警判断，取任一视图的 neval，理论上一致）。"""
    cells = []
    neval = 0
    for name in view_names:
        acc, sd, neval = results[name]
        cells.append("   nan" if np.isnan(acc) else f"{acc*100:5.1f}±{sd*100:3.1f}")
    flag = " ⚠<300" if neval < 300 else ""
    print(f"  {label:<14}" + "".join(f"{c:>13}" for c in cells) + f"{neval:>10}{flag}")
    return neval


def run_combo(samples: list[Sample], seg_ids: np.ndarray, gap: float, window: int,
             tau_hi: float, tau_lo: float, quantiles: list[float], split_mode: str,
             clf_kind: str, verbose: bool) -> dict:
    """跑单个 (gap, window, τ_hi, τ_lo) 组合：三层对数统计 + 构图层/细节层探针矩阵。
    verbose=True 时打印完整分层计数 + 完整矩阵（主口径组合）；否则只算数不打印细节，
    由调用方汇总成一行摘要（其余扫描组合，避免报告过长）。
    返回结构化结果字典，供 markdown 报告与"最佳制式"汇总使用。
    """
    orig_all = np.stack([s.orig for s in samples])
    ratings = np.array([s.rating for s in samples])
    views = _views_for(samples)
    view_names = list(views.keys())

    wp = compute_window_pairs(samples, seg_ids, window)
    ii, jj, cos, dr, cv_dist = wp["ii"], wp["jj"], wp["cos"], wp["dr"], wp["cv_dist"]

    if len(ii) == 0:
        if verbose:
            print(f"\n■ gap={gap:.0f}min · window={window} · τ_hi={tau_hi:.2f} · τ_lo={tau_lo:.2f} —— 0 对配对，跳过")
        return dict(gap=gap, window=window, tau_hi=tau_hi, tau_lo=tau_lo, comp_pairs=0,
                   detail_pairs=0, cross_pairs=0, best_view=None, best_acc=float("nan"),
                   comp_neval=0, detail_result=None, split_used_comp=None, split_used_detail=None)

    detail_mask, comp_mask, cross_mask = three_tier_masks(cos, tau_hi, tau_lo)

    if verbose:
        print(f"\n■ gap={gap:.0f}min · window={window} · τ_hi={tau_hi:.2f} · τ_lo={tau_lo:.2f} "
              f"—— 候选对(dr>0) {len(ii)}：细节层 {int(detail_mask.sum())} · "
              f"构图层 {int(comp_mask.sum())} · 跨场景(排除) {int(cross_mask.sum())}")
        for q in quantiles:
            keep_q, tau_cv_q = conditional_retain_detail(cv_dist[detail_mask], q)
            n_detail = int(detail_mask.sum())
            n_keep = int(keep_q.sum())
            print(f"    细节层条件保留 @P{int(q*100)}（τ_cv={tau_cv_q:.3f}）："
                  f"留 {n_keep} / tie 丢弃 {n_detail - n_keep}")

    # 构图层：段级 split（不可用回退连拍组级），本版固定用 --split 参数选择的口径
    comp_ii, comp_jj, comp_dr = ii[comp_mask], jj[comp_mask], dr[comp_mask]
    if split_mode == "pair":
        comp_unit_fold, comp_split_used = None, "对级(乐观上界)"
    else:
        comp_unit_fold, comp_split_used = build_unit_fold_with_fallback(
            orig_all, seg_ids, tau_hi, comp_ii, comp_jj)
        if split_mode == "group":
            group_ids = _burst_group_cache.setdefault(tau_hi, cluster_bursts(orig_all, tau_hi))
            group_size = {int(g): int(c) for g, c in zip(*np.unique(group_ids, return_counts=True))}
            comp_unit_fold = assign_unit_folds_balanced(group_ids, group_size, FOLDS, FOLD_SEED)
            comp_split_used = "连拍组级(指定)"

    comp_strata = {"全部": np.ones(len(comp_ii), bool), "Δ=1": comp_dr == 1, "Δ≥2": comp_dr >= 2}
    comp_rows = {}
    best_view, best_acc, best_neval = None, float("-inf"), 0
    if verbose:
        print(f"  —— 构图层探针矩阵（{comp_split_used} split）——")
        print(f"  {'分层':<14}" + "".join(f"{k:>13}" for k in view_names) + f"{'评估对数':>10}")
    for label, mask in comp_strata.items():
        if mask.sum() < 20:
            if verbose:
                print(f"  {label:<14}{'(样本过少)':>13}")
            comp_rows[label] = None
            continue
        results = probe_stratum(views, comp_ii[mask], comp_jj[mask], ratings, comp_unit_fold, clf_kind)
        comp_rows[label] = results
        if verbose:
            print_stratum_row(label, results, view_names)
        if label == "Δ=1":
            for name in view_names:
                acc, sd, neval = results[name]
                if not np.isnan(acc) and acc > best_acc:
                    best_view, best_acc, best_neval = name, acc, neval

    # —— 诊断（仅 verbose 主口径）：构图层 Δ=1 按星级边界 / 按事件拆解 OOF 准确率 + 对级对照 ——
    # 用途：段级 chance 时分辨病灶——0-1 边界（技术质量）chance 而 2-3+（美学）可行 → 特征够用、
    # 技术层归 CV；全边界/全事件 chance → 特征瓶颈（H1）。见 EXECUTION-LOG 2026-07-19。
    if verbose and comp_rows.get("Δ=1") is not None:
        d_mask = comp_strata["Δ=1"]
        d_ii, d_jj = comp_ii[d_mask], comp_jj[d_mask]
        r_lo = np.minimum(ratings[d_ii], ratings[d_jj])
        r_hi = np.maximum(ratings[d_ii], ratings[d_jj])
        events = np.array([s.event or "(无事件)" for s in samples])
        d_event = events[d_ii]
        cls_views = {k: v for k, v in views.items() if k.startswith("CLS")}
        cls_names = list(cls_views.keys())
        oof_by_view = {}
        if comp_unit_fold is not None:
            for name, feat in cls_views.items():
                oof_by_view[name] = probe_oof_correct(feat, d_ii, d_jj, ratings,
                                                      comp_unit_fold, clf_kind=clf_kind)
            bounds = [(b, b + 1) for b in range(5)]
            print("  —— 诊断：构图层 Δ=1 按星级边界拆解（段级 split OOF 准确率%）——")
            print(f"  {'制式':<12}" + "".join(f"{f'{lo}-{hi}':>13}" for lo, hi in bounds))
            print(f"  {'对数':<12}" + "".join(
                f"{int(((r_lo == lo) & (r_hi == hi)).sum()):>13}" for lo, hi in bounds))
            for name in cls_names:
                correct, ev_mask = oof_by_view[name]
                cells = []
                for lo, hi in bounds:
                    m = ev_mask & (r_lo == lo) & (r_hi == hi)
                    cells.append(f"{correct[m].mean()*100:5.1f}(n={m.sum()})" if m.sum() >= 20 else "·")
                print(f"  {name:<12}" + "".join(f"{c:>13}" for c in cells))
            print("  —— 诊断：构图层 Δ=1 按事件拆解（段级 split OOF 准确率%）——")
            ev_counts = {e: int((d_event == e).sum()) for e in np.unique(d_event)}
            print(f"  {'事件':<24}" + "".join(f"{k:>13}" for k in cls_names) + f"{'对数':>10}")
            for e in sorted(ev_counts, key=ev_counts.get, reverse=True):
                m_ev = d_event == e
                cells = []
                for name in cls_names:
                    correct, ev_mask = oof_by_view[name]
                    m = ev_mask & m_ev
                    cells.append(f"{correct[m].mean()*100:5.1f}" if m.sum() >= 20 else "·")
                print(f"  {e:<24}" + "".join(f"{c:>13}" for c in cells) + f"{ev_counts[e]:>10}")
        print("  —— 对照：构图层 Δ=1 对级 split（乐观上界，分布内可学性）——")
        pair_results = probe_stratum(cls_views, d_ii, d_jj, ratings, None, clf_kind)
        print_stratum_row("Δ=1 对级", pair_results, cls_names)
        # 事件条件化探针（Kronecker one-hot⊗diff）：给每个事件自己的线性方向。
        # 判读：段级 >>50% → "单一全局规则"是病灶、特征可用（题材条件架构可解，无需升 backbone）；
        # 仍 ~50% → 特征真盲（H1），走决策 8 升级梯。见 EXECUTION-LOG 2026-07-19。
        if comp_unit_fold is not None:
            ev_list = sorted(set(d_event))
            onehot = np.stack([(d_event == e).astype(np.float64) for e in ev_list], axis=1)
            label_d = np.where(ratings[d_ii] > ratings[d_jj], 1, -1)
            same_d = comp_unit_fold[d_ii] == comp_unit_fold[d_jj]
            pf_d = comp_unit_fold[d_ii]
            print(f"  —— 诊断：事件条件化探针（one-hot x diff · {len(ev_list)} 事件 · 段级 split）——")
            for name in cls_names:
                feat = cls_views[name]
                diff = feat[d_ii] - feat[d_jj]
                kron = np.concatenate([diff * onehot[:, k:k + 1] for k in range(onehot.shape[1])], axis=1)
                accs = []
                for f in range(FOLDS):
                    te = same_d & (pf_d == f); tr = same_d & (pf_d != f)
                    if te.sum() < 10 or tr.sum() < 10:
                        continue
                    Xtr = np.concatenate([kron[tr], -kron[tr]]); ytr = np.concatenate([label_d[tr], -label_d[tr]])
                    Xte = np.concatenate([kron[te], -kron[te]]); yte = np.concatenate([label_d[te], -label_d[te]])
                    accs.append(_make_clf(clf_kind).fit(Xtr, ytr).score(Xte, yte))
                if accs:
                    print(f"    {name:<12}{np.mean(accs)*100:5.1f}±{np.std(accs)*100:3.1f}"
                          f"（{len(accs)} 折 · 特征 {kron.shape[1]} 维）")

    # 细节层（条件保留后，中位数分位）：全部 一行作对照
    detail_ii, detail_jj = ii[detail_mask], jj[detail_mask]
    detail_cv = cv_dist[detail_mask]
    keep_med, tau_cv_med = conditional_retain_detail(detail_cv, 0.5)
    detail_ii_kept, detail_jj_kept = detail_ii[keep_med], detail_jj[keep_med]
    detail_result = None
    detail_split_used = None
    if len(detail_ii_kept) >= 20:
        if split_mode == "pair":
            detail_unit_fold, detail_split_used = None, "对级(乐观上界)"
        else:
            detail_unit_fold, detail_split_used = build_unit_fold_with_fallback(
                orig_all, seg_ids, tau_hi, detail_ii_kept, detail_jj_kept)
            if split_mode == "group":
                group_ids = _burst_group_cache.setdefault(tau_hi, cluster_bursts(orig_all, tau_hi))
                group_size = {int(g): int(c) for g, c in zip(*np.unique(group_ids, return_counts=True))}
                detail_unit_fold = assign_unit_folds_balanced(group_ids, group_size, FOLDS, FOLD_SEED)
                detail_split_used = "连拍组级(指定)"
        detail_result = probe_stratum(views, detail_ii_kept, detail_jj_kept, ratings, detail_unit_fold, clf_kind)
        if verbose:
            print(f"  —— 细节层对照（条件保留后·全部，τ_cv@P50={tau_cv_med:.3f}，{detail_split_used} split）——")
            print(f"  {'分层':<14}" + "".join(f"{k:>13}" for k in view_names) + f"{'评估对数':>10}")
            print_stratum_row("全部", detail_result, view_names)
    elif verbose:
        print(f"  —— 细节层对照：条件保留后仅 {len(detail_ii_kept)} 对，样本过少，跳过 ——")

    return dict(
        gap=gap, window=window, tau_hi=tau_hi, tau_lo=tau_lo,
        comp_pairs=int(comp_mask.sum()), detail_pairs=int(detail_mask.sum()),
        cross_pairs=int(cross_mask.sum()), best_view=best_view, best_acc=best_acc,
        comp_neval=best_neval, comp_rows=comp_rows, detail_result=detail_result,
        split_used_comp=comp_split_used, split_used_detail=detail_split_used,
        view_names=view_names,
    )


def plot_tsne(samples: list[Sample], out_dir: Path) -> None:
    """原片 vs 增强 CLS 的 t-SNE 2D 目视图，按星级着色（近重复是否聚簇、跨题材是否分离）。"""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from sklearn.manifold import TSNE

    orig = np.stack([s.orig for s in samples])
    enh = np.stack([s.enh for s in samples])
    ratings = np.array([s.rating for s in samples])
    fig, axes = plt.subplots(1, 2, figsize=(16, 7))
    for ax, feat, title in ((axes[0], orig, "原片 CLS"), (axes[1], enh, "增强 CLS")):
        emb = TSNE(n_components=2, perplexity=30, init="pca", random_state=0).fit_transform(feat)
        sc = ax.scatter(emb[:, 0], emb[:, 1], c=ratings, cmap="viridis", s=8, alpha=0.7)
        ax.set_title(f"{title}  (t-SNE, n={len(samples)})")
        ax.set_xticks([]); ax.set_yticks([])
        fig.colorbar(sc, ax=ax, label="rating")
    out = out_dir / "tsne_orig_vs_enh.png"
    fig.tight_layout(); fig.savefig(out, dpi=120); plt.close(fig)
    print(f"\nt-SNE 图: {out}")


def write_markdown(out_dir: Path, gap_list: list[float], seg_reports: list[tuple],
                   primary: dict, all_combo_results: list[dict], n_samples: int,
                   n_missing_time: int) -> None:
    """把关键数字落一份 markdown 结论，路径 probe_out/probe_conclusion.md。"""
    lines = ["# DINOv3 特征可行性探针结论（段级 split 版）\n",
             f"样本 {n_samples} 组（capture_time 缺失/无法解析剔除 {n_missing_time} 组）。\n",
             "## 1. 拍摄段切分结构\n",
             "| gap 阈值(分) | 段数 | 有效段(≥2张) | 段大小均值 | 段大小最大 | 段时长均值(分) | 段时长最大(分) |",
             "|---|---|---|---|---|---|---|"]
    for gap, seg_ids, times in seg_reports:
        n_seg = int(seg_ids.max()) + 1
        sizes = np.bincount(seg_ids)
        durations = np.empty(n_seg)
        for s in range(n_seg):
            idx = np.where(seg_ids == s)[0]
            durations[s] = (times[idx[-1]] - times[idx[0]]).total_seconds() / 60.0
        valid = int((sizes >= 2).sum())
        lines.append(f"| {gap:.0f} | {n_seg} | {valid} | {sizes.mean():.1f} | {int(sizes.max())} | "
                     f"{durations.mean():.1f} | {durations.max():.1f} |")

    lines += ["", f"## 2. 主口径组合（gap={primary['gap']:.0f}min, window={primary['window']}, "
             f"τ_hi={primary['tau_hi']:.2f}, τ_lo={primary['tau_lo']:.2f}）\n",
             f"细节层对数 {primary['detail_pairs']} · 构图层对数 {primary['comp_pairs']} · "
             f"跨场景排除 {primary['cross_pairs']}\n",
             "### 构图层探针矩阵（段级 split，" + str(primary.get('split_used_comp')) + "）\n",
             "| 分层 | " + " | ".join(primary["view_names"]) + " | 评估对数 |",
             "|---|" + "---|" * (len(primary["view_names"]) + 1)]
    for label, results in (primary.get("comp_rows") or {}).items():
        if results is None:
            lines.append(f"| {label} | " + " | ".join(["样本过少"] * len(primary["view_names"])) + " | - |")
            continue
        cells = []
        neval = 0
        for name in primary["view_names"]:
            acc, sd, neval = results[name]
            cells.append("nan" if np.isnan(acc) else f"{acc*100:.1f}±{sd*100:.1f}")
        lines.append(f"| {label} | " + " | ".join(cells) + f" | {neval} |")

    lines += ["", "### 细节层对照（条件保留后·全部）\n"]
    if primary.get("detail_result"):
        lines.append("| 分层 | " + " | ".join(primary["view_names"]) + " | 评估对数 | split |")
        lines.append("|---|" + "---|" * (len(primary["view_names"]) + 2))
        cells = []
        neval = 0
        for name in primary["view_names"]:
            acc, sd, neval = primary["detail_result"][name]
            cells.append("nan" if np.isnan(acc) else f"{acc*100:.1f}±{sd*100:.1f}")
        lines.append(f"| 全部 | " + " | ".join(cells) + f" | {neval} | {primary.get('split_used_detail')} |")
    else:
        lines.append("（条件保留后对数不足，跳过）")

    lines += ["", "## 3. 全组合扫描摘要（构图层 Δ=1，各组合最佳制式）\n",
             "| gap | window | τ_hi | τ_lo | 最佳制式 | 准确率% | 评估对数 |",
             "|---|---|---|---|---|---|---|"]
    for r in all_combo_results:
        acc_str = "nan" if np.isnan(r["best_acc"]) or r["best_acc"] == float("-inf") else f"{r['best_acc']*100:.1f}"
        lines.append(f"| {r['gap']:.0f} | {r['window']} | {r['tau_hi']:.2f} | {r['tau_lo']:.2f} | "
                     f"{r['best_view'] or '-'} | {acc_str} | {r['comp_neval']} |")

    best_acc_pct = primary["best_acc"] * 100 if primary["best_view"] else float("nan")
    verdict = "≥80%，ViT-S 特征够用" if primary["best_view"] and primary["best_acc"] >= 0.8 else "未达 80%"
    lines += ["", "## 4. 一句话结论\n",
             f"主口径组合下，构图层 Δ=1 段级 split 最佳制式为 **{primary['best_view']}**，"
             f"准确率 **{best_acc_pct:.1f}%**（{primary['comp_neval']} 对）—— {verdict}。"]

    (out_dir / "probe_conclusion.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"\nmarkdown 结论: {out_dir / 'probe_conclusion.md'}")


def run_event_regime(samples: list[Sample], args) -> None:
    """高星全局段探针（用户 2026-07-19 标注学补充：3-5★ 是"0-3★ 局部最优选出后、事件内总体
    美学最优"——该段的合法配对域是**事件全集**（比较当年就是全局发生的），不是滑窗；split 用
    **事件级留出**，测绝对美学的跨事件迁移——M4 绝对涌现的缩微预演。配合 --min-rating 3 使用。
    宪法"禁跨段组对"约束的是 0-3★ 局部段，不约束本段。"""
    views = _views_for(samples)
    cls_views = {k: v for k, v in views.items() if k.startswith("CLS")}
    cls_names = list(cls_views.keys())
    ratings = np.array([s.rating for s in samples])
    events = np.array([s.event or "(无事件)" for s in samples])
    ev_list = sorted(set(events))
    ev_idx = np.array([ev_list.index(e) for e in events])

    # 事件内全配对（rating 不同的无序对；不做事前相似度分层——全局段对天然低相似）
    ii_l, jj_l = [], []
    for e in ev_list:
        idx = np.where(events == e)[0]
        for a in range(len(idx)):
            ra = ratings[idx[a]]
            for b in range(a + 1, len(idx)):
                if ra != ratings[idx[b]]:
                    ii_l.append(idx[a]); jj_l.append(idx[b])
    ii = np.array(ii_l); jj = np.array(jj_l)
    dr = np.abs(ratings[ii] - ratings[jj])
    print(f"\n■ 全局段探针（≥{args.min_rating}★ · 事件内全配对 · 事件级 split）—— "
          f"{len(samples)} 组 · {len(ev_list)} 事件 · 候选对 {len(ii)}")
    for e in ev_list:
        print(f"    {e}: {int((events == e).sum())} 组")

    unit_size = {int(u): int(c) for u, c in zip(*np.unique(ev_idx, return_counts=True))}
    ev_fold = assign_unit_folds_balanced(ev_idx, unit_size, FOLDS, FOLD_SEED)

    strata = {"全部": np.ones(len(ii), bool), "Δ=1": dr == 1, "Δ≥2": dr >= 2}
    print(f"  {'分层':<14}" + "".join(f"{k:>13}" for k in cls_names) + f"{'评估对数':>10}")
    for label, mask in strata.items():
        if mask.sum() < 20:
            print(f"  {label:<14}{'(样本过少)':>13}")
            continue
        results = probe_stratum(cls_views, ii[mask], jj[mask], ratings, ev_fold, args.clf)
        print_stratum_row(label, results, cls_names)
        if label == "Δ=1":
            d_ii, d_jj = ii[mask], jj[mask]
            r_lo = np.minimum(ratings[d_ii], ratings[d_jj])
            r_hi = np.maximum(ratings[d_ii], ratings[d_jj])
            print("  —— Δ=1 按边界拆解（事件级 split OOF 准确率%）——")
            bounds = sorted(set(zip(r_lo.tolist(), r_hi.tolist())))
            print(f"  {'制式':<12}" + "".join(f"{f'{lo}-{hi}':>13}" for lo, hi in bounds))
            print(f"  {'对数':<12}" + "".join(f"{int(((r_lo == lo) & (r_hi == hi)).sum()):>13}"
                                              for lo, hi in bounds))
            for name in cls_names:
                correct, ev_mask = probe_oof_correct(cls_views[name], d_ii, d_jj, ratings,
                                                     ev_fold, clf_kind=args.clf)
                cells = []
                for lo, hi in bounds:
                    m = ev_mask & (r_lo == lo) & (r_hi == hi)
                    cells.append(f"{correct[m].mean()*100:5.1f}(n={m.sum()})" if m.sum() >= 20 else "·")
                print(f"  {name:<12}" + "".join(f"{c:>13}" for c in cells))
            print("  —— 对级 split 对照（乐观上界）——")
            pr = probe_stratum(cls_views, d_ii, d_jj, ratings, None, args.clf)
            print_stratum_row("Δ=1 对级", pr, cls_names)
    print("\n判据：全局段（≥3★）Δ=1 事件级 split 最佳制式 ≥80% → 绝对美学腿在 ViT-S 立住；"
          "仍 chance → 全局段同样受特征瓶颈约束（决策 8 梯 2）")


def main() -> int:
    ap = argparse.ArgumentParser(description="DINOv3 增强制式线性探针 (Plan-3-1 §1.2，段级 split 版)")
    ap.add_argument("--db", default="D:/PhotoDB/dataset/photos_dataset.db")
    ap.add_argument("--model-id", default=MODEL_ID_ORIG)
    ap.add_argument("--enh-model", default=None,
                    help="增强 model_id；缺省则从库自动发现（非原片的那行）")
    ap.add_argument("--thresholds", default="0.55,0.65,0.75",
                    help="（仅 --group-report 用）逗号分隔的相似度门槛列表")
    ap.add_argument("--out", default="Training/probes/out")
    ap.add_argument("--fp-split", action="store_true",
                    help="[deprecated] 旧指纹级 split 开关，已被 --split 取代，保留仅为兼容旧调用不报错")
    ap.add_argument("--min-rating", type=int, default=0,
                    help="只保留 rating≥该值的样本（如 1 = 排除 0★，测纯质量段内细差）")
    ap.add_argument("--clf", choices=["linear", "mlp"], default="linear",
                    help="探针分类器：linear（默认）或 mlp（单隐层非线性头）")
    ap.add_argument("--group-report", action="store_true",
                    help="只聚连拍组并报告组结构（组数/大小/组内星级跨度），供核验聚类阈值")
    ap.add_argument("--with-patch", action="store_true",
                    help="额外读原片 patch token 做 mean / mean+std 池化探针（与 CLS 同框架对比）")
    ap.add_argument("--no-tsne", action="store_true", help="跳过 t-SNE（较慢）")
    ap.add_argument("--gap-mins", default="10,20,45", help="拍摄段切分 gap 阈值列表（分钟，逗号分隔）")
    ap.add_argument("--window", default="20,40", help="段内滑动窗口宽度列表（按张数，逗号分隔）")
    ap.add_argument("--tau-hi", default="0.95,0.98", help="细节层 cosine 下界列表（逗号分隔）")
    ap.add_argument("--tau-lo", default="0.83,0.88", help="构图层 cosine 下界列表（逗号分隔）")
    ap.add_argument("--split", choices=["seg", "group", "pair"], default="seg",
                    help="split 口径：seg=段级(主口径,不可用时自动回退组级) / group=连拍组级(强制) / "
                        "pair=对级(乐观上界对照)")
    ap.add_argument("--pair-scope", choices=["window", "event"], default="window",
                    help="配对域：window=段内滑窗（0-3★ 局部段，默认）/ event=事件内全配对（3-5★ 全局段，"
                        "配合 --min-rating 3，split 固定事件级留出；gap/window/τ 参数不生效）")
    args = ap.parse_args()

    if not Path(args.db).exists():
        print(f"[ERROR] 库不存在: {args.db}", file=sys.stderr)
        return 1
    if args.fp_split:
        print("[WARN] --fp-split 已弃用，本版按 --split（默认 seg）执行，此开关不再生效")

    model_enh = args.enh_model or discover_enhanced_model_id(args.db, args.model_id)
    print(f"原片 model_id = {args.model_id}\n增强 model_id = {model_enh}")
    samples, n_missing_time = load_samples(args.db, args.model_id, model_enh, with_patch=args.with_patch)
    if not samples:
        print("[ERROR] 未读到任何同时具备原片+增强 CLS 的样本", file=sys.stderr)
        return 1
    if args.min_rating > 0:
        n0 = len(samples)
        samples = [s for s in samples if s.rating >= args.min_rating]
        print(f"过滤 rating≥{args.min_rating}：{n0} → {len(samples)} 组（排除低星/0★）")

    if args.pair_scope == "event":
        run_event_regime(samples, args)
        return 0

    ratings = np.array([s.rating for s in samples])
    dist = ", ".join(f"{k}★:{int((ratings==k).sum())}" for k in range(6))
    print(f"样本 {len(samples)} 组 · 星级分布 [{dist}] · capture_time 缺失/解析失败剔除段切分统计 {n_missing_time} 组")

    if args.group_report:
        thresholds = [float(t) for t in args.thresholds.split(",")]
        report_groups(samples, thresholds)
        return 0

    out_dir = Path(args.out); out_dir.mkdir(parents=True, exist_ok=True)

    # capture_time 缺失的样本无法参与段切分，直接剔除（数量已在上面打印）
    samples_with_time = [s for s in samples if s.capture_time is not None]
    if len(samples_with_time) < len(samples):
        print(f"[WARN] {len(samples) - len(samples_with_time)} 组缺失 capture_time，已从段切分/配对流程剔除")
    samples_sorted = sorted(samples_with_time, key=lambda s: s.capture_time)
    times = [s.capture_time for s in samples_sorted]

    gap_list = [float(g) for g in args.gap_mins.split(",")]
    window_list = [int(w) for w in args.window.split(",")]
    tau_hi_list = [float(t) for t in args.tau_hi.split(",")]
    tau_lo_list = [float(t) for t in args.tau_lo.split(",")]
    quantiles = [0.4, 0.6]

    print("\n========== ① 拍摄段切分结构（各 gap 档） ==========")
    seg_reports = []
    seg_cache: dict[float, np.ndarray] = {}
    for g in gap_list:
        seg_ids = split_segments(times, g)
        seg_cache[g] = seg_ids
        seg_reports.append((g, seg_ids, times))
        report_segment_structure(seg_ids, times, g)

    print("\n========== ② 段内滑窗配对规模（各 gap × window） ==========")
    for g in gap_list:
        for w in window_list:
            wp = compute_window_pairs(samples_sorted, seg_cache[g], w)
            print(f"  gap={g:.0f}min window={w}: 原始窗口对 {wp['n_raw']} · rating不同(dr>0) {len(wp['ii'])}")

    print("\n========== ③④⑥ 三层对数分布 + 探针矩阵（主口径 = 各列表第一项，完整打印；"
         "其余组合仅列构图层 Δ=1 最佳制式，见 markdown 摘要） ==========")
    primary_gap, primary_window = gap_list[0], window_list[0]
    primary_tau_hi, primary_tau_lo = tau_hi_list[0], tau_lo_list[0]
    all_combo_results = []
    primary_result = None
    for g in gap_list:
        for w in window_list:
            for th in tau_hi_list:
                for tl in tau_lo_list:
                    is_primary = (g == primary_gap and w == primary_window
                                 and th == primary_tau_hi and tl == primary_tau_lo)
                    res = run_combo(samples_sorted, seg_cache[g], g, w, th, tl, quantiles,
                                    args.split, args.clf, verbose=is_primary)
                    all_combo_results.append(res)
                    if is_primary:
                        primary_result = res

    print("\n========== 全组合扫描摘要（构图层 Δ=1，最佳制式） ==========")
    print(f"  {'gap':>5}{'window':>8}{'τ_hi':>7}{'τ_lo':>7}{'最佳制式':>12}{'准确率%':>10}{'评估对数':>10}")
    for r in all_combo_results:
        acc_str = " nan" if np.isnan(r["best_acc"]) or r["best_acc"] == float("-inf") else f"{r['best_acc']*100:5.1f}"
        print(f"  {r['gap']:>5.0f}{r['window']:>8}{r['tau_hi']:>7.2f}{r['tau_lo']:>7.2f}"
              f"{(r['best_view'] or '-'):>12}{acc_str:>10}{r['comp_neval']:>10}")

    write_markdown(out_dir, gap_list, seg_reports, primary_result, all_combo_results,
                   len(samples), n_missing_time)

    if not args.no_tsne:
        plot_tsne(samples_sorted, out_dir)

    print("\n判据：主口径组合下构图层 Δ=1 段级 split 最佳制式准确率 ≥ 80% → ViT-S 够用；"
         "多视图 vs 仅原片是否显著更高 → 决定增强是否入模（plan §1.2 制式决策）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
