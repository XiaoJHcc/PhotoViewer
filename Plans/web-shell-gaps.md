# Web 参考壳 — 设计文档缺口台账

> 配套 [web-shell-plan-1.md](web-shell-plan-1.md) §2。Web 壳在只读 `Design/` 的条件下独立实现，再由审计子代理逐条对照 macOS 原生壳与共享 C#。下表是全部差异与裁定；"落点"写明改了哪份文档或哪一端。规范正文已按"文档缺口 / 文档错误"两类修订，平台差异登记在 `Design/platforms.md` 的 `@web` 条目，Mac 缺口记入 `Design/status.md`。

分类：`文档缺口`（没写或写不清）· `文档错误`（与作者认可行为冲突）· `Mac 缺口`（Mac 偏离规则）· `web 差异`（浏览器约束，登记 `@web`）· `web 缺陷`（Web 没照文档做）。

## 结论

- 46 条差异：文档缺口 23、文档错误 6、Mac 缺口 5、web 缺陷 7、web 差异 4、无需处理 1。
- 只依据文档写出的几何与 macOS 9 布局 363 节点比对，唯一差异是计数文本宽（缺字号令牌，G11；固定为 Mac 实测宽后 0 差异）；卡片内部 12 个节点虽逐点一致，却是"猜对"的（G1）。行为层面，文档最大的盲区是相似面板（分数口径、行布局宽度、提取范围）、提示条文案与 EXIF 格式。
- 本轮修订：新增令牌 19 个（含 `sim.dock.fontSize`），`sim.list.cross` 161 → 159；修订 spec 01-regions、FILTER-01/02/03/04/06、FILM-01/02/05、IMAGE-03/05/06、ANALYSIS-04/06/09/10、SIM-02/06/07/08、TOOLBAR-04/05、INPUT-04/07、OPEN-02/03、STATE-01/02/03、README §8、architecture §5.1。

## 台账

