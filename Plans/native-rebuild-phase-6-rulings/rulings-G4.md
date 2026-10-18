# G4 控制栏 / 设置 — 裁定

范围：B12 B14 B15 B25 B26 B27 B35 C11 C12 C13 C14 C27 C28 D07 D10 D12 D27。事实按当前代码核对；与问题原文不符处已在脚注标出。

| Q | 类别(U/P/X) | Avalonia | mac | win | iOS | android | 裁定 | 依据(多数/更优/原版) | 后续(谁改什么) | 需作者 |
|---|---|---|---|---|---|---|---|---|---|---|
| B12 | U | 勾选+名称+主/次录制 | 勾选+名称+主/次录制 | 勾选+名称+主/次录制 | 勾选+名称，无绑定 | 勾选+名称+上下，无绑定 | 移动端必须只读显示主/次绑定与冲突；录制可省；桌面保持录制 | 多数（3/5 显示绑定）+规范 SETTINGS-01 | iOS 行副标题填主/次绑定；Android CheckableRow 副标题同；不补录制 | 否 |
| B14 | U | 无校验，可 0/负 | >0 且 ≤1600 | 1–1600 | >0，无上限 | >0，无上限 | 四端统一 >0 且 ≤1600；非法不写入并恢复原值 | 更优+规范（SETTINGS-02 范围一致；mac/win 已有） | iOS/Android 补上限；spec:88 去掉“桌面端”；Avalonia 随退役 | 否 |
| B15 | P | 有清库（可见 arm） | 系统确认+清库 | 系统确认+清库 | 无 | 无 | 移动端长期不提供清库；配置保留、入口不显示 | 规范（SETTINGS-10、status 已记“不适用”） | 无代码；status/platforms 注明“长期不提供，非临时缺口” | 否 |
| B25 | U | 星级 0.1；命令星形 | Enabled=false；命令数字 | 0.1；命令小星 | Enabled=false；命令数字 | 无禁用态；命令数字 | 禁用统一系统禁用态（降强调不隐藏）；评分快捷命令统一数字，0=叉 | 更优（0.1 近隐藏，违反 TOOLBAR-10）+多数（数字 3:2） | Win 改 Enabled+数字；Android 补评分禁用态；spec:53 改为数字 | 否 |
| B26 | U | tooltip 名称+绑定 | tooltip 名称+绑定 | tooltip 名称+绑定 | 命令仅名称；EXIF 标签+值 | 无无障碍名称 | 不做长按浮层；读屏名称须含命令名+绑定、EXIF 标签+值 | 规范 TOOLBAR-04/05/11+多数（桌面 tooltip） | iOS 命令 label 补绑定；Android 随 C12 建无障碍树 | 否 |
| B27 | P | 30×30 | 30×30 | 30×30 | 30×30（<44 点） | 30×30 | 接受 30×30，不加长按补偿；登记 TOOLBAR-03@ios | 规范（token 30 必须）+原版 | platforms 登记 @ios；可选把命中扩到组高 40，不改绘制 | 否 |
| B35 | P | 数值框/滑条；拖拽 | 步进/滑条；拖拽 | NumberBox/滑条；整行拖 | UIStepper/Slider；系统拖拽 | +/-；上下按钮；无拖入 | 三项均属合法平台差异（控件选型、无桌面拖入） | 规范（SETTINGS-06/15、OPEN-07 允许）+平台控件 | platforms 登记 settings-06/15、open-07@android；无代码 | 否 |
| C11 | X | WrapPanel 无滚动 | 滚动宿主，可滚 | 无滚动宿主 | 未设 ContentSize，实际不可滚 | 自绘受分区裁切 | 单组超长必须可滚；四端都要滚动宿主 | 规范 TOOLBAR-08+mac 唯一正例 | iOS Arrange 后设 ContentSize；Win 包 ScrollViewer；Android 加偏移滚动 | 否 |
| C12 | X | 真实控件树 | NSButton | Button | UIButton 有 label | 单 View 无 a11y | Android 必须暴露虚拟节点：名称/禁用/焦点顺序 | 规范 TOOLBAR-11 | AndroidControlBar 接 ExploreByTouchHelper；名称按 TOOLBAR-11 | 否 |
| C13 | X | — | 已接线 | 已接线 | 已接线 | 代码已接线，文档写待 A5 | 以代码为准改文档；补一次 Android 实机核对 | 代码+Android 计划 #22 已解决 | 改 ui-contract:156、status TOOLBAR-01、toolbar-notes:39 | 否 |
| C14 | X | 无滚动，无偏移 | 宿主自然保持 | 无滚动 | 无 ContentSize，无偏移 | 无滚动 | 规则保留：重建不得重置偏移；溢出平台保存偏移并夹取 | 规范 TOOLBAR-08+mac 行为 | iOS/Win/Android 随 C11 实现并保存；mac 补验收断言 | 否 |
| C27 | X | 失败仅 console；损坏→默认并覆盖 | 同共享 | 同共享 | 同共享（NSUserDefaults） | 同共享（SharedPreferences） | 损坏文档先备份再生效；保存失败给一次可见状态 | 更优（不得破坏文档）+SETTINGS-05 | SettingsService/各存储备份；设置页加状态行；SETTINGS-14 补句 | 是（备份/提示策略） |
| C28 | X | 可删空；重启回默认 | 同共享 | 同共享 | 同共享 | 同共享 | 禁止删空：剩 1 项时删除无效/禁用；旧空文档仍补默认 | 规范 SETTINGS-07 | 共享 RemoveScalePreset 守卫；四端删除按钮禁用 | 否 |
| D07 | P | 固定 #333 深色 | 强制 DarkAqua | 固定 Dark+Mica | 跟随系统 | Material3.Dark 固定 | 维持桌面/Android 固定深色，iOS 跟随系统；登记差异 | 多数（4/5 深色）+原版+SETTINGS-03“可以” | platforms 登记 settings-03@ios；spec 明确默认深色、iOS 例外 | 否 |
| D10 | U | 无按下态 | 无 | 无 | 无 | 无 | 维持不表达（四端已一致）；状态仍由菜单勾选/面板承担 | 多数（全无）+TOOLBAR-12“可以” | 不改；若要做先样机定视觉，再四端同步 | 否 |
| D12 | X | 11 项；Make 死分支 | 同共享 | 同共享 | 同共享 | 同共享 | 保持 11 项（不加相机厂商），删除死分支 | 规范 11 项+多数（四端均 11） | 删 ControlViewModel:258 与 Avalonia 转换器分支 | 否 |
| D27 | X | 3 秒 arm（文案可见） | 系统确认+隐形 arm | 对话框+隐形 arm | 无入口 | 无入口 | 一次系统确认即执行；删除隐形 arm，arm 只留无系统确认的壳 | 规范（二选一）+更优（隐形态不可见） | 共享加“已确认”入口；mac/win 直调；改相关断言 | 否 |

