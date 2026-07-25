# 照片解码加载方案 v2：统一优先级调度 + 代际闸 + 距离保护淘汰

> 2026-07-25 立项。目标：根治「首图不预载」「快速切换乱闪/错图」「主图与预载抢资源」三类问题，建立可推理的三级优先级解码体系。
> 涉及模块：`PhotoViewer/Core/Image/`（BitmapLoader、BitmapPrefetcher、新增 DecodeScheduler）、`ViewModels/Main/`（ImageViewModel、FolderViewModel、ThumbnailListViewModel）。

## 0. 现状与根因

链路：`CurrentFile` 变更 → 三路订阅：`ImageViewModel` 起主图解码、`ThumbnailListViewModel` 触发 `PrefetchAroundCurrent`、`MainViewModel` setter 里 `PreloadNearbyFiles()`（仅缩略图）。预取由 `BitmapPrefetcher` 串行 + 轮询退避；缓存为 `BitmapLoader` 静态 LRU（纯 `LastAccessTime`）。

| 问题 | 根因 | 位置 |
|---|---|---|
| 首图不预载（开单文件） | 阶段1 列表仅 1 项时预取空跑；阶段2 补全列表后 CurrentFile 不再变，不重触发 | `FolderViewModel.cs:357-396` |
| 首图预载慢（开文件夹） | 预取被 `WaitForHighPriorityIdleAsync` 轮询退避（缩略图通道繁忙，上限 5s） | `BitmapPrefetcher.cs:152-163` |
| 快速切换乱闪/定格错图 | `LoadImageAsync` 无代际闸，`SourceBitmap` 按解码完成顺序无条件赋值；切图时旧图保留显示 | `ImageViewModel.cs:99-104,144,181` |
| 同图重复解码 | `GetBitmapAsync` 无在途去重 | `BitmapLoader.cs:267-289` |
| 主图解码卡 UI | await 链无 `ConfigureAwait(false)`，`new Bitmap(stream)` 续体在 UI 线程 | `BitmapLoader.cs:309-324`（调用方 `ImageViewModel.cs:136`） |
| 新预取请求被丢弃 | `_busy` 门：上一轮未结束时新一轮整体丢弃而非重新排队 | `BitmapPrefetcher.cs:170` |
| 淘汰误伤临近/刚看过的图 | 纯 LRU，无距离保护 | `BitmapLoader.cs` Cleanup/EnsureCapacity/LruRemoveToSize |
| `IsCurrentImageLoading` 误判 | 用 `SourceBitmap == null && !IsInCache` 反推，切换瞬间旧图还在 → 误判空闲 | `BitmapPrefetcher.cs:140-147` |

## 1. 设计原则（优先级铁律）

- **P0 当前照片**：主图画面永远显示用户指定的当前照片；未解码完成就显示空白，绝不显示错图/旧图。P0 在途时后台任务暂停取新工作。
- **P1 高概率目标**：当前 ±1 邻居位图、可见区域缩略图、当前图缩略图。
- **P2 机会型预载**：±2..N 邻居位图、缩略图列表滚动停止落点附近位图。
- **P3 杂项**：分析缓存预热等纯 IO/CPU 任务。
- 淘汰顺序与加载优先级镜像：**从距当前照片最远处开始淘汰**，临近与刚浏览过的照片受保护。

## 2. 实施步骤

### Step 1 — 主图代际闸 + 切图即清屏（治乱闪）

`ImageViewModel.cs`：
- 新增单调递增 `_loadGeneration`（`Interlocked.Increment`）。每次 `CurrentFile` 变更：递增代际、捕获目标文件与代际号。
- `LoadImageAsync` 完成解码后，仅当 `generation == _loadGeneration && file 仍是 Main.CurrentFile.File` 才赋值 `SourceBitmap`；否则丢弃（位图留在缓存无害）。
- 切换瞬间：新当前图命中缓存 → 立即上屏；未命中 → `SourceBitmap = null`（显示空白）。旧图不再留守。
- 解码链路传 `CancellationToken`，新切换取消在途解码（`new Bitmap` 不可中断，但结果被代际闸丢弃；流读取/HEIF 可中途停）。
- 增强 token（`_enhanceToken`）逻辑保留，与代际闸正交。

### Step 2 — 解码移出 UI 线程 + 在途去重（治抢资源/重复解码）

