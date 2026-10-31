using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.ViewModels.Main;

/// <summary>排序下拉选项(显示名 + 枚举值)</summary>
public class SortOption
{
    public string DisplayName { get; set; } = null!;
    public object Value { get; set; } = null!;
}

/// <summary>排序方式</summary>
public enum SortMode { Name, Date, Rating, Size }
/// <summary>排序方向</summary>
public enum SortOrder { Ascending, Descending }

/// <summary>
/// 文件源视图模型:仅负责打开/加载文件夹与单文件,维护 <see cref="AllFiles"/> 原始集合。
/// 筛选、排序、缩略图队列、位图预取等由 <see cref="File.ThumbnailListViewModel"/> 订阅本 VM 的事件后完成。
/// 每次打开由一次 <see cref="FolderLoadSession"/> 承担：它持有访问租约、取消源与工作列表，
/// 后发打开取消先发，只有提交成功的快照才替换 <see cref="AllFiles"/>。
/// </summary>
public class FolderViewModel : ReactiveObject, IDisposable
{
    public MainViewModel Main { get; }

    private List<ImageFile> _allFiles = new();
    /// <summary>原始文件集合(未筛选、未排序),写入星级后同步刷新</summary>
    public IReadOnlyList<ImageFile> AllFiles => _allFiles;

    private IFolderRef? _currentFolder;
    /// <summary>当前已提交的图片目录;单图模式为 null。</summary>
    public IFolderRef? CurrentFolder => _currentFolder;

    private string _folderName = "";
    /// <summary>当前文件夹名称(若为单图模式则为文件名)</summary>
    public string FolderName
    {
        get => _folderName;
        set => this.RaiseAndSetIfChanged(ref _folderName, value);
    }

    /// <summary>原始文件集合发生变化时触发(新增/清空/重载),由 ThumbnailListViewModel 订阅以重算筛选</summary>
    public event Action? AllFilesChanged;

    /// <summary>请求 View 滚动到当前文件(事件转发给 ThumbnailListView)</summary>
    public event Action? ScrollToCurrentRequested;

    /// <summary>请求优先加载某张图的缩略图(由 ThumbnailListViewModel 订阅)</summary>
    public event Action<ImageFile>? PriorityThumbnailRequested;

    /// <summary>最新一次打开会话（取消链的持有者）。</summary>
    private FolderLoadSession? _activeSession;

    /// <summary>
    /// 尚未归还访问租约的会话：在途会话 + 已发布过快照的会话。
    /// 新会话提交成功时统一归还其余会话，保证旧快照在新会话成功前不失去权限。
    /// </summary>
    private readonly List<FolderLoadSession> _liveSessions = new();

    private bool _disposed;

    /// <summary>
    /// 构造文件源视图模型。
    /// </summary>
    /// <param name="main">主视图模型</param>
    public FolderViewModel(MainViewModel main)
    {
        Main = main;
    }

    /// <summary>
    /// 触发滚动到当前文件事件。
    /// </summary>
    public void ScrollToCurrent() => ScrollToCurrentRequested?.Invoke();

