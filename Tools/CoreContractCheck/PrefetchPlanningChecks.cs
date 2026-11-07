using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>
/// 预取档位与 RAW 提前窗口的纯规划规则：<see cref="BitmapPrefetcher.ShouldPrefetchFull"/> 与
/// <see cref="BitmapPrefetcher.SelectRawAlongsideIndex"/>。文件引用是传入测试根目录下的合成路径，不读照片、不改静态配置。
/// </summary>
internal static class PrefetchPlanningChecks
{
    private const int ProxyTarget = 4532;

    public static void Run(Checks c, string root)
    {
        c.Section("预取档位规划");
        c.True("适应态 +1 只取代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, false, 1, false));
        c.True("适应态 +1 旁路也只取代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, false, 1, true));
        c.True("Full 需要时 +1 取 Full", BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 1, false));
        c.True("Full 需要时 +1 旁路取 Full", BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 1, true));
        c.True("Full 需要时方向 +2 旁路取 Full", BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 2, true));
        c.True("Full 需要时方向 +2 非旁路仍代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 2, false));
        c.True("Full 需要时提前 RAW（+4）取 Full", BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 4, true));
        c.True("Full 需要时提前 RAW（+6）取 Full", BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 6, true));
        c.True("Full 需要时普通远邻（+3）仍代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 3, false));
        c.True("Full 需要时普通远邻（+6）仍代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, true, 6, false));
        c.True("禁 Full 邻居时 +1 旁路仍代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, false, 1, true));
        c.True("禁 Full 邻居时提前 RAW 仍代理", !BitmapPrefetcher.ShouldPrefetchFull(ProxyTarget, false, 6, true));
        c.True("缺少代理目标时 +1 按旧口径取 Full", BitmapPrefetcher.ShouldPrefetchFull(0, false, 1, false));
        c.True("缺少代理目标时远处普通邻居也取 Full", BitmapPrefetcher.ShouldPrefetchFull(-1, false, 6, false));

        c.Section("RAW 提前窗口选择");
        var window = Files(root, "a.jpg", "b.jpg", "c.arw", "d.jpg", "e.arw");
        c.Equal("两张 RAW 只选最近一个", 2, BitmapPrefetcher.SelectRawAlongsideIndex(window, 0, 1, 6, 6, 6));
        c.Equal("方向 +2 的 RAW", 2, BitmapPrefetcher.SelectRawAlongsideIndex(window, 0, 1, 6, 6, 2));

        var boundary = Files(root, "0.jpg", "1.jpg", "2.jpg", "3.jpg", "4.jpg", "5.jpg", "6.arw", "7.jpg");
        c.Equal("窗口边界 +6 入选", 6, BitmapPrefetcher.SelectRawAlongsideIndex(boundary, 0, 1, 10, 6, 6));
        c.Equal("窗口外 +6 不入选", -1, BitmapPrefetcher.SelectRawAlongsideIndex(boundary, 0, 1, 10, 6, 5));
        c.Equal("默认窗口 2 不含 +3",
            -1, BitmapPrefetcher.SelectRawAlongsideIndex(Files(root, "a.jpg", "b.jpg", "c.jpg", "d.arw"), 0, 1, 6, 6, 2));
        c.Equal("窗口 0 不放行",
            -1, BitmapPrefetcher.SelectRawAlongsideIndex(Files(root, "a.jpg", "b.arw"), 0, 1, 6, 6, 0));

        var capped = Files(root, "a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.arw");
        c.Equal("超出配置前向份数不入选", -1, BitmapPrefetcher.SelectRawAlongsideIndex(capped, 0, 1, 3, 6, 6));
        c.Equal("前向份数边界入选", 3,
            BitmapPrefetcher.SelectRawAlongsideIndex(Files(root, "a.jpg", "b.jpg", "c.jpg", "d.arw"), 0, 1, 3, 6, 6));

        var backward = Files(root, "0.jpg", "1.jpg", "2.jpg", "3.arw", "4.jpg", "5.jpg", "6.jpg");
        c.Equal("超出配置后向份数不入选", -1, BitmapPrefetcher.SelectRawAlongsideIndex(backward, 6, -1, 6, 2, 6));
        c.Equal("后向份数边界入选", 3, BitmapPrefetcher.SelectRawAlongsideIndex(backward, 6, -1, 6, 3, 6));
        c.Equal("负方向沿实际方向选最近", 5,
            BitmapPrefetcher.SelectRawAlongsideIndex(
                Files(root, "0.jpg", "1.jpg", "2.jpg", "3.jpg", "4.jpg", "5.arw", "6.jpg"), 6, -1, 1, 6, 6));

        var forward = Files(root, "0.jpg", "1.jpg", "2.jpg", "3.arw", "4.jpg", "5.jpg", "6.jpg");
        c.Equal("方向 2 只按符号前进一格", 3, BitmapPrefetcher.SelectRawAlongsideIndex(forward, 0, 2, 6, 6, 6));
        c.Equal("方向 -2 只按符号后退一格", 3, BitmapPrefetcher.SelectRawAlongsideIndex(forward, 6, -2, 6, 6, 6));
        c.Equal("方向 0 不选中", -1, BitmapPrefetcher.SelectRawAlongsideIndex(forward, 0, 0, 6, 6, 6));
        c.Equal("末图向后越界", -1, BitmapPrefetcher.SelectRawAlongsideIndex(forward, 6, 1, 6, 6, 6));
        c.Equal("首图向前越界", -1, BitmapPrefetcher.SelectRawAlongsideIndex(forward, 0, -1, 6, 6, 6));
        c.Equal("无 RAW 不选中", -1,
            BitmapPrefetcher.SelectRawAlongsideIndex(Files(root, "a.jpg", "b.jpg", "c.jpg"), 1, 1, 6, 6, 6));
    }

    private static List<ImageFile> Files(string root, params string[] names)
        => names.Select(name => new ImageFile(new LocalFileRef(Path.Combine(root, name)))).ToList();
}
