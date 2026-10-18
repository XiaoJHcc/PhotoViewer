# 控制栏（TOOLBAR）· 拆分草稿

来源：`Design/ui-contract.md` §3 TOOLBAR-01 段与 §4 TOOLBAR-01 行、`Plans/native-rebuild-phase-2-ui.md` §1.3、`Plans/native-rebuild-windows-p2.md` §12/§15、`Plans/native-rebuild-phase-ipad.md`、`Plans/native-rebuild-phase-android.md`，以及四端原生控制栏实现与共享 `ControlBarLayout`。本文件只做事实登记，规则以 `toolbar.md` 为准。

## platforms

| 项 | macOS | iOS / iPadOS | Windows | Android |
|---|---|---|---|---|
| 控制栏容器 | `Native/MacControlBar.cs`（`NSView`）置于 `NSScrollView`（`MacMainWindow.cs:119`） | `Native/iOSControlBar.cs`（`UIView`）置于 `UIScrollView`（`iOSNativeRootViewController.cs:243`） | `Native/WindowsControlBar.cs`（`Grid` + `WindowsWrapPanel`）直接放入分区（`WindowsMainWindow.cs:537-543` / `566-572`） | `Native/AndroidControlBar.cs`（单个自绘 `View`）直接放入分区（`AndroidNativeRootView.cs:128-130, 343-344`） |
| 分组/子控件 | 三个子 `NSView` 分组，子控件为真实 `NSButton`/`NSTextField` | 三个子 `UIView` 分组，子控件为 `UIButton`/`UILabel` | 三个 `Border`/`StackPanel` 分组，子控件为 `Button`/`TextBlock` | 无子 `View`：分组与子控件只是数据 + `Rect`，绘制与命中都在 `OnDraw`/`OnTouchEvent` |
| 布局数学 | 共享 `ControlBarLayout`（分组尺寸、换行、居中、文档尺寸） | 同左 | 自绘 `WindowsWrapPanel` + `WindowsLayout` 常量，口径对齐共享数学 | 共享 `ControlBarLayout` |
| 命令执行 | `MacControlBar.Rebuild(main, execute, rate, openTools)` | `iOSControlBar.Rebuild(...)` | `WindowsControlBar` 构造注入 `execute`/`openTools` | `AndroidControlBar.Wire(execute, rate, openTools)` |
| EXIF 组点击 | `GroupView.MouseDown` → `openTools`（`MacControlBar.cs:44, 217-221`） | `UITapGestureRecognizer`（`iOSControlBar.cs:67-71`） | `Border.Tapped` + tooltip「工具」（`WindowsControlBar.cs:62-66`） | 命中任一 EXIF 子块 → `ShowTools`（`AndroidControlBar.cs:290-292`；`AndroidNativeRootView.cs:129`） |
| 无照片点击 EXIF 组 | 仍打开工具：`Tools.ShowList()` 后展示工具列表（`MacControlBar.cs:44`；`ToolsViewModel.cs:91-97`） | 同语义（打开工具页列表） | 同语义 | 同语义 |

已登记差异（`ID@平台`）：

