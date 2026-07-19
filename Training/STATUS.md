# STATUS — 三期当前进度

> **进度真源：每次会话结束重写本文件（不追加）**。开工先读这里；实验证据链见 [EXECUTION-LOG.md](EXECUTION-LOG.md)（append-only）；计划见 [plans/](plans/)（plan-3-0 宪法 → plan-3-1 M1 详案）；入库批次台账见 [data/BATCHES.md](data/BATCHES.md)。
> 最近重写：2026-07-19 晚（梯 2 完成 + 0★ 裁定落库后）

- **里程碑位置**：M1（[plan-3-1](plans/dinov3-photo-ranking-plan-3-1-data-foundation.md)）§1.2 探针系列 + 决策 8 **梯 2（ViT-L 重提复测）完成**。2×2 判读：**特征非决定性瓶颈 → M2 锚定路线确立为主路**；**backbone 定格（ViT-S vs ViT-L）待用户拍板**。
- **最近 GATE 状态**：梯 2 增提双 GATE PASS（8057 + 1361 组、零失败，全库 9418 组 ViT-L 双路 CLS 齐）；ONNX parity PASS（100 样本 CLS/patch cosine 均值 1.000000）。
- **梯 2 结果（0★ 回填后全标签集 9418 组，ViT-S 同步重跑同口径对照；主口径 gap=10min/w20/τ_hi=0.95/τ_lo=0.83）**：
  - **跨段/跨事件迁移仍全 chance**：局部段 Δ=1 段级 split ViT-S 49.9/49.4/50.3 → ViT-L 53.0/53.2/53.1；全局段事件级 split 50.0/49.5/50.7 → 51.0/51.2/51.2；事件条件化 52——ViT-L 仅 +1~3pt，远不及 ≥80% 线。**跨段零迁移 = 标签结构（宪法挑战 3），不是特征瓶颈，两 backbone 同解**；
  - **事件内可学性（对级对照上界）ViT-L 显著更强**：全局段 79.3/79.1/84.0 → **90.2/90.1/93.9**（+10pt）；局部段 61.3/61.1/62.8 → 65.9/66.6/67.9（+5pt）。
- **关键判读**：M2 锚定（评分偏移 / b_seg / 精修锚点）不可绕过、与 backbone 无关。backbone 定格的新信息 = ViT-L 事件内 +10pt（M3 事件内监督质量天花板更高）vs 成本（1.2GB 模型、端侧不现实、patch ≈+38GB 未提）。
- **下一步（一件事）**：**用户裁定 backbone 定格** → 进 M2（[plan-3-2](plans/dinov3-photo-ranking-plan-3-2-calibration-pairs.md) 跨段绝对校准）。
- **等待用户输入**：
  - **backbone 定格**：ViT-S（四路全量在库、端侧 86MB、事件内 79-84%）vs ViT-L（仅双 CLS、1.2GB、事件内 90-94%、patch 待补 +38GB）——宪法决策 8 "v1=ViT-S" 是否因事件内 +10pt 改定；
  - 精修回溯匹配执行（**M2 锚定路线确立后已是关键路径核心腿**——决策 3b 精修锚点；OUT-JPG = 精修已确认）；
  - 后续外拍保留原始 dump（§1.1，废片闸门 + 金标准废片组唯一来源）。
- **已裁定（2026-07-19）**：**F:\照片2026-P2 与 D:\PhotoDB 全部已评，0★ 即真 0★**——NULL 回填 2732 行（重庆春天 1970 + 旧批 762），全库 9418 组标签 NULL 清零；重庆春天非整事件 0★（2887 组 = 已评 917 + 回填 1970）。
- **暂缓事项**：§1.2 制式决策（增强是否入模）与影调对 B-测试——三制式在 CLS 任务上无差异，特征/锚定主线解决前无决策意义。
- **已冻结 / 待校准参数**：
  - 增强 = CLHE `ClipFactor=2.0` / `SaturationScale=1.0`（YCbCr 恒定色度），后缀 `+clhe2.0ycc1.0` 冻结进 model_id 契约——改参 = 整库重提；
  - 在库 model_id：`dinov3_vits16_f32_518_v1`（四路全）+ `dinov3_vitl16_f32_518_v1`（双路 CLS；ONNX 在 `D:\PhotoDB\dataset\models\dinov3_vitl16.onnx`；**非终选，待 backbone 裁定**）；
  - 探针主口径 gap=10min / window=20 / τ_hi=0.95 / τ_lo=0.83——仍是扫描默认值，正式校准是 §1.4 data_audit 的事；
  - 工程备注：`Tools/.venv` 已装 transformers/onnx/onnxruntime/onnxscript；torch 导出/校验需 `PYTHONUTF8=1`（✅ 打印撞 GBK 控制台）；探针多 model_id 共存时须显式 `--enh-model`。
- **分组与标注学规则（用户 07-19 确认）**：① 不同事件文件夹的照片**不可直接做星级比对**；同一事件文件夹内部可看作一组（0-3★ 局部段段切分在事件内部进行）。② **0-3★ 偏局部**（临近相似选优，配对域=滑窗）、**3-5★ 偏全局**（局部最优后事件内总体美学最优，配对域=事件全集）——已落成探针 `--pair-scope` 双模式（见 [EXECUTION-LOG.md](EXECUTION-LOG.md) 07-19 条）。
- **并行长周期**：金标准集攒集（plan-3-1 §5，~50-100 组按七类场景；所在拍摄段必须入 test，攒集时随记段归属）。
- **数据现状**：`D:\PhotoDB\dataset\photos_dataset.db` = 9418 组；ViT-S 四路全齐 + ViT-L 双路 CLS 全齐；标签 0★5564 / 1★1947 / 2★985 / 3★489 / 4★249 / 5★184（**全量有标签**）；10 事件 ≈120 段（gap=10min）；明细见 [data/BATCHES.md](data/BATCHES.md)。
- **已知事项**：DatasetBuilder 跑完主流程后进程会悬挂不退（ONNX session 清理期），手动/任务清理即可，不影响结果；`._*` AppleDouble 残桩已过滤（07-19 修复）。
