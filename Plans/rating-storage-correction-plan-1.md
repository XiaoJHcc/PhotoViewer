# 评分存储纠正：全来源统一写入

> 2026-10-25 立项。取代 [core-legacy-rebuild-plan-1](core-legacy-rebuild-plan-1.md) 中"专有 RAW 只写 `<stem>.xmp`、sidecar 唯一权威"的旧口径；旧计划保留历史，不再作为评分实现依据。规范正文见 [Design/spec/rating.md](../Design/spec/rating.md)（RATING-01–06），状态见 [Design/status.md](../Design/status.md#rating)。本文件只登记实施清单、验收待办与挂账，不是设计真源。

## 0. 产品口径

1. HIF / ARW / JPG 等各文件的内嵌 Rating 与每个**实际已有**的 `<stem>.xmp` 都是独立来源；读取与写入都没有优先级。
2. sidecar 不存在时**绝不创建**；存在时必须同步。
3. RAW 必须写内嵌，保证 Sony 相机与 Imaging Edge 可读；只写 sidecar 不算成功。
4. 所有来源统一写：先全部预检；安全模式下失败按逆序回滚；不承诺跨文件断电原子；补偿不完整时 `Partial` 按磁盘真值显示冲突。
5. 安全写入按结构定位、最小区间与等长包长写，用合法 XML 空白吸收 `-1` 的位数差；无法证明非目标数据安全时为 `Unsupported`；不允许启发式全文定位。
6. 数字与"以上"筛选仍按代表文件内嵌 Rating；`Conflict` 独立；新增"已拒绝"按代表 -1；筛选项从 12 变 13；-1 的星级 UI 挂账。
7. 冲突时四端胶片栏与控制栏 0 号位置把清零叉换成红色警示三角，几何与命中区不变；未实现浮窗的平台点击三角不动作、不写值。来源浮窗以 macOS 版本为设计基准：按来源纵向排列，宽度随星级控件；信息行默认单行（文件名左、来源标签 + 状态右并垂直居中），长文件名改双行，条目间加分隔线；每来源一行 0–5，任一条目点值都统一写全部来源，只有写入成功且刷新后冲突解除才关闭。横排入口默认向下、底部横排入口向上、右侧竖排入口向左。
8. 所有显式打开（文件、文件夹、拖入、系统外部激活/命令行）都重新枚举目标父目录/目录，并完整刷新新增、删除、全部 Rating 来源、sidecar 存在性与内容、基本属性和 EXIF；当前目标保留筛选、排序、滚动、面板与视口，新目标按 OPEN-05 Fit；单图权限不足只刷新可访问项并明示边界；新增删除不生成重复卡片。
9. 训练侧实际选用文件内部为 `Conflict` / `Invalid` 时 Rating 不落快照，不任选值。
10. 旧设置明确不迁移，不写迁移补丁。
11. 本轮先保证真实 HIF / ARW 与已有 sidecar；复杂容器结构挂账。手测是后续独立任务，不纳入本轮自动完成条件。

## 1. 设计落点

| 位置 | 内容 |
|---|---|
| [spec/rating.md](../Design/spec/rating.md) | RATING-01–06 新条目（来源、安全写、事务、冲突浮窗、状态/训练、支持边界） |
| [Design/README.md](../Design/README.md) | 登记 `RATING` 前缀 |
| [Design/architecture.md](../Design/architecture.md) §5.3、§5.2 | 删除 RAW 只写 sidecar / sidecar 唯一权威；改为全来源枚举、预检、顺序写、逆序补偿；所有显式打开完整刷新 |
| [spec/film.md](../Design/spec/film.md) / [toolbar.md](../Design/spec/toolbar.md) | 冲突三角、来源浮窗入口与全来源写回引用；-1 显示口径 |
| [spec/filter.md](../Design/spec/filter.md) | 13 项、已拒绝、冲突独立、刷新时机 |
| [spec/state.md](../Design/spec/state.md) | 部分来源失败文案与真实状态展示 |
| [spec/settings.md](../Design/spec/settings.md) | 安全模式的全来源预检语义；旧设置不迁移不补丁 |
| [spec/open.md](../Design/spec/open.md) | 所有显式打开的完整刷新统一在 OPEN-08；当前目标保留视口、新目标 Fit |
| [Design/platforms.md](../Design/platforms.md) / [status.md](../Design/status.md) | L4 定位/写入入口与字形；各端实现状态只记在 status |
| [Design/tokens.json](../Design/tokens.json) | `color.star.conflict`、`icon.warning` |

## 2. 实施清单

### 2.1 来源模型与读取

- [x] 组内来源枚举：每个参与文件的内嵌 XMP + 每个实际存在的 `<stem>.xmp`；不以扩展名判断 sidecar 该不该存在。
- [x] 去掉"专有 RAW 优先 sidecar / sidecar 唯一权威"分支（`XmpRatingReader` 的 sidecar 优先与 `ExifLoader` 的 RAW 读取口子），所有来源并行读取。
- [x] 来源级状态（缺失 / 已评分 / 已拒绝 / 无效 / 冲突 / 不可读）与组级一致性判定；代表值只用于显示与数字筛选。
- [x] 所有显式打开刷新时重新读取各来源与 sidecar 是否存在（OPEN-08）。

### 2.2 写入定位与安全写

- [x] 逐来源生成写入计划：容器适配器按结构定位 canonical XMP 与 Rating 值区间；移除任何正则/全文搜索定位。
- [x] 样本核对：`TestCase/A7C01489.ARW`、`TestCase/A7C01495.ARW` 的 IFD0 均含 tag 700（XMP），真实 ARW 走现有条目即可，不需要新增结构。
- [x] 最小写入区间；等长优先；`-1` 与 0–5 互换用合法 XML 空白吸收位数差。
- [x] ARW / TIFF 改写已有 IFD0 tag 700 内嵌；HEIF 家族改写已有标准 `mime` XMP 项；已有 sidecar 同步写。
- [x] 写后回读 `xap:Rating` 与容器记账字段（段长、RIFF、tag count/offset、`iloc`）。
- [x] 无 canonical XMP 包（含 JPEG 无 APP1）不新建或插入容器结构，按 `Unsupported` 在首次修改前失败；已有 canonical 包但缺 Rating 时，仅在包结构唯一且空白足够时于包内插入 `xap:Rating`，否则同样不支持。不建 sidecar、不用 sidecar 顶替。
- [ ] PNG 写入（含标准 `iTXt` 与容器级 CRC 事务）：整体挂账，当前 `Unsupported`。

### 2.3 事务

- [x] 全来源预检（结构、长度策略、能力、可写性、安全模式字节/修订）。
- [x] 固定顺序写入（代表内嵌 → 其余内嵌 → 已有 sidecar）；失败按安全模式逆序补偿。
- [x] 结果 `Applied` / `NoChange` / `Superseded` / `RolledBack` / `Partial` / `Canceled`；`Applied` 与 `NoChange` 都按成功处理；`Partial` 逐来源重读并呈现真实冲突。
- [x] 保留同组串行、latest-wins 与"物理写入开始后不中途取消"。
- [x] 全部来源已是目标值时返回 `NoChange`：不写盘、不创建 sidecar、不增长文件。

### 2.4 冲突指示与来源浮窗

- [x] 四端胶片栏卡片与控制栏 0 号槽位：冲突时清零叉换 `icon.warning` + `color.star.conflict`，几何与命中区不变；未实现浮窗的平台点击三角不动作、不写值。
- [x] 规范：来源纵向排列；信息行默认单行（文件名居左、来源标签 + 状态居右并垂直居中），长文件名改双行；每来源一行 0–5，条目间加分隔线；浮窗宽度由星排与固定内边距决定、不收窄，任一条目点值都整组统一写入。
- [x] 规范：横排入口默认向下、底部横排入口向上、右侧竖排入口向左；刷新覆盖全部条目；只有成功且刷新后冲突解除才关闭，锚点消失/布局重排/入口滚出可视区也关闭，刷新失败不因此关闭。
- [x] Mac：以 `NSPopover` 实现纵向条目与三种方向；宽度、三信息行/长名双行、条目分隔线、原生毛玻璃与星按钮 hover（修订前实现）；toolbar 真实窗口回归通过，作者验收通过（不冒充修订后验收）。
- [ ] iOS / iPadOS：按 RATING-04 实现纵向来源浮窗与整组写入。
- [ ] Windows / Android / Web：按同一规范实现来源浮窗，并确定各端原生弹层细节。
- [x] 四端字形：Apple SF Symbols、Windows `E7BA`、Android / Web `icon.warning`。

### 2.5 筛选与训练

- [x] 筛选项 12 → 13，新增"已拒绝"（代表内嵌 -1）；"星级冲突"口径改为全来源一致性。
- [x] 数字与"以上"档明确按代表内嵌 Rating；代表为 -1 只命中"已拒绝"；无效/不可读不命中数字档。
- [x] -1 不归一化为 0；UI 不提供 -1 星级入口。
- [x] `Training/DatasetBuilder`：只取实际选用文件的内部 Rating；内部 `Conflict` / `Invalid` 时不落快照；去掉组内 `Max` 取值的旧口径。

### 2.6 打开与刷新

- [x] 所有显式打开（文件 / 文件夹 / 拖入 / 外部激活 / 命令行）都重新枚举目标父目录或目录，刷新新增、删除、全部 Rating 来源、sidecar 存在性与内容、基本属性与 EXIF。
- [x] 当前目标保留视口，新目标按 OPEN-05 Fit；滚动 / 筛选 / 面板保持，不生成重复卡片，当前照片不在新列表时按 OPEN-04 取首项。
- [x] 单图权限不足只刷新可访问项，并在提示条明示「无法读取父目录，仅刷新当前照片」；刷新失败保留旧列表与当前照片。

### 2.7 设置与迁移

- [x] 安全模式语义对齐 RATING-02/03：始终执行结构/实际可写性预检、revision 与编辑区间复核、写后校验；开启时额外比较整个原始包并逆序补偿，关闭时不回滚此前成功写入的来源。
- [x] 旧设置不迁移、不写字段级迁移补丁；沿用备份后默认值启动的现有路径。

### 2.8 令牌与生成物

- [x] `Design/tokens.json` 增加 `color.star.conflict`（#FF453A）与 `icon.warning`（U+FE33），描述关联 RATING / FILM / TOOLBAR。
- [x] `python3 Tools/generate-design-tokens.py` 生成 `DesignTokens.g.cs`（303 个常量）与 `PhotoViewer.Web/src/tokens.g.js`。
- [x] 四端与 Web 接入新令牌（随 2.4 实现）。

## 3. 验收待办

程序化（随实现）：

- [x] 来源枚举：`CoreContractCheck` 635 PASS / 1 SKIP / 0 FAIL；真实 HIF/ARW 副本内嵌与已有 sidecar 独立枚举，2→-1→3 全 `Applied`；无 sidecar 组成功且不创建。
- [x] 写入：ARW / HEIF 内嵌、已有 sidecar 同步；无 sidecar 不创建；同值 `NoChange` 不写盘；真实副本三来源 2→-1→3 一致，无 sidecar 组写 4 一致。
- [x] 事务：CoreContractCheck；Mac faults PASS；iOS faults 6；Android `storage` 24/0、`faults` 22/0。
- [x] Mac UI：toolbar 真实窗口回归连续两轮通过，覆盖纵向来源条目、三种实际方向、各入口连续重开三次且每次停留 1.3 秒、原生内容不重用、条目不越界、指针移入不关闭与整组写入；只写自有测试副本。
- [x] 冲突三角：macOS 作者验收；iOS `layout 25/25`、Android `faults 22/0`、Web `node --test` 318/318 与 `?check=all` 50/50、Windows `WindowsTypeCheck` 0 错（运行时待本机）。
- [ ] 来源浮窗：iOS / Windows / Android / Web 尚未实现，按 Mac 定稿规范落地后重跑各自套件。
- [x] 筛选：CoreContractCheck + Web `?check=all`；13 项、已拒绝、冲突独立。
- [x] 训练：Training.sln 0 错 0 警；scan-only 验证 HIF 内嵌取 3、sidecar/伴侣不取 max、Invalid/Conflict 记 0 标签。
- [x] 打开：所有显式打开全量刷新（CoreContractCheck 覆盖文件/文件夹/拖入与单图回退）；Mac input 与 base/layout 通过，iOS / Android 构建通过；Windows 运行时未验。

未覆盖边界：其他平台的来源浮窗与运行时验证；Windows WinUI 运行时与真实 UI；PNG 写入（含标准 `iTXt`、容器 CRC 事务）；无 canonical 包的新建/插入与复杂容器结构；iOS 真机 security scope、Android USB/MTP/云 Provider、桌面链接/ACL/跨卷/断电恢复。

作者手测（后续独立任务，不计入本轮自动完成）：

- [ ] 真实 HIF/ARW + 已有 sidecar 的 0–5 往返，ExifTool 分别核查内嵌与 sidecar。
- [ ] Imaging Edge 与 Sony 相机读取 RAW 内嵌星级。
- [ ] 冲突组浮窗、全来源统一写入与失败保留（macOS 修订前已验收，修订后待重验；其余平台待浮窗实现）。
- [x] Mac 新浮窗易用性验收（修订前实现，不冒充修订后验收）。
- [ ] 其他平台浮窗实现后的对应验收。
- [ ] 各平台只读文件、只读目录、云 Provider、跨卷与中断恢复。

## 4. 挂账

- Windows WinUI 运行时与真实 UI 手验：仅 WindowsTypeCheck 通过，不当作已验收。
- PNG 写入整体挂账（含标准 `iTXt`，需容器级 CRC 事务），当前 `Unsupported`。
- 无 canonical XMP 包的新建/插入容器结构（含 JPEG 无 APP1）；复杂容器结构：非标准/重复 JPEG APP1；HEIF `idat`、多 extent、多个 XML/RDF 项、无 XMP mime 项；TIFF tag 700 在子 IFD、重复 tag 700、BigTIFF、无 tag 700 新增；WebP 多 XMP chunk；CR3 等超范围结构。
- 其它专有 RAW（CR2 / NEF / RAF / ORF / RW2 / SRW）的容器差异未逐一实测，按实际结构预检。
- -1 的专用图标/文案与来源级可访问性细节。
- 按来源逐项设置不同值、来源排序与来源级删除/重建。
- 真实设备与存储边界：iOS 真机 security scope、Android DocumentsUI/USB/MTP/云 Provider、桌面 ACL/xattr/硬链接/符号链接/跨卷替换/写中断电恢复。
- 旧设置迁移：明确不做，不新增补丁。

## 5. 不做

- 不承诺跨文件或断电原子；不以整文件重写冒充等长原位。
- 不以 sidecar 成功代替 RAW 内嵌写入。
- 不创建缺席的 sidecar。
- 不允许启发式全文定位或"尽力写一部分"。

## 6. 复审纠正

> 2026-10-26 复审。以下规范裁定已写入 [spec/rating.md](../Design/spec/rating.md)、[spec/open.md](../Design/spec/open.md) 及相关条目；代码修复由各端执行，回归结果待主代理复核后回填 [status.md](../Design/status.md)。

### 6.1 审查问题与裁定

| 问题 | 裁定 |
|---|---|
| 组级冲突把单来源缺失与多来源缺失混在一起 | 单来源仅 Missing 不冲突；两个及以上来源中任一 Missing（含全部 Missing）即冲突；单来源 Invalid/Unreadable 仍为冲突 |
| Unreadable 与 Missing 的区分和参与判定需要写实 | 作为与 Missing 并列的来源级状态参与比较；同一文件多份 canonical XMP 中缺 Rating 的那份也参与比较 |
| 结构定位被当作非目标数据安全的证明 | 结构定位只确定区间；安全还需区间边界、区间外字节不变与写后回读共同证明 |
| 容器内重叠或共享区间可能被误写 | 写入区间与记账字段不得与其它段/项/extent/IFD 条目重叠，也不得改写被其它数据引用的共享范围；不能证明独占即 `Unsupported` |
| 预检只依赖"可写"声明 | 预检改为真实随机读写能力（可定位读写句柄）；云 Provider、不可定位句柄在首次修改前失败 |
| `NoChange` 可能由缓存或上次回执推定 | `NoChange` 也必须逐来源实读并严格解析确认 |
| 写后只确认目标值 | 增加全部来源回读与来源集合确认（sidecar 未增删、内嵌包位置未替换） |
| 浮窗规范写成 macOS 实现数值（204 点、196 宽、六格收窄） | 规范宽度由星排（0 号 + 5 星）与固定内边距决定，星按钮不收窄；任一端实现不作规范真源 |
| 浮窗刷新范围与关闭条件需补全 | 刷新覆盖全部条目；锚点消失、布局重排、入口滚出可视区时关闭；刷新失败不因此关闭 |
| 训练快照的落值范围过宽 | 只落有效 1–5；0、-1、Missing、Invalid、Conflict、Unreadable 都不落 |
| "挂账"混用实现状态与规范边界 | 规范统一写"不支持"；未实测边界只留在 status 与本计划 |
| 完整刷新在 OPEN 各条重复 | 统一收敛到 OPEN-08，各入口只留触发与边界差异 |
| 规范混入实施语气与计划链接 | 删除"本轮/定稿/已实现/待实现"叙述与计划头链接；平台现状只留在 status |

### 6.2 规范修订（本次完成）

- [spec/rating.md](../Design/spec/rating.md)：RATING-01 冲突矩阵与多包 Missing 比较；RATING-02 区间独占与"定位不是安全证明"；RATING-03 真实读写能力预检、`NoChange` 实读证据、写后全来源与集合确认；RATING-04 非目标来源刷新、关闭条件、底部卡片向上、宽度不收窄、删除平台现状与实现基准叙述；RATING-05 训练仅落有效 1–5；RATING-06 "挂账"改为"不支持"。
- [spec/open.md](../Design/spec/open.md)：完整刷新集中到 OPEN-08，OPEN-01/02/03/05/06/07 只留触发与边界差异。
- [spec/film.md](../Design/spec/film.md)、[spec/filter.md](../Design/spec/filter.md)、[spec/toolbar.md](../Design/spec/toolbar.md)、[spec/state.md](../Design/spec/state.md)：只保留自身语义与对 RATING / OPEN-08 的引用，删除重复的来源、事务与平台现状描述。
- [Design/platforms.md](../Design/platforms.md)：RATING-02/06 改述为 `XmpRatingSpanLocator` + `ContainerRatingStore` + 只读定位适配器，删除 `XmpPacketEditor` 正常路径、六格收窄与 Mac 数值；评分相关入口优先符号名，去掉脆弱行号。
- [Design/status.md](../Design/status.md)：新近历史验收细节归档到本计划 §6.5；各条目按 §6.4 的实际回归范围回填，旧作者验收不替代修订后复验。
- [spec/settings.md](../Design/spec/settings.md)：安全模式语义按本次裁定核对（全来源预检、修订/区间复核、写后整组确认；safe 额外整包比较与逆序补偿，关闭时不回滚已成功来源），未改动。

### 6.3 代码修复与回归

- [x] 来源语义：多 canonical 包 Missing 参与比较；单来源 Missing 与多来源全 Missing 区分；Unreadable 与可解析但无效的 Rating 分开，数字筛选排除不可信来源。
- [x] 容器安全：TIFF 有界遍历目录/数据范围并拒绝共享字节；HEIF 拒绝共享 extent、重复索引、多 XML/RDF 候选与不可证明的项间引用；修正 idat 绝对基址、base_offset 与溢出边界。
- [x] 唯一写入路径：删除适配器写/恢复/追加与 `XmpPacketEditor` 整包重序列化入口，夹具通过独立造包或正式存储入口操作。
- [x] 事务：真实非截断 RW 预检；NoChange 实读与修订复核；成功前重新发现来源集合并逐来源回读；失败按安全模式补偿或保留磁盘真值。组内其余文件按格式优先级稳定排序。
- [x] 刷新与打开：按存储身份独立刷新代际，写后重新发现 sidecar；单图刷新说明父目录访问边界；可读但无 EXIF 的文件不误报无法打开；macOS 本地身份按卷上的实际拼写归一；iOS 在安全作用域内固化实际拼写身份；设置缓存张数/内存按规范上界夹取。
- [x] Android：新鲜 Provider 属性与会话 revision、数字 URI 显示名、选择器后台准备/迟到结果隔离、不可定位打开即拒绝；`SetLength` 改用 ftruncate，覆盖扩展零填充与回缩。
- [x] iOS：枚举子项保留原安全作用域；选择器与外部打开在作用域内解析实际路径并缓存身份，目录子项在租约内使用同一规则；胶片栏以原子显示快照统一数据源/布局/定位；共享通知先回主线程再访问 UIKit。回归不手动调用数据源 dequeue，改由 UIKit 滚动请求卡片。
- [x] 现有 macOS 浮窗：非当前目标及组成员实时刷新、失败保留、成功仍冲突保留、Esc/切图关闭、底部卡片方向及锚点失效关闭；几何字号使用令牌；popover/controller/content 每窗复用一套，旧关闭通知不影响快速重开。
- [x] Web：单 Missing 不冲突、Rejected 可改写 0–5、冲突解除后 aria-pressed 更新；未新增来源浮窗。
- [x] 训练消费只取实际选用文件有效 1–5；共享测试验证不可信状态不落值，Training.sln 构建与 scan-only 通过，未修改独立训练仓。
- [x] 核心、Web、Mac、iOS 模拟器与 Android 模拟器回归及 Windows 类型检查，覆盖与证据见 §6.4。
- [x] Windows 二次激活转发改为有界等待，超时记诊断并退出二次进程；类型检查通过，主实例无响应场景仍待 Windows 实机验证。
- [ ] iOS / Windows / Android / Web 来源浮窗：按本次范围明确不补实现，仍仅三角指示且点击不动作。

### 6.4 修复后的验证证据与限制

2026-10-26，macOS 本机；全部评分测试仅写自有夹具或临时副本，原始 TestCase 只读。

| 检查 | 结果与证据 |
|---|---|
| CoreContractCheck | 830 PASS / 1 SKIP / 0 FAIL；`release/core-review-fix-checks.log`，跳过 Windows 专属身份分支；含两份真实 ARW、真实 HEIC 副本往返/字节还原及合成重叠/事务/刷新回归 |
| SettingsDefaultsCheck | ALL PASS；`release/settings-review-fix-checks.log` |
| Web | Node 319/319；内置浏览器 `?check=all` 53/53、0 FAIL；令牌生成物最新 |
| macOS Debug | 构建 0 错；base 39 checks，input/layout/filmstrip/toolbar/analysis/similarity/tools/settings 全 PASS，faults 6 PASS；`release/mac-review-fix-*.log`。每套复跑使用干净隔离设置，input 不加载照片，避免旧套件留存快捷键/单项缩放预设污染前提 |
| macOS 浮窗 | `release/native-p2-acceptance/toolbar-ui-checks.txt`；真实窗口覆盖非当前来源变更、组重建、失败保留、成功仍冲突保留、Esc/切图、上下左右方向、隐藏/重排/滚动锚点关闭 |
| iOS Simulator | iPad mini (A17 Pro)，iOS 26.3，Debug 构建 0 错；layout 27 条（含 1 NOTE）无 FAIL，input 48 PASS / 3 NOTE / 0 FAIL，faults 6 PASS；`release/ios-review-fix-{layout,input,faults}.txt`。input 自建工作副本时不并发启动 `--pv-open` |
| Android | 既有 API 35 arm64 模拟器，使用 SDK 10.0.201 构建 0 错；storage 33/0、faults 22/0、input 13/0；`release/android-review-fix-{storage,faults,input}.txt`。数字 URI/选择器为 Debug Provider、同 UID，未冒充 DocumentsUI/跨 UID 授权实测 |
| Windows | WindowsTypeCheck 0 错；`release/windows-review-fix-typecheck.log`，不替代 Windows 本机构建/WinUI 运行时 |
| Training | `dotnet build Training/Training.sln` 0 错 0 警；现有夹具 scan-only 正常，未写训练库；`release/training-review-fix-{build,scan}.log` |

本轮限制收尾复验：CoreContractCheck 830 PASS / 1 SKIP / 0 FAIL；macOS Debug 构建 0 错，base 39、input、layout、toolbar 全 PASS。toolbar 中 32 轮快速关闭/重开始终复用同一原生实例且 content frame 不累计偏移，`phys_footprint` 样本 659572392 → 613615272 字节（作为进程样本而非逐对象归因），报告为 `release/native-p2-acceptance/toolbar-ui-checks.txt`。iOS 同型号模拟器 Debug 构建 0 错，input 48 PASS / 4 NOTE / 0 FAIL，layout 24 PASS / 3 NOTE / 0 FAIL，报告归档为 `release/ios-identity-{input,layout}-checks.txt`；同文件大小写别名在当前模拟器卷无法构造，保留 NOTE，不冒充通过。

保留限制：

- Windows WinUI 运行时、iOS 真机 Files 作用域、Android USB/MTP/云 Provider 与跨 UID 授权、Imaging Edge / 相机读取往返、断电恢复未验。
- PNG 写入、无 canonical 包的新建与复杂容器扩展均未新增；其他平台来源浮窗仍未实现。

### 6.5 修订前的程序化证据（自 status.md 移入）

> 以下结果产生于本次规则修订之前，只作历史证据，不冒充修订后的验收。

**2026-10-25 评分存储纠正（核心层）验收**：`Tools/CoreContractCheck` 635 PASS / 1 SKIP / 0 FAIL，`SettingsDefaultsCheck` 全通过。真实用户 HIF/ARW 副本：内嵌与已有 sidecar 2→-1→3 全 `Applied` 且一致，无 sidecar 组 4 星成功且不创建 sidecar。Training.sln 0 错 0 警；scan-only 验证 HIF 内嵌取 3、sidecar 与伴侣不取 max、Invalid/Conflict 记 0 标签。Windows 仅完成 TypeCheck，WinUI 运行时未验。冲突指示与来源浮窗（RATING-04）的 macOS 版本已另行验收。

**2026-10-26 星级冲突指示回归**：iOS 模拟器 `--pv-native-layout-check` 25/25 PASS（含 RATING-04 冲突三角）；Android `pv-check faults` 22/0、`storage` 24/0（含 RATING-04 冲突三角）；Web `node --test` 318/318、`?check=all` 50/50；Windows `WindowsTypeCheck` 0 错；macOS 无代码改动，沿用作者验收。来源浮窗仅 macOS 已实现，iOS / Windows / Android / Web 未实现，未实现平台点击三角不动作。

**修订前作者验收**：macOS 来源浮窗的纵向条目、三种方向、宽度/长名双行/分隔线与原生毛玻璃经作者验收；该验收覆盖修订前实现，修订后需重新核验。
