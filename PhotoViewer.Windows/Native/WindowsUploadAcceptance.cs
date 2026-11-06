using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Windows.Core;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsPhotoView
{
    internal (int Recent, long Spare, long Staging, long Total) TextureMemoryForChecks
        => (_recentTextures.Count, _spareTexture?.Bytes ?? 0, _stagedUpload?.Bytes ?? 0,
            System.Threading.Interlocked.Read(ref _gpuBytes));
}

internal sealed partial class WindowsMainWindow
{
    internal async Task RunUploadAcceptanceAsync()
    {
        var checks = new List<string>();
        try
        {
            await WaitForSmokeAsync(() => _photo.PhysicalViewport.Width > 1, "上传专项视口");
            await RunUploadChecksAsync(checks);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            checks.Add("FAIL " + ex);
            NativeStartupLog.Fail("upload acceptance", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(SmokeOutputDirectory, "upload-checks.json"),
                System.Text.Json.JsonSerializer.Serialize(checks, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }

    private async Task RunUploadChecksAsync(List<string> checks)
    {
        var original = _main.CurrentFile;
        var provider = WindowsNativeApplication.MemoryBudgetProvider;
        var submitted = new List<int>();
        void OnSubmitted(DecodedImage image, int generation) => submitted.Add(generation);
        _main.CurrentFile = null;
        _photo.SetDisplayTransformForChecks(WindowsDisplayTransform.SystemManaged);
        _photo.FrameSubmitted += OnSubmitted;
        using var small = SolidLinear(0.2f, 0.3f, 0.4f);
        using var first = UploadFixture(3000, 2000, 0.1f);
        using var second = UploadFixture(3000, 2000, 0.6f);
        try
        {
            _photo.SetImage(small, -100);
            RequireSmoke(submitted.SequenceEqual([-100]), "同步帧未在 Present 返回后通知");
            long before = _photo.TextureMemoryForChecks.Total;
            _photo.SetImage(first, -101);
            RequireSmoke(_photo.IsUploadPending && _photo.CurrentTextureSize == (320, 200),
                "后台上传未保持前帧或待提交状态提前结束");
            var inFlight = _photo.TextureMemoryForChecks;
            RequireSmoke(inFlight.Staging >= 3000L * 2000 * 8 && inFlight.Total >= before + inFlight.Staging,
                "在途 staging 未纳入纹理客户端账");
            RequireSmoke(!submitted.Contains(-101), "异步帧在纹理绑定前提前通知");
            _photo.SetImage(second, -102);
            RequireSmoke(_photo.IsUploadPending && !submitted.Contains(-102), "接力目标的待提交状态丢失");
            await WaitForSmokeAsync(() => submitted.Contains(-102), "后台上传最新代际提交");
            await WaitForUploadAsync("后台上传接力收尾");
            RequireSmoke(!submitted.Contains(-101) && _photo.CurrentTextureSize == (3000, 2000),
                "过期代际被上屏或最新帧未提交");
            _photo.SetDisplayTransformForChecks(WindowsDisplayTransform.SystemManaged);
            RequireSmoke(_photo.TryReadDisplayPixel(0.5, 0.5, out var pixel), "后台上传像素不可读");
            Near(pixel, (0.6f, 0.6f, 0.6f), 0.008f, "后台拷贝真实像素");
            RequireSmoke(_photo.TextureMemoryForChecks.Staging == 0, "后台上传完成后仍记在途 staging");
            checks.Add("PASS upload submission: async waits for target generation Present; old frame retained; superseded generation not signalled; staging accounted; GPU pixels exact");

            WindowsNativeApplication.InitializeMemoryBudget(new UploadPressureBudget(provider.GetBudgetBytes()));
            await Task.Delay(80);
            RequireSmoke(MemoryGovernor.CurrentLevel == PressureLevel.Caution, "压力替身未进入 Caution");
            _photo.SetImage(first, -103);
            await WaitForUploadAsync("压力下首帧提交");
            _photo.SetImage(second, -104);
            await WaitForUploadAsync("持续压力下第二帧提交");
            _photo.SetImage(small, -105);
            var trimmed = _photo.TextureMemoryForChecks;
            RequireSmoke(trimmed.Recent == 0 && trimmed.Spare == 0 && trimmed.Staging == 0,
                "持续 Caution 切图后非当前纹理重新回填");
            checks.Add("PASS sustained Caution: repeated synchronous/asynchronous switches retain no recent or spare textures");
        }
        finally
        {
            _photo.FrameSubmitted -= OnSubmitted;
            WindowsNativeApplication.InitializeMemoryBudget(provider);
            for (int i = 0; i < 3; i++) MemoryGovernor.EvaluateNow();
            _photo.SetImage(null, -106);
            _photo.SetDisplayTransformForChecks(WindowsDisplayColor.Current);
            _main.CurrentFile = original;
            if (original != null)
            {
                await WaitForSmokeAsync(() => _session.Image != null, "上传检查后恢复原图");
                await WaitForUploadAsync("上传检查后恢复纹理");
            }
        }
    }

    private static DecodedImage UploadFixture(int width, int height, float value)
    {
        int stride = checked(width * 8);
        var bytes = DecodedImage.RentPixels(checked(stride * height));
        var values = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan(0, stride * height));
        for (int i = 0; i < values.Length; i += 4)
        {
            values[i] = values[i + 1] = values[i + 2] = (Half)value;
            values[i + 3] = (Half)1;
        }
        return DecodedImage.CreatePooled(bytes, width, height, stride, PixelEncoding.RgbaF16,
            ImageColorSpace.LinearSrgb, width, height);
    }

    private sealed class UploadPressureBudget(long budget) : IMemoryBudgetProvider
    {
        public long GetBudgetBytes() => budget;
        public long GetAvailableBytes() => budget * 3 / 10;
        public long GetFootprintBytes() => budget * 7 / 10;
    }
}