## 脚注（file:line）

**B12**
- Avalonia：`PhotoViewer/Views/Settings/ControlSettingsView.axaml:52-78`（`HotkeyButton` 主/次）
- mac：`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:930-953`
- win：`PhotoViewer.Windows/Native/WindowsSettingsPages.Control.cs:93-128`
- iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1469-1478`（副标题传 null）、`:1773-1869`（单元格）、`:265`（自述“只读查看”）
- android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:319-323`
- 规范：`Design/spec/settings.md:20`

**B14**
- Avalonia：`PhotoViewer/ViewModels/Settings/SettingsViewModel.ImagePreview.cs:92-108`（只判解析）
- mac：`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:364-373`（≤1600）
- win：`PhotoViewer.Windows/Native/WindowsSettingsPages.Preview.cs:88-110`（NumberBox 1–1600，问题原文“未设上限”不成立）
- iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1348-1366`（仅 >0）
- android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:427-440`（仅 >0）
- 规范：`Design/spec/settings.md:88`

**B15**
- Avalonia：`PhotoViewer/Views/Settings/AiSettingsView.axaml:70-91`
- mac：`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:1092`、`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:376-381`
- win：`PhotoViewer.Windows/Native/WindowsSettingsPages.Ai.cs:88-126`
- iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1537-1571`
- android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:350-365`
- 规范/状态：`Design/spec/settings.md:116`、`Design/status.md:178`

