using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 为 Avalonia iOS 文本输入 responder 注入原生修正，解决数字键盘缺少完成键和 iPad 锚点错误的问题。
/// </summary>
internal static class iOSTextInputWorkarounds
{
    private const string AvaloniaTextInputResponderClassName = "Avalonia_iOS_AvaloniaView_TextInputResponder";
    private static readonly IntPtr TextInputViewSelector = Selector.GetHandle("textInputView");
    private static readonly IntPtr InputAccessoryViewSelector = Selector.GetHandle("inputAccessoryView");
    private static readonly IntPtr CaretRectForPositionSelector = Selector.GetHandle("caretRectForPosition:");
    private static readonly IntPtr FirstRectForRangeSelector = Selector.GetHandle("firstRectForRange:");
    private static readonly IntPtr NextResponderSelector = Selector.GetHandle("nextResponder");
    private static readonly IntPtr KeyboardTypeSelector = Selector.GetHandle("keyboardType");

    private static readonly NativeGetter TextInputViewGetter = GetTextInputView;
    private static readonly NativeGetter InputAccessoryViewGetter = GetInputAccessoryView;
    private static readonly NativeRectGetter CaretRectGetter = GetCaretRectForPosition;
    private static readonly NativeRectGetter FirstRectGetter = GetFirstRectForRange;

    private static bool _installed;
    private static UIToolbar? _numericKeyboardToolbar;

    /// <summary>
    /// 安装原生方法覆盖。重复调用会自动忽略。
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        var responderClass = Class.GetHandle(AvaloniaTextInputResponderClassName);
        if (responderClass == IntPtr.Zero)
        {
            return;
        }

        ReplaceInstanceMethod(responderClass, TextInputViewSelector, Marshal.GetFunctionPointerForDelegate(TextInputViewGetter), "@@:");
        ReplaceInstanceMethod(responderClass, InputAccessoryViewSelector, Marshal.GetFunctionPointerForDelegate(InputAccessoryViewGetter), "@@:");
        ReplaceInstanceMethod(responderClass, CaretRectForPositionSelector, Marshal.GetFunctionPointerForDelegate(CaretRectGetter));
        ReplaceInstanceMethod(responderClass, FirstRectForRangeSelector, Marshal.GetFunctionPointerForDelegate(FirstRectGetter));

