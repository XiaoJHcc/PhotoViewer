using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using PhotoViewer.Core.Settings;
using ReactiveUI;
using System.Reactive;

namespace PhotoViewer.ViewModels.Settings;

public partial class SettingsViewModel
{
    //////////////
    /// EXIF 显示设置
    //////////////
    
    private ObservableCollection<ExifDisplayItem> _exifDisplayItems = new();
    public ObservableCollection<ExifDisplayItem> ExifDisplayItems
    {
        get => _exifDisplayItems;
        set => this.RaiseAndSetIfChanged(ref _exifDisplayItems, value);
    }

    private List<ExifDisplayItem> _enabledExifItems = new();
    public List<ExifDisplayItem> EnabledExifItems
    {
        get => _enabledExifItems;
        private set => this.RaiseAndSetIfChanged(ref _enabledExifItems, value);
    }

    private void InitializeExifDisplay()
    {
        MoveExifDisplayCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveExifDisplay);

        ExifDisplayItems = new ObservableCollection<ExifDisplayItem>(
            SettingsCatalog.ExifFields.Select(definition => new ExifDisplayItem(
                definition.DisplayName,
                definition.Id,
                definition.IsEnabledByDefault)));

        // 监听集合变化
        ExifDisplayItems.CollectionChanged += OnExifDisplayItemsChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged += OnExifDisplayItemChanged;
        }

        UpdateEnabledExifItems();
    }

    private void OnExifDisplayItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ExifDisplayItem item in e.NewItems)
            {
                item.PropertyChanged += OnExifDisplayItemChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (ExifDisplayItem item in e.OldItems)
            {
                item.PropertyChanged -= OnExifDisplayItemChanged;
            }
        }

        UpdateEnabledExifItems();
        RequestSave();
    }

    private void OnExifDisplayItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExifDisplayItem.IsEnabled))
        {
            UpdateEnabledExifItems();
            RequestSave();
        }
    }

    private string _enabledExifItemsKey = string.Empty;

    private void UpdateEnabledExifItems()
    {
        // 其他消费者依赖新的 ExifDisplayItem 列表实例，因此总是替换；展示通道只按内容摘要发布。
        EnabledExifItems = ExifDisplayItems
            .Where(item => item.IsEnabled)
            .ToList();

        var key = string.Join(
            "\u001f",
            EnabledExifItems.Select(item => item.PropertyName + "\u001e" + item.DisplayName));
        if (key == _enabledExifItemsKey) return;

        _enabledExifItemsKey = key;
        PublishPresentationImpact(SettingsChangeImpact.Exif);
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveExifDisplayCommand { get; private set; } = null!;

    private void OnMoveExifDisplay(MoveCommandParameter parameter)
    {
        MoveExifDisplay(parameter.FromIndex, parameter.ToIndex);
    }

    public void MoveExifDisplay(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= ExifDisplayItems.Count || 
            toIndex < 0 || toIndex >= ExifDisplayItems.Count || 
            fromIndex == toIndex)
            return;

        // 移除 + 插入会各发一次 EnabledExifItems，排序按一次展示刷新发布。
        RunInPresentationBatch(() =>
        {
            var item = ExifDisplayItems[fromIndex];
            ExifDisplayItems.RemoveAt(fromIndex);
            ExifDisplayItems.Insert(toIndex, item);
        });
        RequestSave();
    }

    public class ExifDisplayItem : ReactiveObject
    {
        private string _displayName;
        private string _propertyName;
        private bool _isEnabled;

        public string DisplayName
        {
            get => _displayName;
            set => this.RaiseAndSetIfChanged(ref _displayName, value);
        }

        public string PropertyName
        {
            get => _propertyName;
            set => this.RaiseAndSetIfChanged(ref _propertyName, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        public ExifDisplayItem(string displayName, string propertyName, bool isEnabled = true)
        {
            _displayName = displayName;
            _propertyName = propertyName;
            _isEnabled = isEnabled;
        }
    }
}
