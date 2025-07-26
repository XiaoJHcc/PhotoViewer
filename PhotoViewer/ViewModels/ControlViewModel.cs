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
        private readonly MainViewModel _mainViewModel;
        
        public ReactiveCommand<Unit, ImageFile?> PreviousCommand { get; }
        public ReactiveCommand<Unit, ImageFile?> NextCommand { get; }
        public ReactiveCommand<Unit, ImageFile?> ClearCommand { get; }
        
        public bool CanPrevious => _mainViewModel.HasPreviousFile();
        public bool CanNext => _mainViewModel.HasNextFile();
        
        public ControlViewModel(MainViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;
            
            // 创建命令
            
            PreviousCommand = ReactiveCommand.Create(
                () => _mainViewModel.CurrentFile = _mainViewModel.GetPreviousFile(),
                this.WhenAnyValue(x => x.CanPrevious));
            
            NextCommand = ReactiveCommand.Create(
                () => _mainViewModel.CurrentFile = _mainViewModel.GetNextFile(),
                this.WhenAnyValue(x => x.CanNext));
            
            ClearCommand = ReactiveCommand.Create(() => _mainViewModel.CurrentFile = null);
            
            // 当状态变化时更新命令可用性
            _mainViewModel.WhenAnyValue(vm => vm.CurrentFile)
                .Subscribe(_ => 
                {
                    this.RaisePropertyChanged(nameof(CanPrevious));
                    this.RaisePropertyChanged(nameof(CanNext));
                });
        }
        
        private void OnPrevious()
        {
            if (!CanPrevious) return;
            
            var currentIndex = _mainViewModel.FilteredFiles.IndexOf(_mainViewModel.CurrentFile);
            _mainViewModel.CurrentFile = _mainViewModel.FilteredFiles[currentIndex - 1];
        }
        
        private void OnNext()
        {
            if (!CanNext) return;
            
            var currentIndex = _mainViewModel.FilteredFiles.IndexOf(_mainViewModel.CurrentFile);
            _mainViewModel.CurrentFile = _mainViewModel.FilteredFiles[currentIndex + 1];
        }
        
        private void OnClear()
        {
            _mainViewModel.CurrentFile = null;
        }
    }
}