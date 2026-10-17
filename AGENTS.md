# AGENTS.md — PhotoViewer

> 只做入口索引。详细上下文按任务与目录读取；不要在这里追加实现细节或阶段状态。

## 项目概览

PhotoViewer 是面向摄影师的跨平台选片应用（JPG/HEIF 浏览、评分写回 RAW、键盘优先）。技术栈为 .NET 10 + ReactiveUI；共享代码在 `PhotoViewer/`，Windows / macOS / iOS / Android 为平台头工程。Core 层不依赖 Avalonia，平台能力在启动时注入。

## 文档入口

- 产品范围、截图与支持格式：[README.md](README.md)
- 构建、运行与发布：[DEV.md](DEV.md)（统一使用 VS Code Tasks）
- 跨模块架构、平台差异与关键流程：[Design/architecture.md](Design/architecture.md)
- UI 行为契约与验收状态：[Design/ui-contract.md](Design/ui-contract.md)
- 当前重构进度、分支与下一步：[Plans/native-rebuild-handoff.md](Plans/native-rebuild-handoff.md)
- AI 训练独立仓：[Training/README.md](Training/README.md) · [Training/AGENTS.md](Training/AGENTS.md)

## 项目结构

```text
PhotoViewer/                  # 共享工程：Core、ViewModels、Views、Controls、Platform
PhotoViewer.Windows/          # Windows 头工程：WinUI 3 原生壳 + 迁移期旧壳
PhotoViewer.Mac/              # macOS 头工程：AppKit + Metal 原生壳 + 迁移期旧壳
PhotoViewer.Apple/            # Apple 共享源（链接进 Mac/iOS，不单独成程序集）
PhotoViewer.iOS/              # iOS / iPadOS 头工程：UIKit + Metal 原生壳 + 迁移期旧壳
PhotoViewer.Android/          # Android 头工程
Tools/                        # 诊断与维护工具
Training/                     # AI 训练独立 git 仓库（主库忽略）
Design/                       # 架构说明与 UI 契约
Plans/                        # 方案、阶段清单与任务进度
release/                      # 发布产物
```

## 模块手册

| 目录 | 职责 |
|---|---|
| [PhotoViewer/Core/AI/](PhotoViewer/Core/AI/AGENTS.md) | DINOv3、CV、相似聚类与分析展示器 |
| [PhotoViewer/Core/Color/](PhotoViewer/Core/Color/AGENTS.md) | 色彩标签与线性/显示编码转换 |
| [PhotoViewer/Core/Database/](PhotoViewer/Core/Database/AGENTS.md) | SQLite 照片缓存、指纹与特征表 |
| [PhotoViewer/Core/Exif/](PhotoViewer/Core/Exif/AGENTS.md) | EXIF/XMP 读写与标签解析 |
| [PhotoViewer/Core/Image/](PhotoViewer/Core/Image/AGENTS.md) | 解码、缓存、加载会话、预取与增强 |
| [PhotoViewer/Core/Input/](PhotoViewer/Core/Input/AGENTS.md) | 平台中立手势与快捷键模型 |
| [PhotoViewer/Core/Memory/](PhotoViewer/Core/Memory/AGENTS.md) | 内存压力状态机、预算与准入 |
| [PhotoViewer/Core/Platform/](PhotoViewer/Core/Platform/AGENTS.md) | 性能预算、外部打开与平台能力抽象 |
| [PhotoViewer/Core/Settings/](PhotoViewer/Core/Settings/AGENTS.md) | JSON 设置持久化与原生设置会话 |
| [PhotoViewer/Core/Storage/](PhotoViewer/Core/Storage/AGENTS.md) | 存储引用与文件选择器抽象 |
| [PhotoViewer/Core/Tools/](PhotoViewer/Core/Tools/AGENTS.md) | 照片数据统计服务 |
| [PhotoViewer/ViewModels/Main/](PhotoViewer/ViewModels/Main/AGENTS.md) | 主窗口外壳、文件源、图像与分析视图模型 |
| [PhotoViewer/ViewModels/Settings/](PhotoViewer/ViewModels/Settings/AGENTS.md) | 设置页视图模型（partial 分文件） |
| [PhotoViewer/ViewModels/Tools/](PhotoViewer/ViewModels/Tools/AGENTS.md) | 工具页视图模型 |
| [PhotoViewer/Views/Main/](PhotoViewer/Views/Main/AGENTS.md) | 主窗口视图 |
| [PhotoViewer/Views/Settings/](PhotoViewer/Views/Settings/AGENTS.md) | 设置页视图 |
| [PhotoViewer/Views/Tools/](PhotoViewer/Views/Tools/AGENTS.md) | 工具页视图 |
| [PhotoViewer.Apple/](PhotoViewer.Apple/AGENTS.md) | Apple 共享解码、Metal、几何与输入映射 |
| [PhotoViewer.Mac/](PhotoViewer.Mac/AGENTS.md) | macOS 原生壳与平台接线 |
| [PhotoViewer.iOS/](PhotoViewer.iOS/AGENTS.md) | iOS / iPadOS 原生壳与平台接线 |
| [PhotoViewer.Windows/](PhotoViewer.Windows/AGENTS.md) | Windows 原生壳与平台接线 |
| [Training/](Training/AGENTS.md) | 训练数据、模型导出与数据契约 |