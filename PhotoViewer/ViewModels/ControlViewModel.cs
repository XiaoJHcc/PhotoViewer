using System;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Linq;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels
{
    public class ControlViewModel : ReactiveObject
    {
        private readonly AppState _state;
        
        public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
        public ReactiveCommand<Unit, Unit> NextCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCommand { get; }
        
        public bool CanPrevious => 
            _state.CurrentFile != null && 
            _state.FilteredFiles.IndexOf(_state.CurrentFile) > 0;
        
        public bool CanNext => 
            _state.CurrentFile != null && 
            _state.FilteredFiles.IndexOf(_state.CurrentFile) < _state.FilteredFiles.Count - 1;
        
        public ControlViewModel(AppState state)
        {
            _state = state;
            
            // 创建命令
            PreviousCommand = ReactiveCommand.Create(OnPrevious);
            NextCommand = ReactiveCommand.Create(OnNext);
            ClearCommand = ReactiveCommand.Create(OnClear);
            
            // 当状态变化时更新命令可用性
            // _state.WhenAnyValue(s => s.CurrentFile)
            //     .Subscribe(_ => UpdateCommandState());
            //
            // _state.FilteredFiles.CollectionChanged += (s, e) => UpdateCommandState();
            // Deepseek BUG
            
            _state.WhenAnyValue(s => s.CurrentFile)
                .Subscribe(_ => UpdateCommandState());
        }
        
        private void UpdateCommandState()
        {
            this.RaisePropertyChanged(nameof(CanPrevious));
            this.RaisePropertyChanged(nameof(CanNext));
            
            // 更新命令的可执行状态
            PreviousCommand.ThrownExceptions.Subscribe(ex => Console.WriteLine(ex.Message));
            NextCommand.ThrownExceptions.Subscribe(ex => Console.WriteLine(ex.Message));
        }
        
        private void OnPrevious()
        {
            if (!CanPrevious) return;
            
            var currentIndex = _state.FilteredFiles.IndexOf(_state.CurrentFile);
            _state.CurrentFile = _state.FilteredFiles[currentIndex - 1];
        }
        
        private void OnNext()
        {
            if (!CanNext) return;
            
            var currentIndex = _state.FilteredFiles.IndexOf(_state.CurrentFile);
            _state.CurrentFile = _state.FilteredFiles[currentIndex + 1];
        }
        
        private void OnClear()
        {
            _state.CurrentFile = null;
        }
    }
}