# Android 半屏与列表体验优化

## 范围与结果

- TOOLBAR-06 / TOOLS-01/02：四端控制栏 EXIF 入口直接选择 EXIF 详情，已展示其他工具页时同样切换；主图菜单保持单例前置且保留当前页。无 EXIF 按平台回退，加载中不当作缺失。
- SETTINGS-04：Android 设置与工具共用内容区手势隔离，双向滚动和顶端下拉不收起；顶部把手/标题仍可拖下关闭，设置关闭回写不变。
- SETTINGS-01/03/07/09/15：分类箭头 20dp 居中；预设、纯触控控制功能与 EXIF 单行最小高度 48dp；勾选字形左缘 16dp、命中区 44dp；删除预设与 EXIF/控制表冗余脚注。
- 修正命令图标字号二次换算与裁切，评分为星内数字；可排序表用完整高度测量，17 条控制命令全部可达，页面承担滚动。

## 验收环境与素材

2026-11-05，macOS 宿主；Android API 35 arm64 模拟器（1080×2400），.NET SDK 10.0.201。素材为应用内部 `files/pv-fixtures` 的仓库 JPEG 夹具副本。Apple 使用本机 macOS Debug 原生壳与 iPad mini A17 Pro / iOS 26.3 模拟器；iOS 使用既有沙盒 `Documents/tools-fixtures` 的同名 JPG/ARW 副本。没有操作用户照片或安装真机。

## 验证结果

- Android Debug 构建通过（0 错误；既有 nullable/API/原生库重复项与 NuGet 漏洞缓存权限警告保留）。`pv-check tools`：35 PASS / 0 FAIL。
- `pv-check settings`：density 420 / fontScale 1.0 竖屏 85 PASS / 0 FAIL；420 / 1.3 竖屏 85 PASS / 0 FAIL；420 / 1.0 横屏 84 PASS / 0 FAIL。
- density 525 / fontScale 1.3 竖屏：84 PASS / 1 FAIL。本次新增手势、行高、图标、完整列表、边距、脚注检查全部通过；失败是原有主屏“筛选条在文件栏内容区内”断言，其把可滚动文档宽 398dp 与窄视口 329dp 直接比较，未修改该非本次范围检查。
- Android 额外经 ADB 真实输入链点击控制栏进入 EXIF、长表上滑/下滑、顶部拖下关闭，并逐帧截图核对；设置控制页上滑可到评分 1–5，预设列表无底部说明。
- macOS `--pv-native-smoke --pv-native-tools Fixtures/jpeg` 通过；iOS `--pv-native-tools-check tools-fixtures` 通过。Windows `Tools/WindowsTypeCheck` 编译通过，未在 Windows 本机运行，不以类型检查替代运行验收。
- CoreContractCheck：926 PASS / 1 SKIP（非 Windows 路径测试）/ 0 FAIL；Web 单测 323 PASS，内置浏览器 `?check=all` 53/53 PASS；设计令牌生成校验与 `git diff --check` 通过。

## 证据与边界

- Android 保留证据：`artifacts/acceptance/android-experience-polish/`，含控制页前后、预设前后、完整控制列表底部、EXIF 直达/上滑/下滑/标题关闭截图，以及最终横屏设置和工具报告。
- macOS：`artifacts/acceptance/native-p2-acceptance/native-tools-checks.txt`。
- iOS：模拟器应用沙盒 `Documents/PhotoViewer/native-tools-checks.txt`。
- 改前旧 APK 与本次构建安装后均复现过启动早期 SIGSEGV（退出状态 11），再次启动后完成检查；未将该已登记启动问题归因于本次修改或宣称已解决。
- 测试后已恢复模拟器 density 420、fontScale 1.0 及原旋转设置；临时 Web 服务、浏览器验收页与 scratch 截图已清理。源树原有 `PhotoViewer.Mac/Info.plist` 修改保留，iOS 构建自动产生的版本写回已还原。
