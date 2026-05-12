using Avalonia.Controls;
using Avalonia.Input;
using PhotoViewer.Controls;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Views.Tools;

/// <summary>
/// DINO 诊断工具视图。6 张诊断瓦片共享同一十字准星:任意瓦片点击 → VM 下发新坐标;
/// 瓦片外的空闲区域点击 → 清空准星。
/// </summary>
public partial class DinoDebugView : UserControl
{
    /// <summary>初始化 DINO 诊断视图。</summary>
    public DinoDebugView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 瓦片点击:把归一化坐标转发给 VM。瓦片内部已把事件标记 Handled,此处只处理 VM 调度。
    /// </summary>
    private void OnTileClicked(object? sender, TileClickedEventArgs e)
    {
        if (DataContext is not DinoDebugViewModel vm) return;
        vm.OnTileClicked(e.U, e.V);
    }

    /// <summary>
    /// 空闲区域点击:瓦片内部点击已 Handled,只有瓦片之外的 StackPanel/ScrollViewer 背景会冒泡到这里。
    /// </summary>
    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not DinoDebugViewModel vm) return;
        vm.ClearCrosshair();
    }
}
