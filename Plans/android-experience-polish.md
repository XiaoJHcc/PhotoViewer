# Android 半屏与列表体验优化

## 范围与结果

- TOOLBAR-06 / TOOLS-01/02：四端控制栏 EXIF 入口直接选择 EXIF 详情，已展示其他工具页时同样切换；主图菜单保持单例前置且保留当前页。无 EXIF 按平台回退，加载中不当作缺失。
- SETTINGS-04：Android 设置与工具共用内容区手势隔离，双向滚动和顶端下拉不收起；顶部把手/标题仍可拖下关闭，设置关闭回写不变。
- SETTINGS-01/03/07/09/15：分类箭头 20dp 居中；预设、纯触控控制功能与 EXIF 单行最小高度 48dp；勾选字形左缘 16dp、命中区 44dp；删除预设与 EXIF/控制表冗余脚注。
- 修正命令图标字号二次换算与裁切，评分为星内数字；可排序表用完整高度测量，17 条控制命令全部可达，页面承担滚动。

### 行高与半屏初始状态修正（2026-11-06）

- 48dp 只保留给单行紧凑内容：纯触控控制功能、EXIF 显示与缩放预设。文件格式多行与显示键鼠绑定控件的控制功能行恢复 52dp / 上下 8dp；普通设置行、分类行与工具分类行恢复 52dp / 上下 12dp / 左右 16·12dp；缩放预设用独立紧凑参数（上下 2dp，左右对称 16dp，保证输入组仍在整行居中）。
- 设置与工具浮层在 `Show()` 之前配置 `BottomSheetBehavior`（Peek = 屏高一半、SkipCollapsed = false、State = Collapsed），初始即半屏收起，不再先展开后闪回；标题上拖扩大到内容高度（屏高 85%），下滑关闭，内容区滚动隔离不变。
- 控制功能行勾选字形→命令图标、命令图标→命令名间距改为 16dp；图标左边距仍扣除 44dp 命中区的透明内缩。
- 设计令牌新增 `settings.android.list.row.paddingY`、`settings.android.row.minHeight` / `paddingY` / `paddingRight`、`settings.android.icon.gap`、`settings.android.sheet.heightRatio` / `peekRatio`，并同步 spec、platforms、status 与 Android 模块手册。

**修正后验证**：Android Debug 构建 0 错误 / 30 警告（均为既有警告）；API 35 arm64 模拟器 `emulator-5554`、density 420 / fontScale 1.0 / 竖屏：`pv-check settings` 87 PASS / 0 FAIL，`pv-check tools` 36 PASS / 0 FAIL。settings 中新断言记录初始 `state=collapsed top=1001 parentHeight=2201 peek=1200`（屏高 2400）、标题上拖后 `state=expanded top=161 height=2040`；tools 同样为 `state=collapsed top=1001 peek=1200`。tools 首次启动命中既有偶发启动 SIGSEGV（signal 11），重跑通过，未归因于本次修改。构建用 `artifacts/scratch/sdk201/global.json` 固定 .NET SDK 10.0.201，未改仓库 `global.json`。

最终交付包复核：重新构建并安装 Debug APK 后，settings 再跑 87 PASS / 0 FAIL，tools 再跑 36 PASS / 0 FAIL；报告保存在 `artifacts/acceptance/android-settings-restored/`。设计令牌校验、Web 单测 323/323 与 `git diff --check` 通过。本次修正尚未重跑大字体、窄屏和横屏矩阵，也未覆盖安装真机。

### 半屏可见高度滚动修复（2026-11-06）

