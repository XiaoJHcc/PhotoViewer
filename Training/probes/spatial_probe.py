"""
spatial_probe.py — DINOv3 空间感知头判别实验（Plan-3-1 §1.2 特征可行性后续，段级 split 版）

背景：feature_probe.py 已证明——在"构图层 Δ=1 段级 split"这批对上，池化 CLS 表征线性可分性只有
40-44%（CLS原片 40.8 / CLS增强 44.1 / CLS多视图 41.3，均 ≤ 随机）。本实验问：**同一批对、同一段级
split**，换成"保留空间结构"的头，能不能显著超过池化基线、逼近 80%？

- 能 → 信号在 ViT-S 里只是被均值池化坍缩掉了（H2 空间坍缩假说成立）。
- 不能 → 信号本身就弱，是容量/数据问题（H1/H3），换头没用。

方法论（与 feature_probe.py 苹果对苹果，全部直接 import 复用，不改一行配对/split 逻辑）：
    1. import feature_probe，复用其切拍摄段 / 段内滑窗配对 / 三层 cosine 分层 / 段级 fold 分配。
    2. 取主口径组合（gap=10min, window=20, τ_hi=0.95, τ_lo=0.83）——feature_probe 参数列表的
       第一项，与其 verbose 主报告完全一致——得到构图层 Δ=1 对集合与段级 fold 分配。
    3. 在这同一批对 + 同一 split 上跑四个头：
       A 池化-线性（patch 均值池化 384d，复用 feature_probe 自己的"patch均值"视图）
       B 池化-MLP（patch 均值+std 池化 768d，同上"patch均+std"视图，换 MLP 分类器）
       C 空间金字塔-线性（32×32 网格按 2×2 / 4×4 粗粒度分块均值池化拼接，强 L2 逻辑回归）
       D 微型卷积头（torch，1×1→3×3 stride2→3×3→GAP→linear，真正的空间感知测试）
    4. 每个头都报三个数：段级测试 acc（结论）、train acc（拟合能力）、pair 级泄漏 split acc
       （乐观上界，供解读用）。

用法：
    Training/.venv/Scripts/python.exe Training/probes/spatial_probe.py --db D:/PhotoDB/dataset/photos_dataset.db
"""
from __future__ import annotations

import argparse
import sqlite3
import sys
from pathlib import Path

import numpy as np

import feature_probe as fprobe

# 主口径组合：与 feature_probe.py 参数列表默认值的第一项完全一致（其 verbose 主报告用的就是这组）。
GAP_MINUTES = 10.0
WINDOW = 20
TAU_HI = 0.95
TAU_LO = 0.83

PATCH_GRID = 32  # DINO patch 网格边长（32×32=1024），见 PhotoViewer/Core/AI/DinoModelResources.PatchGrid


# ---------------------------------------------------------------------------
# 原始 patch 网格加载（按需，只读参与 Δ=1 构图层对的那部分指纹，不整批常驻 ~2GB）
# ---------------------------------------------------------------------------

def load_raw_patch_grids(db_path: str, model_orig: str, fingerprints: list[str]) -> dict[str, np.ndarray]:
    """按指纹子集读取原片 patch token 原始张量，还原成 (32,32,384) 空间网格。
    还原顺序与 PhotoViewer/Core/AI/PatchHeatmap.cs 的 ComputePcaRgb 对齐：patch token 本身按
    token-major 存储、且 token 序号 i 本身即对应行主序空间位置 (y=i//32, x=i%32)（PatchHeatmap.cs
    直接用 token 序号 i 当输出像素序号，无额外置换），故 numpy 对 (1024,384) 做 C-order
    reshape(32,32,384) 即得到正确的 (y,x,channel) 网格，无需手工换算下标。
    只按需读取传入的指纹集合（本实验只需 Δ=1 构图层对涉及的几百张），避免像 feature_probe 的
    mean/std 池化路径那样需要整批 ~2GB 常驻。
    """
    if not fingerprints:
        return {}
    conn = sqlite3.connect(db_path)
    out: dict[str, np.ndarray] = {}
    try:
        placeholders = ",".join("?" for _ in fingerprints)
        query = (f"SELECT fingerprint, patch_tokens FROM photo_patches "
                 f"WHERE model_id = ? AND fingerprint IN ({placeholders})")
        for fp_id, blob in conn.execute(query, (model_orig, *fingerprints)):
            t = np.frombuffer(blob, dtype="<f4")
            if t.size != fprobe.PATCH_TOKENS * fprobe.FEATURE_DIM:
                continue
            out[fp_id] = t.reshape(PATCH_GRID, PATCH_GRID, fprobe.FEATURE_DIM).astype(np.float32)
    finally:
        conn.close()
    return out


