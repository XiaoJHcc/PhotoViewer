using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PhotoViewer.Controls;

/// <summary>
/// 将一段图标文本拆分为多个字符并叠放显示。
/// 主要用于控制栏按钮中由多字符组成的图标字形。
/// </summary>
public class OverlayGlyphText : Grid
{
    /// <summary>
    /// 需要叠放显示的文本。
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OverlayGlyphText, string?>(nameof(Text));

    /// <summary>
    /// 应用于内部 TextBlock 的样式类名。
    /// </summary>
    public static readonly StyledProperty<string?> GlyphClassProperty =
        AvaloniaProperty.Register<OverlayGlyphText, string?>(nameof(GlyphClass));

    static OverlayGlyphText()
    {
        TextProperty.Changed.AddClassHandler<OverlayGlyphText>((control, _) => control.RebuildGlyphs());
        GlyphClassProperty.Changed.AddClassHandler<OverlayGlyphText>((control, _) => control.RebuildGlyphs());
    }

    /// <summary>
    /// 构造叠放字形控件。
    /// </summary>
    public OverlayGlyphText()
    {
        RebuildGlyphs();
    }

    /// <summary>
    /// 获取或设置需要叠放显示的文本。
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// 获取或设置应用于内部 TextBlock 的样式类名。
    /// </summary>
    public string? GlyphClass
    {
        get => GetValue(GlyphClassProperty);
        set => SetValue(GlyphClassProperty, value);
    }

    /// <summary>
    /// 根据当前文本重新生成叠放字符。
    /// </summary>
    private void RebuildGlyphs()
    {
        Children.Clear();

        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        foreach (var character in Text.Reverse())
        {
            var textBlock = new TextBlock
            {
                Text = character.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            if (!string.IsNullOrWhiteSpace(GlyphClass))
            {
                textBlock.Classes.Add(GlyphClass);
            }

            Children.Add(textBlock);
        }
    }
}