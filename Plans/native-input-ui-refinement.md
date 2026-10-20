# 原生输入与设置/工具界面收口

## 完成条件

落实作者确认的 Mac/iPad 问题与跨端共同规则；设计先行，原生呈现留在平台层。构建、既有回归与真实事件路径分别记录，编译或模拟外设能力不能代替真机输入验收。保留用户已有 `DEV.md` 与 `Tools/RefreshIosProfile/RefreshIosProfile.xcodeproj/project.pbxproj` 修改，不纳入本任务提交；本任务按验收完成的独立任务分批提交，不发布，不改用户照片。

## 规则归属

| 规则 | 归属与范围 |
|---|---|
| 细节绿框 | ANALYSIS-04/11：实际指针悬停；直接触控按下显示、移动切换唯一目标、抬起/取消清除。隐藏、换图、失活清除，视口变化同步重算；双击定位与诊断准星独立。四端和 Web 共用。 |
| 键鼠设置 | INPUT-13、SETTINGS-01/08/11/13：触控优先设备连接键盘、鼠标、触控板任一完整显示绑定/录制/清空/冲突；仅直接触控隐藏全部相关控件，模型配置保留。连接变化不丢草稿或弹软键盘；Apple 映射仅 Apple。 |
| 倍率编辑 | SETTINGS-07：单个居中输入值加百分号；新增默认100%/删除项非步进。失焦、空白、完成、导航、关闭校验到工作副本；设置关闭才落盘，不抢回用户主动移走的焦点。 |
| 原生控件 | TOOLBAR-10、FILTER-08 与 L4：完整反馈与无裁切是共同要求，原生控件由系统提供色彩/轻量状态动效，自绘仍按令牌。 |
| 工具导航 | TOOLS-01/02/03：同平台与设置统一。Mac 常驻侧栏＋详情；iPad/Android 分组列表导航与同款 sheet；保留只读、EXIF比例、DINO 3+2和生命周期。 |
| 顶部筛选 | FILTER-07/SHELL-02：iPad 整组安全内容区居中；Android前导对齐差异保留。 |
| 普通缺陷 | FILTER-06初始同步、INPUT-03/10窗口系统操作优先、ANALYSIS-04实时换算、ANALYSIS-08诊断准星和SETTINGS-08布局解释按既有意图修复。 |

## 实施进度

