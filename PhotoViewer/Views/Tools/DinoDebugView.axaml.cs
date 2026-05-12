using Avalonia.Controls;
using Avalonia.Input;
using PhotoViewer.Core.AI;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Views.Tools;

/// <summary>
/// DINO 诊断工具视图。PCA 预览的点击由本类翻译为 patch 网格坐标,回调到 VM 重算 cosine 图。
/// </summary>
public partial class DinoDebugView : UserControl
{
    /// <summary>初始化 DINO 诊断视图。</summary>
    public DinoDebugView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 处理 PCA 预览点击:将鼠标坐标(相对 Image 控件)映射到 32×32 patch 网格,通知 VM 更新参考点。
    /// </summary>
    private void OnPcaImagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image img) return;
        if (DataContext is not DinoDebugViewModel vm) return;
        if (vm.PcaRgbMap == null) return;

        var pos = e.GetPosition(img);
        double w = img.Bounds.Width;
        double h = img.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        int gx = (int)(pos.X / w * PatchHeatmap.Grid);
        int gy = (int)(pos.Y / h * PatchHeatmap.Grid);
        vm.SetReferencePoint(gx, gy);
    }
}