`BitmapLoader.cs`：
- `GetBitmapAsync` 增加在途去重：`ConcurrentDictionary<string, Lazy<Task<Bitmap?>>>`，同路径请求加入同一任务；完成后移除。缓存命中仍走快路径。
- 解码本体（流读取、`new Bitmap`、EXIF 旋转、格式转换）包进 `Task.Run`，全链 `ConfigureAwait(false)`，确保续体不回 UI 线程。
- 保持现有 API 签名兼容（增量加 `CancellationToken` 可选参数）。

### Step 3 — 首图即预载 + latest-wins（治现象1）

- `FolderViewModel.LoadFolderAsync`：阶段2 列表补全（第二次 `AllFilesChanged` / `SetCurrentFileAsync`）之后，显式补触发一次 `PrefetchAroundCurrent`（经 `ThumbnailListViewModel` 转发，避免 FolderViewModel 直接依赖 Prefetcher）。
- `BitmapPrefetcher.RunQueuedAsync`：去掉 `_busy` 整体丢弃，改 latest-wins——新请求取消旧 CTS 并重新排队（Step 2 的去重保证不重复解码）。
- 删除 `Task.Delay(15/30)` 节流；`WaitForHighPriorityIdleAsync` 改为消费 DecodeScheduler 的「P0 在途」标志（Step 4 落地后切换，Step 3 阶段先保留轮询但缩短上限）。

### Step 4 — 统一优先级调度器 DecodeScheduler（结构性）

新增 `PhotoViewer/Core/Image/DecodeScheduler.cs`（静态类）：
- 工作项：`(string key, int priority, Func<CancellationToken, Task> work)`，优先级枚举 P0..P3。
- **去重**：同 key 在途/在队则合并；仍在队列中的项用最新请求替换工作本体（latest-wins，旧工作可能携带已取消的轮次 token），高优先级请求同时提升其优先级。
- **执行**：P0 立即在独立 Task 执行（不进 worker 队列），并置「P0 在途」标志；后台 worker（并发度 = `Settings.NativePreloadParallelism`）取任务前先检查该标志，P0 在途则等待。
- 收编两个通道：
  - `ThumbnailListViewModel` 的 3 消费者 `_thumbnailLoadQueue` 改为向 DecodeScheduler 投递 P1 工作项（`LoadVisibleThumbnails` 的队首优先语义由优先级天然实现，`QueueThumbnailLoad(priority: true)` → P1，普通 → P2）。`IsThumbnailLoadingBusy()` 改为查询调度器 P1 队列深度。
  - `BitmapPrefetcher` 不再自建 `SemaphoreSlim`/Delay，把每张邻居位图解码作为 P2 工作项投递；`PrewarmAnalysisAsync` 作为 P3。
- View 滚动防抖上报（`ThumbnailListView.axaml.cs`）不变。

### Step 5 — 淘汰距离保护（治误删）

`BitmapLoader.cs`：
- 新增保护集：`static void SetProtectedPaths(IReadOnlyCollection<string> paths)`（内部存 `HashSet<string>`，volatile 替换）。
- 所有淘汰路径（`EnsureCapacityAsync`、`CleanupCache`、`LruRemoveToSize`、`TrimOnMemoryWarning/TrimToCurrentRatio`）统一：先跳过保护集，非保护项按 LRU 淘汰；内存告警强制 trim 时保护集也参与，但排到最后。
- 保护集上报方：`ThumbnailListViewModel` 在 `CurrentFile` 变更时计算 `FilteredFiles` 中 `当前 ± (PreloadBackward+PreloadForward)` 与最近浏览 M 张（M=窗口大小）的路径集合，调用 `SetProtectedPaths`。

### 验证

- `dotnet build PhotoViewer/PhotoViewer.csproj`（核心库）+ `dotnet build PhotoViewer.Desktop/PhotoViewer.Desktop.csproj` 通过。
- 手测清单：
  1. 打开文件夹 → 首图上屏后立即开始预载邻居（缩略图缓存边框/日志可观测）；
  2. 打开单个文件 → 同上（阶段2 补触发）；
  3. 快速连按方向键穿越未缓存区 → 无乱闪，最终定格当前照片，中间空白；
  4. 长跑浏览 + 内存逼近上限 → 临近照片不被淘汰，远端先淘汰。

## 3. 实施顺序

Step 1–3（中小改动、立竿见影，一次可验证增量）→ Step 4–5（结构性改动）。每步完成后编译验证。