- 设计正文与 L4 登记已修订；旧触屏定格、固定移动端只读、iPad前导对齐口径已清理。令牌仅修描述并重新生成，无数值变化；生成检查通过。
- 已提交五批：设计 `e28bb94`、Web `91be818`、Mac `c6b5177`、Android `a481cda`、iOS `83dfdda`；Windows 仅类型检查，未提交。
- Mac：六项实现完成并已提交 `c6b5177`。最终 Debug osx-arm64 构建通过，父代理增量复跑 0 错误 / 1 条 RID 警告；八套 input/layout/toolbar/analysis/similarity/tools/settings/filmstrip 全 PASS，日志在 `PhotoViewer.Mac/smoke-{input,layout,toolbar,analysis,similarity,tools,settings,filmstrip}.log`。本轮直接证据在 `PhotoViewer.Mac/release/native-p2-acceptance/`：`native-input-checks.txt`（完整命中树排除装饰/兄弟覆盖，仅主图有效点击打开一次）、`native-analysis-checks.txt`（活动源矩形随缩放/平移/窗口重投影，裁剪刷新不激活，失活清除；40×400 小图绿框同 40px）、`toolbar-ui-checks.txt`（hover/按下完整方形面、焦点与 Space）、`settings-ui-checks.txt`（预设真实文本编辑、Enter/失焦/导航/关闭提交、稳定逻辑 ID）、`native-tools-checks.txt`（原生侧栏默认 EXIF、无照片 DINO 回退、无工具首页）、`native-similarity-checks.txt`（隐藏/恢复文件栏保留逻辑态并还原面板）。父代理已读取工具 EXIF/DINO 与设置系统截图。未手验：真实物理鼠标窗口边缘/交通灯/拖动、VoiceOver，不提升状态；工具本轮仅单格式 JPG，伴侣多格式保留路径未专项实测。
- iOS/iPadOS：最终程序化回归完成并提交 `83dfdda`。`release/native-input-ui-acceptance/ios-final/run-summary.json`：`finalSevenPassed=true`、`stabilityPassed=true`、`sourceUnchanged=true`、`allPassed=true`；七套 PASS 数 photo10 / layout21(+3 NOTE) / input36(+1 NOTE) / analysis18 / similarity9(+1 NOTE) / tools9(+1 NOTE) / gesture22(+2 说明行)，同轮 FAIL 0；随后固定 3 轮 analysis18 / gesture22 全 PASS。fresh Debug simulator build 0 错误 / 0 warning；实际编译仍有 28 条既有 nullable warning，增量 0 不等于全 clean 无警告。原始汇总、取证、最终 input/tools 报告与 ios-touch/keyboard/tools-list/exif 截图由父代理读取，原生 CU 不可用，未做真实手指/外设手验。两处旧红测根因均为测试竞态：analysis 旧仅 `Count==2` 而未等主图上屏，现同次 UI 原子检查 file/signal/image/texture/layout 后取当前 preview；真实 SetFile 空态无图不画主图框符合规则，未加固定 delay。gesture 旧 `IsEnhancing` 与 Label 分两次 UI 读取产生 TOCTOU，现原子快照保留 `busyDelay500+200` 采样并加强完成收起断言；实际 707ms busy 可见、900ms complete 隐藏、快代理 227ms 不强制显示；诊断见 `release/native-input-ui-acceptance/ios-final/native-gesture-race-diagnostics.txt`。attempt-1/2 历史报告保留，不删除。
- Android：最终提交 `a481cda`。Release 五组原始报告 PASS 14/43/15/47/16、FAIL 0，位于 `release/native-input-ui-acceptance/android-final/release-{main,analysis,similarity,settings,tools}.txt`；截图 `release/native-input-ui-acceptance/android-settings-pure-touch.png`、`android-settings-device.png`、`android-tools-root.png`，父代理已读。SDK 10.0.201 下完整 clean build Debug/Release 各 0 错误 / 9 条既有 warning；父代理 fresh Debug 增量 build 0 错误 / 0 warning。Release 报告由 Release APK 生成，之后安装 Debug 才用 `run-as` 原样取出，报告头部时间保留原始值、未重跑；Debug 同名报告已被 Release 覆盖，目录不含 Debug 原始文件，不把 Release 归档当 Debug 结果。核心覆盖：两张确定性 Sony MakerNote JPEG 细节；Down/Move 切换、旧指针 Cancel 隔离、owner 优先、鼠标 hover、Up 清除与诊断起手门控；设置真实 RowView 可见性与 AX（纯触控零绑定文字、keyboard-only 或 mouse 完整恢复）、录制吞 mouse 左右/触摸、滚轮与中侧键绑定、冲突、断连取消；预设保留 EditText 实例与 77.777 未失焦草稿跨增删排序保存，1601/NaN/0 恢复并提示；工具与设置原生容器统一。真实 USB/BT 热插拔与触控板 hover 硬件未验证。
- Windows：代码与既有验收扩展已实现，父代理复核后类型检查通过：`dotnet msbuild Tools/WindowsTypeCheck/WindowsTypeCheck.csproj -restore -t:Compile -v:m`（退出码0）。包括细节起手显式捕获/滑动唯一目标、取消与旧指针隔离、键盘及 UIA Invoke、失活清理；预设改为单个居中文本框，非法输入提示恢复、失焦/导航/关闭提交工作副本并保持行身份。新增烟测覆盖上述序列与真实文本编辑，但本机 macOS 无法运行 WinUI；`Check Windows Native`、设置专项、真实触控/笔/键盘/读屏均待 Windows 本机验收，暂不提交，不宣称运行通过。
- Web：按压/滑动/抬手、取消/捕获丢失、隐藏/失活清除及辅助技术已实施；单测297项通过。父代理在恢复的内置浏览器会话运行最新版 `http://localhost:8090/?check=all`：44/44 PASS、FAIL 0，包含 ANALYSIS-04 的取消、stale pointer、隐藏、诊断点按与双击定位序列。截图 `release/native-input-ui-acceptance/web-all.png`。验证标签页与临时服务已关闭。

## 验证与剩余项

- `python3 Tools/generate-design-tokens.py --check`：通过（本次未改令牌数值）。
- `git diff --check`：本次状态/计划更新后复跑通过。
- iOS：最终七套 + 3 轮稳定性全绿；真实手指/外设手验未做。Android：Release 五组已通过，Debug 原始报告缺失不补写；真实 USB/BT 热插拔与触控板 hover 待硬件验证。
- 真实外接键鼠连接/断开、触控板滚轮/指针输入、Mac 物理鼠标窗口边缘/交通灯/拖动与 VoiceOver、Windows 本机 WinUI/读屏：没有实际证据前一律待核验，不以能力注入或类型检查提升状态。

## 真实手验清单

- Mac：空态标题边缘命中、hover 反馈。
- iPad：菜单返回圆角、倍率输入焦点/软键盘、触控绿框/准星。
- iOS / Android：USB/BT 外设热插拔、键鼠录制/断连。

## 结论

Mac / iOS / Android / Web 已实现并程序化验证；Windows 代码同步、类型检查通过，待 Windows 本机，不宣称所有平台最终验收完成。