# ---------------------------------------------------------------------------
# Head C：空间金字塔（2×2 / 4×4 粗粒度分块均值池化，torch-free）
# ---------------------------------------------------------------------------

def _pyramid_level_pooled(grid: np.ndarray, level: int) -> np.ndarray:
    """把 (32,32,384) 网格切成 level×level 个粗粒度块，每块内 token 均值池化后 L2 归一化，
    按行主序展平拼接成 (level*level*384,) 向量（块间保留粗空间位置，块内归一化保证跨块量纲一致）。
    """
    h, w, d = grid.shape
    assert h % level == 0 and w % level == 0, f"网格边长 {h} 必须能被 level={level} 整除"
    step = h // level
    cells = []
    for a in range(level):
        for b in range(level):
            block = grid[a * step:(a + 1) * step, b * step:(b + 1) * step, :]
            m = block.reshape(-1, d).mean(axis=0)
            cells.append(fprobe.l2_normalize(m))
    return np.concatenate(cells)


def spatial_pyramid_feature(grid: np.ndarray) -> np.ndarray:
    """拼接 2×2 层（4 块×384=1536）与 4×4 层（16 块×384=6144）两级金字塔，共 7680 维。"""
    return np.concatenate([_pyramid_level_pooled(grid, 2), _pyramid_level_pooled(grid, 4)])


# ---------------------------------------------------------------------------
# 折划分（段级 / pair 级），与 feature_probe.probe_accuracy 内部逻辑对齐但额外暴露 train 侧
# ---------------------------------------------------------------------------

def unit_level_fold_masks(unit_fold: np.ndarray, ii: np.ndarray, jj: np.ndarray,
                          folds: int) -> list[tuple[np.ndarray, np.ndarray]]:
    """段级/组级 split：按样本级折号 unit_fold，只保留两端同折的对，逐折留一出。
    返回每折 (train_mask, test_mask)，均是对 ii/jj（长度 M 的对位置数组）的布尔掩码——
    与 feature_probe.probe_accuracy 的 unit_fold 分支完全一致的划分规则。
    """
    same = unit_fold[ii] == unit_fold[jj]
    pf = unit_fold[ii]
    return [(same & (pf != f), same & (pf == f)) for f in range(folds)]


def pair_level_fold_masks(label: np.ndarray, folds: int, seed: int) -> list[tuple[np.ndarray, np.ndarray]]:
    """对级 split（乐观上界对照）：忽略段/组归属，直接对全部对做 StratifiedKFold。
    与 feature_probe.probe_accuracy 的 unit_fold=None 分支等价的划分规则。
    """
    from sklearn.model_selection import StratifiedKFold
    skf = StratifiedKFold(n_splits=folds, shuffle=True, random_state=seed)
    idx = np.arange(len(label))
    out = []
    for tr, te in skf.split(idx, label):
        trm = np.zeros(len(label), dtype=bool); trm[tr] = True
        tem = np.zeros(len(label), dtype=bool); tem[te] = True
        out.append((trm, tem))
    return out


# ---------------------------------------------------------------------------
# Head A/B/C 共用：向量特征 + sklearn 分类器的 pairwise 探针（对称化 + 折均值/std + train acc）
# ---------------------------------------------------------------------------

def _make_vector_clf(kind: str, **kwargs):
    """构造向量探针分类器；kwargs 覆盖默认超参（Head C 用它调高 L2 强度）。
    linear 默认对齐 feature_probe._make_clf('linear')：fit_intercept=False, C=1.0；
    mlp 默认对齐 feature_probe._make_clf('mlp')：单隐层 64、alpha=1e-2、早停。
    """
    if kind == "mlp":
        from sklearn.neural_network import MLPClassifier
        params = dict(hidden_layer_sizes=(64,), alpha=1e-2, max_iter=200,
                     early_stopping=True, n_iter_no_change=8, random_state=0)
        params.update(kwargs)
        return MLPClassifier(**params)
    from sklearn.linear_model import LogisticRegression
    params = dict(fit_intercept=False, C=1.0, max_iter=2000)
    params.update(kwargs)
    return LogisticRegression(**params)


