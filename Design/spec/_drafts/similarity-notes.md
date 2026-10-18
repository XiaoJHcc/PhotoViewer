# similarity 拆分草稿（临时，不属规范）

来源：`Design/ui-contract.md` §3/§4、`Plans/native-rebuild-phase-2-ui.md`、`Plans/native-rebuild-phase-3.md`、`Plans/native-rebuild-phase-ipad.md`、`Plans/native-rebuild-phase-android.md`、`Plans/native-rebuild-windows-p2.md`、共享 VM 与四端原生面板源码。本文件只记平台事实、验收措辞与待定问题；规则正文在 `Design/spec/similarity.md`。

## platforms

共享编排：`PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs`（可见性判据 :50，三态/按钮文案 :144/:150/:156，点选 :258–265，取消 :281–294，批量提取 :369–404，重算 :422–460，缩略图高优先级 :472–476）；`Core/AI/SimilarityService.cs`（阈值 :29、数量 :32、硬上限 :35、排除同指纹 :58–62,83、并发度 :71–73、阈值过滤 :88–89、降序 + 时间 tiebreaker :103–115、cosine→[0,1] :130）；`PhotoViewer.Shared/SimilarityPanelGeometry.cs`（MinWidth 106 :12、MinHeight 154 :15、Margin 4 :18、Border 1 :20、ListPadding 8 :24、PanelWidth 110 :27、ListCross :30、RowWidth :41–47）。

- **SIM-01 入口与开合**
  - mac：`FilterToggleButton`「相似聚类」，自绘无系统外观（MomentaryPushIn）、勾选态自持，`Toggled` 写共享 `FilterBar.IsSimilarityPanelOpen`（`PhotoViewer.Mac/Native/MacMainWindow.cs:154,159`）。曾因读回 cell State 导致"点不动"，现由类自持状态（`Plans/native-rebuild-phase-3.md:89`）。
  - iOS：`UIButton` 标题式，勾选时系统蓝底白字、可读值「已展开/已收起」，宽 90、高 27（`PhotoViewer.iOS/Native/iOSFilterBar.cs:54,82–88`；共享高度 `PhotoViewer.Shared/MainScreenLayout.cs:35`）。
  - Windows：自绘 `ToggleButton` 90×32、蓝底蓝框勾选态（`PhotoViewer.Windows/Native/WindowsFilterBar.cs:122–140`），写回 `Settings.SimilarityPanelExpanded`（`Plans/native-rebuild-windows-p2.md:426`）。
  - Android：自绘 90dp（手机窄屏 84/76dp 紧凑宽度，`Plans/native-rebuild-phase-android.md:252`），点击写共享状态（`PhotoViewer.Android/Native/AndroidFilterBar.cs:119,154`）。
  - 展开状态持久化于设置 JSON：`SimilarityPanelExpanded`（`PhotoViewer/Core/Settings/SettingsModel.cs:28`，默认 false）；`FilterBarViewModel.IsSimilarityPanelOpen` 是它的代理（`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:72–83`）。
  - 面板可见 = 展开 且 文件栏可见（VM :50）；各壳布局同样以 `filmVisible && IsSimilarityPanelOpen` 判定（`PhotoViewer.Mac/Native/MacMainWindow.cs:437`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:432`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:274`）；Windows 用文件栏整体 Collapsed 间接实现（`PhotoViewer.Windows/Native/WindowsMainWindow.cs:602–606`）。
- **SIM-02 空态**
  - 四端文案与颜色一致（#555）：mac `MacSimilarityPanel.cs:113–124`、iOS `iOSSimilarityPanel.cs:117–126`、Windows `WindowsSimilarityPanel.cs:58–81`、Android `AndroidSimilarityPanel.cs:89–96`。
  - 空态不显示「N 张未提取」，列表态才显示（旧壳 `PhotoViewer/Views/Main/File/SimilarityListView.axaml` 空态栈无该标签）；Android 空态动作按钮在占位文字**下方**（`Plans/native-rebuild-phase-android.md:254`），与旧壳"整体居中"同义但顺序不同。