| # | ID | 现象 | 分类 | 裁定与落点 |
|---|---|---|---|---|
| G1 | FILM-05 | 卡片内部几何只存在于 Mac 验收常量（`MacThumbnailItem.cs:65-99`） | 文档缺口 | ✅ tokens 补 `film.card.thumb.inset/name.y/name.height/secondLine.height/star.y/badge.inset`；FILM-05 增内部节点表 |
| G2 | FILM-05 | 第二行日期显示格式未定义；无日期时 Mac 显示空串 | 文档缺口 | ✅ FILM-05：`MM-dd HH:mm`，缺失回退文件时间；Mac 无日期占位记缺口 |
| G3 | OPEN-02/03、STATE-02 | "文件栏标题/文件夹名"在主屏不存在 | 文档错误 | ✅ 改为窗口标题元数据，主屏不显示 |
| G4 | FILM-05、STATE-02 | Mac 空列表无「无匹配照片」 | Mac 缺口 | ✅ status.md FILM-05@mac 记缺口 |
| G5 | FILTER-06 | 相似聚类开关四态视觉无规则与令牌 | 文档缺口 | ✅ FILTER-06 增四态；tokens 补 `color.filter.toggle.hover.border/foreground`、`checked.hover.*` |
| G6 | TOOLBAR-08 | Mac 控制栏在 legacy 滚动条偏好下常驻 17 点竖向槽 | Mac 缺口 | ✅ status.md TOOLBAR-08@mac 记缺口；待 Mac 锁 Overlay 后重出几何报告 |
| G7 | INPUT-04 | 浏览器无精确/离散标志，捏合以 ctrl+wheel 投递 | web 差异 | ✅ `INPUT-04@web` |
| G8 | INPUT-01 | 浏览器保留 Ctrl+N | web 差异 | ✅ `INPUT-01@web` |
| G9 | MENU-01、SCOPE-01 | 无设置/工具窗口的壳如何处置菜单项 | 文档缺口 | ✅ `MENU-01@web`、`SCOPE-01@web`、`TOOLBAR-06@web` |
| G10 | SHELL-03 | 浏览器视口可小于最小窗口 | 文档缺口 | ✅ `SHELL-03@web` |
| G11 | FILTER-04 | 计数无字号令牌 | 文档缺口 | ✅ tokens `filter.count.fontSize`=12；FILTER-04 写自然宽度 |
| G12 | 01-regions、FILTER-01 | 行布局示意把筛选条画在胶片栏左侧 | 文档错误 | ✅ 示意改为筛选条整行在上；FILTER-01、01-regions 表同步 |
| G13 | FILM-02 | 短列表居中只写在令牌描述里 | 文档缺口 | ✅ FILM-02 写内容长度与横向整段居中、纵向顶边起排 |
| G14 | FILTER-02/03 | 合并显示名格式、星级与大小取值未写 | 文档缺口 | ✅ FILTER-02/03 写 `名(RAW/HEIF)`、取代表文件 |
| G15 | IMAGE-05 | "边缘最多到视口中心"与钳制公式矛盾；"相对缩放不变"与"倍率不变"矛盾 | 文档错误 | ✅ 改为边缘最多到视口边缘；尺寸变化保持物理倍率与中心点 |
| G16 | IMAGE-06 | 低于适应时导航图是否显示未写 | 文档缺口 | ✅ IMAGE-06：显示，可见框整幅 |
| G17 | INPUT-07 | 长按参数无令牌 | 文档缺口 | ✅ tokens `input.longPress.delay`=500 ms、`slop`=8；INPUT-07 写判定 |
| G18 | ANALYSIS-09/10 | "回退期间显示计算中"与"切图保留上一张"冲突 | 文档错误 | ✅ 按路径区分：只读库保留旧瓦片，现算路径显示计算中 |
| G19 | ANALYSIS-04/06 | 对焦框与裁剪窗口是两个矩形 | 文档缺口 | ✅ ANALYSIS-04 写明独立 |
| G20 | SIM-02/03 | 提取范围"整个文件夹"与 Web 按筛选池不符；未提取计数口径未写 | web 缺陷 + 文档缺口 | ✅ SIM-02：未提取按全文件夹统计；Web 改全文件夹提取 |
| G21 | SIM-07 | 行布局宽度未计停靠区，1–2 项时列表被挤窄；交叉轴 161 > 胶片带 159；节流 150 ms 无令牌 | 文档错误 | ✅ SIM-07 宽度公式加停靠区（tokens `sim.dock.margin`=3）；`sim.list.cross`=159；`motion.sim.params.throttle`=150；Mac 记缺口 |
| G22 | IMAGE-03、architecture §5.1 | 代理失败回退 Full 的说法看似与 STATE-02 冲突 | 文档缺口 | ✅ 实为解码器内部回退，两档都失败才报错；IMAGE-03 与 architecture 写清 |
| G23 | README §8 | 几何节点命名未写 | 文档缺口 | ✅ README §8 补命名规则 |
| G24 | TOOLBAR-05 | 快门中间值与 1 s 格式未定义（Mac 1 s 输出 `1/1`） | 文档缺口 | ✅ TOOLBAR-05 写完整规则；Mac 记待核验 |
| G25 | FILM-01、SIM-05 | 相似点选后上一张/下一张的参照、Exchange 是否更新锚点 | 文档缺口 | ✅ FILM-01 写参照规则与 Exchange 更新锚点 |
| G26 | SETTINGS-06 | 缓存数量默认值未写（Mac 默认 30，低内存设备取小） | 文档缺口 | 待作者确认后写入 SETTINGS-06（不影响主屏验收） |
| G27 | IMAGE-08 | 合成素材方向 5–8 画面侧躺 | 无需处理 | 正确演示形态 |
| G28 | SIM-08 | 分数尺子未定义：Mac 用 `(cos+1)/2`，Web 用原始 cos | 文档缺口 | ✅ SIM-08 写归一公式；Web 对齐 |
| G29 | SIM-07 | 列布局面板外边距与纵向起点 | web 缺陷 + 文档缺口 | ✅ SIM-07 写与胶片栏同顶、右留 4；Web 对齐 |
| G30 | IMAGE-03 | Web 适应态因小图倍率 ≥100% 主动升 Full | web 缺陷 | ✅ IMAGE-03 写"非适应态"前提；Web 修 |
| G31 | INPUT-04 | 精确滚动带修饰键时 Mac 按绑定执行，Web 一律平移 | 文档缺口 | ✅ INPUT-04 写修饰键规则；Web 修 |
| G32 | ANALYSIS-06 | Mac 细节裁剪按逻辑点取像素，Retina 非物理 1:1 | Mac 缺口 | ✅ ANALYSIS-06 写物理倍率；status.md 记缺口 |
| G33 | ANALYSIS-09 | Web 回退未等缩略图通道空闲 | web 缺陷 | ✅ Web 修 |
| G34 | SIM-06 | Web 首轮计算中面板留白 | web 缺陷 | ✅ SIM-06 补"不留白"；Web 修 |
| G35 | FILM-05 | 第二行颜色/字重与卡片清零叉字号无令牌，Web 取错 | web 缺陷 + 文档缺口 | ✅ FILM-05 写颜色与粗体；tokens `film.card.clearGlyph.size`=8；Web 修 |
| G36 | TOOLBAR-03 | Mac 空心星与清零叉用系统次级色 | Mac 缺口 | ✅ status.md 记缺口 |
| G37 | TOOLBAR-04 | Mac 评分快捷命令按钮带星形图标 | Mac 缺口 | ✅ status.md 记缺口 |
| G38 | STATE-03 | 失败文案模板四端不一 | 文档缺口 | ✅ STATE-03 增文案表；Web 对齐，四端待实现 |
| G39 | MENU-05 | Web 触屏卡片长按菜单未接线 | web 缺陷 | ✅ `MENU-05@web` 登记形态；Web 接线 |
| G40 | platforms.md | 声明了 web 代号但无条目 | 文档缺口 | ✅ platforms.md 增 Web 说明与 `@web` 条目 |
| G41 | FILTER-07、SHELL-02 | Web 无交通灯与拖动区 | web 差异 | ✅ `FILTER-07@web`、`SHELL-02@web` |
| G42 | STATE-01 | 空态提示块垂直基准与左右留白未定义 | 文档缺口 | ✅ STATE-01 写整块居中；tokens `image.hint.inset`=10 |
| G43 | TOOLBAR-05 | 光圈/焦距小数位与舍入未定义 | 文档缺口 | ✅ 并入 G24 规则 |
| G44 | TOOLBAR-05 | 拍摄时间摘要格式未定义 | 文档缺口 | ✅ `yyyy-MM-dd HH:mm` |
| G45 | SETTINGS-14 | Web 设置文档字段与四端不互通 | web 差异 | ✅ `SETTINGS-14@web` |
| G46 | TOOLBAR-04 | tooltip 分隔符与重复绑定处理未定义 | 文档缺口 | ✅ TOOLBAR-04：`命令名 · 主 / 次`，重复只显示一次 |

