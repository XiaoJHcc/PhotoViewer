# Web 参考壳 — 设计文档缺口台账

> 配套 [web-shell-plan-1.md](web-shell-plan-1.md) §2。Web 壳只依据 `Design/` 实现；凡照文档写不出、写不对或与 Mac 不一致之处在此登记，作者裁定后再落到 `spec/` / `tokens.json` / `platforms.md` / `status.md`。

分类：`文档缺口`（没写或写不清）· `文档错误`（与作者认可行为冲突）· `Mac 缺口`（Mac 偏离规则）· `web 差异`（浏览器约束，登记 `@web`）· `待审`（实现期发现、尚未与 Mac 对照）。

| # | ID | 现象 | 分类 | 裁定 / 落点 |
|---|---|---|---|---|
| G1 | FILM-05 | 卡片内部几何（文件名行 y=89 高 12、星级行 y=116 步距 14、徽标偏移、照片区 5,5 起点）只存在于 Mac 验收常量，tokens 只有部分（`film.card.secondLine.y`、`film.card.name.width`） | 文档缺口 | 待裁定：补 `film.card.name.y`、`film.card.star.y`、`film.card.thumb.inset` 等令牌 |
| G2 | FILM-05 | 卡片第二行拍摄时间的显示格式未定义（Mac 为 `MM-dd HH:mm`）；只定义了完整时间格式 `yyyy-MM-dd HH:mm:ss zzz` | 文档缺口 | 待裁定 |
| G3 | OPEN-02/03、STATE-02 | 规则引用"文件栏标题/文件夹名"，但 L1 分区与 FILTER-01「固定五项」里没有它的位置；Mac 不显示 | 文档错误 | 待裁定：删去标题表述，或在 L1 立位置 |
| G4 | FILM-05、STATE-02 | 胶片栏空态「无匹配照片」Mac 未实现 | Mac 缺口 | 待裁定 |
| G5 | FILTER-06 | 相似聚类开关的四态视觉（常态/悬停 × 勾选）没有规则与颜色令牌 | 文档缺口 | 待裁定 |
| G6 | TOOLBAR-08 | Mac 控制栏 `NSScrollView` 只在初始化时设 Overlay，系统滚动条偏好为 legacy（接鼠标/始终显示）时被改回并常驻 17 点竖向滚动条槽，内容不溢出也占位；胶片栏已在 `Tile()` 锁 Overlay，控制栏没有。实测：默认偏好 18 处差异，`-AppleShowScrollBars WhenScrolling` 下 0 差异 | Mac 缺口 | 待修 Mac：控制栏同样锁 Overlay；修后重出 `native-layout-geometry.txt`。`status.md` TOOLBAR-08 记缺口 |
| G7 | INPUT-04 | 浏览器 `WheelEvent` 不暴露"精确/离散"标志，只能按 `deltaMode` 与增量形态推断；Chromium/Safari 把触控板捏合投递为 `ctrlKey` + wheel，与「Ctrl+滚轮 = 比例缩放」同一事件 | web 差异 | 待登记 `INPUT-04@web` |
| G8 | INPUT-01 | 浏览器保留 Ctrl/Cmd+N 等组合，页面拦不住，「打开文件」主绑定在 Web 失效（次绑定 Ctrl+O 可拦） | web 差异 | 待登记 `INPUT-01@web` |
| G9 | MENU-01、SCOPE-01、TOOLBAR-06 | 无设置/工具窗口的平台，「工具…/设置…」菜单项与 EXIF 组点击如何处置没有明文规则（MENU-01 说项集合四端相同；SCOPE-01 说未落地能力不显示入口） | 文档缺口 | 待裁定 |
| G10 | SHELL-03 | 浏览器视口可小于最小窗口 560×480，规则未定义此时行为 | 文档缺口 | 待裁定 |