def eval_vector_head(feat: np.ndarray, ii: np.ndarray, jj: np.ndarray, label: np.ndarray,
                     folds_masks: list[tuple[np.ndarray, np.ndarray]],
                     clf_kind: str = "linear", clf_kwargs: dict | None = None) -> tuple[float, float, float]:
    """给定折划分，对特征差 feat[ii]-feat[jj] 做对称化 pairwise 探针（±diff/±label），
    逐折算测试 acc 与 train acc，跳过任一侧样本 <10 的折（与 feature_probe 阈值一致）。
    返回 (测试acc均值, 测试acc标准差, train acc均值)；无可用折时三者均为 nan。
    """
    diff_all = feat[ii] - feat[jj]
    test_accs, train_accs = [], []
    for tr_mask, te_mask in folds_masks:
        if te_mask.sum() < 10 or tr_mask.sum() < 10:
            continue
        Xtr = np.concatenate([diff_all[tr_mask], -diff_all[tr_mask]])
        ytr = np.concatenate([label[tr_mask], -label[tr_mask]])
        Xte = np.concatenate([diff_all[te_mask], -diff_all[te_mask]])
        yte = np.concatenate([label[te_mask], -label[te_mask]])
        clf = _make_vector_clf(clf_kind, **(clf_kwargs or {})).fit(Xtr, ytr)
        test_accs.append(clf.score(Xte, yte))
        train_accs.append(clf.score(Xtr, ytr))
    if not test_accs:
        return float("nan"), float("nan"), float("nan")
    return float(np.mean(test_accs)), float(np.std(test_accs)), float(np.mean(train_accs))


# ---------------------------------------------------------------------------
# Head D：微型卷积头（torch）
# ---------------------------------------------------------------------------

def build_tiny_conv_head(torch_mod, nn_mod, hidden: int = 8, dropout: float = 0.3):
    """构造微型卷积头：1×1 conv(384→hidden,ReLU) → 3×3 stride2(hidden→hidden,ReLU) →
    3×3(hidden→hidden,ReLU) → 全局均值池化 → Dropout → Linear(hidden→1) 输出标量分。
    hidden=8 时参数量约 4.3k（“几千”量级），在 ~600 对小样本上限制容量防止硬记忆。
    用 torch_mod/nn_mod 传入而非在模块顶层 import torch，使得 torch 不可用时其余头仍可正常跑。
    """
    class TinyConvHead(nn_mod.Module):
        """微型空间卷积头本体，见外层 build_tiny_conv_head 的 docstring 说明结构与设计意图。"""

        def __init__(self):
            """构造四层：1×1 conv → 3×3 stride2 conv → 3×3 conv → dropout → 全连接输出层。"""
            super().__init__()
            self.conv1 = nn_mod.Conv2d(fprobe.FEATURE_DIM, hidden, kernel_size=1)
            self.conv2 = nn_mod.Conv2d(hidden, hidden, kernel_size=3, stride=2, padding=1)
            self.conv3 = nn_mod.Conv2d(hidden, hidden, kernel_size=3, stride=1, padding=1)
            self.drop = nn_mod.Dropout(dropout)
            self.fc = nn_mod.Linear(hidden, 1)

        def forward(self, x):
            """前向：输入 (B,384,32,32) 标准化后的 patch 网格，输出 (B,) 标量分。"""
            x = torch_mod.relu(self.conv1(x))
            x = torch_mod.relu(self.conv2(x))
            x = torch_mod.relu(self.conv3(x))
            x = torch_mod.nn.functional.adaptive_avg_pool2d(x, 1).flatten(1)
            x = self.drop(x)
            return self.fc(x).squeeze(-1)

    return TinyConvHead()