    /// <summary>
    /// 结束文件源生命周期：取消在途打开与后台补齐，归还全部访问租约（OPEN-08）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeSession = null;
        foreach (var session in _liveSessions.ToList()) session.Dispose();
        _liveSessions.Clear();
    }

    ////////////////
    // 打开文件
    ////////////////

    #region OpenFile

    /// <summary>
    /// 打开文件选择器。平台通过 <see cref="IFilePickerService.PreferFolderPicker"/> 声明主入口是目录选择
    /// （触屏），否则为单文件选择；选择器自身失败经 <see cref="MainViewModel.OpenFailed"/> 上报。
    /// </summary>
    public async Task OpenFilePickerAsync()
    {
        if (_disposed) return;

        IFileRef? file = null;
        IFolderRef? folder = null;
        try
        {
            if (FilePicker.PreferFolderPicker)
            {
                folder = await FilePicker.PickFolderAsync("选择图片文件夹");
            }
            else
            {
                file = await FilePicker.PickFileAsync("选择图片", GetFilePickFilters());
            }
        }
        catch (Exception exception)
        {
            // STATE-02/STATE-03：选择器失败要留下可见原因，不得静默无响应（OPEN-01）。
            Console.Error.WriteLine($"[Open] 选择器失败：{exception.Message}");
            Main.ReportOpenFailure(StatusMessages.OpenFailed(null, StatusMessages.ReasonFor(exception)));
            return;
        }

        if (folder != null)
        {
            await OpenFolderAsync(folder);
        }
        else if (file != null)
        {
            await OpenImageAsync(file);
        }
    }

    /// <summary>
    /// 打开外部传入的一组文件,优先使用首个可识别图片。
    /// </summary>
    /// <param name="files">外部传入的文件集合</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    /// <param name="resetView">是否让主图回到适应态;重复外部激活同一目标时传 false</param>
    public async Task OpenExternalFilesAsync(
        IEnumerable<IFileRef> files,
        bool scrollToCurrent = true,
        bool resetView = true)
    {
        var targetFile = files.FirstOrDefault(file => IsImageFile(file.Name));
        if (targetFile == null)
        {
            return;
        }
        await OpenImageAsync(targetFile, scrollToCurrent, resetView);
    }

    /// <summary>
    /// 打开指定图片,并尽量进入其所在文件夹;若平台权限不支持,则回退为单图模式。
    /// 显式打开的图片一律重新枚举父目录（新增/删除、全部评分来源、属性与 EXIF），
    /// 当前目标只刷新不打断视口；新目标按 <paramref name="resetView"/> 回到适应态。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    /// <param name="resetView">是否让主图回到适应态（OPEN-05）;当前目标始终不 Fit</param>
    /// <param name="refresh">
    /// 是否重新枚举父目录并刷新元数据。显式打开默认为 true；只有确属纯选中（不重新读取磁盘）的
    /// 内部调用才显式传 false。
    /// </param>
    public async Task OpenImageAsync(IFileRef file, bool scrollToCurrent = true, bool resetView = true, bool refresh = true)
    {
        if (_disposed || !IsImageFile(file.Name))
        {
            return;
        }

        // OPEN-05：新目标回到适应态；当前目标（含同组伴侣）只刷新，保留当前视口。
        if (resetView && !IsCurrentTarget(file)) Main.RequestFitToView();

        try
        {
            var committed = await OpenImageInFolderContextAsync(file, refresh);
            if (committed && scrollToCurrent)
            {
                ScrollToCurrent();
            }
        }
        catch (Exception exception)
        {
            // 打开过程中的意外异常也要有可见出口，不能只写日志（STATE-02）。
            ReportUnexpectedOpenFailure(file.Name, exception);
        }
    }

    /// <summary>目标是否就是当前照片（或它的同组伴侣）：这类显式打开属于刷新，不重新 Fit。</summary>
    private bool IsCurrentTarget(IFileRef file)
    {
        var current = Main.CurrentFile;
        return current != null &&
               (HasSameStorageId(file, current.File) ||
                current.Group?.Files.Any(member => HasSameStorageId(file, member.File)) == true);
    }

    /// <summary>
    /// 打开指定文件夹,并定位到筛选后的第一张图片。
    /// </summary>
    /// <param name="folder">要打开的文件夹</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    /// <param name="resetView">是否让主图回到适应态（OPEN-05）;重复外部激活同一目标时传 false</param>
    public async Task OpenFolderAsync(IFolderRef folder, bool scrollToCurrent = true, bool resetView = true)
    {
        if (_disposed) return;

        // 只有切到新目录才是新的观片（OPEN-05）；同目录入口重复打开属于刷新，保留视口。
        var isSameFolder = HasSameStorageId(folder, _currentFolder);
        if (resetView && !isSameFolder) Main.RequestFitToView();

        // OPEN-08：同目录重开视为刷新,以当前照片为首选目标,且保留缩略图缓存。
        var hasExistingList = isSameFolder && _allFiles.Count > 0;
        var preferredFile = hasExistingList && Main.CurrentFile != null && _allFiles.Contains(Main.CurrentFile)
            ? Main.CurrentFile.File
            : null;

        try
        {
            var committed = await LoadFolderAsync(folder, preferredFile, targetIsExplicit: false);
            if (committed && scrollToCurrent)
            {
                ScrollToCurrent();
            }
        }
        catch (Exception exception)
        {
            // 打开过程中的意外异常也要有可见出口，不能只写日志（STATE-02）。
            ReportUnexpectedOpenFailure(folder.Name, exception);
        }
    }

    /// <summary>
    /// 兼容旧调用路径:打开新图片所在文件夹。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    public async Task LoadNewImageFolder(IFileRef file)
    {
        await OpenImageAsync(file, scrollToCurrent: false);
    }

    /// <summary>
    /// 构建文件选择器的类型过滤器(显示名 + 扩展名模式;UTI/MIME 推导在选择器实现内)。
    /// </summary>
    private List<FilePickFilter> GetFilePickFilters()
    {
        var filters = new List<FilePickFilter>
        {
            new("所有图片", Main.Settings.SelectedFormats.Select(format => $"*{format}").ToArray())
            {
                IsGenericImage = true
            }
        };

        foreach (var formatItem in Main.Settings.FileFormats.Where(f => f.IsEnabled))
        {
            filters.Add(new FilePickFilter($"{formatItem.DisplayName} 文件",
                formatItem.Extensions.Select(ext => $"*{ext}").ToArray()));
        }

        return filters;
    }

    #endregion


    ////////////////
    /// 加载文件夹
    ////////////////

    #region LoadFolder

    /// <summary>
    /// 优先尝试以"文件夹上下文"打开图片;父目录不可得时回退为单图模式（OPEN-03）。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <returns>本次打开是否已提交（被后发打开取代时为 false）</returns>
    private async Task<bool> OpenImageInFolderContextAsync(IFileRef file, bool refresh)
    {
        IFolderRef? folder = null;
        try
        {
            // Provider 查询在后台线程完成，不在 UI 线程做 I/O。
            folder = await Task.Run(async () => await file.GetParentAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Open] 获取父目录失败：{file.Name} — {exception.Message}");
        }

        return await UiDispatcher
            .InvokeAsync(() => OpenImageInFolderContextOnUiAsync(file, folder, refresh))
            .Unwrap()
            .ConfigureAwait(false);
    }

    private async Task<bool> OpenImageInFolderContextOnUiAsync(IFileRef file, IFolderRef? folder, bool refresh)
    {
        if (_disposed) return false;

        if (folder == null)
        {
            var opened = refresh && FindLoadedFile(file) is { } loaded
                ? await ReloadFileAsync(loaded)
                : await OpenSingleImageAsync(file);
            if (opened && refresh) Main.ReportOpenFailure(StatusMessages.SinglePhotoRefreshed);
            return opened;
        }

        if (refresh)
        {
            // 只有目标父目录就是当前目录时才按“同目录刷新”处理（无临时单图快照、失败保留列表）；
            // 跨目录是显式打开：目标先上屏，枚举失败按 OPEN-03 回退单图。
            var sameFolder = HasSameStorageId(folder, _currentFolder);
            if (sameFolder && _allFiles.Count > 0)
            {
                var target = FindLoadedFile(file)?.File ?? file;
                return await LoadFolderAsync(folder, target, targetIsExplicit: false, refreshMode: true);
            }

            return await LoadFolderAsync(folder, file, targetIsExplicit: true, refreshMode: false);
        }

        if (HasSameStorageId(folder, _currentFolder) && _allFiles.Count > 0)
        {
            // 纯选中：目标已在当前目录模型中只切换，不重建目录；伴侣文件经同名组合映射到代表文件。
            var existingFile = FindLoadedFile(file);
            if (existingFile != null)
            {
                SetCurrentFile(ResolveTargetSelection(existingFile), session: null);
                return true;
            }

            return await AddFileToCurrentFolderAsync(folder, file);
        }

        return await LoadFolderAsync(folder, file, targetIsExplicit: true);
    }

    /// <summary>
    /// 目标不在当前目录模型中时补入列表后选中；不重新枚举目录。
    /// </summary>
    private async Task<bool> AddFileToCurrentFolderAsync(IFolderRef folder, IFileRef file)
    {
        var session = BeginSession(folder, file);
        var imageFile = session.EnsureTargetImage();
        if (imageFile == null)
        {
            AbandonSession(session);
            return false;
        }

        try
        {
            await session.LoadTargetRatingAsync().ConfigureAwait(false);
            // 补入列表的文件同样要有大小/修改时间，避免排序与卡片属性空一帧。
            await Task.Run(() => imageFile.LoadBasicPropertiesAsync(session.Token), session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbandonSession(session);
            return false;
        }

        if (!IsSessionActive(session))
        {
            AbandonSession(session);
            return false;
        }

        await UiDispatcher.InvokeAsync(() =>
        {
            if (!IsSessionActive(session)) return;

            var files = new List<ImageFile>(_allFiles);
            if (!files.Any(candidate => candidate.File.StorageId == imageFile.File.StorageId))
            {
                files.Add(imageFile);
            }

            PublishSnapshot(session, files, folder, folder.Name, clearThumbnails: false, retireSuperseded: true);
            SetCurrentFile(ResolveTargetSelection(imageFile), session);
        });

        return IsSessionActive(session);
    }

    /// <summary>
    /// 以单图模式打开图片：父目录不可访问、枚举被拒或只授予单文件权限时，至少让用户看到这一张（OPEN-03）。
    /// </summary>
    private async Task<bool> OpenSingleImageAsync(IFileRef file)
    {
        var session = BeginSession(folder: null, target: file);
        var imageFile = session.EnsureTargetImage();
        if (imageFile == null)
        {
            AbandonSession(session);
            return false;
        }

        try
        {
            await session.LoadTargetRatingAsync().ConfigureAwait(false);
            // 单图也要有大小/修改时间：排序、卡片与控制栏不能空着等后续补齐。
            await Task.Run(() => imageFile.LoadBasicPropertiesAsync(session.Token), session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbandonSession(session);
            return false;
        }

        if (!IsSessionActive(session))
        {
            AbandonSession(session);
            return false;
        }

        await UiDispatcher.InvokeAsync(() =>
        {
            if (!IsSessionActive(session)) return;
            PublishSnapshot(
                session,
                new List<ImageFile> { imageFile },
                folder: null,
                folderName: imageFile.Name,
                clearThumbnails: false,
                retireSuperseded: true);
            SetCurrentFile(imageFile, session);
        });

        return IsSessionActive(session);
    }

    /// <summary>
    /// 加载文件夹内容，并可选定位到指定图片。
    /// 阶段 1 先让指定目标上屏（同目录刷新跳过），阶段 2 在后台枚举到工作列表后一次提交，
    /// 阶段 3 后台补齐属性与星级；任何阶段被后发打开取代都不发布结果。
    /// </summary>
    /// <param name="folder">要加载的文件夹</param>
    /// <param name="preferredFile">优先定位的图片;为空时打开第一张</param>
    /// <param name="targetIsExplicit">目标是调用方显式指定的文件（失败时按 OPEN-03 回退单图），而非同目录刷新沿用的当前图</param>
    /// <param name="refreshMode">
    /// 显式打开触发的刷新：跳过阶段 1 的临时单图快照；枚举失败时保留旧列表、
    /// 只刷新仍然可访问的文件并给出 STATE 提示，不把整个目录降级成单图。
    /// </param>
    /// <returns>本次打开是否已提交</returns>
    private async Task<bool> LoadFolderAsync(
        IFolderRef folder,
        IFileRef? preferredFile,
        bool targetIsExplicit,
        bool refreshMode = false)
    {
        var isRefresh = HasSameStorageId(folder, _currentFolder) && _allFiles.Count > 0;
        // 只有切换到不同目录才整批释放旧缩略图（OPEN-08）；临时快照不改变该判断。
        var clearThumbnailsOnCommit = !HasSameStorageId(folder, _currentFolder);

        var session = BeginSession(folder, preferredFile);
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine("[Folder] === BEGIN OpenFolder: " + folder.StorageId.Value);

        // ── 阶段 1:优先显示目标图片（刷新不发布临时快照，但新目标仍先读好评分/来源） ──
        var targetFile = session.EnsureTargetImage();
        var publishTarget = targetFile != null && !isRefresh && !refreshMode;
        var targetIsNew = preferredFile != null && FindLoadedFile(preferredFile) == null;
        if (targetFile != null && (publishTarget || targetIsNew))
        {
            try
            {
                await session.LoadTargetRatingAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                AbandonSession(session);
                return false;
            }

            if (!IsSessionActive(session))
            {
                AbandonSession(session);
                return false;
            }

            if (publishTarget)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    if (!IsSessionActive(session)) return;
                    PublishSnapshot(
                        session,
                        new List<ImageFile> { targetFile },
                        folder,
                        folder.Name,
                        clearThumbnails: false,
                        retireSuperseded: false);
                    SetCurrentFile(targetFile, session);
                });

                if (!IsSessionActive(session))
                {
                    AbandonSession(session);
                    return false;
                }

                Console.WriteLine($"[Folder] Phase1 (first image): {stopwatch.ElapsedMilliseconds}ms");
            }
        }

        // ── 阶段 2:后台枚举到工作列表 ──
        try
        {
            // 枚举的同步前导段（Provider 查询）也在后台线程，不占 UI 线程。
            await Task.Run(() => session.EnumerateAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            AbandonSession(session);
            return false;
        }
        catch (Exception exception)
        {
            return await HandleEnumerateFailureAsync(
                session,
                folder,
                targetFile,
                targetIsExplicit,
                refreshMode,
                exception);
        }

        if (!IsSessionActive(session))
        {
            AbandonSession(session);
            return false;
        }

        Console.WriteLine($"[Folder] Phase2 (enumerate {session.WorkFiles.Count} files): {stopwatch.ElapsedMilliseconds}ms");

        var snapshot = session.WorkFiles.ToList();
        List<ImageFile>? reused = null;
        await UiDispatcher.InvokeAsync(() =>
        {
            if (!IsSessionActive(session)) return;

            // 同目录刷新：按 StorageId 复用仍存在的文件对象，保住缩略图、EXIF 与卡片身份；
            // 新出现的文件用本次枚举对象，已删除的文件自然退出列表。
            var files = snapshot;
            if (isRefresh) files = MergeLoadedFiles(session, snapshot, out reused);

            // 一次提交：枚举成功后才替换列表、目录名与当前照片，失败时旧快照原样保留。
            PublishSnapshot(
                session,
                files,
                folder,
                folder.Name,
                clearThumbnailsOnCommit,
                retireSuperseded: true);

            // 刷新（同目录或显式重开）以当前/目标照片为首选；目标已不在新列表时退回首项。
            var selection = ResolveCommittedSelection(files, targetFile, isRefresh || refreshMode);

            // 阶段 2 定下的当前照片不再被后续阶段改写（OPEN-02）。
            SetCurrentFile(selection, session);
            Main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();

            // 复用对象重读完整磁盘元数据由本方法在返回前 await；新加入的文件仍走阶段 3 后台首读。
            var backgroundFiles = files;
            if (reused is { Count: > 0 })
            {
                var reusedSet = new HashSet<ImageFile>(reused);
                backgroundFiles = files.Where(file => !reusedSet.Contains(file)).ToList();
            }

            session.StartBackgroundLoads(backgroundFiles, CreateRatingsLoadedCallback(session, backgroundFiles));

            Console.WriteLine($"[Folder] Phase2 (UI updated): {stopwatch.ElapsedMilliseconds}ms");
        });

        // 返回前完成复用对象的属性/EXIF/评分/sidecar 重读：调用方 await 打开即可读到新值。
        if (reused is { Count: > 0 })
        {
            await ReloadRatingsAsync(session, reused).ConfigureAwait(false);
        }

        return IsSessionActive(session);
    }

    /// <summary>
    /// 枚举失败：显式指定的新目标回退单图模式（OPEN-03）；刷新则保留打开前的快照，
    /// 只刷新仍然可访问的文件（OPEN-02/OPEN-08），绝不整体降级为单图。
    /// </summary>
    private async Task<bool> HandleEnumerateFailureAsync(
        FolderLoadSession session,
        IFolderRef folder,
        ImageFile? targetFile,
        bool targetIsExplicit,
        bool refreshMode,
        Exception exception)
    {
        if (!IsSessionActive(session))
        {
            AbandonSession(session);
            return false;
        }

        Console.Error.WriteLine($"[Folder] 枚举文件夹失败: {folder.StorageId.Value} — {exception.Message}");
        // STATE-02：打开失败必须让用户看见对象与原因，不能只写日志。
        Main.ReportOpenFailure(StatusMessages.FolderOpenFailed(folder.Name, StatusMessages.ReasonFor(exception)));

        if (refreshMode || targetFile == null || !targetIsExplicit)
        {
            // 刷新失败：旧列表、当前图、目录名与缩略图缓存原样保留；
            // 目录读不到时仍刷新当前仍可访问的对象，而不是把整个目录变成单图。
            var accessible = _allFiles;
            AbandonSession(session);
            if (refreshMode && accessible.Count > 0)
            {
                // 枚举失败说明旧索引可能早于磁盘现状：清空后逐文件核实 sidecar，
                // 已删除的来源不复活，新出现的来源也不漏。
                foreach (var file in accessible) file.RatingSidecarIndex = null;
                await ReloadRatingsAsync(accessible).ConfigureAwait(false);
            }
            return false;
        }

        await UiDispatcher.InvokeAsync(() =>
        {
            if (!IsSessionActive(session)) return;
            PublishSnapshot(
                session,
                new List<ImageFile> { targetFile },
                folder: null,
                folderName: targetFile.Name,
                clearThumbnails: false,
                retireSuperseded: true);
            SetCurrentFile(targetFile, session);
        });

        return IsSessionActive(session);
    }

    /// <summary>
    /// 发布一次提交快照：替换列表、目录名与当前目录，再通知文件栏重算筛选。
    /// 调用方必须在 UI 线程且确认会话仍然有效。
    /// </summary>
    private void PublishSnapshot(
        FolderLoadSession session,
        List<ImageFile> files,
        IFolderRef? folder,
        string folderName,
        bool clearThumbnails,
        bool retireSuperseded)
    {
        _allFiles = files;
        _currentFolder = folder;
        FolderName = folderName;

        if (clearThumbnails)
        {
            // 仅切换目录时整批释放旧缩略图（统一缓存 Thumb 档清空，不依赖淘汰）。
            BitmapLoader.ClearThumbnailClass();
        }

        foreach (var file in files) file.UpdateCacheStatus();
        session.MarkCommitted();
        AllFilesChanged?.Invoke();

        if (retireSuperseded) DisposeSupersededSessions(session);
    }

    /// <summary>
    /// 切换当前图片,并触发当前图的 EXIF 加载与缩略图优先加载。
    /// </summary>
    /// <param name="imageFile">要设为当前的图片</param>
    /// <param name="session">本次打开的会话;null 表示只是在本目录内切换</param>
    private void SetCurrentFile(ImageFile? imageFile, FolderLoadSession? session)
    {
        if (session != null && !IsSessionActive(session)) return;

        Main.CurrentFile = imageFile;
        if (imageFile == null) return;

        _ = LoadCurrentExifAsync(imageFile, session?.Token ?? CancellationToken.None);
        PriorityThumbnailRequested?.Invoke(imageFile);
    }

    /// <summary>后台读取当前图完整 EXIF，回到 UI 线程刷新控制栏；旧会话或被切走时丢弃结果。</summary>
    private async Task LoadCurrentExifAsync(ImageFile imageFile, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => imageFile.LoadExifDataAsync(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Folder] 当前图 EXIF 加载失败: {imageFile.Name} — {exception.Message}");
            return;
        }

        if (cancellationToken.IsCancellationRequested) return;

        await UiDispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!ReferenceEquals(Main.CurrentFile, imageFile)) return;

            imageFile.RaisePropertyChanged(nameof(imageFile.PhotoDate));
            Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.CurrentExifData));
            Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.StarOpacity));
        });
    }

    /// <summary>星级补齐完成后的提交回调：批量通知星级并重算筛选（不改变已确定的当前照片）。</summary>
    private Func<Task> CreateRatingsLoadedCallback(FolderLoadSession session, IReadOnlyList<ImageFile> snapshot)
    {
        return async () =>
        {
            if (!IsSessionActive(session)) return;

            await UiDispatcher.InvokeAsync(() =>
            {
                if (!IsSessionActive(session)) return;

                foreach (var file in snapshot)
                {
                    file.RaisePropertyChanged(nameof(file.Rating));
                }

                AllFilesChanged?.Invoke();
            });
        };
    }

    /// <summary>在已加载集合中按存储身份查找同一个文件（StorageId 是唯一身份）。</summary>
    private ImageFile? FindLoadedFile(IFileRef file)
    {
        return _allFiles.FirstOrDefault(imageFile => imageFile.File.StorageId == file.StorageId);
    }

    /// <summary>
    /// 同目录刷新时按 StorageId 把枚举结果与已加载对象对齐：仍存在的文件复用旧对象
    /// （缩略图、EXIF、卡片身份与当前照片引用都不变），并保持旧顺序后追加新文件，
    /// 让同名合并组的代表选择（按枚举序号）在刷新前后稳定；复用对象换成本次枚举的 sidecar 索引。
    /// </summary>
    /// <param name="session">本次刷新会话（提供最新的 sidecar 索引）。</param>
    /// <param name="workFiles">本次枚举得到的文件。</param>
    /// <param name="reused">被复用的旧对象（刷新时需要重读完整元数据）。</param>
    private List<ImageFile> MergeLoadedFiles(
        FolderLoadSession session,
        IReadOnlyList<ImageFile> workFiles,
        out List<ImageFile> reused)
    {
        reused = new List<ImageFile>();
        if (_allFiles.Count == 0) return workFiles.ToList();

        var incoming = new Dictionary<StorageItemId, ImageFile>(workFiles.Count);
        foreach (var file in workFiles) incoming.TryAdd(file.File.StorageId, file);

        var merged = new List<ImageFile>(workFiles.Count);
        foreach (var file in _allFiles)
        {
            if (!incoming.Remove(file.File.StorageId, out _)) continue;
            file.RatingSidecarIndex = session.SidecarIndex;
            merged.Add(file);
            reused.Add(file);
        }

        foreach (var file in workFiles)
        {
            if (!incoming.Remove(file.File.StorageId, out _)) continue;
            merged.Add(file);
        }

        return merged;
    }

    /// <summary>
    /// 提交后的选中项：按 StorageId 映射到本次快照里的对象（刷新时即被复用的旧对象），
    /// 目标已不存在时退回枚举首项，绝不把已删除的文件留成当前照片。
    /// </summary>
    /// <param name="snapshot">本次提交的列表。</param>
    /// <param name="targetFile">首选目标。</param>
    /// <param name="fallbackWhenTargetMissing">刷新语义：目标不在快照时退回枚举首项，而不是保留悬空目标。</param>
    private ImageFile? ResolveCommittedSelection(
        IReadOnlyList<ImageFile> snapshot,
        ImageFile? targetFile,
        bool fallbackWhenTargetMissing)
    {
        var candidate = targetFile == null
            ? null
            : snapshot.FirstOrDefault(file => file.File.StorageId == targetFile.File.StorageId)
              ?? (fallbackWhenTargetMissing ? null : targetFile);
        return candidate == null ? ResolveFirstFile(snapshot) : ResolveTargetSelection(candidate);
    }

    /// <summary>
    /// 刷新被复用文件的磁盘元数据（基本属性 + 完整 EXIF + 内嵌评分 + 已存在的 sidecar）。
    /// 磁盘解析全部在后台线程完成（不能被 UI 调度器的同步前导段拖住前 8 个文件）；
    /// 后发打开接管后结果不再发布，刷新完成时统一重算筛选并刷新控制栏 EXIF。
    /// </summary>
    private Task ReloadRatingsAsync(FolderLoadSession session, IReadOnlyList<ImageFile> files)
        => ReloadMetadataAsync(files, session.Token, () => IsSessionActive(session));

    /// <summary>
    /// 没有会话的刷新（目录枚举失败后只刷新仍可访问的对象）：不取消，只受文件源释放约束。
    /// </summary>
    private Task ReloadRatingsAsync(IReadOnlyList<ImageFile> files)
        => ReloadMetadataAsync(files, CancellationToken.None, () => !_disposed);

    private async Task ReloadMetadataAsync(
        IReadOnlyList<ImageFile> files,
        CancellationToken token,
        Func<bool> isStillValid)
    {
        using var semaphore = new SemaphoreSlim(8);
        try
        {
            var tasks = files.Select(async file =>
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
                    if (!isStillValid()) return;
                    // Task.Run 包住整个读取：Task.Run 返回的任务在后台完成，UI 线程不做同步解析。
                    await Task.Run(() => file.RefreshMetadataAsync(token), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 后发打开已接管：旧会话的刷新结果不发布。
                }
                catch (Exception exception)
                {
                    // 单个文件元数据不可读只记录；列表与当前照片保持刷新前状态。
                    Console.WriteLine($"[Folder] 刷新元数据失败: {file.Name} — {exception.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (!isStillValid()) return;

        await UiDispatcher.InvokeAsync(() =>
        {
            if (!isStillValid()) return;

            // 当前照片的 EXIF 摘要与星级按钮随刷新后的数据重画。
            var current = Main.CurrentFile;
            if (current != null)
            {
                current.RaisePropertyChanged(nameof(ImageFile.PhotoDate));
                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.CurrentExifData));
                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.StarOpacity));
            }

            AllFilesChanged?.Invoke();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 单图模式重复激活：对同一对象重读基本属性、完整 EXIF、评分与 sidecar 后回到 UI 线程刷新，
    /// 不换对象、不重新 fit、不改动列表与视口；读取失败按 STATE-02/STATE-03 上报。
    /// 刷新前丢弃 sidecar 索引，强制逐文件核实存在性（删除不复活、新增不漏）。
    /// </summary>
    /// <returns>刷新是否成功（被取消或读取失败为 false）。</returns>
    private async Task<bool> ReloadFileAsync(ImageFile imageFile)
    {
        imageFile.RatingSidecarIndex = null;
        try
        {
            await Task.Run(() => imageFile.RefreshMetadataAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            // STATE-02/STATE-03：单图刷新失败必须有可见原因，不能只写日志。
            ReportUnexpectedOpenFailure(imageFile.Name, exception);
            return false;
        }

        if (_disposed) return false;

        await UiDispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            AllFilesChanged?.Invoke();
            SetCurrentFile(imageFile, session: null);
        }).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 指定目标的首选选中项：同名合并时映射到列表中的代表文件；
    /// 代表文件不在当前筛选结果时仍选中目标本身（OPEN-04 目标优先）。
    /// </summary>
    private ImageFile ResolveTargetSelection(ImageFile target)
    {
        var projection = ProjectCurrent(_allFiles);
        var representative = projection.Groups.RepresentativeFor(target);
        if (representative == null) return target;
        return projection.Items.Contains(representative) ? representative : target;
    }

    /// <summary>
    /// 未指定目标时的首图：筛选后列表首项；筛选为空时回退枚举首项所属合并组的代表文件（OPEN-04）。
    /// </summary>
    private ImageFile? ResolveFirstFile(IReadOnlyList<ImageFile> files)
    {
        var projection = ProjectCurrent(files);
        var first = projection.Items.FirstOrDefault();
        if (first != null) return first;

        var enumerationFirst = files.FirstOrDefault();
        if (enumerationFirst == null) return null;
        return projection.Groups.RepresentativeFor(enumerationFirst) ?? enumerationFirst;
    }

    /// <summary>按当前筛选条与排序设置投影一组文件（与文件栏同一份实现）。</summary>
    private PhotoListProjectionResult ProjectCurrent(IReadOnlyList<ImageFile> files)
    {
        var filter = Main.FileVM.FilterBar;
        return PhotoListProjection.Project(
            files,
            Main.Settings,
            filter.SelectedRatingFilter,
            filter.SortMode,
            filter.SortOrder);
    }

    /// <summary>开启新会话并把取消链指向它；已发布快照的旧会话保留访问租约。</summary>
    private FolderLoadSession BeginSession(IFolderRef? folder, IFileRef? target)
    {
        var session = new FolderLoadSession(this, folder, target);
        var previous = _activeSession;
        _activeSession = session;
        _liveSessions.Add(session);
        previous?.Cancel();
        return session;
    }

    /// <summary>新会话是否仍是当前打开且未被取消（旧结果一律不发布）。</summary>
    private bool IsSessionActive(FolderLoadSession session)
        => !_disposed && ReferenceEquals(_activeSession, session) && !session.IsCancellationRequested;

    /// <summary>
    /// 放弃一个尚未提交的会话：取消并归还租约。
    /// 已发布快照的会话保留到下一次提交，避免旧列表在其显示期间失去访问权限。
    /// </summary>
    private void AbandonSession(FolderLoadSession session)
    {
        if (!session.HasCommitted)
        {
            _liveSessions.Remove(session);
            session.Dispose();
        }

        if (ReferenceEquals(_activeSession, session))
        {
            _activeSession = null;
        }
    }

    /// <summary>新快照提交成功：归还其余会话（在途或旧快照）的访问租约。</summary>
    private void DisposeSupersededSessions(FolderLoadSession keep)
    {
        if (_liveSessions.Count <= 1) return;

        var superseded = _liveSessions.Where(session => !ReferenceEquals(session, keep)).ToList();
        foreach (var session in superseded)
        {
            _liveSessions.Remove(session);
            session.Dispose();
        }
    }

    /// <summary>打开链路的意外异常统一出口：日志 + STATE-03 文案（STATE-02）。</summary>
    private void ReportUnexpectedOpenFailure(string? name, Exception exception)
    {
        Console.Error.WriteLine($"[Open] 打开失败：{name} — {exception}");
        Main.ReportOpenFailure(StatusMessages.OpenFailed(name, StatusMessages.ReasonFor(exception)));
    }

    /// <summary>两个存储项是否指向同一个底层对象（只比较 StorageId）。</summary>
    private static bool HasSameStorageId(IStorageItemRef? left, IStorageItemRef? right)
        => left != null && right != null && left.StorageId == right.StorageId;

    /// <summary>
    /// 根据设置中已启用的格式判断给定文件名是否为图片。
    /// </summary>
    /// <param name="fileName">文件名(含扩展名)</param>
    public bool IsImageFile(string fileName)
    {
        if (IsHiddenFile(fileName)) return false;
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return extension != null && Main.Settings.SelectedFormats.Contains(extension);
    }

    /// <summary>
    /// 是否为隐藏文件(以点号开头)。
    /// </summary>
    public static bool IsHiddenFile(string fileName)
    {
        return fileName.StartsWith('.');
    }

    #endregion
}
