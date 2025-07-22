using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PhotoViewer.Views;

public partial class ControlView : UserControl
{
    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? ClearRequested;
    
    public ControlView()
    {
        InitializeComponent();
        
        PrevButton.Click += (s, e) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        NextButton.Click += (s, e) => NextRequested?.Invoke(this, EventArgs.Empty);
        ClearButton.Click += (s, e) => ClearRequested?.Invoke(this, EventArgs.Empty);
    }
    
    public void SetNavigationState(bool hasPrevious, bool hasNext)
    {
        PrevButton.IsEnabled = hasPrevious;
        NextButton.IsEnabled = hasNext;
    }
}