namespace PhotoViewer.Core.Memory;

public readonly record struct SystemMemoryInfo(long TotalBytes, long AvailableBytes)
{
    public static SystemMemoryInfo Unknown => new(0, -1);
}