- **SIM-03 提取/进度**
  - 按钮 90×28、圆角 6、#2A2A2A/#3A3A3A/#555/#CCC（mac `MacSimilarityPanel.cs:366–372,456–540`；Windows `WindowsLayout.cs:281–296`）。
  - Windows 进度条用两条 `Border` 自绘（`Plans/native-rebuild-windows-p2.md:437`）；Android 无进度条，按钮为纯文本 40×28、进度用文本（`PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:33–34`，`Plans/native-rebuild-phase-android.md:254`）→ **SIM-03@android**。
  - 初始总数 `max(1, 未提取数)`，进度事件随后覆盖；总数 0 时不定态（VM :376、:406–413）。
  - 不可中断、无取消入口（VM :369–372 注释「本期不可中断」）；切换文件夹后继续后台跑，完成后才重算（VM :398–403）。
- **SIM-04 分数与柱状条**
  - 百分比 `round(score*100)+"%"`（旧壳 `ThumbnailConverters.cs:69–87`；mac `MacSimilarityPanel.cs:392`；Windows 格式串 `WindowsSimilarityPanel.cs:248`；Android `AndroidSimilarityPanel.cs:203`）。
  - 柱状条下限 75%、宽度 80、底槽 #444、填充 #991e90ff（`ThumbnailConverters.cs:93–114`；`PhotoViewer/Controls/ThumbnailCard.axaml` 第二行）。
  - 卡片复用：iOS/Android 直接复用 `iOSThumbnailCard`/`AndroidThumbnailCard`（自带长按评分菜单，`PhotoViewer.iOS/Native/iOSThumbnailCard.cs:142–144`）；mac 复用 `MacThumbnailItem` + `Bind(selectByDisplayedImage: true)`（`MacSimilarityPanel.cs:596–599`）；Windows `WindowsThumbnailCard(highlightMainImage: true)`（`WindowsSimilarityPanel.cs:244–253`）。
- **SIM-05 点选/双高亮**
  - `SelectItemCommand` → `MainViewModel.SetCurrentImageKeepAnchor`（VM :258–265；`PhotoViewer/ViewModels/Main/MainViewModel.cs:88–93`）：只更新 `IsCurrentImage`，`IsCurrent` 锚点不动（`MainViewModel.cs:54–81` 的抑制位）。
  - 卡片高亮取显示身份在各壳分别实现：mac `selectByDisplayedImage`、iOS 同（`iOSSimilarityPanel.cs` 绑定）、Windows `highlightMainImage`、Android `selectByDisplayedImage: true`。
  - 点选后抑制一次面板重算，避免列表重排（VM :36–41、:196–201）。
- **SIM-06/08 计算与结果**
  - 面板不可见不计算；打开即算（VM :240–247、:271–276）；阈值/数量变化 150ms 节流后重算（VM :212–217）；候选池 = `FilteredFiles`（VM :208–210、:437）。
  - 阈值 0.75–0.95（默认 0.85）、数量 1–32（默认 8）（`PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:17–26,28,42`；`SettingsModel.cs:31,34`）；服务侧硬上限 64（`SimilarityService.cs:35`）。
  - 候选缺特征不推理（`SimilarityService.cs:21–22` 类注释、:41–43 方法注释、:83–92 只读库）。
  - 大库并发曾压垮连接池/线程池，已改 `Parallel.ForEachAsync` 封顶（`Plans/native-rebuild-windows-p2.md:474`）。