**B25**
- Avalonia：`PhotoViewer/Views/Main/ControlView.axaml:56-59`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs:80`（StarOpacity 0.1）、`PhotoViewer/ViewModels/Settings/SettingsViewModel.Hotkeys.cs:75-90`（命令字形）
- mac：`PhotoViewer.Mac/Native/MacControlBar.cs:57-61`、`:80-84`
- win：`PhotoViewer.Windows/Native/WindowsControlBar.cs:319`、`:254-267`、`:273-286`
- iOS：`PhotoViewer.iOS/Native/iOSControlBar.cs:83-87`、`:110-115`
- android：`PhotoViewer.Android/Native/AndroidControlBar.cs:241-255`、`:262-266`、`:293-295`
- 规范：`Design/spec/toolbar.md:53`、`:101`

**B26**
- Avalonia：`PhotoViewer/Views/Main/ControlView.axaml:259`
- mac：`PhotoViewer.Mac/Native/MacControlBar.cs:99-104`
- win：`PhotoViewer.Windows/Native/WindowsControlBar.cs:290-304`
- iOS：`PhotoViewer.iOS/Native/iOSControlBar.cs:107`（命令仅名称）、`:248`（EXIF 标签+值）
- android：`PhotoViewer.Android/Native/AndroidControlBar.cs:228-272`
- 规范：`Design/spec/toolbar.md:52`、`:64`、`:107-110`

**B27**
- iOS：`PhotoViewer.iOS/Native/iOSControlBar.cs:83`
- 令牌/规范：`Design/tokens.json`（`toolbar.rating.button.size`=30）、`Design/spec/toolbar.md:24`

**B35**
- Android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:270-273`（+/-）、`:319-323`（上下）、`:427-440`（预设 +/-）
- 计划：`Plans/native-rebuild-phase-android.md:250`
- 平台表：`Design/platforms.md:24`、`:244`
- 规范：`Design/spec/settings.md:26`、`:156`、`Design/spec/open.md:77`

**C11**
- Avalonia：`PhotoViewer/Views/Main/ControlView.axaml:107-114`（WrapPanel，无 ScrollViewer）
- mac：`PhotoViewer.Mac/Native/MacMainWindow.cs:119`、`:498`
- win：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:537-543`（无 ScrollViewer）
- iOS：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:501-503`（只切换指示器，未设 `ContentSize`；全目录无 `_controlScroll.ContentSize` 赋值；正确样例见 `PhotoViewer.iOS/Native/iOSDinoDiagnosticView.cs:258`）
- android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs:343-344`
- 规范：`Design/spec/toolbar.md:86`

**C12**
- Android：`PhotoViewer.Android/Native/AndroidControlBar.cs:194-304`（OnDraw/OnTouchEvent，无子 View、无 ContentDescription/Focusable）
- 对照：`PhotoViewer.Mac/Native/MacControlBar.cs:57-58`、`PhotoViewer.Windows/Native/WindowsControlBar.cs:273-286`、`PhotoViewer.iOS/Native/iOSControlBar.cs:101-115`、`PhotoViewer/Views/Main/ControlView.axaml:245-267`
- 规范：`Design/spec/toolbar.md:105-110`

**C13**
- `Design/ui-contract.md:156`、`PhotoViewer.Android/Native/AndroidControlBar.cs:290-292`、`Plans/native-rebuild-phase-android.md:251`、`Design/spec/_drafts/toolbar-notes.md:39`

**C14**
- `PhotoViewer.Mac/Native/MacControlBar.cs:91-111`、`PhotoViewer.Mac/Native/MacMainWindow.cs:119`、`:498`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:501-503`、`PhotoViewer.Windows/Native/WindowsMainWindow.cs:537-543`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:343-344`
- 规范：`Design/spec/toolbar.md:87`

**C27**
- `PhotoViewer/ViewModels/Settings/SettingsViewModel.Persistence.cs:39-57`（加载失败→跳过应用，finally 请求保存）、`:60-72`（保存失败仅 console）
- `PhotoViewer/Core/Settings/SettingsService.cs:40-57`、`:107-121`（损坏返回 null）、`:123-149`（保存异常吞掉）
- `PhotoViewer.iOS/Core/iOSSettingsStorage.cs:13-27`、`PhotoViewer.Android/Core/AndroidSettingsStorage.cs:17-40`
- 规范：`Design/spec/settings.md:50`、`:134`、`:149`

**C28**
- `PhotoViewer/ViewModels/Settings/SettingsViewModel.ImagePreview.cs:39-43`（无守卫）
- `PhotoViewer/ViewModels/Settings/SettingsViewModel.Persistence.cs:161-164`（Count>0 才覆盖默认）
- `PhotoViewer/ViewModels/Main/ImageViewModel.cs:780-800`（空表时挡位命令无效）
- 各端删除入口：`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:284`、`PhotoViewer.Windows/Native/WindowsSettingsPages.Preview.cs:114`、`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1280`、`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:408`
- 规范：`Design/spec/settings.md:89`

**D07**
- Avalonia：`PhotoViewer/Views/Settings/SettingsWindow.axaml:14`（固定 #333）
- mac：`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:309`（DarkAqua）
- win：`PhotoViewer.Windows/Native/WindowsSettingsWindow.cs:51`（ElementTheme.Dark）
- iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:96-104`（无 override，跟随系统）
- android：`PhotoViewer.Android/Resources/values/styles.xml:19`、`:29`（Material3.Dark，问题原文“跟随系统”不成立）
- 规范：`Design/spec/settings.md:33`

