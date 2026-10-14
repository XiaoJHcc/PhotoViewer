# P2 原生产品验收

## 启动

执行 VS Code **Debug Mac**（Debug 默认原生），或 **Run Mac** 复用已构建应用。旧壳对照：

```sh
open -n PhotoViewer.Mac/bin/Debug/net10.0-macos/PhotoViewer.Mac.app --args --legacy-ui
```

Release 发布通道仍为旧壳，原生通过 `--native` 进入；验收前不切换 dmg 默认内容。

## 人工操作清单

- 打开真实 JPEG/HEIF/RAW 或目录；确认目标照片选中、胶片条可滚动、排序/星级筛选/计数正确。
- 主窗口隐藏标题栏；交通灯与筛选器无重叠。上下/左右/自动布局、胶片条/控制栏显隐、系统全屏正常。
- 缩略图区保持方形；当前蓝描边、Full 缓存淡蓝内边、星级与“抖”徽标含义正确。点缩略图星级不会切换主图。
- 双击、F、预设缩放、捏合、拖动、触控板平移与离散滚轮翻图；100% 为源像素对屏幕物理像素 1:1。代理→Full 不跳位置，导航图与可见范围同步。
- 设置用原生侧栏分类与表单：文件/预览/控制/EXIF/AI；格式/按钮/EXIF 可拖动排序，缩放预设可增删编辑，快捷键主次录制/清空/冲突可见。关闭设置后应用，重启检查持久化。
- 评分文件写回、同名合并同步、失败提示；Finder“打开方式”、Dock/窗口拖入及文件 URL 再次打开。
- 系统预览与应用对拍 sRGB/P3/Adobe RGB、灰阶、高细节素材及 OUT-JPG；外接 sRGB 屏拖动后无需重载。帧导出不能代替此屏幕合成器验收。

## 程序化核验与产物

- `Tools/ColorTestTool`：23 项独立矩阵/传递函数、广色域负值/超一、非对齐行距与 8 位往返，最大线性分量误差 0.000488。
- `Tools/generate-native-color-fixtures.py`：系统 ICC 三色域色块/灰阶 + 8 EXIF 方向，使用可写的 XMP Rating 测试槽。
- `--pv-native-smoke <绝对图片路径>`：真实 AppKit 窗口、Metal 上屏、Full、翻图、共享外部打开队列、布局、五类设置实际落盘、无 Avalonia 主图副本、评级/CV/DINO/指纹三表索引读写。生成文件与 RAW 副本目录必须是 agent 自有测试目录。8 个 EXIF 方向独立 GPU 角点验证，ICC sRGB/P3/Adobe 与无 ICC EXIF Adobe 均通过。
- `release/native-p2-acceptance/`：JSON 报告及实际原生控件绘制/GPU 照片帧合成图，构建及运行日志。设置/SQLite 测试独立持久化，原始 TestCase 照片未修改。
- 四平台构建：Mac、Windows、iOS 模拟器、Android 均已编译；Windows/iOS 0 警告，Mac NU1903 与 Android 14 项未改文件中的警告为现有基线。最终检查以阶段清单记录为准。

最近程序化结果：ColorTestTool **23** 项；JPEG 色卡/八方向 **39** 项；RAW 副本 **17** 项；生成 HEIF（sRGB/P3）**14** 项。设置副本编辑期间缓存/键盘映射不变，关闭后同步运行时并实际落盘；分析输入的八方向与 GPU 对拍一致。HEIF 生成样本用于链路验证，不代替相机大图性能测试。

复跑示例（先执行 Debug Mac 构建）：

```sh
dotnet run --project Tools/ColorTestTool
PhotoViewer.Mac/bin/Debug/net10.0-macos/PhotoViewer.Mac.app/Contents/MacOS/PhotoViewer.Mac --native --pv-native-smoke "$PWD/release/native-p2-fixtures/color-srgb.jpg"
```

色卡由 `Tools/generate-native-color-fixtures.py` 生成，需 Pillow 和 macOS 系统 ICC。报告包含实际等待加载的时间；RAW 冷图约 1.7–1.9s，尚未满足 fit <100ms 目标，不宣称性能门通过。原生设置绘制导出可能遗漏系统选中背景，最终以窗口实际显示为准。

## 计划修订与待验范围

胶片条定向回归：`--pv-native-smoke --pv-native-filmstrip <绝对图片路径>`，使用作者 `PhotoViewerTest` 的五组 HIF/ARW 与 12 张色卡，核对左右/上下布局、名称升降序、实际复用卡片身份、每项完整滚入、手动滚动不复位以及横向鼠标滚轮。报告 `filmstrip-five-checks.txt` / `filmstrip-many-checks.txt`；不修改作者照片。

- Apple 表面使用 **ExtendedLinearSrgb**，匹配 F16 线性数据；代理也保留 F16 广色域。
- 加载编排使用共享 **PhotoLoadSession**，旧 ImageViewModel 展示状态留给旧壳；此设计减少新旧变换状态耦合，保持旧壳可回归。
- 冷条目使用统一 LRU 的代理档/淘汰策略；不以 sRGB8 作为广色域唯一中间态。F16 与池化实际分配字节参与账本，GPU 客户端单独登记。
- 仓库实际评分路径是**已有 XMP Rating 的原位更新**，RAW 样本同样原位更新；独立 `.xmp` 的字节写服务可工作，但当前没有自动创建/关联 RAW sidecar。此前总纲与根文档的“RAW 自动 sidecar”描述失准，不能记作已回归的产品功能；P2 保留实际现有行为，新增自动 sidecar 需单独立项。
- 最终人工手感/色彩对拍/跨屏验收待作者执行；性能目标 <100ms 的大图、HEIF/OUT-JPG 矩阵仍以真实素材实测裁定，不能由小色卡冒烟代替。
