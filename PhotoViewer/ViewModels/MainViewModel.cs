using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private string _greeting = "Welcome to PhotoViewer!";
}