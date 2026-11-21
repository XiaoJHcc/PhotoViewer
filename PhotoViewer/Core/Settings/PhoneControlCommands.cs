namespace PhotoViewer.Core.Settings;

public static class PhoneControlCommands
{
    public const int MaxCount = 3;

    /// <summary>SETTINGS-08：按勾选顺序选择手机端实际显示的独立命令。</summary>
    public static string[] Resolve(IEnumerable<string> checkedCommandIds, bool showNavigationButtons)
        => checkedCommandIds
            .Where(id => !showNavigationButtons || id is not ("Previous" or "Next"))
            .Take(MaxCount)
            .ToArray();
}
