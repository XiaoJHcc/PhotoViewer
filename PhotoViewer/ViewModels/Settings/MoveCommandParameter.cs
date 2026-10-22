namespace PhotoViewer.ViewModels.Settings;

/// <summary>
/// 列表排序命令参数：把一项从 <see cref="FromIndex"/> 移到 <see cref="ToIndex"/>。
/// </summary>
public class MoveCommandParameter
{
    public int FromIndex { get; }
    public int ToIndex { get; }

    public MoveCommandParameter(int fromIndex, int toIndex)
    {
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }
}
