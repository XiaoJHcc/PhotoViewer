using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 一次打开会话：持有本次打开的存储访问租约、取消源与工作列表。
/// 工作列表只在调用方确认提交后成为 <see cref="FolderViewModel.AllFiles"/>；
/// 后发会话经 <see cref="Cancel"/> 取代先发的枚举、属性与星级任务，旧结果不再发布。
/// </summary>
internal sealed class FolderLoadSession : IDisposable
{
    /// <summary>属性与星级补齐的并发上限（与旧打开流程同口径）。</summary>
    private const int BackgroundParallelism = 8;

    private readonly FolderViewModel _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _backgroundTasks = new();
    private readonly List<ImageFile> _workFiles = new();
    private readonly RatingSidecarIndex _sidecarIndex = new();
    private readonly IStorageAccessSession? _lease;
    private ImageFile? _targetImage;
    private bool _disposed;
    private volatile bool _enumerationCompleted;

    /// <summary>
    /// 本次打开的 sidecar 索引：只有枚举<b>成功完成</b>后才暴露；枚举前/枚举中返回 null，
    /// 让读取回退到逐文件查找，避免把尚未建好的空索引当作“目录没有 sidecar”。
    /// 单图模式恒为 null。
    /// </summary>
    internal RatingSidecarIndex? SidecarIndex =>
        Folder != null && _enumerationCompleted ? _sidecarIndex : null;

    /// <summary>创建一次打开会话，并立即取得目标文件夹（或单图模式下的文件）的访问租约。</summary>
    /// <param name="owner">发起本次打开的文件源视图模型。</param>
    /// <param name="folder">要枚举的文件夹；单图模式为 null。</param>
    /// <param name="target">本次打开希望选中的图片；可为 null。</param>
    public FolderLoadSession(FolderViewModel owner, IFolderRef? folder, IFileRef? target)
    {
        _owner = owner;
        Folder = folder;
        TargetFile = target;
        _lease = AcquireLease(folder, target);
    }

    /// <summary>本次会话要枚举的文件夹；单图模式为 null。</summary>
    public IFolderRef? Folder { get; }

    /// <summary>本次打开希望选中的图片。</summary>
    public IFileRef? TargetFile { get; }

    /// <summary>本次会话的取消令牌，贯穿枚举、属性与星级读取。</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>本次会话是否已被取消（被后发打开取代或视图模型释放）。</summary>
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    /// <summary>是否已经发布过快照；已发布的会话在取消后仍需保留访问租约。</summary>
    public bool HasCommitted { get; private set; }

    /// <summary>工作列表：枚举结果与指定目标都在这里，提交前不属于 <see cref="FolderViewModel.AllFiles"/>。</summary>
    public IReadOnlyList<ImageFile> WorkFiles => _workFiles;

    /// <summary>把指定目标（若有）建成列表对象并加入工作列表；重复调用返回同一对象。</summary>
    /// <returns>目标图片对象；没有指定目标时为 null。</returns>
    public ImageFile? EnsureTargetImage()
    {
        if (TargetFile == null || _targetImage != null) return _targetImage;
        if (!_owner.IsImageFile(TargetFile.Name)) return null;
        _targetImage = new ImageFile(TargetFile)
        {
            RatingSidecarIndex = SidecarIndex,
        };
        _workFiles.Add(_targetImage);
        return _targetImage;
    }

    /// <summary>读取指定目标的星级（首张上屏前需要，保证星级筛选下的合并组代表正确）。</summary>
    public Task LoadTargetRatingAsync()
    {
        var target = _targetImage;
        return target == null ? Task.CompletedTask : target.LoadRatingOnlyAsync(Token, SidecarIndex);
    }

