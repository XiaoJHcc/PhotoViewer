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
        private readonly AppState _state;
        
        public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
        public ReactiveCommand<Unit, Unit> NextCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCommand { get; }
        
        private bool _canPrevious;
        public bool CanPrevious
        {
            get => _canPrevious;
            set => this.RaiseAndSetIfChanged(ref _canPrevious, value);
        }
        
        private bool _canNext;
        public bool CanNext
        {
            get => _canNext;
            set => this.RaiseAndSetIfChanged(ref _canNext, value);
        }
        
        public ControlViewModel(AppState state)
        {
            _state = state;
            
            // 创建命令
            PreviousCommand = ReactiveCommand.Create(OnPrevious, this.WhenAnyValue(x => x.CanPrevious));
            NextCommand = ReactiveCommand.Create(OnNext, this.WhenAnyValue(x => x.CanNext));
            ClearCommand = ReactiveCommand.Create(OnClear);
            
            // 监听状态变化
            _state.WhenAnyValue(s => s.CurrentFile)
                .Subscribe(_ => UpdateCommandState());
            
            // 监听过滤文件集合变化
            // Observable.FromEventPattern<NotifyCollectionChangedEventArgs>(
            //     h => _state.FilteredFiles.CollectionChanged += h,
            //     h => _state.FilteredFiles.CollectionChanged -= h
            // ).Subscribe(_ => UpdateCommandState());
            
            // 初始更新状态
            UpdateCommandState();
        }
        
        private void UpdateCommandState()
        {
            if (_state.CurrentFile == null || _state.FilteredFiles.Count == 0)
            {
                CanPrevious = false;
                CanNext = false;
                return;
            }
            
            var currentIndex = _state.FilteredFiles.IndexOf(_state.CurrentFile);
            CanPrevious = currentIndex > 0;
            CanNext = currentIndex < _state.FilteredFiles.Count - 1;
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