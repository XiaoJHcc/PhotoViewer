# 执行台账 Execution Log

> **Append-only**——新记录一律追加在文件末尾,不修改/删除历史条目。用途:记录每次训练相关实验的数据/前提条件/命令/结果/解读/下一步,跨会话防遗忘。`Training/probes/out/` 每次运行覆盖式写,历史结论靠本台账留档,不靠 `out/` 里的旧文件。

---

### 2026-07-18 · 特征可行性探针 · 批次 20240212(单批)

- **数据**:`D:\PhotoDB\20240212` → DatasetBuilder 提取入 `D:\PhotoDB\dataset\photos_dataset.db`,1361 指纹组;单日、雾天、纯风光(内容极同质、质量差极细、与场景高度纠缠)。
- **前提索引**:原片 model_id=`dinov3_vits16_f32_518_v1`(ViT-S/16@518,384d);增强 CLS 后缀 `+clhe2.0ycc1.0`;段级 split(整段留出,5 折);段内滑窗配对 + 三层 cosine 分层 + 细节层 CV 距离条件保留判 tie。脚本 `Training/probes/feature_probe.py`、`spatial_probe.py`。
- **命令**:`feature_probe.py --db D:/PhotoDB/dataset/photos_dataset.db --no-tsne`;`spatial_probe.py --db D:/PhotoDB/dataset/photos_dataset.db`。
- **结果**:构图层 Δ=1 段级 split 全 chance-or-below——CLS原片 40.8±3.0%、CLS增强 44.1±3.4%(最佳)、CLS多视图 41.3%;Δ≥2 也 44.8%(不是 Δ=1 噪声边界问题)。细节层对照 56.5%(如期靠 CV 不靠 DINO)。空间头四路(池化线性 50.2 / 池化MLP 49.5 / 空间金字塔 48.9 / 微型CNN 51.4%)全 chance、train acc 73-78%(拟合得动、迁移不了)→ H2 空间坍缩排除。pair 级 split 71-80% vs 段级 chance = 强段内(seen)/零跨段(unseen),plan-3-0 §0.6 挑战3 的实证形状(更 severe:连相对排序规则都不浅层跨段迁移)。
- **解读**:未解 confound = H3 单批同质(单一风光雾天批 = 跨段迁移 worst case),无法在此批分辨 H1(ViT-S 弱)vs H3。**不是特征失败,别盲升 ViT-L。**
- **下一步**:按 plan-3-1 §1.3 建**多样多题材子集**(DatasetBuilder 入库)→ 在 ViT-S 上复用同一探针框架**重探**。只有多样数据能分 H1/H3;多样批仍 chance 才轮到 ViT-L(plan-3-0 决策8 梯2)。别再在 20240212 单批上加实验(已探尽)。
