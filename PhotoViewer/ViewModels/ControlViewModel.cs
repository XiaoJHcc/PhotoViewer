using ReactiveUI;

namespace PhotoViewer.ViewModels
{
    public class ControlViewModel : ReactiveObject
    {
        private readonly MainViewModel _main;
        public MainViewModel Main => _main;
        
        public bool CanPrevious => Main.HasPreviousFile();
        public bool CanNext => Main.HasNextFile();

        public double ScaleSlider
        {
            get => Main.ImageViewModel.Scale;
            set
            {
                Main.ImageViewModel.Zoom(value);
            }
        }


        public ControlViewModel(MainViewModel main)
        {
            _main = main;
        }

        public void Update()
        {
            this.RaisePropertyChanged(nameof(CanPrevious));
            this.RaisePropertyChanged(nameof(CanNext));
        }
        
        public void OnPrevious()
        {
            if (!CanPrevious) return;
            
            var currentIndex = Main.FilteredFiles.IndexOf(Main.CurrentFile);
            Main.CurrentFile = Main.FilteredFiles[currentIndex - 1];
        }
        
        public void OnNext()
        {
            if (!CanNext) return;
            
            var currentIndex = Main.FilteredFiles.IndexOf(Main.CurrentFile);
            Main.CurrentFile = Main.FilteredFiles[currentIndex + 1];
        }
        
        public void OnClear()
        {
            Main.CurrentFile = null;
        }
    }
}