    /// <summary>
    /// 在后台线程枚举文件夹到工作列表：只收当前启用格式、排除点号隐藏文件，
    /// 按 <see cref="StorageItemId"/> 去重（指定目标不重复入列）。
    /// 同一次枚举顺带建立 sidecar 索引，后续评分为每个文件读取时不再查找目录。
    /// </summary>
    public async Task EnumerateAsync()
    {
        if (Folder == null) return;

        _enumerationCompleted = false;
        var token = Token;
        var seen = new HashSet<StorageItemId>(_workFiles.Select(file => file.File.StorageId));
        var enumerated = new List<ImageFile>();

        await foreach (var item in Folder.GetItemsAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (item is not IFileRef file) continue;
            // sidecar 不是照片，但它是独立评分来源：一次枚举内建好索引。
            if (_sidecarIndex.Add(file)) continue;
            if (!_owner.IsImageFile(file.Name)) continue;
            if (!seen.Add(file.StorageId)) continue;
            enumerated.Add(new ImageFile(file)
            {
                RatingSidecarIndex = SidecarIndex,
            });
        }

        _workFiles.AddRange(enumerated);

        // 只有完整枚举成功后才把索引视为权威（可能为空），并把真实索引交给所有工作文件；
        // 枚举前/枚举中的读取已经按 null 回退到逐文件查找，不会漏 sidecar。
        _enumerationCompleted = true;
        foreach (var file in _workFiles)
            file.RatingSidecarIndex = _sidecarIndex;
    }

    /// <summary>
    /// 启动第三阶段：后台补齐基本属性与星级。任务归会话所有，取消后不再写入结果，
    /// 异常全部在会话内观察（不产生未观察的 Task）。
    /// </summary>
    /// <param name="snapshot">本次提交的列表快照。</param>
    /// <param name="ratingsLoaded">星级全部就绪后回到 UI 线程的提交回调。</param>
    public void StartBackgroundLoads(IReadOnlyList<ImageFile> snapshot, Func<Task> ratingsLoaded)
    {
        if (snapshot.Count == 0) return;
        Track(Task.Run(() => LoadPropertiesAsync(snapshot)));
        Track(Task.Run(() => LoadRatingsAsync(snapshot, ratingsLoaded)));
    }

    /// <summary>标记本次会话的快照已发布。</summary>
    public void MarkCommitted() => HasCommitted = true;

    /// <summary>取消会话：停止枚举与后台补齐，已发布快照保留到下一次提交。</summary>
    public void Cancel()
    {
        if (_disposed) return;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放的会话无需再取消。
        }
    }

    /// <summary>释放会话：取消在途工作并归还本次打开的访问租约。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _lease?.Dispose();
        _cancellation.Dispose();
        _backgroundTasks.Clear();
    }

    private async Task LoadPropertiesAsync(IReadOnlyList<ImageFile> snapshot)
    {
        var token = Token;
        using var semaphore = new SemaphoreSlim(BackgroundParallelism);
        var tasks = snapshot.Select(async file =>
        {
            try
            {
                await semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!token.IsCancellationRequested)
                    await file.LoadBasicPropertiesAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 后发打开已接管：旧会话的属性结果不发布。
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载文件 {file.Name} 属性失败: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadRatingsAsync(IReadOnlyList<ImageFile> snapshot, Func<Task> ratingsLoaded)
    {
        var token = Token;
        using var semaphore = new SemaphoreSlim(BackgroundParallelism);
        var tasks = snapshot.Select(async file =>
        {
            try
            {
                await semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!token.IsCancellationRequested)
                    await file.LoadRatingOnlyAsync(token, SidecarIndex).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 后发打开已接管：旧会话的星级结果不发布。
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载文件 {file.Name} 星级失败: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (token.IsCancellationRequested) return;

        try
        {
            await ratingsLoaded().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发布文件星级失败: {ex.Message}");
        }
    }

    private void Track(Task task)
    {
        _backgroundTasks.Add(task);
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 为本次打开建立存储访问租约。平台实现覆盖不到的存储项（如本地会话遇到 provider 项）
    /// 返回 null 而不阻断打开；其余失败交给打开流程按失败上报。
    /// </summary>
    private IStorageAccessSession? AcquireLease(IFolderRef? folder, IFileRef? target)
    {
        IStorageItemRef? root = folder != null ? folder : target;
        if (root == null) return null;

        IStorageAccessSession? lease = null;
        try
        {
            lease = _owner.Main.StorageAccess.Create(root);
            return lease;
        }
        catch (StorageException ex) when (ex.Error == StorageError.Unsupported)
        {
            lease?.Dispose();
            return null;
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }
}
