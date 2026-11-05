# 设置展示失效范围与周期刷新修复

## 目标与根因

修复 Android 筛选下拉被约 2 秒周期刷新关闭的问题，并收口四端设置驱动的展示刷新契约。

已确认的链路：`MemoryGovernor.Probed`（默认 2 秒）→ `SettingsViewModel.BitmapCacheCountInfo` 属性通知 → Android 主屏无条件刷新控制栏并请求布局 → `AndroidFilterBar.Arrange` 无条件关闭下拉。macOS 主屏存在同类无差别重建，iOS 已有本端诊断属性排除与内容摘要保护，Windows 原有属性白名单未受此周期通知影响。

## 方案

- 共享 `SettingsViewModel.PresentationChanged(SettingsChangeImpact)` 正向标记布局、导航图、评分、EXIF、命令、输入绑定、文件投影、分析、相似面板的展示影响。
- 普通 `PropertyChanged` 继续服务精确业务与设置页文本绑定；诊断、未知属性不触发展示失效。内存探测及压力响应不关闭、不降频。
- 快捷键子项编辑与集合排序进入同一展示通道；整份快照回填合并展示通知。
- 四端主屏按共享影响范围刷新，不再各自维护诊断属性排除名单。Android 下拉以实际最终屏幕锚点判断失效，相同几何重复布局不关闭。
- 不改变设置持久化格式、功能项目或控件视觉。

## 验证与边界

| 范围 | 验收内容 | 结果 |
|---|---|---|
| 共享契约 | 诊断通知与真实内存探测不发主屏展示事件；真实设置、子项、排序和快照回填正常发布；释放后不发布；相同快照和仅缓存预算变化在 `PresentationChanged` / `ControlVM.EnabledExifItems` / `EnabledControls` 三路均零结构事件 | `CoreContractCheck` 926 PASS / 1 SKIP / 0 FAIL；`SettingsDefaultsCheck` ALL PASS |
| Android | 三个下拉跨 2s/4s/6s 探测周期、重复布局和窄屏双阶段布局保持同一弹层；真实选择生效，入口移动/隐藏关闭；结构更新回到 UI 线程 | 最终 Debug APK 构建通过；API 35 模拟器 density 420：filter 31/0；density 525：filter 33/0。主屏 22/0、布局 18/0、输入 13/0 |
| macOS | 跨探测周期保留按钮、卡片和来源浮窗锚点；真实设置仍生效 | Debug 构建通过；toolbar/settings/layout/input/analysis/similarity/tools/filmstrip/faults 全 PASS（smoke 初轮 40 项通过）；最终共享代码复验通过 |
| iOS | 共享通道替代本端排除名单；菜单/按钮身份与来源浮窗跨探测周期保持；真实设置仍生效 | Debug 模拟器构建通过；iPad mini A17 Pro / iOS 26.3：layout 31 PASS、input 57 PASS；均使用最终共享代码 |
| Windows | 接入共享通道，补下拉/来源浮窗周期回归；原生运行验收须在 Windows 上执行 | `WindowsTypeCheck` 编译通过；新增 WinUI 周期回归未运行 |
| Web 参考壳 | 既有增量更新实现代码核对；设计令牌与纯逻辑回归 | 令牌最新，323 PASS / 0 FAIL |

证据：Android 最终两档下拉报告及相关回归位于 `artifacts/acceptance/settings-presentation/`；Apple 报告位于 `artifacts/acceptance/native-p2-acceptance/`（`toolbar-ui-checks.txt`、`ios-native-layout-checks.txt`、`ios-native-input-checks.txt`）。临时 SDK 选择文件、构建日志与构建写回的 Info.plist 版本号差异已清理，模拟器密度已恢复。

构建与原生检查只使用隔离设置和自有夹具副本。模拟器与程序化断言不替代真实设备、TalkBack 或作者手验；Windows 类型检查不等同于 WinUI 运行验收。

### 尚未关闭的验证边界与旁支失败

- Windows 新增的真实 ComboBox、来源浮窗与按钮身份跨周期回归已编译，但本机为 macOS，未在 WinUI 上运行；Mac 系统下拉持续展开的实操作用仍待手验，机测覆盖的是相关结构不重建及来源浮窗稳定。
- Android `settings` 回归为 62 PASS / 1 FAIL：倍率输入组中心偏差 5.5px（门槛 ≤1px）。本次没有修改该设置页布局，未修复此项，也未用旧二进制做对照归因。
- Android 验收曾命中项目已登记的启动期 SIGSEGV，重跑通过；崩溃根因仍未定位。iOS 首轮并行负载下出现一次 popover 布局超时，独立重跑及最终复验通过。
- iOS 构建被四处 `double` 到 `NFloat` 的 CS1503 阻塞；仅在约束 API 调用处增加显式 `(nfloat)` 转换以完成原生构建，不改控件几何。
- Android / Windows 构建仍有既有平台、重复原生库和 NuGet 漏洞数据缓存权限告警；未为清告警改动工具链或权限。