        _installed = true;
    }

    /// <summary>
    /// 为 iPad 等场景返回真实的文本输入视图，使系统按 AvaloniaView 的坐标系定位输入面板。
    /// </summary>
    /// <param name="self">当前 responder。</param>
    /// <param name="cmd">当前 selector。</param>
    /// <returns>文本输入宿主视图句柄。</returns>
    private static IntPtr GetTextInputView(IntPtr self, IntPtr cmd)
    {
        var nextResponderHandle = IntPtr_objc_msgSend(self, NextResponderSelector);
        if (nextResponderHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return Runtime.GetNSObject(nextResponderHandle) is UIView nextView
            ? nextView.Handle
            : IntPtr.Zero;
    }

    /// <summary>
    /// 仅在 iPhone 数字键盘场景返回完成工具栏，补齐系统本身缺失的 Done 按钮。
    /// </summary>
    /// <param name="self">当前 responder。</param>
    /// <param name="cmd">当前 selector。</param>
    /// <returns>工具栏句柄；无需显示时返回空。</returns>
    private static IntPtr GetInputAccessoryView(IntPtr self, IntPtr cmd)
    {
        if (UIDevice.CurrentDevice.UserInterfaceIdiom != UIUserInterfaceIdiom.Phone)
        {
            return IntPtr.Zero;
        }

        var keyboardType = (UIKeyboardType)nint_objc_msgSend(self, KeyboardTypeSelector);
        if (!IsNumericKeyboardType(keyboardType))
        {
            return IntPtr.Zero;
        }

        return GetOrCreateNumericKeyboardToolbar().Handle;
    }

    /// <summary>
    /// 使用 Avalonia 根视图中的光标矩形作为 UIKit 光标定位结果，修复 iPad 数字输入面板锚点错误。
    /// </summary>
    /// <param name="self">当前 responder。</param>
    /// <param name="cmd">当前 selector。</param>
    /// <param name="argument">UIKit 传入的位置对象。</param>
    /// <returns>修正后的光标矩形。</returns>
    private static CGRect GetCaretRectForPosition(IntPtr self, IntPtr cmd, IntPtr argument)
    {
        return TryGetRootCursorRect(self, out var rect)
            ? ToCGRect(rect)
            : CGRect.Empty;
    }

    /// <summary>
    /// 使用 Avalonia 根视图中的光标矩形作为选区首矩形，避免 iPad 把数字输入浮层定位到左上角。
    /// </summary>
    /// <param name="self">当前 responder。</param>
    /// <param name="cmd">当前 selector。</param>
    /// <param name="argument">UIKit 传入的文本范围对象。</param>
    /// <returns>修正后的首矩形。</returns>
    private static CGRect GetFirstRectForRange(IntPtr self, IntPtr cmd, IntPtr argument)
    {
        return TryGetRootCursorRect(self, out var rect)
            ? ToCGRect(rect)
            : CGRect.Empty;
    }

    /// <summary>
    /// 判断当前是否为需要补完成键的数字键盘类型。
    /// </summary>
    /// <param name="keyboardType">UIKit 键盘类型。</param>
    /// <returns>纯数字相关键盘返回 true。</returns>
    private static bool IsNumericKeyboardType(UIKeyboardType keyboardType)
    {
        return keyboardType is UIKeyboardType.NumberPad or UIKeyboardType.DecimalPad or UIKeyboardType.PhonePad;
    }

    /// <summary>
    /// 懒加载数字键盘工具栏，并绑定关闭键盘动作。
    /// </summary>
    /// <returns>可复用的工具栏实例。</returns>
    private static UIToolbar GetOrCreateNumericKeyboardToolbar()
    {
        if (_numericKeyboardToolbar is not null)
        {
            return _numericKeyboardToolbar;
        }

        var toolbar = new UIToolbar
        {
            Translucent = true,
            BarStyle = UIBarStyle.Default,
        };
        toolbar.SizeToFit();

        var flexibleSpace = new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace);
        var doneButton = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => DismissCurrentResponder());
        toolbar.SetItems([flexibleSpace, doneButton], false);

        _numericKeyboardToolbar = toolbar;
        return toolbar;
    }

    /// <summary>
    /// 尝试让当前活动输入 responder 放弃焦点，从而收起数字键盘。
    /// </summary>
    private static void DismissCurrentResponder()
    {
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene windowScene)
            {
                continue;
            }

            foreach (var window in windowScene.Windows)
            {
                if (window.IsKeyWindow)
                {
                    window.EndEditing(true);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 从 Avalonia 的内部 responder 中反射读取根视图光标矩形。
    /// </summary>
    /// <param name="self">当前 responder。</param>
    /// <param name="rect">读取到的根坐标矩形。</param>
    /// <returns>成功读取返回 true。</returns>
    private static bool TryGetRootCursorRect(IntPtr self, out Rect rect)
    {
        rect = default;

        var responder = Runtime.GetNSObject(self);
        if (responder is null)
        {
            return false;
        }

        var responderType = responder.GetType();
        var viewField = responderType.GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic);
        if (viewField?.GetValue(responder) is null)
        {
            return false;
        }

        var avaloniaView = viewField.GetValue(responder);
        var cursorRectField = avaloniaView?.GetType().GetField("_cursorRect", BindingFlags.Instance | BindingFlags.NonPublic);
        if (cursorRectField?.GetValue(avaloniaView) is not Rect rootRect)
        {
            return false;
        }

        rect = rootRect;
        return rootRect.Width > 0 || rootRect.Height > 0;
    }

    /// <summary>
    /// 将 Avalonia 矩形转换为 UIKit 矩形。
    /// </summary>
    /// <param name="rect">Avalonia 矩形。</param>
    /// <returns>UIKit 矩形。</returns>
    private static CGRect ToCGRect(Rect rect)
    {
        return new CGRect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>
    /// 替换 Objective-C 实例方法；若目标类尚未实现则直接添加。
    /// </summary>
    /// <param name="cls">目标类。</param>
    /// <param name="selector">目标 selector。</param>
    /// <param name="implementation">新的 IMP。</param>
    /// <param name="fallbackTypes">当无法读取已有签名时使用的兜底签名。</param>
    private static void ReplaceInstanceMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string? fallbackTypes = null)
    {
        var method = class_getInstanceMethod(cls, selector);
        if (method != IntPtr.Zero)
        {
            var types = method_getTypeEncoding(method);
            class_replaceMethod(cls, selector, implementation, types);
            return;
        }

        if (!string.IsNullOrEmpty(fallbackTypes))
        {
            class_addMethod(cls, selector, implementation, fallbackTypes);
        }
    }

    /// <summary>
    /// Objective-C 实例 getter 方法签名。
    /// </summary>
    /// <param name="self">对象实例。</param>
    /// <param name="cmd">selector。</param>
    /// <returns>返回对象句柄。</returns>
    private delegate IntPtr NativeGetter(IntPtr self, IntPtr cmd);

    /// <summary>
    /// Objective-C 返回 CGRect 的实例方法签名。
    /// </summary>
    /// <param name="self">对象实例。</param>
    /// <param name="cmd">selector。</param>
    /// <param name="argument">selector 参数。</param>
    /// <returns>返回 CGRect。</returns>
    private delegate CGRect NativeRectGetter(IntPtr self, IntPtr cmd, IntPtr argument);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr method_getTypeEncoding(IntPtr method);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr class_replaceMethod(IntPtr cls, IntPtr name, IntPtr imp, IntPtr types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint nint_objc_msgSend(IntPtr receiver, IntPtr selector);
}