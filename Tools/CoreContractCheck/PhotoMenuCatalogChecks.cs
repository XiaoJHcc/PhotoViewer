using PhotoViewer.ViewModels.Main;

namespace CoreContractCheck;

/// <summary>
/// MENU-01/02/06/07：主图上下文菜单的固定项、顺序、分组、触屏差异、勾选与置灰。
/// </summary>
internal static class PhotoMenuCatalogChecks
{
    public static void Run(Checks c)
    {
        c.Section("主图上下文菜单目录");

        var state = new PhotoMenuState(true, true, false, true, true);
        var desktop = PhotoMenuCatalog.Build(state, includeOpenFile: true);
        c.Equal("桌面共 8 项", 8, desktop.Count);
        c.Equal("桌面项与顺序固定", "打开文件…/打开文件夹…/胶片栏/分析栏/控制栏/雾天增强/工具…/设置…",
            string.Join("/", desktop.Select(item => item.Text)));
        c.Equal("分组序号固定", "0/0/1/1/1/2/3/3",
            string.Join("/", desktop.Select(item => item.Group)));
        c.Equal("命令标识顺序固定",
            "OpenFile/OpenFolder/Filmstrip/Analysis/ControlBar/Enhance/Tools/Settings",
            string.Join("/", desktop.Select(item => item.Id)));
        c.Equal("只有栏位与预览项是勾选项", "Filmstrip/Analysis/ControlBar/Enhance",
            string.Join("/", desktop.Where(item => item.IsCheck).Select(item => item.Id)));
        c.Equal("勾选态取状态快照", "True/False/True/True",
            string.Join("/", desktop.Where(item => item.IsCheck).Select(item => item.Checked)));

        var touch = PhotoMenuCatalog.Build(state, includeOpenFile: false);
        c.Equal("触屏省略打开文件", 7, touch.Count);
        c.True("触屏首项是打开文件夹", touch[0].Id == PhotoMenuItemId.OpenFolder && touch[0].Text == "打开文件夹…");
        c.True("触屏项集合与桌面去掉打开文件后一致",
            touch.Select(item => item.Id).SequenceEqual(desktop.Skip(1).Select(item => item.Id)));

        var empty = PhotoMenuCatalog.Build(new PhotoMenuState(false, true, true, true, false), includeOpenFile: false);
        var disabled = empty.Where(item => !item.Enabled).ToArray();
        c.Equal("无照片时只有雾天增强置灰", "Enhance", string.Join("/", disabled.Select(item => item.Id)));
        c.True("无照片时工具与设置仍可用",
            empty.Single(item => item.Id == PhotoMenuItemId.Tools).Enabled
            && empty.Single(item => item.Id == PhotoMenuItemId.Settings).Enabled);
        c.True("项集合不随照片状态增删", empty.Count == touch.Count);

        var withPhoto = PhotoMenuCatalog.Build(new PhotoMenuState(true, false, false, false, false), includeOpenFile: true);
        c.True("有照片时雾天增强可用且未勾选",
            withPhoto.Single(item => item.Id == PhotoMenuItemId.Enhance) is { Enabled: true, Checked: false });
        c.True("栏位勾选跟随共享状态",
            withPhoto.Where(item => item.Group == PhotoMenuCatalog.PaneGroup).All(item => !item.Checked));

        c.True("组序号从打开组递增到窗口组",
            desktop.Zip(desktop.Skip(1)).All(pair => pair.Second.Group - pair.First.Group is 0 or 1)
            && desktop[^1].Group == PhotoMenuCatalog.WindowGroup);

        // 宽形态显式传 includeExif:false 时必须逐位等于旧八项。
        var wide = PhotoMenuCatalog.Build(state, includeOpenFile: true, includeExif: false);
        var legacyIds = new[]
        {
            PhotoMenuItemId.OpenFile, PhotoMenuItemId.OpenFolder, PhotoMenuItemId.Filmstrip,
            PhotoMenuItemId.Analysis, PhotoMenuItemId.ControlBar, PhotoMenuItemId.Enhance,
            PhotoMenuItemId.Tools, PhotoMenuItemId.Settings,
        };
        c.True("includeExif=false 与旧八项逐位一致",
            wide.Select(item => (item.Id, item.Text, item.Group, item.IsCheck, item.Checked, item.Enabled))
                .SequenceEqual(desktop.Select(item => (item.Id, item.Text, item.Group, item.IsCheck, item.Checked, item.Enabled)))
            && wide.Select(item => item.Id).SequenceEqual(legacyIds)
            && wide.All(item => item.Id != PhotoMenuItemId.Exif),
            string.Join("/", wide.Select(item => item.Id)));

        // 手机形态在栏位组末尾多一项可勾选的 EXIF。
        var phoneState = new PhotoMenuState(true, true, false, true, true, ExifVisible: true);
        var phone = PhotoMenuCatalog.Build(phoneState, includeOpenFile: false, includeExif: true);
        c.Equal("手机形态共 8 项", 8, phone.Count);
        c.Equal("手机形态项与顺序固定", "打开文件夹…/胶片栏/分析栏/控制栏/EXIF/雾天增强/工具…/设置…",
            string.Join("/", phone.Select(item => item.Text)));
        c.Equal("手机形态分组序号固定", "0/1/1/1/1/2/3/3",
            string.Join("/", phone.Select(item => item.Group)));

        var controlIndex = phone.ToList().FindIndex(item => item.Id == PhotoMenuItemId.ControlBar);
        bool exifAfterControl = controlIndex >= 0 && controlIndex + 1 < phone.Count
            && phone[controlIndex + 1] is { Id: PhotoMenuItemId.Exif, Text: "EXIF", Group: PhotoMenuCatalog.PaneGroup };
        c.True("EXIF 紧跟控制栏并留在栏位组", exifAfterControl,
            controlIndex >= 0 ? string.Join("/", phone.Skip(controlIndex).Select(item => item.Id)) : "缺少控制栏项");

        var exifItem = phone.Single(item => item.Id == PhotoMenuItemId.Exif);
        c.True("EXIF 为可勾选项且勾选态取开关", exifItem is { IsCheck: true, Checked: true, Enabled: true },
            exifItem.ToString() ?? string.Empty);

        var phoneExifOff = PhotoMenuCatalog.Build(phoneState with { ExifVisible = false }, includeOpenFile: false, includeExif: true);
        c.True("EXIF 关时该项未勾选且不离开菜单",
            phoneExifOff.Count == phone.Count
                && phoneExifOff.Single(item => item.Id == PhotoMenuItemId.Exif) is { IsCheck: true, Checked: false },
            string.Join("/", phoneExifOff.Where(item => item.IsCheck).Select(item => item.Checked)));
    }
}
