namespace PhotoViewer.ViewModels.Main;

/// <summary>主图上下文菜单的命令标识；各端据此映射原生图标与执行入口（MENU-01/02/06/07）。</summary>
public enum PhotoMenuItemId
{
    /// <summary>打开文件…（仅桌面；触屏平台无此项）。</summary>
    OpenFile,
    /// <summary>打开文件夹…。</summary>
    OpenFolder,
    /// <summary>胶片栏显隐（勾选）。</summary>
    Filmstrip,
    /// <summary>分析栏显隐（勾选）。</summary>
    Analysis,
    /// <summary>控制栏显隐（勾选）。</summary>
    ControlBar,
    /// <summary>雾天增强（勾选；无当前照片时置灰）。</summary>
    Enhance,
    /// <summary>工具…。</summary>
    Tools,
    /// <summary>设置…。</summary>
    Settings,
}

/// <summary>主图菜单构建时的共享状态快照；勾选与可用性只取决于这里。</summary>
/// <param name="HasPhoto">当前是否有照片。</param>
/// <param name="FilmVisible">胶片栏是否可见。</param>
/// <param name="AnalysisVisible">分析栏是否可见。</param>
/// <param name="ControlVisible">控制栏是否可见。</param>
/// <param name="Enhanced">雾天增强是否开启。</param>
public readonly record struct PhotoMenuState(
    bool HasPhoto,
    bool FilmVisible,
    bool AnalysisVisible,
    bool ControlVisible,
    bool Enhanced);

/// <summary>主图菜单的一项：固定文案、分组序号、勾选与可用性。</summary>
/// <param name="Id">命令标识。</param>
/// <param name="Text">固定文案（含省略号约定，MENU-06）。</param>
/// <param name="Group">分组序号：打开 0、栏位 1、预览 2、窗口 3；组间由宿主插入分隔。</param>
/// <param name="IsCheck">是否为勾选项。</param>
/// <param name="Checked">勾选态（仅 <paramref name="IsCheck"/> 有意义）。</param>
/// <param name="Enabled">是否可用。</param>
public sealed record PhotoMenuItem(
    PhotoMenuItemId Id,
    string Text,
    int Group,
    bool IsCheck,
    bool Checked,
    bool Enabled);

/// <summary>
/// MENU-01/02/06/07 主图上下文菜单目录：四组固定顺序（打开 / 栏位 / 预览 / 窗口），
/// 组内不重排、项集合不随照片或布局增删；勾选与置灰只取共享状态快照。
/// 各端按 <see cref="PhotoMenuItem.Id"/> 映射原生图标与执行入口，只负责插入原生分隔线与落位。
/// </summary>
public static class PhotoMenuCatalog
{
    /// <summary>打开组。</summary>
    public const int OpenGroup = 0;
    /// <summary>栏位组。</summary>
    public const int PaneGroup = 1;
    /// <summary>预览组。</summary>
    public const int PreviewGroup = 2;
    /// <summary>窗口组。</summary>
    public const int WindowGroup = 3;

    /// <summary>按固定顺序构建主图菜单。</summary>
    /// <param name="state">当前共享状态。</param>
    /// <param name="includeOpenFile">是否包含「打开文件…」（桌面为 true，触屏为 false）。</param>
    /// <returns>按显示顺序排列的菜单项。</returns>
    public static IReadOnlyList<PhotoMenuItem> Build(PhotoMenuState state, bool includeOpenFile)
    {
        var items = new List<PhotoMenuItem>(includeOpenFile ? 8 : 7);
        if (includeOpenFile) items.Add(new PhotoMenuItem(PhotoMenuItemId.OpenFile, "打开文件…", OpenGroup, false, false, true));
        items.Add(new PhotoMenuItem(PhotoMenuItemId.OpenFolder, "打开文件夹…", OpenGroup, false, false, true));
        items.Add(new PhotoMenuItem(PhotoMenuItemId.Filmstrip, "胶片栏", PaneGroup, true, state.FilmVisible, true));
        items.Add(new PhotoMenuItem(PhotoMenuItemId.Analysis, "分析栏", PaneGroup, true, state.AnalysisVisible, true));
        items.Add(new PhotoMenuItem(PhotoMenuItemId.ControlBar, "控制栏", PaneGroup, true, state.ControlVisible, true));
        // MENU-07：无当前照片时雾天增强置灰，其余项照常可用。
        items.Add(new PhotoMenuItem(PhotoMenuItemId.Enhance, "雾天增强", PreviewGroup, true, state.Enhanced, state.HasPhoto));
        items.Add(new PhotoMenuItem(PhotoMenuItemId.Tools, "工具…", WindowGroup, false, false, true));
        items.Add(new PhotoMenuItem(PhotoMenuItemId.Settings, "设置…", WindowGroup, false, false, true));
        return items;
    }
}
