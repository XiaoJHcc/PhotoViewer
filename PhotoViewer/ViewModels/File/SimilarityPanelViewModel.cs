using ReactiveUI;

namespace PhotoViewer.ViewModels.File;

/// <summary>
/// 相似聚类面板的视图模型(阶段 3 填充)。
/// 当前为空壳:暴露空集合与 IsEmpty,UI 显示占位文案;阶段 3 接入 SimilarityService 后实现实际逻辑。
/// </summary>
public class SimilarityPanelViewModel : ReactiveObject
{
    /// <summary>是否无相似项,用于显示占位文案</summary>
    public bool IsEmpty => true;
}
