namespace PhotoViewer.Shared;

public readonly record struct FilmSideMeasure(double Width, double ContentTrailing, double FilmstripX);

public static class FilmSideGeometry
{
    public static double Padding => MainScreenLayout.FilterMargin;

    public static FilmSideMeasure Measure(double leadingSafe, double filmBand, double filmLeadingPadding,
        double filmTrailingPadding, double entryWidth)
    {
        double filmTrailing = Math.Max(0, leadingSafe - filmLeadingPadding) + filmBand - filmTrailingPadding;
        double contentTrailing = Math.Max(filmTrailing, Padding + entryWidth);
        double width = contentTrailing + filmTrailingPadding;
        return new FilmSideMeasure(width, contentTrailing, width - filmBand);
    }

    public static LayoutRect AlignTrailing(double contentTrailing, double top, double width, double height)
        => new(contentTrailing - width, top, width, height);
}