- 现象与根因：`SetContentView` 把浮层内容固定为屏高 85%（2040px），`BottomSheetBehavior` 收起时只把整个容器下移到屏内露出 50%（1200px），但内层「把手 + 标题 + 滚动区」仍按 85% 量测，ScrollView 高 1873px、窗口内只可见 1033px，底部 870px 落在屏外；滚到 `maxScroll` 时最后一项仍在屏外（文件页 `scrollY=1067`、`tailVisible=False`，EXIF 工具页同类）。
- 修复：新增共享 `AndroidSheetViewport`（`Native/AndroidSheetScrollView.cs`），在绘制前把内层根布局高度同步为面板当前可见高度（内容顶端到浮层宿主可用底边，扣除底部内边距）；外层面板高度、Peek、FitToContents 与拖动几何完全不变，因此 `expandedOffset` 不跳变、也不会形成布局回路。设置与工具两个 sheet 在 `OnAttachedToWindow` 挂接、`OnDetachedFromWindow`/关闭时解绑，收起、拖动/惯性、展开后重新收起、窗口尺寸与 insets 变化都在下一帧前刷新；高度未变时不触发布局。
- 检查补充：`AndroidNativeChecks.Settings.cs` 在仍为 Collapsed 时断言内层 root 高度等于可见区域、ScrollView 高度等于 root 剩余空间且全局可见矩形未被裁切；文件页用真实窗口触摸上滑到末尾（可多次），控制页/EXIF/缩放预设页用 `FullScroll` 到末尾并核对末项完整可见；标题上拖中途停住不抬起，断言拖动中的内容高度就已跟随当时可见区域；展开后核对 85% 档、重新收起后再验底部。滚动复位改为等惯性结束（单次 `ScrollTo(0,0)` 会被仍在跑的 fling 推走，横屏首跑曾因此误报一次“到顶下滑”）。`AndroidNativeChecks.cs` 的 tools 检查补 `ScrollHostForChecks`，验证工具收起态布局与 EXIF 长页底部。
- 改前证据（同一 APK 仅加检查）：`pv-check settings` 90 PASS / 7 FAIL，`pv-check tools` 36 PASS / 2 FAIL；失败集中在 `收起态内容布局高度等于可见区域`（可见高度 1200、内层 root 2040、scroll 1873；当时可见高度尚未扣除浮层宿主内缩，报告显示 1263）与各长表 `tailVisible=False`。
- 修复后：`pv-check settings` 98 PASS / 0 FAIL（竖屏 fontScale 1.0，根 1200 / 滚动 1033，真实上滑 2 次到底且末项完整可见；拖动中 top=695 时根/滚动已同步为 1506/1339；展开根 2040 / 滚动 1873；重新收起仍到底）；fontScale 1.3 竖屏 98 PASS / 0 FAIL；横屏（2400×1080）97 PASS / 0 FAIL（根 540 / 滚动 373，拖动中 675/508，真实上滑 7 次到底）。`pv-check tools` 38 PASS / 0 FAIL（EXIF 长页 4820px，滚动 1033，末项完整可见）。构建 0 错误 / 25 警告（均为既有 nullable/API/原生库重复项）。本轮未出现已登记的启动期 SIGSEGV。
- 证据：`artifacts/acceptance/android-sheet-visible/`（prefix-settings/tools、fixed-tools、fixed-settings-font13、fixed-settings-landscape 报告）。模拟器已恢复 fontScale 1.0、竖屏、density 420。随后按现有发布脚本成功构建正式签名 Release，已覆盖安装至小米 25128PNA1C，保留应用数据；启动存活并截图确认设置控制页默认半屏。作者随后确认真机问题已解决；模拟器程序化检查与作者真机手验分别记录，不互相替代。
- 提交前同步远端 `4100a74`（共享色彩/Windows 更新），`Design/status.md` 自动合并且双方状态保留，无未解决冲突。补齐 `ViewTreeObserver` 空值保护，消除本次新增的两处 nullable 警告；最终 Android Debug 0 错误 / 23 个既有警告。拉取后重新安装模拟器验证 settings 98/98、tools 38/38、main 22/22；CoreContractCheck 926 PASS / 1 SKIP / 0 FAIL，Web 323/323、令牌校验与 `git diff --check` 通过。Android 报告在 `artifacts/acceptance/android-after-pull/`。版本号与 Apple plist 的原有本地改动保留，不纳入本次 Android 修复提交。

### 26MP HIF 放大停顿调查

- 本轮只调查，不修改主图与手势产品路径。40.3% 对应 2496px 代理 / 6192px 源图的覆盖边界；跨越后由 `PhotoViewport.NeedsFull` 触发 Full 升级，解码在后台，但产物经会话通知在 UI 线程同步上传。
- 已核对本机 .NET Android 36.1.53 的 `ByteBuffer.Put(byte[], offset, length)` 运行时 IL：每次分块调用先对整个数组执行 `NewArray`，返回时又 `CopyArray` 整幅拷回，offset/length 不限制绑定层搬运。26MP RGBA8 分 25 块，额外整图往返约 5GB，产生大量 Java 临时大数组。
- 已连接小米 25128PNA1C 的现有日志出现主线程反复 `Waiting for a blocking GC Alloc`、104MB / 208MB 大对象回收与约 1461–1502ms 的超长帧，支持上传搬运/GC 阻塞的结论，而非单纯等待 HEIF 后台解码。
- 长按按原始 DownTime 计时；UI 阻塞后积压的 Down/Move 可能与长按消息错序，现有长按校验只能看到已送达的位移。属于性能缺陷叠加输入时序边界；尚未重新捕获用户这一次手势的完整运行时链路，不将代码分析冒充真机完整复现。
- 真机拒绝安装独立临时诊断程序，未重试、未替换真机 PhotoViewer；第二模拟器因实例限制也未启动，临时诊断工程已清理。原始 GC 日志、绑定 IL 与调查说明保存在 `artifacts/diagnostics/android-hif-zoom/`。后续应先消除分块路径反复整图 JNI 搬运，再测 UI 帧预算并补事件积压回归，不通过加长长按阈值或降低画质掩盖。

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
