# 评分存储纠正：全来源统一写入

> 2026-10-25 立项。取代 [core-legacy-rebuild-plan-1](core-legacy-rebuild-plan-1.md) 中"专有 RAW 只写 `<stem>.xmp`、sidecar 唯一权威"的旧口径；旧计划保留历史，不再作为评分实现依据。规范正文见 [Design/spec/rating.md](../Design/spec/rating.md)（RATING-01–06），状态见 [Design/status.md](../Design/status.md#rating)。本文件只登记实施清单、验收待办与挂账，不是设计真源。

## 0. 产品口径

1. HIF / ARW / JPG 等各文件的内嵌 Rating 与每个**实际已有**的 `<stem>.xmp` 都是独立来源；读取与写入都没有优先级。
2. sidecar 不存在时**绝不创建**；存在时必须同步。
3. RAW 必须写内嵌，保证 Sony 相机与 Imaging Edge 可读；只写 sidecar 不算成功。
4. 所有来源统一写：先全部预检；安全模式下失败按逆序回滚；不承诺跨文件断电原子；补偿不完整时 `Partial` 按磁盘真值显示冲突。
5. 安全写入按结构定位、最小区间与等长包长写，用合法 XML 空白吸收 `-1` 的位数差；无法证明非目标数据安全时为 `Unsupported`；不允许启发式全文定位。
6. 数字与"以上"筛选仍按代表文件内嵌 Rating；`Conflict` 独立；新增"已拒绝"按代表 -1；筛选项从 12 变 13；-1 的星级 UI 挂账。
7. 冲突时胶片栏与控制栏 0 号位置把清零叉换成红色警示三角；点击打开横排来源浮窗，列出全部实际来源；任一来源点 0–5 都统一写全部来源；成功后关闭，失败保留（RATING-04，待优化，未验收）。
8. 所有显式打开（文件、文件夹、拖入、系统外部激活/命令行）都重新枚举目标父目录/目录，并完整刷新新增、删除、全部 Rating 来源、sidecar 存在性与内容、基本属性和 EXIF；当前目标保留筛选、排序、滚动、面板与视口，新目标按 OPEN-05 Fit；单图权限不足只刷新可访问项并明示边界；新增删除不生成重复卡片。
9. 训练侧实际选用文件内部为 `Conflict` / `Invalid` 时 Rating 不落快照，不任选值。
10. 旧设置明确不迁移，不写迁移补丁。
11. 本轮先保证真实 HIF / ARW 与已有 sidecar；复杂容器结构挂账。手测是后续独立任务，不纳入本轮自动完成条件。

## 1. 设计落点

| 位置 | 内容 |
|---|---|
| [spec/rating.md](../Design/spec/rating.md) | RATING-01–03、05–06 新条目（来源、安全写、事务、状态/训练、支持边界）；RATING-04 冲突浮窗待优化 |
| [Design/README.md](../Design/README.md) | 登记 `RATING` 前缀 |
| [Design/architecture.md](../Design/architecture.md) §5.3、§5.2 | 删除 RAW 只写 sidecar / sidecar 唯一权威；改为全来源枚举、预检、顺序写、逆序补偿；所有显式打开完整刷新 |
| [spec/film.md](../Design/spec/film.md) / [toolbar.md](../Design/spec/toolbar.md) | 全部实际来源写回与 -1 显示口径；冲突三角与来源浮窗（RATING-04）待优化 |
| [spec/filter.md](../Design/spec/filter.md) | 13 项、已拒绝、冲突独立、刷新时机 |
| [spec/state.md](../Design/spec/state.md) | 部分来源失败文案与真实状态展示 |
| [spec/settings.md](../Design/spec/settings.md) | 安全模式的全来源预检语义；旧设置不迁移不补丁 |
| [spec/open.md](../Design/spec/open.md) | 所有显式打开重新枚举并完整刷新；当前目标保留视口、新目标 Fit |
| [Design/platforms.md](../Design/platforms.md) / [status.md](../Design/status.md) | 来源枚举/事务落点与缺口状态；浮窗宿主待优化 |
| [Design/tokens.json](../Design/tokens.json) | `color.star.conflict`、`icon.warning`（待优化，未提交） |

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

### 2.4 冲突指示与来源浮窗（待优化）

- [ ]（待优化） 胶片栏卡片与控制栏 0 号槽位：冲突时清零叉换 `icon.warning` + `color.star.conflict`，几何与命中区不变。
- [ ]（待优化） 横排来源浮窗：每来源一列（标签、状态、当前值、0–5 与清零叉），任一列点击都是整组统一写入。
- [ ]（待优化） 成功后关闭；`RolledBack` / `Partial` / `Canceled` 保留浮窗并按磁盘真值刷新；切图/关窗关闭浮窗。
- [ ]（待优化） 桌面指针、触屏点按、键盘进入与 `Esc` 关闭；不进入模态，不影响照片快捷键。
- [ ]（待优化） 四端字形：Apple SF Symbols、Windows `E7BA`、Android / Web `icon.warning`。

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

- [x] 安全模式开关语义对齐 RATING-02/03 文案：关闭只跳过外部修改比对，不跳过结构预检与写后校验。
- [x] 旧设置不迁移、不写字段级迁移补丁；沿用备份后默认值启动的现有路径。

### 2.8 令牌与生成物（待优化）

- [ ]（待优化） `Design/tokens.json` 增加 `color.star.conflict`（#FF453A）与 `icon.warning`（U+FE33），描述关联 RATING / FILM / TOOLBAR。
- [ ]（待优化） `python3 Tools/generate-design-tokens.py` 生成 `DesignTokens.g.cs`（303 个常量）与 `PhotoViewer.Web/src/tokens.g.js`。
- [ ]（待优化） 四端与 Web 接入新令牌（随 2.4 实现）。

## 3. 验收待办

程序化（随实现）：

- [x] 来源枚举：`CoreContractCheck` 635 PASS / 1 SKIP / 0 FAIL；真实 HIF/ARW 副本内嵌与已有 sidecar 独立枚举，2→-1→3 全 `Applied`；无 sidecar 组成功且不创建。
- [x] 写入：ARW / HEIF 内嵌、已有 sidecar 同步；无 sidecar 不创建；同值 `NoChange` 不写盘；真实副本三来源 2→-1→3 一致，无 sidecar 组写 4 一致。
- [x] 事务：CoreContractCheck；Mac faults PASS；iOS faults 6；Android `storage` 24/0、`faults` 24/0。
- [ ] UI：冲突三角与来源浮窗（RATING-04）待优化，未验收；本轮不作通过项。
- [x] 筛选：CoreContractCheck + Web `?check=all`；13 项、已拒绝、冲突独立。
- [x] 训练：Training.sln 0 错 0 警；scan-only 验证 HIF 内嵌取 3、sidecar/伴侣不取 max、Invalid/Conflict 记 0 标签。
- [x] 打开：所有显式打开全量刷新（CoreContractCheck 覆盖文件/文件夹/拖入与单图回退）；Mac input 与 base/layout 通过，iOS / Android 构建通过；Windows 运行时未验。

未覆盖边界：Windows WinUI 运行时与真实 UI；PNG 写入（含标准 `iTXt`、容器 CRC 事务）；无 canonical 包的新建/插入与复杂容器结构；iOS 真机 security scope、Android USB/MTP/云 Provider、桌面链接/ACL/跨卷/断电恢复。

作者手测（后续独立任务，不计入本轮自动完成）：

- [ ] 真实 HIF/ARW + 已有 sidecar 的 0–5 往返，ExifTool 分别核查内嵌与 sidecar。
- [ ] Imaging Edge 与 Sony 相机读取 RAW 内嵌星级。
- [ ] 冲突组浮窗、全来源统一写入与失败保留。
- [ ] 各平台只读文件、只读目录、云 Provider、跨卷与中断恢复。

## 4. 挂账

- Windows WinUI 运行时与真实 UI 手验：仅 WindowsTypeCheck 通过，不当作已验收。
- 冲突三角与来源浮窗 UI（RATING-04）与令牌接入：待优化，未验收，未随本次核心层提交。
- PNG 写入整体挂账（含标准 `iTXt`，需容器级 CRC 事务），当前 `Unsupported`。
- 无 canonical XMP 包的新建/插入容器结构（含 JPEG 无 APP1）；复杂容器结构：非标准/重复 JPEG APP1；HEIF `idat`、多 extent、多个 XML/RDF 项、无 XMP mime 项；TIFF tag 700 在子 IFD、重复 tag 700、BigTIFF、无 tag 700 新增；WebP 多 XMP chunk；CR3 等超范围结构。
- 其它专有 RAW（CR2 / NEF / RAF / ORF / RW2 / SRW）的容器差异未逐一实测，按实际结构预检。
- -1 的专用图标/文案、来源浮窗的来源级可访问性与排序。
- 按来源逐项设置不同值、来源排序与来源级删除/重建。
- 真实设备与存储边界：iOS 真机 security scope、Android DocumentsUI/USB/MTP/云 Provider、桌面 ACL/xattr/硬链接/符号链接/跨卷替换/写中断电恢复。
- 旧设置迁移：明确不做，不新增补丁。

## 5. 不做

- 不承诺跨文件或断电原子；不以整文件重写冒充等长原位。
- 不以 sidecar 成功代替 RAW 内嵌写入。
- 不创建缺席的 sidecar。
- 不允许启发式全文定位或"尽力写一部分"。
