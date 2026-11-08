# Fixtures

四端原生验收与 `Tools/CoreContractCheck` 共用的固定素材。这里是**测试输入**，随版本库分发；验收证据写 `artifacts/acceptance/`，分发包与打包过程产物写 `release/`，两者都不进版本库。

| 目录 | 内容 | 主要消费方 |
|---|---|---|
| `jpeg/` | 1200×800 色卡 `color-srgb.jpg` / `color-p3.jpg` / `color-adobe.jpg` / `color-untagged-adobe.jpg` 与 `orientation-1..8.jpg`，共 12 张（`Tools/generate-native-color-fixtures.py` 生成） | Core 契约自检、四端色彩与方向套件、Windows 设置及双版本往返 |
| `heif/` | 同色卡的 HEVC 容器 `color-srgb.heic` / `color-p3.heic`，canonical XMP 在容器内 | 容器 XMP 写回、HEIF 解码 |
| `jpeg-large/color-p3-large.jpg` | 6000×4000 Display P3 大色卡 | iOS 代理→Full 档验收、Windows AI 批量提取压力验收 |
| `toolbar/` | 工具栏评分回归的自有副本 `toolbar-01..03.jpg` | Mac 工具栏套件 |
| `raw/` | 真实 ILCE-7CM2 RAW `A7C01489.ARW` / `A7C01495.ARW`；IFD0 tag 700 带 canonical XMP，`A7C01489` 的 `xmpMM:DocumentID` 固定为 `urn:uuid:e342971a-5835-4d56-b3ff-f434089d2e00` | Core 契约自检、四端 RAW 验收 |

约定：

- 初始星级属于夹具契约：`jpeg/color-srgb.jpg` 与 `heif/color-srgb.heic` 内嵌 3 星，`raw/A7C01489.ARW` 为 0 星，测试按这些初值断言。
- 需要写评分/写回的验收只操作目录副本，不直接写这里的文件；RAW 就地评分用 `artifacts/scratch/native-p2-raw/` 副本（不入库，可随时重建）。
- 色卡可重新生成：`python3 Tools/generate-native-color-fixtures.py Fixtures/jpeg --large-output Fixtures/jpeg-large`。生成脚本产出的 JPEG 默认 0 星，重新生成后须把 `color-srgb.jpg` 的 `xmp:Rating` 写回 3；HEIF 素材不在生成脚本内，按仓库内既有容器包使用。
- Core 契约自检在仓库根运行 `dotnet run --project Tools/CoreContractCheck -c Release`；素材不全时在开跑前报缺件并非零退出，不跑半套结果。
- Windows 原生验收用启动参数 `--pv-acceptance-fixtures=<本目录绝对路径>`（取值是本目录本身，不是仓库根）显式取素材，与只决定报告与隔离状态的 `--pv-acceptance-output` 分离：不依赖启动工作目录、不做程序目录或祖先搜索、不读环境变量兜底。每套检查只要求自己用到的文件，缺参、相对路径或所需缺件在创建窗口前失败（退出码 1，隔离日志与 `fixture-checks.json` 记 `FAIL`）。**Check Windows Native** / **Check Windows Color** 四个 Task 传 `${workspaceFolder}/Fixtures`，发布脚本传自身仓库根下的 `Fixtures/`。素材始终只读，验收自建的副本与报告写输出目录；固定素材随版本库分发，不随产品包携带。