def _cnn_pair_accuracy(model, torch_mod, grids_norm, ii_c: np.ndarray, jj_c: np.ndarray,
                       label: np.ndarray) -> float:
    """对称化（同时评估 (i,j,label) 与 (j,i,-label)）算一批对的 pairwise 符号预测准确率。"""
    model.eval()
    with torch_mod.no_grad():
        s = model(grids_norm)
        ii_t = torch_mod.as_tensor(ii_c, dtype=torch_mod.long)
        jj_t = torch_mod.as_tensor(jj_c, dtype=torch_mod.long)
        margin = s[ii_t] - s[jj_t]
        margin = torch_mod.cat([margin, -margin])
        y = torch_mod.as_tensor(np.concatenate([label, -label]), dtype=torch_mod.float32)
        pred = (margin > 0).float() * 2 - 1
        return float((pred == y).float().mean().item())


def train_cnn_fold(torch_mod, nn_mod, compact_grids_t, ii_c: np.ndarray, jj_c: np.ndarray, label: np.ndarray,
                   train_mask: np.ndarray, val_mask: np.ndarray, test_mask: np.ndarray,
                   epochs: int = 200, patience: int = 25, lr: float = 2e-3,
                   weight_decay: float = 1e-2, seed: int = 0) -> tuple[float, float]:
    """单折训练：inner-train 拟合、val（同折内切出、绝不含 test）早停、test 折出准确率。
    BT/pairwise logistic loss：margin = score_i - score_j，target = (label==1)，
    BCEWithLogits 对称化增广（(i,j,label) 与 (j,i,-label) 都进训练，消除方向偏置）。
    输入网格按 inner-train 折图像的 per-channel 均值/标准差标准化，绝不用 val/test 统计。
    返回 (test_acc, train_acc)（train_acc 用 inner-train 对，best-val checkpoint 处的值）。
    """
    torch_mod.manual_seed(seed)
    train_ii, train_jj, train_lb = ii_c[train_mask], jj_c[train_mask], label[train_mask]
    val_ii, val_jj, val_lb = ii_c[val_mask], jj_c[val_mask], label[val_mask]
    test_ii, test_jj, test_lb = ii_c[test_mask], jj_c[test_mask], label[test_mask]

    train_img_idx = np.unique(np.concatenate([train_ii, train_jj]))
    mean = compact_grids_t[train_img_idx].mean(dim=(0, 2, 3), keepdim=True)
    std = compact_grids_t[train_img_idx].std(dim=(0, 2, 3), keepdim=True).clamp_min(1e-6)
    grids_norm = (compact_grids_t - mean) / std

    model = build_tiny_conv_head(torch_mod, nn_mod)
    opt = torch_mod.optim.AdamW(model.parameters(), lr=lr, weight_decay=weight_decay)

    # 对称增广训练集：(i,j,label) + (j,i,-label)
    tr_ii = np.concatenate([train_ii, train_jj])
    tr_jj = np.concatenate([train_jj, train_ii])
    tr_lb = np.concatenate([train_lb, -train_lb])
    tr_ii_t = torch_mod.as_tensor(tr_ii, dtype=torch_mod.long)
    tr_jj_t = torch_mod.as_tensor(tr_jj, dtype=torch_mod.long)
    tr_target = torch_mod.as_tensor((tr_lb == 1).astype(np.float32))

    best_val_loss = float("inf")
    best_state = None
    bad_epochs = 0
    loss_fn = nn_mod.BCEWithLogitsLoss()

    for epoch in range(epochs):
        model.train()
        opt.zero_grad()
        s = model(grids_norm)
        margin = s[tr_ii_t] - s[tr_jj_t]
        loss = loss_fn(margin, tr_target)
        loss.backward()
        opt.step()

        model.eval()
        with torch_mod.no_grad():
            s_eval = model(grids_norm)
            v_ii_t = torch_mod.as_tensor(val_ii, dtype=torch_mod.long)
            v_jj_t = torch_mod.as_tensor(val_jj, dtype=torch_mod.long)
            v_margin = s_eval[v_ii_t] - s_eval[v_jj_t]
            v_target = torch_mod.as_tensor((val_lb == 1).astype(np.float32))
            val_loss = float(loss_fn(v_margin, v_target).item())
        if val_loss < best_val_loss - 1e-5:
            best_val_loss = val_loss
            best_state = {k: v.clone() for k, v in model.state_dict().items()}
            bad_epochs = 0
        else:
            bad_epochs += 1
            if bad_epochs >= patience:
                break

    if best_state is not None:
        model.load_state_dict(best_state)

    train_acc = _cnn_pair_accuracy(model, torch_mod, grids_norm, train_ii, train_jj, train_lb)
    test_acc = _cnn_pair_accuracy(model, torch_mod, grids_norm, test_ii, test_jj, test_lb)
    return test_acc, train_acc