## 作者验收返工（R 轮）

| # | ID | 现象 | 分类 | 裁定与落点 |
|---|---|---|---|---|
| R1 | FILTER-08、MENU-03、TOOLBAR-04 | Web 下拉、右键菜单与 tooltip 仿 macOS 原生，属 Mac 平台差异，不应作为 Web 基准 | 文档缺口 | ✅ README §3：无原生控件的平台以旧壳 Avalonia Fluent 深色外观为准；考古补 `dropdown.*`/`popup.*`/`menu.*`/`tooltip.*`；Web 改为自绘 Fluent 外观并自绘 tooltip |
| R2 | TOOLBAR-04、FILM-05 | 图标无统一体系、尺寸缺定义，Web 仿 SF Symbols | 文档缺口 | ✅ `icon.font`/`icon.fontSolid` + `icon.*` 码位（取自旧壳命令表），各控件 glyph 字号令牌；平台系统图标集登记差异；Web 改用 uicons 子集（`tools/subset-icons.py`） |
| R3 | TOOLBAR-04 | 评分快捷命令标准：星形内叠数字（原规则"只画数字"） | 文档错误 | ✅ TOOLBAR-04 改为实心星内叠数字（`toolbar.rating.digit.fontSize`）；macOS、Web 已实现，iOS/Windows/Android 登记差异 |
| R4 | FILM-05、STATE-02 | 列表为空不应显示「无匹配照片」 | 文档错误 | ✅ 规则改为留空；Web 去掉，Windows 待实现 |
| R5 | INPUT-03/10 | Mac 空态点击任何地方（含交通灯）都打开选择器 | Mac 缺口 | ✅ 规则写明只有主图分区响应；Mac 修复并加反例断言 |
| R6 | IMAGE-06 | fix/windows-main-controls 分支把导航图白框可拖动改为四端必须 | 审查通过 | ✅ 保留；修 IMAGE-02→IMAGE-05 引用与过期平台差异；iOS 点按/拖动判别、iOS/Android 拖出缩略图中断的问题已修；Web 补实现 |
| R7 | TOOLBAR-05、FILTER-06、ANALYSIS-08 | 分支改动：EXIF 加粗、相似开关勾选悬停文字 #FFFFFF、直方图无准星 | 审查裁定 | ✅ 基准恢复半粗、加粗登记 TOOLBAR-05@win；勾选悬停文字取旧壳 #CFE0F5；直方图无准星作为四端一致规则 |
| R8 | TOOLBAR-08、ANALYSIS-06、TOOLBAR-03、SIM-07 | 待修的 Mac 缺口（G6、G32、G36、G21） | Mac 缺口 | ✅ 已修并加断言；layout 验收需大图素材（现有 1200×800 夹具让导航图拖动断言无处可平移，既有问题） |
| R9 | TOOLBAR-04、FILM-05、FILTER-08、MENU-03、SHELL-06 | 图标字形进入 `role=application` 容器的可读文本，纯字形控件名称不稳定 | web 缺陷 | ✅ 字形统一放在单独的 `aria-hidden="true"` span，可访问名称由 `aria-label` 提供；`?check=shell` 增 SHELL-06，遍历窗口文本节点断言私有区字符不落在 aria-hidden 之外 |
| R10 | TOOLBAR-04、MENU-03、FILM-05 | tooltip 偏移与连续划过延迟、菜单勾字号与最小宽、缩略图占位字形字号硬编码在 Web | 文档缺口 | ✅ tokens 补 `tooltip.offset.x/y`、`motion.tooltip.chainDelay`、`menu.check.fontSize`、`menu.minWidth`、`film.card.placeholderGlyph.fontSize`；TOOLBAR-04、MENU-03、FILM-05 引用令牌；Web 改用令牌并重新生成产物 |
| R11 | IMAGE-06 | 导航图接管第一指，双指捏合起手失败 | 文档错误 | ✅ IMAGE-06 写明拖动中出现第二指即让位给主图捏合；Web 手势统一由 `PhotoGesture` 判定，单测与 IMAGE-06 页内断言覆盖 |

| R12 | TOOLBAR-04、FILM-05、SIM-07、STATE-03、TOOLBAR-05 | R3/R4/R8 与 G24/G38 修订后三端未跟进 | 平台缺口 | ✅ 共享层新增 `ExifDisplayFormat`（TOOLBAR-05）与 `StatusMessages`（STATE-03），四端只显示；iOS/Windows/Android 评分快捷命令星内叠数字、`RowWidth` 传入停靠区宽，Windows 去掉「无匹配照片」与增强开关提示；Mac layout 导航图拖动断言先放大到 4 倍，默认夹具即可运行。Windows 只过类型检查 |

## 后续（不在本计划内）

- Windows 本机复核 smoke（FILM-05 空列表、评分快捷命令星内数字居中、行布局相似面板停靠区）。
- 统一图标字体是否推到 macOS/iOS/Windows 由作者决定（当前登记为平台差异）。
- G26 缓存数量默认值待作者确认。
