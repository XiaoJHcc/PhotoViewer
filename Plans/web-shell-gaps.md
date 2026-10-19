# Web 参考壳 — 设计文档缺口台账

> 配套 [web-shell-plan-1.md](web-shell-plan-1.md) §2。Web 壳只依据 `Design/` 实现；凡照文档写不出、写不对或与 Mac 不一致之处在此登记，作者裁定后再落到 `spec/` / `tokens.json` / `platforms.md` / `status.md`。

分类：`文档缺口`（没写或写不清）· `文档错误`（与作者认可行为冲突）· `Mac 缺口`（Mac 偏离规则）· `web 差异`（浏览器约束，登记 `@web`）· `待审`（实现期发现、尚未与 Mac 对照）。

| # | ID | 现象 | 分类 | 裁定 / 落点 |
|---|---|---|---|---|
| G1 | FILM-05 | 卡片内部几何只存在于 Mac 验收常量，tokens 只有部分（`film.card.secondLine.y`、`film.card.name.width`）。W1 按文档猜出行高 `ceil(字号×1.2)`、照片区与文件名间距 4、徽标内缩 2、星级行 = 日期底 + 2 并在 90 宽内居中，结果与 Mac 逐点一致，但这些数仍不在文档里 | 文档缺口 | 待裁定：补 `film.card.name.y`、`film.card.star.y`、`film.card.thumb.inset` 等令牌 |
| G2 | FILM-05 | 卡片第二行拍摄时间的显示格式未定义（Mac 为 `MM-dd HH:mm`）；只定义了完整时间格式 `yyyy-MM-dd HH:mm:ss zzz` | 文档缺口 | 待裁定 |
| G3 | OPEN-02/03、STATE-02 | 规则引用"文件栏标题/文件夹名"，但 L1 分区与 FILTER-01「固定五项」里没有它的位置；Mac 不显示 | 文档错误 | 待裁定：删去标题表述，或在 L1 立位置 |
| G4 | FILM-05、STATE-02 | 胶片栏空态「无匹配照片」Mac 未实现 | Mac 缺口 | 待裁定 |
| G5 | FILTER-06 | 相似聚类开关的四态视觉（常态/悬停 × 勾选）没有规则与颜色令牌 | 文档缺口 | 待裁定 |
| G6 | TOOLBAR-08 | Mac 控制栏 `NSScrollView` 只在初始化时设 Overlay，系统滚动条偏好为 legacy（接鼠标/始终显示）时被改回并常驻 17 点竖向滚动条槽，内容不溢出也占位；胶片栏已在 `Tile()` 锁 Overlay，控制栏没有。实测：默认偏好 18 处差异，`-AppleShowScrollBars WhenScrolling` 下 0 差异 | Mac 缺口 | 待修 Mac：控制栏同样锁 Overlay；修后重出 `native-layout-geometry.txt`。`status.md` TOOLBAR-08 记缺口 |
| G7 | INPUT-04 | 浏览器 `WheelEvent` 不暴露"精确/离散"标志，只能按 `deltaMode` 与增量形态推断；Chromium/Safari 把触控板捏合投递为 `ctrlKey` + wheel，与「Ctrl+滚轮 = 比例缩放」同一事件 | web 差异 | 待登记 `INPUT-04@web` |
| G8 | INPUT-01 | 浏览器保留 Ctrl/Cmd+N 等组合，页面拦不住，「打开文件」主绑定在 Web 失效（次绑定 Ctrl+O 可拦） | web 差异 | 待登记 `INPUT-01@web` |
| G9 | MENU-01、SCOPE-01、TOOLBAR-06 | 无设置/工具窗口的平台，「工具…/设置…」菜单项与 EXIF 组点击如何处置没有明文规则（MENU-01 说项集合四端相同；SCOPE-01 说未落地能力不显示入口） | 文档缺口 | 待裁定 |
| G10 | SHELL-03 | 浏览器视口可小于最小窗口 560×480，规则未定义此时行为 | 文档缺口 | 待裁定 |
| G11 | FILTER-04/07 | 计数文本没有字号令牌（只在 `filter.count.height` 描述里提到"旧壳 12 点字"），也没有字宽口径；行布局整组居中依赖它，导出几何因此差 0.8–1.6 点（W1 唯一的几何差异） | 文档缺口 | 待裁定：补 `filter.count.fontSize` |
| G12 | 01-regions、FILTER-01 | 行布局写成"筛选条在文件栏左端/胶片栏左侧"，但 SHELL-01 行布局与 FILTER-07（整组占高 52、整组居中）和 Mac 实际都是筛选条整行在上、胶片带在下 | 文档错误 | 待裁定：改 01-regions 示意与 FILTER-01 位置描述 |
| G13 | FILM-02/04 | 横向短列表"首项居中"只写在 `film.list.padLeft` 的令牌描述里，spec 正文没有；纵向短列表是否居中未写 | 文档缺口 | W1 按"横向整段居中、纵向顶边起排"实现（与 Mac 一致） |
| G14 | FILTER-02/03 | 同名合并显示名后缀格式（多伴侣如何写）、合并卡片按哪个文件的星级命中精确档与"以上"档、按文件大小排序取哪个文件，均未写 | 文档缺口 | W1：`名.JPG(RAW/HEIF)`，星级与大小取代表文件 |
| G15 | IMAGE-05 | 条内自相矛盾：①"同一图像点留在视口中心、相对缩放不变"与"不改变倍率"；②"照片边缘最多到达视口中心"与公式"（显示 − 视口）的一半"（后者意味着边缘最多到视口边缘） | 文档错误 | W1 取公式与"倍率不变"；待裁定后改正文 |
| G16 | IMAGE-06、IMAGE-04 | 桌面缩到低于适应倍率时导航图是否显示未写 | 文档缺口 | W1：显示，可见框为整幅 |
| G17 | INPUT-07、MENU-03 | 长按时长与移动容差没有令牌 | 文档缺口 | W1：500 ms / 8 点 |
| G18 | ANALYSIS-09/10 | "回退期间四张诊断瓦片显示计算中…"与"切图时先保留上一张瓦片直到新数据就绪"冲突 | 文档错误 | W1：只读库路径保留旧瓦片，现算路径显示计算中 |
| G19 | ANALYSIS-04/06 | 对焦点瓦片的裁剪窗口（瓦片边长方块）与主图绿框（实际对焦框尺寸）是两个矩形，正文写得像同一个 | 文档缺口 | W1 分为 `detailCrop` 与 `highlightRect` |
| G20 | SIM-03、SIM-08 | 提取"整个当前文件夹"，候选池却是筛选结果；提取范围与未提取计数的口径不一致 | 文档缺口 | W1 暂按筛选池；待裁定 |
| G21 | SIM-07 | 行布局面板交叉轴 161（`sim.list.cross`）大于胶片带 159，落位方式未写；参数合并节流 150 ms 不在 tokens | 文档缺口 | 待 W2 对照 Mac |
| G22 | IMAGE-03、architecture §5.1 | architecture 写"代理失败自动回退 Full"，与 STATE-02/IMAGE-03"代理失败即失败提示"冲突 | 文档错误 | W1 按 spec |
| G23 | 比对格式 | Mac 报告 Auto 布局节点名为 `photoPane`，其余布局为 `root.photoPane`；`Design/README.md` §8 未写节点命名 | 文档缺口 | Web 导出沿用 Mac 命名 |
