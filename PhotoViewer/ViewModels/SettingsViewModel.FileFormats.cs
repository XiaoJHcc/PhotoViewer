using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using PhotoViewer.Controls;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
    //////////////
    /// 同名文件处理
    //////////////
    
    private bool _sameNameAsOnePhoto = true;
    public bool SameNameAsOnePhoto
    {
        get => _sameNameAsOnePhoto;
        set => this.RaiseAndSetIfChanged(ref _sameNameAsOnePhoto, value);
    }
    
    //////////////
    /// 文件格式支持
    //////////////
    
    private ObservableCollection<FileFormatItem> _fileFormats = new();
    public ObservableCollection<FileFormatItem> FileFormats
    {
        get => _fileFormats;
        set => this.RaiseAndSetIfChanged(ref _fileFormats, value);
    }

    private List<string> _selectedFormats = new();
    public List<string> SelectedFormats
    {
        get => _selectedFormats;
        private set => this.RaiseAndSetIfChanged(ref _selectedFormats, value);
    }

    private void InitializeFileFormats()
    {
        FileFormats = new ObservableCollection<FileFormatItem>
        {
            new("JPG", new[] { ".jpg", ".jpeg" }, true),
            new("HEIF", new[] { ".heif", ".heic", ".avif", ".hif" }, true),
            new("PNG", new[] { ".png" }, true),
            new("TIFF", new[] { ".tiff", ".tif" }, false),
            new("WebP", new[] { ".webp" }, true),
            new("RAW", new[] { ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".srw" }, true),
            new("BMP", new[] { ".bmp" }, false),
            new("GIF", new[] { ".gif" }, false),
        };

        // 监听集合变化
        FileFormats.CollectionChanged += OnFileFormatsChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in FileFormats)
        {
            item.PropertyChanged += OnFileFormatItemChanged;
        }

        UpdateSelectedFormats();
    }

    private void OnFileFormatsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 为新添加的项目订阅属性变化
        if (e.NewItems != null)
        {
            foreach (FileFormatItem item in e.NewItems)
            {
                item.PropertyChanged += OnFileFormatItemChanged;
            }
        }

        // 为移除的项目取消订阅
        if (e.OldItems != null)
        {
            foreach (FileFormatItem item in e.OldItems)
            {
                item.PropertyChanged -= OnFileFormatItemChanged;
            }
        }

        UpdateSelectedFormats();
    }

    private void OnFileFormatItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileFormatItem.IsEnabled))
        {
            UpdateSelectedFormats();
        }
    }

    private void UpdateSelectedFormats()
    {
        SelectedFormats = FileFormats
            .Where(f => f.IsEnabled)
            .SelectMany(f => f.Extensions)
            .ToList();
    }

    // 新增：根据扩展名获取格式显示名（用于同名合并显示 RAW 等）
    public string GetFormatDisplayNameByExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        ext = ext.ToLowerInvariant();
        var item = FileFormats.FirstOrDefault(f => f.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)));
        return item?.DisplayName.ToUpperInvariant() ?? ext.TrimStart('.').ToUpperInvariant();
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveFileFormatCommand { get; private set; }

    private void OnMoveFileFormat(MoveCommandParameter parameter)
    {
        MoveFileFormat(parameter.FromIndex, parameter.ToIndex);
    }

    public void MoveFileFormat(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= FileFormats.Count || 
            toIndex < 0 || toIndex >= FileFormats.Count || 
            fromIndex == toIndex)
            return;

        var item = FileFormats[fromIndex];
        FileFormats.RemoveAt(fromIndex);
        FileFormats.Insert(toIndex, item);
    }

    public class FileFormatItem : ReactiveObject
    {
        private string _displayName;
        private string[] _extensions;
        private bool _isEnabled;

        public string DisplayName
        {
            get => _displayName;
            set => this.RaiseAndSetIfChanged(ref _displayName, value);
        }

        public string[] Extensions
        {
            get => _extensions;
            set => this.RaiseAndSetIfChanged(ref _extensions, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        // 用于显示的扩展名字符串
        public string ExtensionsText => string.Join(" / ", Extensions);

        // 保持向后兼容的 Name 属性
        public string Name => DisplayName;

        public FileFormatItem(string displayName, string[] extensions, bool isEnabled = true)
        {
            _displayName = displayName;
            _extensions = extensions;
            _isEnabled = isEnabled;
        }
    }
}

