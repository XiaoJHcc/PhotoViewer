# Fixtures

四端原生验收与 `Tools/CoreContractCheck` 共用的固定素材。这里是**测试输入**，随版本库分发；验收证据写 `artifacts/acceptance/`，分发包与打包过程产物写 `release/`，两者都不进版本库。

| 目录 | 内容 | 主要消费方 |
|---|---|---|
| `jpeg/` | 12 张 1200×800 色卡与 EXIF 方向图（`Tools/generate-native-color-fixtures.py` 生成） | Core 契约自检、Mac/iOS 色彩与方向套件 |
| `heif/` | 同色卡的 HEVC 容器，canonical XMP 在容器内 | 容器 XMP 写回、HEIF 解码 |
| `jpeg-large/` | 6000×4000 Display P3 大色卡 | iOS 代理→Full 档验收 |
| `toolbar/` | 3 张工具栏评分回归副本 | Mac 工具栏套件 |
| `raw/` | 两张真实 ILCE-7CM2 RAW（IFD0 tag 700 带 canonical XMP） | Core 契约自检、Mac/iOS RAW 验收 |

约定：

- 初始星级属于夹具契约：`jpeg/color-srgb.jpg` 与 `heif/color-srgb.heic` 内嵌 3 星，`raw/A7C01489.ARW` 为 0 星，测试按这些初值断言。
- 需要写评分/写回的验收只操作目录副本，不直接写这里的文件；RAW 就地评分用本地 `native-p2-raw/` 副本。
- 色卡可重新生成：`python3 Tools/generate-native-color-fixtures.py Fixtures/jpeg --large-output Fixtures/jpeg-large`（生成后把 `color-srgb.jpg` 的 `xmp:Rating` 写回 3）。
- 素材清单、初始星级与「缺件即失败」的检查口径见 [DEV.md](../DEV.md#验收素材)。
