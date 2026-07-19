# CLAUDE.md — Training

> 模块内手册。跨模块联动(产品算法如何被训练消费、AI 特征如何入库)见根 `CLAUDE.md` §5.4。

## 模块职责

AI 训练一等模块:从产品仓库(`PhotoViewer/Core`)提取 DINOv3 特征 + CV 网格数据,填充独立训练数据集库,支撑照片排序模型的可行性探针与训练迭代。与产品共用一套提取算法(C# `ProjectReference`),但物理上不拆仓——仓内模块化。

## 子目录索引

| 目录 | 职责 |
|---|---|
| [DatasetBuilder/](DatasetBuilder/) | C# CLI:清单驱动扫描训练用照片文件夹 → 指纹聚合(RAW/HEIF/JPG 合一)→ EXIF/rating → DINO(原片 CLS + 增强 CLS + patch)→ CV grid → 写入独立数据集库 + 覆盖率报告。深度 `ProjectReference` 共享项目 `PhotoViewer/PhotoViewer.csproj`,提取算法与产品共演进,不允许分叉实现。 |
| [probes/](probes/) | Python 特征可行性探针:`feature_probe.py`(线性探针 + t-SNE,判断 backbone/增强/多视图是否够分)、`spatial_probe.py`(空间感知头判别,复用 `feature_probe` 的配对/split 逻辑)。`out/` 是每次运行的覆盖式输出(不入库)。 |
| [onnx/](onnx/) | DINOv3 模型导出/校验:`export_dinov3_onnx.py` 从 HuggingFace/ModelScope 权重导出双输出(CLS + patch)ONNX;`verify_onnx_parity.py` 校验 PyTorch vs ONNX 一致性(cosine ≥ 0.999)。改动需同步 `PhotoViewer/Core/AI/DinoModelResources.cs`。 |
| [notebooks/](notebooks/) | `cv_grid_design.ipynb` —— CV 网格设计 PoC(numpy 全量标量验证),已定型归档,不再迭代。 |
| [plans/](plans/) | 三期计划文档:plan-3-0 宪法 + plan-3-1(M1 详案)+ plan-3-2/3-3/3-4 契约册,彼此用文件名相对链接。一/二期基建历史与 copilot 原始讨论已归还主仓 [../Plans/](../Plans/)(考古专用;已否定方向收编在 plan-3-0 §3 附录),现行基建状态以根 `CLAUDE.md` §5.4 为准。 |
| [data/](data/) | 数据契约文档([data/README.md](data/README.md)):数据集库 schema、与产品 `photos.db` 的对齐关系、`dataset_meta` 版本化约定;入库批次台账([data/BATCHES.md](data/BATCHES.md)):批次源/题材/分组规则/特殊情况,每入库一批更新。数据本体在仓外 `D:\PhotoDB`。 |
| [EXECUTION-LOG.md](EXECUTION-LOG.md) | 执行台账(append-only):每次实验的数据/前提/命令/结果/解读/下一步,跨会话防遗忘;`probes/out/` 每次覆盖写,靠本台账留档历史结论。 |
| [STATUS.md](STATUS.md) | 进度真源(每次会话末重写,不追加):里程碑位置 / 最近 GATE / 下一步 / 等待用户项 / 已冻结参数。开工先读。 |

## 构建 / 运行入口

- **构建**:`dotnet build Training/Training.sln`(独立解决方案,仅含 `DatasetBuilder`;**不要**把它加进主 `PhotoViewer.sln`——`DatasetBuilder` 是 `net10.0-windows`,加进跨平台主 sln 会连累 Mac/iOS 头的构建)。
- **运行提取**:`dotnet run --project Training/DatasetBuilder -- --manifest <manifest.json>`(清单驱动,见 [DatasetBuilder/manifest.sample.json](DatasetBuilder/manifest.sample.json))或 `dotnet run --project Training/DatasetBuilder -- <folder>... --scan-only`(只扫描不建库,快速核验批次分布)。
- **探针**:Python 侧用仓根 `Tools/.venv`(独立虚拟环境,首轮探针即用它;新建则 `pip install -r Training/probes/requirements.txt`),例如 `Tools/.venv/Scripts/python.exe Training/probes/feature_probe.py --db D:/PhotoDB/dataset/photos_dataset.db`(从仓根执行)。

## 对产品的契约

- **单向依赖,产品是唯一真源**:`PhotoViewer/Core`(DINOv3 推理、CV 网格提取、EXIF 读取、增强算法等)是训练侧提取逻辑的唯一真源;`DatasetBuilder` 通过 `ProjectReference` 直接复用,不重新实现、不分叉。
- **训练消费产品,不反向影响产品行为**:训练侧的数据集 schema、探针结论、模型训练不得要求产品代码为训练目的改变用户可见行为;产品功能(如控制栏增强 toggle)独立立项,训练侧只是复用其确定性算法。
- **Python 只吃数据集库 schema**:`probes/` 与产品之间没有代码耦合,仅通过 SQLite 数据集库的表结构契约(见 [data/README.md](data/README.md))交互——产品与提取算法可以自由演进,只要 schema 契约(表名/列名/`model_id`/`cv_grid_spec` 版本化规则)不破坏,探针脚本不需要跟着改。
- **数据在仓外**:训练用原始照片与生成的数据集库位于 `D:\PhotoDB`(仓外,不受仓库大小/清理影响,便携)。仓内只有代码、脚本、计划与文档。
