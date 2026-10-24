namespace PhotoViewer.Core.Settings;

/// <summary>跨会话保留的轻量界面状态（不属于可持久化设置文档）。</summary>
public interface IViewStateStore
{
    bool? GetBool(string key);

    void SetBool(string key, bool value);
}

/// <summary>界面状态键名；平台偏好实现与 VM 共用。</summary>
public static class SettingsViewStateKeys
{
    public const string SimilarityPanelExpanded = "SimilarityPanelExpanded";
}