- **SIM-07 几何**
  - 行布局宽度公式在 mac/iOS/Android 使用共享 `RowWidth`（`MacMainWindow.cs:523–526`、`iOSNativeRootViewController.cs:434–437`、`AndroidNativeRootView.cs:276–280`）；相似项出现后必须重排（iPad 缺陷记录 `Plans/native-rebuild-phase-ipad.md:143`，现由订阅 `SimilarItems`/`HasItems` 实现 `iOSNativeRootViewController.cs:908–912`）。
  - 列布局：文件栏加宽（`MainScreenLayout.FileBarMainLength` 的 extraCross），面板贴 116 栏右侧（`MacMainWindow.cs:487–488`、`iOSNativeRootViewController.cs:484–488`、`AndroidNativeRootView.cs:333–335`）。
  - **Windows 未见共享 `RowWidth`**：面板是文件栏 Grid 的 Auto 列（`WindowsMainWindow.cs:331–337`），只有 `MinWidth 106`（`WindowsSimilarityPanel.cs:118`），行布局 `VerticalAlignment=Top`（:168）→ 行布局面板宽是否被夹到"可用宽一半"未验证（见 questions 2）。
  - 拖动区不得盖住 toggle（mac 曾因拖动区右段起点取计数右边缘导致 toggle 时灵时不灵，`Plans/native-rebuild-phase-3.md:88`；`MacMainWindow.cs:505`）。
- **SIM-10/11**
  - 错误标签只挂在空态栈：mac `MacSimilarityPanel.cs:124`、iOS `iOSSimilarityPanel.cs:126`、Windows `WindowsSimilarityPanel.cs:81`、Android `AndroidSimilarityPanel.cs:96` → 列表态下「补齐全部」失败无可见错误（见 questions 1）。
  - 面板列表滚轮/触控板：旧壳横向列表用 `HorizontalScrollWheelBehavior`（`SimilarityListView.axaml`），与 FILM-04 同口径。

## status

按 `Design/ui-contract.md` §4 SIM-01 行（:154）与各阶段清单原文抄录；新拆出的 SIM-02…SIM-11 尚无独立状态记录。

- **macOS**：SIM-01「本轮实机程序化验证（toggle 双向一致 / 两向让位几何 / 列表与空态互斥 / 收起还原）；作者真机使用后反馈的两处问题（点选未双高亮、toggle 闪烁与"点不动"）已修并补真实点击断言，修复后未见新反馈」（2026-10-16/17）。阶段记录：`--pv-native-similarity` PASS，清单含 toggle 双向与真实点击、两向让位与收起还原、MinWidth 106、顶边拖动区与 toggle 不相交（`Plans/native-rebuild-phase-3.md:73,86–89`）；2026-10-18 首图同名伴侣修复后「Mac base/filmstrip/similarity PASS」（`Plans/native-rebuild-phase-ipad.md:190`）。
- **iOS/iPadOS**：SIM-01「模拟器两机型 + 真机程序化通过（2026-10-17，`--pv-native-similarity-check`：toggle 双向与真实点击、两向让位/收起还原、MinWidth 106、空态/列表态与共享 VM、卡片分数条、真实"提取全部"、相似卡片点选双高亮且锚点保留）；触摸手感待作者真机」。执行清单：模拟器相似检查 10 项、真机 10 项（`Plans/native-rebuild-phase-ipad.md:141,173`）。
- **Windows**：SIM-01「`--pv-native-smoke`（150%）：相似聚类 toggle → 空态 → 真实「提取全部」→ 分数列表 → 点选切主图且不重锚，程序化通过；逐项视觉与鼠标手感待作者手验」。阶段记录同项（`Plans/native-rebuild-windows-p2.md:441`）。
- **Android**：SIM-01「2026-10-18 模拟器目视：toggle 真实点击、空态/未提取/真实"提取全部"后 100% 分数卡片与柱状条、行布局让位均可见；程序化断言待 A6」；A6 起步后 `--es pv-check similarity` 在 API 35 arm64 模拟器 **PASS 15**（`Plans/native-rebuild-phase-android.md:11`），矩阵（API 24/28/33/36、两向、fontScale、DPI）与真机门未做。

## questions

