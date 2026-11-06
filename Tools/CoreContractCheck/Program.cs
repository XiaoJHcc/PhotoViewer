namespace CoreContractCheck;

/// <summary>
/// Core 存储契约自检入口：全部用例只操作 <c>Path.GetTempPath()/PhotoViewer.CoreContractCheck/&lt;guid&gt;</c>，
/// 结束时清理；任何失败逐项输出并非零退出。
/// </summary>
internal static class Program
{
    /// <summary>契约自检依赖的仓库夹具；缺失时直接失败，不在半套素材上跑出误导性的部分结果。</summary>
    private static readonly (string Path, string Purpose)[] RequiredFixtures =
    [
        ("Fixtures/jpeg/color-srgb.jpg", "1200×800 sRGB 色卡，内嵌 Rating=3 与 ICC"),
        ("Fixtures/heif/color-srgb.heic", "同色卡 HEVC HEIF，容器内 canonical XMP，内嵌 Rating=3"),
        ("Fixtures/raw/A7C01489.ARW", "真实 ILCE-7CM2 RAW，IFD0 tag 700 canonical XMP 与固定 DocumentID"),
        ("Fixtures/raw/A7C01495.ARW", "第二张真实 RAW，用于 0..5/-1 往返"),
    ];

    private static int Main()
    {
        Console.WriteLine("Core 存储契约自检");
        if (!CheckRequiredFixtures()) return 1;

        var checks = new Checks();
        var root = Path.Combine(
            Path.GetTempPath(),
            "PhotoViewer.CoreContractCheck",
            Guid.NewGuid().ToString("N"));

        Console.WriteLine("临时根目录：" + root);
        Directory.CreateDirectory(root);

        try
        {
            PresentationCacheChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            StorageItemIdChecks.Run(checks, root);
            StorageIdentityRegressionChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            LocalFileRefChecks.Run(checks, root);
            LocalFolderChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            RandomAccessChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            AtomicWriteChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            DeleteChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            XmpChecks.Run(checks, root);
            OpenRequestChannelChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            XmpContainerChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            ContainerSafetyRegressionChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            PhotoGroupChecks.Run(checks, root);
            RatingChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            RatingTransactionRegressionChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            SettingsChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            RefreshRegressionChecks.RunAsync(checks, root).GetAwaiter().GetResult();
            StatusMessageChecks.Run(checks);
            TooltipTextChecks.Run(checks);
            SettingsPresentationChecks.Run(checks);
            PhotoMenuCatalogChecks.Run(checks);
            PhotoHintTextChecks.Run(checks);
            RatingSourcesLayoutChecks.Run(checks);
            TitleBarLayoutChecks.Run(checks);
            MemoryAdmissionChecks.Run(checks);
        }
        catch (Exception exception)
        {
            checks.Fail("测试执行", "未处理的异常：" + exception);
        }
        finally
        {
            Cleanup(root);
        }

        Console.WriteLine();
        Console.WriteLine($"合计 {checks.Passed} PASS / {checks.Skipped} SKIP / {checks.Failures} FAIL");
        Console.WriteLine(checks.Failures == 0 ? "ALL PASS" : $"{checks.Failures} FAIL");
        return checks.Failures == 0 ? 0 : 1;
    }

    private static bool CheckRequiredFixtures()
    {
        var missing = RequiredFixtures.Where(item => !File.Exists(item.Path)).ToArray();
        Console.WriteLine($"验收素材：{RequiredFixtures.Length - missing.Length}/{RequiredFixtures.Length} 就绪");
        if (missing.Length == 0) return true;

        foreach (var (path, purpose) in missing)
            Console.WriteLine($"  缺少夹具 {path}（{purpose}）");
        Console.WriteLine("验收素材不全，未运行用例；请检出仓库内对应文件后再跑。");
        return false;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
            Console.WriteLine("已清理临时目录");
            return;
        }
        catch (Exception first)
        {
            Console.WriteLine("临时目录首次清理失败：" + first.Message);
        }

        // 只读权限文件（权限用例会临时设置）重置后再删一次。
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            Directory.Delete(root, recursive: true);
            Console.WriteLine("已清理临时目录（重置只读后）");
        }
        catch (Exception exception)
        {
            Console.WriteLine("警告：临时目录清理失败，需手动删除 " + root + "：" + exception.Message);
        }
    }
}
