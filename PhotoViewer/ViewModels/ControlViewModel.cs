using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Binding;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels
{
    public class ControlViewModel : ReactiveObject
    {
        private readonly MainViewModel Main;
        
        // public ReactiveCommand<Unit, ImageFile?> PreviousCommand { get; }
        // public ReactiveCommand<Unit, ImageFile?> NextCommand { get; }
        // public ReactiveCommand<Unit, ImageFile?> ClearCommand { get; }
        
        public bool CanPrevious => Main.HasPreviousFile();
        public bool CanNext => Main.HasNextFile();
        
        public ControlViewModel(MainViewModel main)
        {
            Main = main;
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