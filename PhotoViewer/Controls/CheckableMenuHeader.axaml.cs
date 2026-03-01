using Avalonia;
using Avalonia.Controls;

namespace PhotoViewer.Controls;

public partial class CheckableMenuHeader : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CheckableMenuHeader, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<bool> IsIconVisibleProperty =
        AvaloniaProperty.Register<CheckableMenuHeader, bool>(nameof(IsIconVisible), false);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsIconVisible
    {
        get => GetValue(IsIconVisibleProperty);
        set => SetValue(IsIconVisibleProperty, value);
    }

    public CheckableMenuHeader()
    {
        InitializeComponent();
    }
}
