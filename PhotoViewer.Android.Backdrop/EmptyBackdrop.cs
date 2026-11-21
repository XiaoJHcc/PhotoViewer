using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>空取样源，对应上游 <c>emptyBackdrop()</c>：不画任何内容。</summary>
public sealed class EmptyBackdrop : IBackdrop
{
    /// <summary>共享实例。</summary>
    public static EmptyBackdrop Instance { get; } = new();

    private EmptyBackdrop()
    {
    }

    /// <inheritdoc />
    public bool IsCoordinatesDependent => false;

    /// <inheritdoc />
    public void DrawBackdrop(Gfx.Canvas canvas, in BackdropDrawContext context)
    {
    }
}