- `TOOLBAR-03@win`：列布局的星序由子控件**逆序加入**实现（`WindowsControlBar.cs:326-344`），macOS/iOS/Android 用共享 `ChildOffsets` 反序。
- `TOOLBAR-03@win`：无照片时评分按钮用 `Opacity=0.1`（`WindowsControlBar.cs:319`），macOS/iOS 用 `Enabled=false`（`MacControlBar.cs:61`、`iOSControlBar.cs:87`），Android 评分外观不变、点按在宿主层被 null 检查吞掉（`AndroidNativeRootView.cs:129`）。
- `TOOLBAR-04@mac`：tooltip = 命令名 + 按苹果键盘映射格式化后的主/次绑定，过滤未设置项（`MacControlBar.cs:72-73, 98-104`）；`TOOLBAR-04@win` 用 `AppGesture.ToDisplayString()`（`WindowsControlBar.cs:290-304`）；`TOOLBAR-04@ios` 无 tooltip，只有 `AccessibilityLabel = 命令名`（`iOSControlBar.cs:107`）；`TOOLBAR-04@android` 既无 tooltip 也无无障碍名称（`AndroidControlBar.cs:258-272`）。
- `TOOLBAR-04` 评分快捷命令：macOS/iOS 用数字标题 11 号字（`MacControlBar.cs:80-85`、`iOSControlBar.cs:110-115`），Windows 画 1–5 个 10 号实心星、0 画叉（`WindowsControlBar.cs:254-267`），Android 画数字（`AndroidControlBar.cs:115, 262-266`）。
- `TOOLBAR-05@mac`：块 tooltip 为 `标签 · 值`（`MacControlBar.cs:232`）；`TOOLBAR-05@win` 为 `标签:值`（`WindowsControlBar.cs:216`）；`TOOLBAR-05@ios` 只把 `标签 值` 放进 `AccessibilityLabel`（`iOSControlBar.cs:248`）；`TOOLBAR-05@android` 自绘尾部截断，无任何完整值入口（`AndroidControlBar.cs:228-237`）。
- `TOOLBAR-05@android`：EXIF 文字为 10sp 标签 + 12sp 粗值（`AndroidControlBar.cs:62-63`），底色 `#262626`（`AndroidUiTheme.ControlFill`），token 已登记为该平台差异。
- `TOOLBAR-07@ios`：重建时必须先 `RemoveFromSuperview` 再 `Dispose`（`iOSControlBar.cs:210-229`），否则设置回写后新旧分组重叠——2026-10-17 作者真机缺陷③。
- `TOOLBAR-07@win`：按影响范围合并到一次 dispatcher turn（EXIF/评分/命令分开刷新，`WindowsControlBar.cs:404-499`）。
- `TOOLBAR-07@mac`：逐项订阅 `IsDisplay` / `Name` / `PrimaryHotkey` / `SecondaryHotkey` 后请求宿主合并刷新（`MacControlBar.cs:91-111`）。
- `TOOLBAR-07@android`：`MarkDirty` + `OnDraw` 内补齐 `Arrange`（`AndroidControlBar.cs:197-198`；`AndroidNativeRootView.cs:738-740`），修复过「只剩容器」。
- `TOOLBAR-08@mac`：行布局且文档宽超过视口时才开横向滚动指示器（`MacMainWindow.cs:498`），纵向滚动条创建时即开（`MacMainWindow.cs:119`）。
- `TOOLBAR-08@ios`：行布局主轴溢出显示横向指示器，列布局溢出显示纵向指示器（`iOSNativeRootViewController.cs:501-502`）。
- `TOOLBAR-08@win`：无滚动宿主，`WindowsWrapPanel` 直接排进分区（`WindowsMainWindow.cs:537-543`）。
- `TOOLBAR-08@android`：无滚动宿主，自绘 `View` 被 `LayoutChild` 约束到分区矩形（`AndroidNativeRootView.cs:343-344`）。
- `TOOLBAR-11@android`：整个控制栏是一个 `View`，没有子节点、没有 `ContentDescription`/`Focusable`（`AndroidControlBar.cs:277` 起只有 `OnTouchEvent`）。

## status

- **TOOLBAR-01 · Windows**：150% 实机对拍通过（ui-contract §3 TOOLBAR-01 段，2026-10-17）；EXIF 信息块整组可点打开工具窗口已实机验证；200%/175% DPI 与手感待验（§4）。
- **TOOLBAR-01 · macOS 原生**：ui-contract §3 记录 2026-10-15 与本轮对拍几何（四项 EXIF/六个命令的真实布局比对），§4 写「见 P2/P3」；`--pv-native-toolbar` 回归输出 `release/native-p2-acceptance/toolbar-ui-checks.txt`，2026-10-16 作者主界面视觉/手感确认通过（P2 验收结论）。
- **TOOLBAR-01 · iOS/iPadOS**：模拟器两机型程序化通过（2026-10-17：三分组 50×40 / 30×30 / 40×40、换行/居中、I5 命令可用、EXIF 块打开工具页）；真机 `--pv-native-tools-check` 覆盖 EXIF 块入口；触摸手感待作者真机。
- **TOOLBAR-01 · Android**：原生壳 2026-10-18 模拟器目视——三分组换行/居中、评分写回、命令显隐/顺序；ui-contract §4 仍写「EXIF 块打开工具页待 A5」，Android 计划称 A5 已完成（见 questions）。
- **新增 ID TOOLBAR-02–12**：来源条目只按 TOOLBAR-01 汇总，没有各自的四端验收记录；这些行为在当前四端实现中均有对应代码，但**不能把 TOOLBAR-01 的状态复制给新 ID**。status.md 建立后逐 ID 重新登记（含 OS、设备、素材、证据路径）。
- 可复用的验收事实（macOS `NativeToolbarAcceptance`）：空工具栏文档 20×20（`NativeToolbarAcceptance.cs:89`）、空分组不保留占位（`:85`）、`SetRating3` 按钮标题为 `3`（`:97`）、无照片时评分全禁用而 `Open` 可用、超长命令组可横向滚动。
- 证据路径参考：macOS `release/native-p2-acceptance/toolbar-ui-checks.txt`；Windows `release/native-win-acceptance/checks.json`；iOS 模拟器/真机检查报告；Android 报告写在 App 内部 `files/PhotoViewer/android-checks-<mode>.txt`。

