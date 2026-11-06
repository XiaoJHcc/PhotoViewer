using System;
using System.Diagnostics;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsPhotoView
{
    private long _wantedStarted;
    private WeakReference<DecodedImage>? _submitted;
    private int _submittedGeneration = -1;

    internal event Action<DecodedImage, int>? FrameSubmitted;

    private void TrackWanted(DecodedImage? image, int generation)
    {
        if (generation == _generation && _wanted?.TryGetTarget(out var previous) == true
            && ReferenceEquals(previous, image)) return;
        _wantedStarted = Stopwatch.GetTimestamp();
    }

    private void NotifyFrameSubmitted()
    {
        if (_current is not { Key: { } key } current || !key.TryGetTarget(out var image)
            || _wanted == null || !current.Matches(image)
            || !_wanted.TryGetTarget(out var wanted) || !ReferenceEquals(image, wanted)) return;
        if (_submittedGeneration == _generation && _submitted?.TryGetTarget(out var submitted) == true
            && ReferenceEquals(submitted, image)) return;
        _submittedGeneration = _generation;
        _submitted = new WeakReference<DecodedImage>(image);
        WindowsUploadStats.RecordSubmission(WindowsUploadStats.Elapsed(_wantedStarted));
        FrameSubmitted?.Invoke(image, _generation);
    }
}

internal sealed partial class WindowsMainWindow : IDisplayLoadSignal
{
    private event Action<IFileRef>? PixelsReady;

    IDisposable IDisplayLoadSignal.SubscribePixelsReady(Action<IFileRef> handler)
    {
        PixelsReady += handler;
        return new SubmissionSubscription(() => PixelsReady -= handler);
    }

    string IDisplayLoadSignal.CompletionStage => "present-return";
    bool IDisplayLoadSignal.CurrentIsProxy => _session.IsProxy;
    PixelEncoding? IDisplayLoadSignal.CurrentEncoding => _session.Image?.Encoding;

    IDisposable IDisplayLoadSignal.Subscribe(Action<IFileRef> handler)
    {
        void Submitted(DecodedImage image, int generation)
        {
            if (generation == _session.Generation && !_session.IsSubstitute
                && ReferenceEquals(image, _session.Image) && _main.CurrentFile is { } file)
                handler(file.File);
        }
        _photo.FrameSubmitted += Submitted;
        return new SubmissionSubscription(() => _photo.FrameSubmitted -= Submitted);
    }

    private sealed class SubmissionSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