def run_head_d_segment(torch_mod, nn_mod, compact_grids_t, ii_c, jj_c, label,
                       unit_fold: np.ndarray, ii: np.ndarray, jj: np.ndarray,
                       folds: int, seed: int) -> tuple[float, float, float]:
    """Head D 段级 split：外层留一折出，val 折 = (test折+1)%folds（同样属于"非 test"的段，
    不碰 test 数据），其余折做 inner-train。返回 (测试acc均值, 测试acc标准差, train acc均值)。
    """
    same = unit_fold[ii] == unit_fold[jj]
    pf = unit_fold[ii]
    test_accs, train_accs = [], []
    for f in range(folds):
        val_f = (f + 1) % folds
        test_mask = same & (pf == f)
        val_mask = same & (pf == val_f)
        train_mask = same & (pf != f) & (pf != val_f)
        if test_mask.sum() < 10 or train_mask.sum() < 10 or val_mask.sum() < 5:
            continue
        test_acc, train_acc = train_cnn_fold(torch_mod, nn_mod, compact_grids_t, ii_c, jj_c, label,
                                             train_mask, val_mask, test_mask, seed=seed)
        test_accs.append(test_acc)
        train_accs.append(train_acc)
    if not test_accs:
        return float("nan"), float("nan"), float("nan")
    return float(np.mean(test_accs)), float(np.std(test_accs)), float(np.mean(train_accs))