## questions

1. **禁用态两套语言**：Windows 无照片时把评分按钮设 `Opacity=0.1` 但仍可点（`WindowsControlBar.cs:319, 391-395`），macOS/iOS 用 `Enabled=false`（`MacControlBar.cs:61`、`iOSControlBar.cs:87`），Android 评分外观完全不变、点按在宿主被吞（`AndroidNativeRootView.cs:129`）。`toolbar.md` TOOLBAR-03 只要求「保留几何可见且不可点」，未定禁用外观；是否要求四端统一（降透明 vs 系统禁用色）需要拍板。
2. **触屏读不到完整信息**：iOS 的 EXIF 完整值只有 `AccessibilityLabel`（`iOSControlBar.cs:248`），Android 连这个都没有（`AndroidControlBar.cs:228-237`）；命令的快捷键提示在 iOS/Android 无悬停等价物（`iOSControlBar.cs:107`、`AndroidControlBar.cs:258-272`）。`toolbar.md` 已用「整组点击进工具窗口」兜底 EXIF 值、用设置页快捷键列表兜底绑定，但两者都不是就地可见；是否需要就地长按提示需拍板。
3. **Windows/Android 溢出无滚动**：macOS/iOS 有溢出滚动（`MacMainWindow.cs:498`、`iOSNativeRootViewController.cs:501-502`），Windows 无滚动宿主（`WindowsMainWindow.cs:537-543`）、Android 自绘视图被分区矩形约束（`AndroidNativeRootView.cs:343-344`）。命令显示较多或窗口很小时，超出部分可能不可达。是缺口还是应登记 `TOOLBAR-08@win` / `@android` 差异？
4. **Android 控制栏没有无障碍树**：整栏是一个 `View`，`OnDraw` 绘制、`OnTouchEvent` 命中，无子节点、无 `ContentDescription`、无 `Focusable`（`AndroidControlBar.cs:194-304`）。TOOLBAR-11 的「每个控件有可读名称 + 焦点顺序」在该实现下无法成立。
5. **开关型命令按钮无状态**：任何平台都没有按下态或强调色（`MacControlBar.cs:73-79`、`WindowsControlBar.cs:273-286`、`iOSControlBar.cs:101-109`、`AndroidControlBar.cs:258-272`），状态只在菜单勾选（ui-contract iOS 差异段）与面板可见性上。TOOLBAR-12 记为 `可以`；若要统一表达，需先在样机定视觉。
6. **Android EXIF 入口状态矛盾**：ui-contract §4（2026-10-18）仍写「EXIF 块打开工具页待 A5」，但 `AndroidControlBar` 已接线 `ShowTools`（`AndroidControlBar.cs:290-292`），Android 计划 §7 #22 记为 A5 已解决。需以实机核对后回填 status。
7. **同一命令两种视觉语言**：评分快捷命令在 macOS/iOS/Android 显示数字（`MacControlBar.cs:80-85` 等），Windows 显示星形（`WindowsControlBar.cs:254-267`）。TOOLBAR-04 允许二选一；是否要求四端统一尚未拍板。
8. **EXIF 设置列表缺「相机厂商」**：`SettingsViewModel.ExifDisplay.cs:37-48` 的 11 项里没有 `CameraMake`，而 `ControlViewModel.FormatExifValue` 支持它（`ControlViewModel.cs:258`）。是漏项还是有意只暴露型号？
9. **触控目标偏小**：评分按钮 30×30（`iOSControlBar.cs:83`）低于 iOS 44 点建议；卡片星级「不放大、由卡片菜单补足」是作者已确认的决定（ui-contract iOS 差异段），控制栏没有等价补偿入口。是否需要为控制栏登记 `TOOLBAR-03@ios` 差异？
10. **滚动位置是否保持未核对**：TOOLBAR-08 写「普通重建不主动回到起点」，但四端都没有专项断言或代码显式保存/恢复控制栏滚动偏移（Mac 重建后依赖 `NSScrollView` 自身不重置，`MacControlBar.Rebuild` 未触碰 `contentView.bounds`）。需要在重建 + 溢出场景实测后确认，或改为明确规则。
