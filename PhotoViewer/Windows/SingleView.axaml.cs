using System;
using Avalonia.Controls;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Windows;

public partial class SingleView : UserControl
{
    public SingleView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            var insetsManager = TopLevel.GetTopLevel(this)?.InsetsManager;
            if (insetsManager != null)
            {
                insetsManager.DisplayEdgeToEdgePreference = true;
                insetsManager.IsSystemBarVisible = false;
            }
        };
    }
}