**D10**
- `PhotoViewer.Mac/Native/MacControlBar.cs:73-79`、`PhotoViewer.Windows/Native/WindowsControlBar.cs:273-286`、`PhotoViewer.iOS/Native/iOSControlBar.cs:101-109`、`PhotoViewer.Android/Native/AndroidControlBar.cs:258-272`
- 规范：`Design/spec/toolbar.md:112-115`

**D12**
- `PhotoViewer/ViewModels/Settings/SettingsViewModel.ExifDisplay.cs:35-48`（11 项）
- `PhotoViewer/ViewModels/Main/ControlViewModel.cs:258`、`PhotoViewer/Views/Main/ControlView.axaml.cs:219`（CameraMake 死分支）
- 规范：`Design/spec/settings.md:102`

**D27**
- 共享 arm：`PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:126-142`
- mac：`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:376-381`、`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:1092`
- win：`PhotoViewer.Windows/Native/WindowsSettingsPages.Ai.cs:115-125`
- 规范：`Design/spec/settings.md:114`

## 裁定要点

- **C11 新事实**：问题只列了 win/android，但 iOS 也没有把控制栏文档尺寸写入 `UIScrollView.ContentSize`，指示器会亮而实际不能滚；`platforms.md:187` 的“@mac/@ios 有溢出滚动”只对 mac 成立。四端都应补滚动宿主，不登记为平台差异。
- **B25 的 0.1 不取原版**：Avalonia/Windows 的 `StarOpacity=0.1` 与 TOOLBAR-10“禁用只降强调，不隐藏”直接冲突；mac/iOS 的系统禁用态是更优解，Windows 与 Android 需补齐；命令字形按 3:2 多数取数字（Windows 的 5 颗 10 号小星在 40 见方内较难辨）。
- **B14 事实修正**：Windows 已在 `NumberBox` 夹取 1–1600，缺上限的是 iOS/Android；按 SETTINGS-02“取值范围四端一致”统一到 >0 且 ≤1600，并把 spec 的“桌面端”限定去掉。
- **D07 事实修正**：Android 设置 BottomSheet 用 `Theme.Material3.Dark`，也是固定深色；实际平台差异只有 iOS 跟随系统。该条属 SETTINGS-03“可以”的外观范围，登记而非改代码。
- **C27 需作者**：是否备份损坏的设置文档、以及保存失败提示放在设置页还是主屏状态条，属于持久化契约与提示位置的取舍，代码无先例；建议“损坏文档备份后再写默认值 + 设置页一行可见状态”，若作者选择静默恢复，则应在 SETTINGS-14 明写“覆盖损坏文档且不提示”。
