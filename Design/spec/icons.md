# 图标

数值令牌：`icon.*`（Web 参考壳）、各控件的 glyph 字号与尺寸（见 [tokens.json](../tokens.json)）。语义 ID、中文名称、用途分组与 Apple / Fluent 具体图标选取的唯一目录是 [icons.json](../icons.json)；本文只定义规则，不另建造型清单，平台实现见 [platforms.md](../platforms.md#icons)。

## ICON-01 语义清单 · 必须

- 图标按业务语义登记，不以字体码位、英文资源名或某个平台的造型作为跨端契约。每个条目包含稳定语义 ID、中文名称、用途分组、Apple SF Symbol 与共享 Fluent 图标。
- 同一语义在各入口复用同一 ID；同概念但表达不同状态的条目分别登记，例如描边星与实心星、筛选数字圆与评分快捷命令、筛选冲突与评分冲突。
- 新增需要图标的命令、菜单、筛选、设置或工具入口时，必须先登记语义 ID 并补齐 Apple / Fluent 映射，再实现平台界面。设置文档只保存命令 ID，不保存图标或字体码位。
- 图标不参与命令能否执行的判定；缺少图标是展示与验收错误，不得改变输入命令表。

## ICON-02 平台图标族 · 必须

- Apple 使用系统 SF Symbols；Windows 与 Android 使用同一份 Fluent UI System Icons Regular / Filled 字体子集。Web 参考壳使用 `icon.font` / `icon.fontSolid` 的 uicons 子集。
- Fluent 字体由 `Design/icons.json` 固定上游提交，生成只含已登记码位的随包资产。不得依赖设备已安装字体、在线字体服务、OEM 系统 drawable 或另一图标族的运行时回退。
- Regular / Filled 由条目对应的 Fluent 资源名确定，不自行换族、混用 Light 或按平台另选造型。
- Windows 自绘窗口按钮保留系统 Segoe Fluent Icons / MDL2 回退链，属于窗口 chrome，不进入业务图标映射。应用图标、调试 DocumentsProvider 的系统缩略图也不属于本条。

## ICON-03 语义、造型与光学尺寸 · 必须

- 四端语义必须一致。Windows 与 Android 对同一语义必须使用相同 Fluent 字形、填充状态和视觉尺寸；Apple 以同语义的 SF Symbol 为造型与光学尺寸参照，允许保留系统家族的轮廓差异。
- 选择 Fluent 映射时按 SF 实际符号核对构图、方向、轮廓、填充与修饰符，不得只按英文名称近似或平台习惯选择明显不同的图形。
- 具体语义 ID、中文名称、用途分组、SF Symbol、Fluent 资源名及 Regular / Filled 状态只在 `icons.json` 维护。本文、其他模块规范与平台手册不得另列同义映射或造型清单；各模块只引用语义 ID 和所属控件的显示规则。
- 各控件的 `glyph.fontSize` 是标称设计字号，不等于字体的 em 外框或实际墨迹尺寸。图标槽尺寸、命中区和布局几何是独立契约，不得通过放大槽位或命中区补偿字形大小。
- 生成 Fluent 字体时，以 `icons.json` 固定系统版本的 SF Symbol 在 18 点 Regular、50% alpha 覆盖下的实际墨迹为参照，分别计算各项 SF / Fluent 墨迹边界框对角线之比，再取全部语义的算术平均作为唯一归一倍率。所有字形共用该倍率，不按单项拟合尺寸，也不设置单项例外；保持原始宽高比、排版边距和非对称构图。归一结果随字体固定，Windows 与 Android 不在各自 UI 中另加缩放系数。
- Android 图标按 dp 换算，不使用 sp，不随系统文字缩放改变；普通文字仍按既有 sp 规则。Windows 使用 DIP，Apple 使用点。原生菜单若由系统管理符号尺寸，保持系统行为；自绘菜单必须使用 `menu.icon.fontSize` 绘制，不得误用 `menu.icon.size`。
- 图标颜色、着色状态、命中区、布局尺寸和读屏规则仍归所属模块。换图标不得改变控件结构、入口数量、命令行为或几何令牌。

## ICON-04 生成与完整性 · 必须

- `Design/icons.json` 是三端映射、生成码位表、随包字体子集和对比报告的唯一数据源。生成产物不得手改；上游版本升级必须显式修改固定提交并重新生成、重新目视全部变化项。
- `python3 Tools/generate-icons.py --check` 校验生成码位表、字体摘要和全部码位覆盖。缺失语义、未知上游图标名、缺字体、缺码位或产物漂移均为失败，不允许静默使用默认图标。
- `--refresh` 在 macOS 上重新取得固定 Fluent 上游、渲染 SF 参照并重建归一字体；普通生成复用已入库字体。资产清单保存全目录统一倍率与各项参考墨迹尺寸，供检查与报告使用。
- 字体许可证随资产保留。生成与检查不访问用户照片，不写用户设置。

## ICON-05 对比与验收 · 必须

- `python3 Tools/generate-icons.py` 生成 `artifacts/acceptance/icon-catalog/index.html`，按语义逐行并列 Apple 实际系统渲染与共享 Fluent 字形，展示 18 / 24 点两档及资源名；Apple 图片按其实际画布尺寸显示，不以 CSS 另放大。
- 报告及机器可读指标同时给出 SF 原始墨迹、全目录统一倍率与归一后墨迹，不得只用相同 `font-size` 推断视觉大小一致。
- 对比检查语义、视觉粗细、墨迹面积、圆角、填充配对和原始构图，不要求 Apple 与 Fluent 的轮廓或 SVG 外框完全相同。原生控件截图仍需核对基线、着色、禁用态、光栅化和原始排版位置，不通过居中修正字体，资产对比页不能替代真屏验收。
- Windows 至少覆盖主图菜单、筛选、控制栏、胶片卡片、评分来源浮窗、设置与工具分类；Android 另覆盖手机胶囊、设置排序行和星级弹窗，并在不同 fontScale 下确认图标尺寸不变。截图与报告写入隔离验收目录，不作为产品 UI。
