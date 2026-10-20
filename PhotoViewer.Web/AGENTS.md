# PhotoViewer.Web — Web 参考壳

浏览器里的第五个壳（平台代号 `web`）：完整复刻主屏的业务行为与 macOS 视觉，照片为程序化合成数据。它同时是三样东西：免下载的在线试用、设计规则的可执行基准（规则变更可先在这里验证再分发四端）、各平台几何比对的基准来源（替代原 `Design/prototype/` 样机）。设置窗口与工具窗口不做（`SCOPE-01@web`）。计划与进度见 [Plans/web-shell-plan-1.md](../Plans/web-shell-plan-1.md)，文档缺口台账见 [Plans/web-shell-gaps.md](../Plans/web-shell-gaps.md)。

## 唯一依据：Design/

**硬规则**：本壳只依据 `Design/`（`spec/`、`tokens.json`、`README.md`、`platforms.md` 中的 `@web` 条目）实现，**不读取** `PhotoViewer/`、`PhotoViewer.*/`（本目录除外）、`Design/baseline/`、`release/` 下的源码与验收常量，也不照搬其他平台的写法。规则缺失、含糊或无法实现时，按最合理的理解实现，同时在缺口台账追加一条（分类写 `待审`），不得去其他平台的代码里找答案。这样本壳与 Mac 的每一处差异都能定位到"文档没写清"或"某端没照文档做"。

## 运行与测试

```bash
cd PhotoViewer.Web
python3 -m http.server 8080      # 打开 http://localhost:8080/
node --test "tests/**/*.test.js" # 纯逻辑单测（零依赖）
```

VS Code Tasks：**Serve Web**、**Test Web**。ES module 在 `file://` 下会被浏览器拦截，必须经本地服务打开。令牌产物 `src/tokens.g.js` 由 `python3 Tools/generate-design-tokens.py` 生成，不得手改。

页面参数：`?demo` 打开窗口外演示面板（尺寸、设置、故障注入、状态读数、几何导出）；`?check=<模块|all>` 运行页内行为检查并输出按设计 ID 的报告；`?platform=mac` 切到 mac 配置档（只为几何比对：交通灯让位等 `@mac` 差异）；`?empty` 以空态启动；`?folder=<id>` 指定首个文件夹。

## 结构与分层

```text
index.html
src/
  tokens.g.js           # 生成；默认导出基准值，platformTokens 导出平台覆盖
  core/                 # 纯逻辑：无 DOM、无全局状态，可在 Node 里单测
    types.js            # 模块间共用数据形状（JSDoc）
    tokens.js           # 按平台配置档取令牌（缺令牌立即抛错）
    clock.js            # 可注入的时间源与定时器（单测用假时钟）
    settings.js         # 设置模型默认值（只取 spec/settings.md 中影响主屏的项）
    library.js          # 合成照片库
    filter.js           # 同名合并、格式、星级筛选、排序、暂留、锚点翻页
    viewport.js         # 主图视口数学
    filmscroll.js       # 胶片栏几何（步长/内容长/居中偏移）、定位动画器与定位门控
    features.js         # 合成 DINO/CV 特征（同一连拍场景彼此相似）
    input.js            # 手势编码、主/次绑定、Repeatable、滚轮分流
    session.js          # 加载/代理→Full/增强/药丸/提示条时序
    analysis.js         # 分析栏瓦片集合、三态、准星、cosine
    similarity.js       # 相似面板三态、计算/取消、提取进度
    exifformat.js       # 控制栏 EXIF 值与卡片时间格式化
    app.js              # 主屏模型：整合以上模块，对外只暴露状态快照、动作与事件
    layout/             # 分区与各区几何（只依据 spec + tokens 推导）；胶片栏步长取 filmscroll.js
  render/               # 浏览器实现：procedural（矢量程序化照片，存储朝向）、decoder（代理/Full/增强）、store（特征库）、thumbs（缩略图队列）
  shell/                # DOM 组件：订阅 app 事件按区域增量更新，不整窗重建
    dom.js / menu.js    # 组件约定与工具（el/place/令牌 CSS 变量 --pv-<点分名转连字符，保留驼峰>）；共用弹出菜单
    filterbar / filmstrip / card / similarity / photo / analysis / controlbar / picker  # 各分区组件与 .css
  checks/               # ?check=<模块|all> 页内行为检查，按设计 ID 出报告
  demo/                 # 窗口外演示面板（尺寸、设置项、故障注入、减少动态效果）
tests/                  # node --test，测试名以设计 ID 开头
dev/                    # 开发用（不进产品）：render-preview（方向码/代理/Full/增强/连拍目视）、<组件>-harness（单组件自查页）
tools/export-geometry.mjs  # 导出几何 JSON（--profile mac 与 macOS 报告比对；--count-width 固定计数宽，Design/README §8）
```

- **依赖方向**：`shell/ → core/app.js → core/*`；`core/` 不得引用 `shell/`、`document`、`window`。时间一律经 `clock.js`，便于假时钟断言 300/350/500/4000 ms 等门槛。
- **状态唯一**：业务状态只在 `app.js`；组件不缓存第二份（菜单勾选每次弹出重读快照，MENU-02）。
- **单位**：几何一律为逻辑点（CSS px = pt）；物理 100% 用 `devicePixelRatio` 换算（IMAGE-01）。
- **注释**：默认不写；只在实现某条规则的关键分支写 `// FILM-03：<为什么>`。不写阶段号。
- **风格**：4 空格缩进、双引号、分号；命名导出；不引入任何第三方依赖。

## 平台差异（`@web`）

登记在 [Design/platforms.md](../Design/platforms.md)，本目录不另存一份清单。
