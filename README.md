# PhotoViewer

一个基于 Avalonia 开发的全平台照片查看器，专为摄影选片流程优化设计。

## 项目简介

PhotoViewer 是一款专为摄影师打造的高效选片工具，支持 Windows、macOS、iOS 和 Android 平台。专注于优化摄影工作流，让选片过程更加快速、随时随地，帮助您在拍摄后第一时间筛选出最佳作品。

*这是一个个人项目，先期版本主要解决以下痛点：*
1. *Windows 上缺少浏览 HEIF 格式的图片查看器（尤其是 SONY HEIF 10bit 4:2:2）*
2. *想在只浏览 JPG /HEIF 的情况下给 RAW 同步标星*
3. *需要操作习惯统一的移动端应用*
4. *需要针对摄影流程优化布局、精简功能*

## 核心特色

### 🎯 选片优化
- **全平台支持**：随时随地使用 USB 连接相机立即开始选片
- **格式兼容**：浏览 JPG 或 HEIF 文件，同时将星级同步至对应的 RAW 文件
- **高效筛选**：使用快捷键快速浏览、对比、标星

### 🖼️ 视觉体验
- **质量优先**：照片始终以最高质量显示，尽可能占满屏幕空间
- **信息精简**：仅在剩余空间显示关键的 EXIF 信息和星级评价
- **平台统一**：跨平台一致的 UI 设计和使用体验

### ⚡ 完全自定义
- **快捷键**：全平台支持自定义快捷键键位
- **界面布局**：横纵布局、控制面板按钮、EXIF 信息显示均可自定义

## 推荐工作流程

1. **拍摄阶段**：相机设置为 RAW + JPG / HEIF 格式拍摄
2. **预选阶段**：利用零散时间（如交通工具上）通过 USB 直连相机进行初筛
3. **精选阶段**：在空闲时间（如酒店内）拷卡至笔记本进行精选
4. **归档阶段**：可以使用其他工具（如 SONY Imaging Edge Desktop）筛选并移动照片的文件夹，本应用暂不开发
5. **后期阶段**：精选后的照片导入 PS 后期处理

## 平台支持

### 已测试平台
- **Windows 11**
- **macOS** （MacBook Air M1）
- **iPadOS** （iPad mini A17 Pro）
- **Android** （小米 13）

### 已测试相机
- **SONY A6100**
- **SONY A7C2**
- *其他品牌相机的 RAW 标星功能暂未测试，为避免文件损坏，使用前应先验证可靠性*

### 平台支持情况

| 功能          | Windows                                                      | macOS                                                           | iOS/iPadOS | Android |
|-------------|--------------------------------------------------------------|-----------------------------------------------------------------|------------|-----|
| JPG 预览      | ✅ 支持                                                         | ✅ 支持                                                            | ✅ 支持       | ✅ 支持 |
| HEIF 预览     | ✅ 支持                                                         | ✅ 原生支持                                                          | ✅ 原生支持       | ⚠️ 部分支持 |
| RAW 预览      | ❌ 待开发                                                        | ❌ 待开发                                                           | ❌ 待开发      | ❌ 待开发 |
| SONY ARW 标星 | ✅ 支持                                                         | ✅ 支持                                                            | ✅ 支持       | ✅ 支持 |
| 其他 RAW 标星   | 未测试                                                          | 未测试                                                             | 未测试     | 未测试 |
| 快捷键         | ✅ 支持                                                         | ✅ 支持                                                            | ✅ 外接键盘     | ✅ 外接键盘 |
| 触摸          | 触控板未优化                                                       | 触控板未优化                                                          | ✅ 触屏手势     | ✅ 触屏手势 |
| 下载          | [Releases 页面](https://github.com/XiaoJHcc/PhotoViewer/releases) | [Releases 页面](https://github.com/XiaoJHcc/PhotoViewer/releases) | ⚠️ 暂未上架    | [Releases 页面](https://github.com/XiaoJHcc/PhotoViewer/releases) |

- *Android 平台对 HEIF 的支持取决于系统本身，如果系统相册支持则本应用也支持*
- *小米 13 实测情况：本机和 iPhone 拍摄的 HEIC 可支持，SONY 拍摄的 HIF 不支持*
- *iOS 版暂未上架 AppStore，如需使用可自行使用 Xcode 编译安装*

## 安装与使用

### 下载安装
[Releases 页面](https://github.com/XiaoJHcc/PhotoViewer/releases) 

- **Windows** : 下载 `PhotoViewer.Windows.exe` 直接运行
- **Android** : 下载 `PhotoViewer.Android.apk` 并安装
- **macOS** : 下载 `PhotoViewer.Mac.dmg` 打开映像，并将 app 拖入应用程序文件夹。启动时显示“Apple无法验证……移到废纸篓”，则打开设置, 进入“隐私与安全性”，拉到最底点击“仍要打开”。

## 开发计划

### 🚧 待开发功能
- **设置保存**
- **RAW 格式预览**
- **长曝抖动检查**（尝试自动化）
- **连拍/HDR/堆栈自动分组**
- **SONY 对焦点识别**
- *……其他需要的功能*

## 许可证

本项目因使用 [LibHeif](https://github.com/strukturag/libheif) 等开源库，遵顼协议采用 [GPL 许可证](LICENSE)。