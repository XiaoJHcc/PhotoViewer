using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Input;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主窗口键盘入口的纯判定:平台事件已翻译成共享手势之后的"消费/执行"规则。
/// 真实事件挂在根元素 PreviewKeyDown 上,不依赖冒泡焦点路径;这里只保留可离线验证的决策部分。
/// </summary>
internal static class WindowsKeyDispatch
{
    /// <summary>快捷键路由:<c>Consume</c> 表示按键归主窗所有,<c>Command</c> 非 null 时才执行。</summary>
    /// <param name="Command">要执行的命令;null 表示只消费不执行</param>
    /// <param name="Consume">是否消费按键</param>
    public readonly record struct Route(string? Command, bool Consume)
    {
        /// <summary>放行:按键继续交给焦点控件与系统。</summary>
        public static Route PassThrough => new(null, false);

        /// <summary>是否真的要执行命令(不可重复命令的重复按键只消费不执行)。</summary>
        public bool ShouldExecute => Command != null;
    }

    /// <summary>
    /// 解析一次按键:文本编辑焦点、未映射键、未绑定键与未知命令都放行;
    /// 不可重复命令的自动重复按键只消费不执行(INPUT-05)。
    /// </summary>
    /// <param name="gesture">已翻译的共享手势</param>
    /// <param name="settings">当前设置</param>
    /// <param name="textEditingFocus">焦点是否在文本编辑控件内</param>
    /// <param name="wasKeyDown">是否为按住产生的重复按键</param>
    /// <returns>路由结果</returns>
    public static Route Resolve(
        AppGesture? gesture,
        SettingsViewModel settings,
        bool textEditingFocus,
        bool wasKeyDown)
    {
        if (textEditingFocus || gesture is null)
        {
            return Route.PassThrough;
        }

        // 命令是否可达只看共享命令表,图标映射不参与输入闸门。
        var command = InputCommandMap.CommandForGesture(gesture, settings);
        if (command == null || !InputCommandMap.IsCommandKnown(command))
        {
            return Route.PassThrough;
        }

        if (wasKeyDown && !InputCommandMap.IsRepeatable(command))
        {
            return new Route(null, true);
        }

        return new Route(command, true);
    }

    /// <summary>
    /// 焦点元素是否落在文本编辑控件内(文本框、富文本、密码框、自动建议框)。
    /// 快捷键在根元素预处置,必须先放行这些控件,否则输入内容会被当成快捷键吞掉。
    /// </summary>
    /// <param name="element">待判定的元素(通常为焦点元素)</param>
    /// <returns>属于文本编辑控件时返回 true</returns>
    public static bool IsTextEditingElement(DependencyObject? element)
    {
        for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or RichEditBox or PasswordBox or AutoSuggestBox)
            {
                return true;
            }
        }

        return false;
    }
}
