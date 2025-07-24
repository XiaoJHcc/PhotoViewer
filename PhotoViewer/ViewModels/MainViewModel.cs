using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewer.Views;

namespace PhotoViewer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
        private IStorageFile? _currentFile;
        private List<IStorageFile>? _currentFolderFiles;
        
        public ThumbnailViewModel ThumbnailViewModel { get; } = new ThumbnailViewModel();

        public IStorageFile? CurrentFile;
        
        public void SetFolderFiles(IEnumerable<IStorageFile> files)
        {
            _currentFolderFiles = new List<IStorageFile>(files);
        }
        
        public void ClearFolderFiles()
        {
            _currentFolderFiles = null;
            CurrentFile = null;
        }
        
        public bool HasPreviousFile()
        {
            if (_currentFolderFiles == null || CurrentFile == null) 
                return false;
            
            var currentIndex = _currentFolderFiles.IndexOf(CurrentFile);
            return currentIndex > 0;
        }
        
        public bool HasNextFile()
        {
            if (_currentFolderFiles == null || CurrentFile == null) 
                return false;
            
            var currentIndex = _currentFolderFiles.IndexOf(CurrentFile);
            return currentIndex < _currentFolderFiles.Count - 1;
        }
        
        public IStorageFile? GetPreviousFile()
        {
            if (_currentFolderFiles == null || CurrentFile == null) 
                return null;
            
            var currentIndex = _currentFolderFiles.IndexOf(CurrentFile);
            return currentIndex > 0 ? _currentFolderFiles[currentIndex - 1] : null;
        }
        
        public IStorageFile? GetNextFile()
        {
            if (_currentFolderFiles == null || CurrentFile == null) 
                return null;
            
            var currentIndex = _currentFolderFiles.IndexOf(CurrentFile);
            return currentIndex < _currentFolderFiles.Count - 1 
                ? _currentFolderFiles[currentIndex + 1] 
                : null;
        }
}