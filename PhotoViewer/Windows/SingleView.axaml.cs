using System;
using Avalonia;
using Avalonia.Controls;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Windows;

public partial class SingleView : UserControl
{
    public SingleView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var insetsManager = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (insetsManager != null)
        {
            insetsManager.DisplayEdgeToEdgePreference = true;
            insetsManager.IsSystemBarVisible = false;
        }
    }
}