1. **列表态的错误提示没有承载位置**：四端都把错误标签放进"空态栈"（`MacSimilarityPanel.cs:124`、`iOSSimilarityPanel.cs:126`、`WindowsSimilarityPanel.cs:81`、`AndroidSimilarityPanel.cs:96`），而「补齐全部」只在已有列表时可用——失败必然发生在列表态，此时错误不可见，与 `Design/ui-contract.md:154` 提到的"错误文案"及"失败必须可见"（PRIN-02）冲突。spec 已按"错误必须与当前状态同时可见"写（SIM-10），四端需补列表态承载。
2. **Windows 行布局面板宽度未按公式夹取**：`WindowsSimilarityPanel.cs:118` 只有 MinWidth 106，`WindowsMainWindow.cs:337` 面板在文件栏 Auto 列，未调用共享 `SimilarityPanelGeometry.RowWidth`；行布局下列表较长时面板可能超过"可用宽一半"、挤压胶片栏。是缺口还是另有约束（如 ScrollViewer 量测）未能确认，需实机核对后补 `SIM-07@win` 差异或改实现。
3. **柱状条下限 75% 与可调阈值冲突**：阈值可调到 75%（`SettingsViewModel.AI.cs:17`），此时所有项柱长都为 0（`ThumbnailConverters.cs:98`），柱状条失去信息；而阈值 0.95 时柱条最多只到宽度的 80%。柱条下限是否应跟随阈值（或改为绝对百分比宽度）需要作者决定。
4. **批量提取不可中断**：`SimilarityPanelViewModel.cs:369–372` 明确"本期不可中断"，无取消入口；大文件夹（数千张）期间按钮退化为进度、用户无法停止，且切换文件夹后仍在后台跑（:398–403）。是否新增取消/暂停入口（与 PRIN-04 的关系）未决定，spec 暂未写取消。
5. **面板关闭与重开时锚点跳变**：用户从相似面板跳到某张相似项后关闭再打开，`OnPanelOpened` 把锚点重置为当前照片（VM :271–276），面板整列表随之换成新锚点的邻居。这是"以主图为锚"的自然结果，但用户可能期望"面板记住浏览起点"。需要确认语义（spec 现按重锚写）。
6. **提取中空态文案与进度并存**：未提取计数只在提取完成后重算（VM :398–400），所以空态在提取全程同时显示「尚未提取特征」和进度条；spec 只写"按钮由进度取代"，未禁止文案共存。是否要在提取中隐藏占位文字待定。
7. **文件栏隐藏时 toggle 仍可点亮**（`MacMainWindow.cs:437`）：面板不显示、无提示，恢复文件栏后突然展开。是否需要在文件栏隐藏时禁用/置灰 toggle 未定；spec 现按"勾选态保留"写。
8. **相似度语义未在界面说明**：分数是 cosine 线性映射到 [0,1]（`SimilarityService.cs:142`），阈值 85% 实际是 cosine ≈ 0.70；设置页文案只写"相似度"。是否需要在设置里说明映射、或直接用 cosine 显示，未定。
9. **Android 动作按钮与进度形态偏离**：`AndroidSimilarityPanel.cs:33–34` 为 40×28 纯文本按钮、进度仅文本，无 90×28 按钮外观与不定态进度条；空态动作按钮位于占位下方（`Plans/native-rebuild-phase-android.md:254`）。属功能等价还是需对齐，待作者裁定后决定登记 `SIM-03@android` / `SIM-02@android`。
10. **面板卡片触屏评分入口**：iOS 面板复用自带长按评分菜单的卡片（`iOSThumbnailCard.cs:142–144`），Android 面板卡片是否挂长按菜单未验证（`AndroidSimilarityPanel.cs:200–205` 只传评分回调）；星级几何 14×14 低于 44 点建议（FILM-05 已登记），面板内是否同样由菜单补足需实机确认。
11. **行布局面板与胶片栏的滚动位置**：面板展开时胶片栏变窄，各壳只把偏移夹到合法范围（`MacSimilarityPanel`/`MainScreenLayout` 相关布局），但共享层没有"因面板开合而重新定位"的显式请求；spec 按 FILM 口径写"只夹取，不居中"，与 Mac/iOS 现状一致，Android 窄屏（面板占半宽）可能表现为当前卡片跑出视口，需实机核对。
