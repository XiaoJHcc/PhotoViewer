using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PhotoViewer.Controls;
using PhotoViewer.ViewModels;
using Avalonia.Data.Converters;

namespace PhotoViewer.Views;

public partial class ExifSettingsView : UserControl
{
    public ExifSettingsView()
    {
        InitializeComponent();
    }
}