using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    internal async Task RunRatingChecksAsync()
    {
        var checks = new List<string>();
        try
        {
            await WaitForSmokeAsync(() => _sized && _photo.PhysicalViewport.Width > 1, "评分专项初始布局");
            PlaceSmokeWindowInWorkArea();
            await Task.Delay(150);
            _root.UpdateLayout();
            var fixture = Path.Combine(SmokeOutputDirectory, "fixtures", "single", "checker.jpg");
            await Task.Run(() => CreateFixture(fixture));
            await OpenPathsAsync([fixture]);
            await WaitForSmokeAsync(() => _session.Image != null, "评分专项初始照片");
            await WaitForUploadAsync("评分专项初始纹理");
            await RunRatingConflictChecksAsync(checks);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            checks.Add("FAIL " + ex);
            NativeStartupLog.Fail("rating acceptance", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            File.WriteAllText(Path.Combine(SmokeOutputDirectory, "rating-checks.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
