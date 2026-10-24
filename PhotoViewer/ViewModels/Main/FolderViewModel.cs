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
            Main.ReportOpenFailure(StatusMessages.OpenFailed(null, exception.Message));
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
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    /// <param name="resetView">是否让主图回到适应态（OPEN-05）;重复外部激活同一目标时传 false</param>
    public async Task OpenImageAsync(IFileRef file, bool scrollToCurrent = true, bool resetView = true)
    {
        if (_disposed || !IsImageFile(file.Name))
        {
            return;
        }

        // OPEN-05：显式打开开始新的观片，主图回到适应态。
        if (resetView) Main.RequestFitToView();

        try
        {
            var committed = await OpenImageInFolderContextAsync(file);
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

    /// <summary>
    /// 打开指定文件夹,并定位到筛选后的第一张图片。
    /// </summary>
    /// <param name="folder">要打开的文件夹</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    /// <param name="resetView">是否让主图回到适应态（OPEN-05）;重复外部激活同一目标时传 false</param>
    public async Task OpenFolderAsync(IFolderRef folder, bool scrollToCurrent = true, bool resetView = true)
    {
        if (_disposed) return;


        // OPEN-05：显式打开开始新的观片，主图回到适应态。
        if (resetView) Main.RequestFitToView();

        // OPEN-08：同目录重开视为刷新,以当前照片为首选目标,且保留缩略图缓存。
        var isSameFolder = HasSameStorageId(folder, _currentFolder) && _allFiles.Count > 0;
        var preferredFile = isSameFolder && Main.CurrentFile != null && _allFiles.Contains(Main.CurrentFile)
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
    private async Task<bool> OpenImageInFolderContextAsync(IFileRef file)
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
            .InvokeAsync(() => OpenImageInFolderContextOnUiAsync(file, folder))
            .Unwrap()
            .ConfigureAwait(false);
    }

    private async Task<bool> OpenImageInFolderContextOnUiAsync(IFileRef file, IFolderRef? folder)
    {
        if (_disposed) return false;

        if (folder == null)
        {
            return await OpenSingleImageAsync(file);
        }

        if (HasSameStorageId(folder, _currentFolder) && _allFiles.Count > 0)
        {


            // OPEN-03：目标已在当前目录模型中只切换，不重建目录；伴侣文件经同名组合映射到代表文件。
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
    /// <returns>本次打开是否已提交</returns>
    private async Task<bool> LoadFolderAsync(IFolderRef folder, IFileRef? preferredFile, bool targetIsExplicit)
    {
        var isRefresh = HasSameStorageId(folder, _currentFolder) && _allFiles.Count > 0;
        // 只有切换到不同目录才整批释放旧缩略图（OPEN-08）；临时快照不改变该判断。
        var clearThumbnailsOnCommit = !HasSameStorageId(folder, _currentFolder);

        var session = BeginSession(folder, preferredFile);
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine("[Folder] === BEGIN OpenFolder: " + folder.StorageId.Value);

        // ── 阶段 1:优先显示目标图片 ──
        var targetFile = session.EnsureTargetImage();
        if (targetFile != null)
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

            if (!isRefresh)
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
            return await HandleEnumerateFailureAsync(session, folder, targetFile, targetIsExplicit, exception);
        }

        if (!IsSessionActive(session))
        {
            AbandonSession(session);
            return false;
        }

        Console.WriteLine($"[Folder] Phase2 (enumerate {session.WorkFiles.Count} files): {stopwatch.ElapsedMilliseconds}ms");

        var snapshot = session.WorkFiles.ToList();
        await UiDispatcher.InvokeAsync(() =>
        {
            if (!IsSessionActive(session)) return;

            // 一次提交：枚举成功后才替换列表、目录名与当前照片，失败时旧快照原样保留。
            PublishSnapshot(
                session,
                snapshot,
                folder,
                folder.Name,
                clearThumbnailsOnCommit,
                retireSuperseded: true);

            var selection = targetFile != null
                ? ResolveTargetSelection(targetFile)
                : ResolveFirstFile(snapshot);

            // 阶段 2 定下的当前照片不再被后续阶段改写（OPEN-02）。
            SetCurrentFile(selection, session);
            Main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();
            session.StartBackgroundLoads(snapshot, CreateRatingsLoadedCallback(session, snapshot));

            Console.WriteLine($"[Folder] Phase2 (UI updated): {stopwatch.ElapsedMilliseconds}ms");
        });

        return IsSessionActive(session);
    }

    /// <summary>
    /// 枚举失败：显式指定的目标回退单图模式（OPEN-03）；
    /// 无目标或同目录刷新沿用的当前图则保留打开前的快照（OPEN-02/OPEN-08）。
    /// </summary>
    private async Task<bool> HandleEnumerateFailureAsync(
        FolderLoadSession session,
        IFolderRef folder,
        ImageFile? targetFile,
        bool targetIsExplicit,
        Exception exception)
    {
        if (!IsSessionActive(session))
        {
            AbandonSession(session);
            return false;
        }

        Console.Error.WriteLine($"[Folder] 枚举文件夹失败: {folder.StorageId.Value} — {exception.Message}");
        // STATE-02：打开失败必须让用户看见对象与原因，不能只写日志。
        Main.ReportOpenFailure(StatusMessages.FolderOpenFailed(folder.Name, exception.Message));

        if (targetFile == null || !targetIsExplicit)
        {
            // OPEN-02/OPEN-08：没有显式目标时保持打开前的列表、当前图、目录名与缩略图缓存。
            AbandonSession(session);
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
        Main.ReportOpenFailure(StatusMessages.OpenFailed(name, exception.Message));
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
