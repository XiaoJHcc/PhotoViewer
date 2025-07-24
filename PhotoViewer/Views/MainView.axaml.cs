using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class MainView : UserControl
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    
    public MainView()
    {
        InitializeComponent();
    }
}