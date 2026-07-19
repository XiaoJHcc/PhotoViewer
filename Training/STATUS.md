# STATUS — 三期当前进度

> **进度真源：每次会话结束重写本文件（不追加）**。开工先读这里；实验证据链见 [EXECUTION-LOG.md](EXECUTION-LOG.md)（append-only）；计划见 [plans/](plans/)（plan-3-0 宪法 → plan-3-1 M1 详案）。
> 最近重写：2026-07-19

- **里程碑位置**：M1（[plan-3-1](plans/dinov3-photo-ranking-plan-3-1-data-foundation.md)）§1.2 → §1.3 之间。增强制式 / 特征可行性探针框架已建成并跑完首轮；首轮结论要求换多样多题材数据重探。
- **最近 GATE 状态**：§1.2 首轮探针（2026-07-18，批次 20240212 单批、1361 指纹组）——段级 split 全 chance（最佳 CLS增强 44.1±3.4%）；H2 空间坍缩已排除；H1（ViT-S 弱）vs H3（单批同质 worst case）未分辨。**非失败：confound 未解，不是特征判死**。
- **下一步（一件事）**：按 §1.3 用 DatasetBuilder 入**多样多题材子集**（必须含"靠影调 / 氛围区分"的对，否则增强 B 问题依旧测不出）→ 复用 feature_probe 重探。多样批仍 chance 才升 ViT-L（plan-3-0 决策 8 梯 2）。20240212 单批已探尽，不再加实验。
- **等待用户输入**：
  - 雾 / 低对比片正反例样本（plan-3-1 §1.2 探针样本）；
  - 精修成品存放位置盘点（§1.1，决定 `is_retouched` 与决策 3b 存亡）；
  - 后续外拍保留原始 dump（§1.1，废片闸门 + 金标准废片组唯一数据来源）。
- **已冻结 / 待校准参数**：
  - 增强 = CLHE `ClipFactor=2.0` / `SaturationScale=1.0`（YCbCr 恒定色度），后缀 `+clhe2.0ycc1.0` 冻结进 model_id 契约——改参 = 整库重提；
  - 入库 model_id = `dinov3_vits16_f32_518_v1`（ViT-S/16@518，384d）；
  - 探针主口径 gap=10min / window=20 / τ_hi=0.95 / τ_lo=0.83——仍是扫描默认值，正式校准是 §1.4 data_audit 的事。
- **并行长周期**：金标准集攒集（plan-3-1 §5，~50-100 组按七类场景；所在拍摄段必须入 test，攒集时随记段归属）。
- **数据现状**：`D:\PhotoDB\dataset\photos_dataset.db` 仅含 20240212 单批（原片 + 增强 CLS + patch + CV 四路齐）；多样多题材子集未入库。
