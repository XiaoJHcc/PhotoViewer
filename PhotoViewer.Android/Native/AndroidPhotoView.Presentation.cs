using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Android.Native;

internal sealed partial class AndroidPhotoView
{
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private CancellationTokenSource? _preparation;
    private DecodedImage? _requestedImage;
    private int _requestedGeneration;
    private int _presentationVersion;
    private int _surfaceVersion;
    private Java.Lang.Runnable? _drawFrame;
    private bool _drawScheduled;
    private int _renderedFrames;

    private int _preparationCount;
    internal bool PreparationPending => Volatile.Read(ref _preparationCount) > 0;
    internal DecodedImage? PresentedImage => _image;
    internal double LastPreparationMs { get; private set; }
    internal int LastPreparationThread { get; private set; }
    internal int RenderedFrames => _renderedFrames;
    internal string? PreparationError { get; private set; }
    internal event Action<string>? PresentationFailed;

    internal void SetImageForChecks(DecodedImage? image, int generation)
    {
        InvalidatePresentation();
        PreparationError = null;
        SetRequestedImage(image, generation);
        SetImageCore(image, generation);
    }

    internal void PresentImage(DecodedImage? image, int generation)
    {
        if (_disposed) return;
        if (_preparation != null && ReferenceEquals(image, _requestedImage) && generation == _requestedGeneration) return;
        InvalidatePresentation();
        PreparationError = null;
        SetRequestedImage(image, generation);
        if (image == null || !_surfaceReady || !_renderer.NeedsPreparation(image))
        {
            SetImageCore(image, generation);
            return;
        }
        if (!image.TryAcquire()) throw new ObjectDisposedException(nameof(image));
        var cancellation = new CancellationTokenSource();
        _preparation = cancellation;
        Interlocked.Increment(ref _preparationCount);
        int request = _presentationVersion, surface = _surfaceVersion;
        var target = SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
        _ = Task.Run(() => PrepareImageAsync(image, generation, request, surface, target, cancellation));
    }

    private async Task PrepareImageAsync(DecodedImage source, int generation, int request, int surface,
        ImageColorSpace target, CancellationTokenSource cancellation)
    {
        DecodedImage? prepared = null;
        MemoryGovernor.AdmissionPermit? permit = null;
        bool entered = false;
        var ct = cancellation.Token;
        try
        {
            await _preparationGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            ct.ThrowIfCancellationRequested();
            MemoryGovernor.TryAcquire(checked((long)source.Width * source.Height * 4 * 9 / 8),
                DecodePriority.P0_Current, out permit);
            long start = Stopwatch.GetTimestamp();
            int thread = Environment.CurrentManagedThreadId;
            prepared = ColorEngine.ToDisplayEncoding(source, PixelEncoding.Rgba8, target, cancellationToken: ct);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            ct.ThrowIfCancellationRequested();
            await UiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested || request != _presentationVersion
                    || surface != _surfaceVersion || !_surfaceReady) return;
                LastPreparationMs = elapsed;
                LastPreparationThread = thread;
                SetImageCore(source, generation, prepared);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] 主图后台准备失败：{exception}");
            await UiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || request != _presentationVersion || surface != _surfaceVersion) return;
                PreparationError = StatusMessages.ReasonFor(exception);
                SetRequestedImage(_image, _generation);
                PresentationFailed?.Invoke(PreparationError);
            }).ConfigureAwait(false);
        }
        finally
        {
            prepared?.Release();
            permit?.Dispose();
            source.Release();
            if (entered) _preparationGate.Release();
            await UiDispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_preparation, cancellation)) _preparation = null;
                cancellation.Dispose();
                Interlocked.Decrement(ref _preparationCount);
            }).ConfigureAwait(false);
        }
    }

    private void SetRequestedImage(DecodedImage? image, int generation)
    {
        if (!ReferenceEquals(image, _requestedImage))
        {
            if (image != null && !image.TryAcquire()) throw new ObjectDisposedException(nameof(image));
            var previous = _requestedImage;
            _requestedImage = image;
            previous?.Release();
        }
        _requestedGeneration = generation;
    }

    private void InvalidatePresentation()
    {
        _presentationVersion++;
        _preparation?.Cancel();
        _preparation = null;
    }

    private void RequestDraw()
    {
        if (_disposed || !_surfaceReady || _drawScheduled) return;
        _drawFrame ??= new Java.Lang.Runnable(() =>
        {
            _drawScheduled = false;
            if (!_disposed) Draw();
        });
        _drawScheduled = true;
        PostOnAnimation(_drawFrame);
    }

    private void CancelDraw()
    {
        if (_drawFrame != null) RemoveCallbacks(_drawFrame);
        _drawScheduled = false;
    }

    private AndroidPhotoRenderer CreateRenderer()
    {
        long budget = MemoryGovernor.BudgetBytes;
        return new AndroidPhotoRenderer
        {
            RecentTextureByteLimit = budget > 0 ? Math.Clamp(budget / 12, 16L << 20, 384L << 20) : 64L << 20,
        };
    }
}
