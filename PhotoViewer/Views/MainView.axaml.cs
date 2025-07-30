using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace PhotoViewer.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            Loaded += (_, _) =>
            {
            
                var insetsManager = TopLevel.GetTopLevel(this).InsetsManager;
                insetsManager.DisplayEdgeToEdgePreference = true;
                insetsManager.IsSystemBarVisible = false;
            };
        }
    }
}