def run_head_d_pairlevel(torch_mod, nn_mod, compact_grids_t, ii_c, jj_c, label,
                         folds: int, seed: int) -> float:
    """Head D 对级泄漏 split（乐观上界对照）：StratifiedKFold 忽略段归属，
    train 内再切 20% 做早停 val（随机、非按段，纯粹为了这个乐观对照有个一致的早停机制）。
    返回测试 acc 均值。
    """
    from sklearn.model_selection import StratifiedKFold, train_test_split
    skf = StratifiedKFold(n_splits=folds, shuffle=True, random_state=seed)
    idx = np.arange(len(label))
    test_accs = []
    for tr_idx, te_idx in skf.split(idx, label):
        if len(te_idx) < 10 or len(tr_idx) < 10:
            continue
        inner_tr_idx, val_idx = train_test_split(tr_idx, test_size=0.2, random_state=seed,
                                                 stratify=label[tr_idx])
        train_mask = np.zeros(len(label), dtype=bool); train_mask[inner_tr_idx] = True
        val_mask = np.zeros(len(label), dtype=bool); val_mask[val_idx] = True
        test_mask = np.zeros(len(label), dtype=bool); test_mask[te_idx] = True
        test_acc, _ = train_cnn_fold(torch_mod, nn_mod, compact_grids_t, ii_c, jj_c, label,
                                     train_mask, val_mask, test_mask, seed=seed)
        test_accs.append(test_acc)
    if not test_accs:
        return float("nan")
    return float(np.mean(test_accs))


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def main() -> int:
    """整体流程：复用 feature_probe 的段切分/滑窗配对/三层分层/段级 fold 分配拿到主口径组合下
    构图层 Δ=1 对集合，然后跑 A/B/C/D 四个头，打印对照表并写 markdown 结论。
    """
    ap = argparse.ArgumentParser(description="DINOv3 空间感知头判别实验（spatial_probe）")
    ap.add_argument("--db", default="D:/PhotoDB/dataset/photos_dataset.db")
    ap.add_argument("--out", default="Training/probes/out")
    args = ap.parse_args()

    if not Path(args.db).exists():
        print(f"[ERROR] 库不存在: {args.db}", file=sys.stderr)
        return 1
    out_dir = Path(args.out); out_dir.mkdir(parents=True, exist_ok=True)

    model_orig = fprobe.MODEL_ID_ORIG
    model_enh = fprobe.discover_enhanced_model_id(args.db, model_orig)
    print(f"原片 model_id = {model_orig}\n增强 model_id = {model_enh}")

    samples, n_missing_time = fprobe.load_samples(args.db, model_orig, model_enh, with_patch=True)
    if not samples:
        print("[ERROR] 未读到任何同时具备原片+增强 CLS+patch 的样本", file=sys.stderr)
        return 1
    samples_with_time = [s for s in samples if s.capture_time is not None]
    samples_sorted = sorted(samples_with_time, key=lambda s: s.capture_time)
    times = [s.capture_time for s in samples_sorted]
    print(f"样本 {len(samples)} 组（capture_time 缺失剔除 {n_missing_time} 组）· "
         f"参与段切分/配对 {len(samples_sorted)} 组")

    ratings = np.array([s.rating for s in samples_sorted])
    orig_all = np.stack([s.orig for s in samples_sorted])
    views = fprobe._views_for(samples_sorted)

    # ---- 复用 feature_probe：段切分 → 段内滑窗配对 → 三层分层 → 段级 fold 分配 ----
    seg_ids = fprobe.split_segments(times, GAP_MINUTES)
    n_seg = int(seg_ids.max()) + 1
    wp = fprobe.compute_window_pairs(samples_sorted, seg_ids, WINDOW)
    ii_all, jj_all, cos, dr = wp["ii"], wp["jj"], wp["cos"], wp["dr"]
    detail_mask, comp_mask, cross_mask = fprobe.three_tier_masks(cos, TAU_HI, TAU_LO)
    comp_ii, comp_jj, comp_dr = ii_all[comp_mask], jj_all[comp_mask], dr[comp_mask]
    print(f"gap={GAP_MINUTES:.0f}min window={WINDOW} τ_hi={TAU_HI} τ_lo={TAU_LO} —— "
         f"{n_seg} 段 · 候选对(dr>0) {len(ii_all)} · 构图层(全部Δ) {len(comp_ii)}")

    # 段级 fold 分配：与 feature_probe.run_combo 完全一致的调用方式——用"全部构图层对"
    # （非 Δ=1 子集）判定可用性、分配折号，Δ=1 只是取其子集复用同一份 unit_fold。
    unit_fold, split_used = fprobe.build_unit_fold_with_fallback(orig_all, seg_ids, TAU_HI, comp_ii, comp_jj)
    print(f"段级 fold 分配口径：{split_used}（folds={fprobe.FOLDS}, seed={fprobe.FOLD_SEED}）")

    d1_mask = comp_dr == 1
    ii, jj = comp_ii[d1_mask], comp_jj[d1_mask]
    label = np.where(ratings[ii] > ratings[jj], 1, -1)
    n_pairs = len(ii)
    print(f"构图层 Δ=1 对数 = {n_pairs}（应与 feature_probe.py 报的 764 一致）")

    # ---- 锚点核对：用同一套折划分复现 feature_probe 自己的 CLS原片 / patch均值 两个数字 ----
    seg_folds_masks = unit_level_fold_masks(unit_fold, ii, jj, fprobe.FOLDS)
    anchor_cls_test, anchor_cls_sd, anchor_cls_train = eval_vector_head(
        views["CLS原片"], ii, jj, label, seg_folds_masks, clf_kind="linear")
    anchor_patchmean_test, anchor_patchmean_sd, anchor_patchmean_train = eval_vector_head(
        views["patch均值"], ii, jj, label, seg_folds_masks, clf_kind="linear")
    print("\n■ 锚点核对（同一批 764 对 + 同一段级 split，复现 feature_probe 主口径报告数字）")
    print(f"  CLS原片  重算 = {anchor_cls_test*100:.1f}±{anchor_cls_sd*100:.1f}%"
         f"  vs  feature_probe 报告 40.8±3.0%")
    print(f"  patch均值 重算 = {anchor_patchmean_test*100:.1f}±{anchor_patchmean_sd*100:.1f}%"
         f"  vs  feature_probe 报告 50.2±6.7%（Head A 就是这个视图，见下）")

    # ---- 原始 patch 空间网格：只按需加载 Δ=1 对涉及的指纹 ----
    needed_idx = np.unique(np.concatenate([ii, jj]))
    needed_fps = [samples_sorted[i].fingerprint for i in needed_idx]
    raw_map = load_raw_patch_grids(args.db, model_orig, needed_fps)
    missing = [f for f in needed_fps if f not in raw_map]
    if missing:
        print(f"[ERROR] {len(missing)} 个指纹缺 patch_tokens 原始网格（不应发生，with_patch=True 已过滤）",
             file=sys.stderr)
        return 1
    compact_grids = np.stack([raw_map[samples_sorted[i].fingerprint] for i in needed_idx])  # (U,32,32,384)
    idx_map = {int(orig): pos for pos, orig in enumerate(needed_idx)}
    ii_c = np.array([idx_map[int(i)] for i in ii])
    jj_c = np.array([idx_map[int(j)] for j in jj])
    print(f"原始空间网格：{len(needed_idx)} 张唯一图像，{compact_grids.nbytes/1e6:.0f} MB")

    pair_folds_masks = pair_level_fold_masks(label, fprobe.FOLDS, fprobe.FOLD_SEED)

    results = {}

    # ---- Head A：池化-线性（patch 均值 384d）----
    test_a, sd_a, train_a = eval_vector_head(views["patch均值"], ii, jj, label, seg_folds_masks, "linear")
    leak_a, _, _ = eval_vector_head(views["patch均值"], ii, jj, label, pair_folds_masks, "linear")
    results["A 池化-线性(patch均值)"] = (test_a, sd_a, train_a, leak_a)

    # ---- Head B：池化-MLP（patch 均值+std 768d）----
    test_b, sd_b, train_b = eval_vector_head(views["patch均+std"], ii, jj, label, seg_folds_masks, "mlp")
    leak_b, _, _ = eval_vector_head(views["patch均+std"], ii, jj, label, pair_folds_masks, "mlp")
    results["B 池化-MLP(patch均+std)"] = (test_b, sd_b, train_b, leak_b)

    # ---- Head C：空间金字塔-线性（2×2+4×4=7680d，强 L2）----
    C_STRENGTH = 0.02  # 强正则：7680 维 vs 每折 train 仅数百对，弱正则会硬记忆
    feat_pyr = np.stack([spatial_pyramid_feature(g) for g in compact_grids])  # (U,7680)
    test_c, sd_c, train_c = eval_vector_head(feat_pyr, ii_c, jj_c, label, seg_folds_masks,
                                             "linear", clf_kwargs=dict(C=C_STRENGTH, max_iter=5000))
    leak_c, _, _ = eval_vector_head(feat_pyr, ii_c, jj_c, label, pair_folds_masks,
                                    "linear", clf_kwargs=dict(C=C_STRENGTH, max_iter=5000))
    results["C 空间金字塔-线性"] = (test_c, sd_c, train_c, leak_c)

    # ---- Head D：微型卷积头（torch）----
    torch_ok = True
    torch_err = None
    try:
        import torch
        import torch.nn as nn
    except ImportError as e:
        torch_ok = False
        torch_err = str(e)

    if torch_ok:
        compact_grids_t = torch.from_numpy(compact_grids.transpose(0, 3, 1, 2)).float()  # (U,384,32,32)
        test_d, sd_d, train_d = run_head_d_segment(torch, nn, compact_grids_t, ii_c, jj_c, label,
                                                    unit_fold, ii, jj, fprobe.FOLDS, fprobe.FOLD_SEED)
        leak_d = run_head_d_pairlevel(torch, nn, compact_grids_t, ii_c, jj_c, label,
                                      fprobe.FOLDS, fprobe.FOLD_SEED)
        results["D 微型卷积头"] = (test_d, sd_d, train_d, leak_d)
    else:
        print(f"\n[WARN] torch 不可用（{torch_err}），跳过 Head D，把 Head C 当空间测试主结果")
        results["D 微型卷积头"] = (float("nan"), float("nan"), float("nan"), float("nan"))

    # ---- 打印对照表 ----
    print("\n■ 四头对照表（同一批 764 对构图层 Δ=1、同一段级 split）")
    print(f"  {'头':<26}{'段级测试acc±std':>18}{'train acc':>12}{'pair级泄漏acc':>14}{'参与对数':>10}")
    for name, (test_acc, sd, train_acc, leak_acc) in results.items():
        test_str = "nan" if np.isnan(test_acc) else f"{test_acc*100:.1f}±{sd*100:.1f}%"
        train_str = "nan" if np.isnan(train_acc) else f"{train_acc*100:.1f}%"
        leak_str = "nan" if np.isnan(leak_acc) else f"{leak_acc*100:.1f}%"
        print(f"  {name:<26}{test_str:>18}{train_str:>12}{leak_str:>14}{n_pairs:>10}")

    best_name = max((n for n in results if not np.isnan(results[n][0])), key=lambda n: results[n][0],
                    default=None)
    pooled_baseline = max(test_a, anchor_cls_test)
    if best_name is not None:
        best_test = results[best_name][0]
        verdict = ("逼近/达到 80% → 支持 H2（空间坍缩：池化杀信号，换空间头能救回来）"
                  if best_test >= 0.75 else
                  "仍远低于 80%、未显著超过池化基线" if best_test < pooled_baseline + 0.05 else
                  "显著超过池化基线但未到 80% → 部分支持 H2，信号存在但弱")
        print(f"\n一句话结论：最佳空间头 = {best_name}，段级测试 acc = {best_test*100:.1f}%，"
             f"相对池化基线（Head A {test_a*100:.1f}% / CLS原片 {anchor_cls_test*100:.1f}%）"
             f"{'提升' if best_test > pooled_baseline else '未提升'} "
             f"{abs(best_test-pooled_baseline)*100:.1f} 个百分点 —— {verdict}")
    else:
        best_test = float("nan")
        verdict = "所有头均无可用折，无法下结论"
        print(f"\n一句话结论：{verdict}")

    # ---- 写 markdown ----
    lines = ["# 空间感知头判别实验结论（spatial_probe）\n",
             f"主口径组合：gap={GAP_MINUTES:.0f}min, window={WINDOW}, τ_hi={TAU_HI}, τ_lo={TAU_LO} "
             f"（与 feature_probe.py 参数列表首项一致）\n",
             f"样本 {len(samples_sorted)} 组 · {n_seg} 段 · 段级 fold 分配口径：**{split_used}** "
             f"（folds={fprobe.FOLDS}）\n",
             f"构图层 Δ=1 对数 = **{n_pairs}**（与 feature_probe.py 报告的 764 一致）\n",
             "## 锚点核对（同一批对 + 同一 split，复现 feature_probe 主口径报告数字）\n",
             f"- CLS原片重算 = {anchor_cls_test*100:.1f}±{anchor_cls_sd*100:.1f}% "
             f"vs feature_probe 报告 40.8±3.0%\n",
             f"- patch均值重算 = {anchor_patchmean_test*100:.1f}±{anchor_patchmean_sd*100:.1f}% "
             f"vs feature_probe 报告 50.2±6.7%（Head A 即此视图）\n",
             "## 四头对照表\n",
             "| 头 | 段级测试acc±std | train acc | pair级泄漏acc | 参与对数 |",
             "|---|---|---|---|---|"]
    for name, (test_acc, sd, train_acc, leak_acc) in results.items():
        test_str = "nan" if np.isnan(test_acc) else f"{test_acc*100:.1f}±{sd*100:.1f}%"
        train_str = "nan" if np.isnan(train_acc) else f"{train_acc*100:.1f}%"
        leak_str = "nan" if np.isnan(leak_acc) else f"{leak_acc*100:.1f}%"
        lines.append(f"| {name} | {test_str} | {train_str} | {leak_str} | {n_pairs} |")
    if not torch_ok:
        lines.append(f"\n（torch 不可用：{torch_err}，Head D 未跑，Head C 为空间测试主结果）")
    lines += ["", "## 一句话结论\n",
             f"最佳空间头 = **{best_name}**，段级测试 acc = **{best_test*100:.1f}%**"
             if best_name else "所有头均无可用折"]
    if best_name:
        lines[-1] += (f"，相对池化基线（Head A {test_a*100:.1f}% / CLS原片 {anchor_cls_test*100:.1f}%）"
                     f"{'提升' if best_test > pooled_baseline else '未提升'} "
                     f"{abs(best_test-pooled_baseline)*100:.1f} 个百分点 —— {verdict}。")
    (out_dir / "spatial_probe_conclusion.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"\nmarkdown 结论: {out_dir / 'spatial_probe_conclusion.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
