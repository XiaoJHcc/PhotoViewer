using PhotoViewer.Core.Platform;

namespace CoreContractCheck;

/// <summary>
/// STATE-01/02、IMAGE-10、FILM-05/MENU-05：主图区提示文案与拍摄时间格式的四端共同契约。
/// </summary>
internal static class PhotoHintTextChecks
{
    public static void Run(Checks c)
    {
        c.Section("主图区提示文案");
        c.Equal("桌面空态标题", "点击打开文件 或 拖拽图片到此处",
            PhotoHintText.Resolve(false, null, false, touch: false).Title);
        c.Equal("触屏空态标题", "点击打开文件夹",
            PhotoHintText.Resolve(false, null, false, touch: true).Title);
        var empty = PhotoHintText.Resolve(false, "", false, touch: true);
        c.True("空态不带详情", empty.Detail.Length == 0);
        c.Equal("失败标题固定", "无法打开该图片",
            PhotoHintText.Resolve(true, "文件内容无效或已损坏", false, touch: false).Title);
        c.Equal("失败详情是解码原因", "文件内容无效或已损坏",
            PhotoHintText.Resolve(true, "文件内容无效或已损坏", false, touch: false).Detail);
        c.Equal("无照片时失败仍优先", "无法打开该图片",
            PhotoHintText.Resolve(false, "文件内容无效或已损坏", false, touch: true).Title);
        c.Equal("加载标题与四端一致", "正在载入…",
            PhotoHintText.Resolve(true, "", true, touch: false).Title);
        c.True("加载不带详情", PhotoHintText.Resolve(true, "", true, touch: true).Detail.Length == 0);
        var none = PhotoHintText.Resolve(true, "", false, touch: false);
        c.True("有照片且图已就绪不显示提示", none.Title.Length == 0 && none.Detail.Length == 0);

        c.Section("拍摄时间格式");
        var date = new DateTimeOffset(2026, 11, 6, 9, 5, 7, TimeSpan.FromHours(8));
        c.Equal("完整拍摄时间含时区偏移", "2026-11-06 09:05:07 +08:00", ShotTimeText.FormatShotTime(date));
        c.Equal("无拍摄时间返回 null", null, ShotTimeText.FormatShotTime(null));
        c.Equal("格式常量与契约一致", "yyyy-MM-dd HH:mm:ss zzz", ShotTimeText.Format);
    }
}
