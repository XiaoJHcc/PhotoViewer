using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Input;
using PhotoViewer.Core.Settings;
using ReactiveUI;


namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
     private IDisposable InitializePersistence()
    {
        var subscription = _saveRequests
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .SelectMany(async _ => { await PersistAsync(); return Unit.Default; })
            .Subscribe();

        this.Changed.Subscribe(_ => RequestSave());

        ScalePresets.CollectionChanged += OnScalePresetsChanged;
        foreach (var preset in ScalePresets)
        {
            preset.PropertyChanged += OnScalePresetPropertyChanged;
        }

        return subscription;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var model = await _settingsService.LoadAsync().ConfigureAwait(false);
            if (model.Version > 0)
            {
                ApplyModel(model);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsViewModel.LoadSettingsAsync failed: {ex.Message}");
        }
        finally
        {
            _hasLoaded = true;
            RequestSave(); // create storage when missing
        }
    }

    private async Task PersistAsync()
    {
        if (_isRestoring || !_hasLoaded) return;
        try
        {
            var model = ToModel();
            await _settingsService.SaveAsync(model).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsViewModel.PersistAsync failed: {ex.Message}");
        }
    }

    private void RequestSave()
    {
        if (_isRestoring || !_hasLoaded) return;
        _saveRequests.OnNext(Unit.Default);
    }

    private SettingsModel ToModel()
    {
         return new SettingsModel
         {
            Version = 1,
             LayoutMode = LayoutMode,
            ShowZoomIndicator = ShowZoomIndicator,
            ScalePresets = ScalePresets.Select(s => s.Value).ToList(),
            ShowRating = ShowRating,
            SafeSetRating = SafeSetRating,
            SameNameAsOnePhoto = SameNameAsOnePhoto,
            FileFormats = FileFormats.Select(f => new FileFormatModel
            {
                DisplayName = f.DisplayName,
                Extensions = f.Extensions.ToList(),
                IsEnabled = f.IsEnabled
            }).ToList(),
            ExifDisplayItems = ExifDisplayItems.Select(e => new ExifDisplayModel
            {
                DisplayName = e.DisplayName,
                PropertyName = e.PropertyName,
                IsEnabled = e.IsEnabled
            }).ToList(),
            Hotkeys = Hotkeys.Select(h => new HotkeyModel
            {
                Name = h.Name,
                Command = h.Command,
                DisplaySymbol = h.DisplaySymbol,
                Tooltip = h.Tooltip,
                IsDisplay = h.IsDisplay,
                Primary = ToGestureModel(h.PrimaryHotkey),
                Secondary = ToGestureModel(h.SecondaryHotkey)
            }).ToList(),
            UseAppleKeyboard = UseAppleKeyboard,
            MapCommandTarget = MapCommandTarget,
            MapOptionTarget = MapOptionTarget,
            MapControlTarget = MapControlTarget,
            BitmapCacheMaxCount = BitmapCacheMaxCount,
            BitmapCacheMaxMemory = BitmapCacheMaxMemory,
            PreloadForwardCount = PreloadForwardCount,
            PreloadBackwardCount = PreloadBackwardCount,
            VisibleCenterPreloadCount = VisibleCenterPreloadCount,
            VisibleCenterDelayMs = VisibleCenterDelayMs,
            PreloadParallelism = PreloadParallelism
        };
    }

    private void ApplyModel(SettingsModel model)
    {
        _isRestoring = true;
        try
        {
            LayoutMode = model.LayoutMode;
            ShowZoomIndicator = model.ShowZoomIndicator;

            if (model.ScalePresets.Count > 0)
            {
                ResetScalePresets(model.ScalePresets);
            }

            ShowRating = model.ShowRating;
            SafeSetRating = model.SafeSetRating;
            SameNameAsOnePhoto = model.SameNameAsOnePhoto;

            if (model.FileFormats.Count > 0)
            {
                ResetFileFormats(model.FileFormats);
            }

            if (model.ExifDisplayItems.Count > 0)
            {
                ResetExifDisplayItems(model.ExifDisplayItems);
            }

            if (model.Hotkeys.Count > 0)
            {
                ResetHotkeys(model.Hotkeys);
            }

            UseAppleKeyboard = model.UseAppleKeyboard;
            MapCommandTarget = model.MapCommandTarget;
            MapOptionTarget = model.MapOptionTarget;
            MapControlTarget = model.MapControlTarget;

            if (model.BitmapCacheMaxCount > 0) BitmapCacheMaxCount = model.BitmapCacheMaxCount;
            if (model.BitmapCacheMaxMemory > 0) BitmapCacheMaxMemory = model.BitmapCacheMaxMemory;
            if (model.PreloadForwardCount > 0) PreloadForwardCount = model.PreloadForwardCount;
            if (model.PreloadBackwardCount > 0) PreloadBackwardCount = model.PreloadBackwardCount;
            if (model.VisibleCenterPreloadCount > 0) VisibleCenterPreloadCount = model.VisibleCenterPreloadCount;
            if (model.VisibleCenterDelayMs > 0) VisibleCenterDelayMs = model.VisibleCenterDelayMs;
            if (model.PreloadParallelism > 0) PreloadParallelism = model.PreloadParallelism;
        }
        finally
        {
            _isRestoring = false;
        }

        // 更新派生状态
        UpdateSelectedFormats();
        UpdateEnabledExifItems();
        CheckHotkeyConflicts();
    }

    private void ResetScalePresets(IEnumerable<double> presets)
    {
        ScalePresets.CollectionChanged -= OnScalePresetsChanged;
        foreach (var preset in ScalePresets)
        {
            preset.PropertyChanged -= OnScalePresetPropertyChanged;
        }

        ScalePresets.Clear();
        foreach (var value in presets)
        {
            var text = (value * 100).ToString("0.###", CultureInfo.InvariantCulture);
            ScalePresets.Add(new ScalePreset(text));
        }

        SortScalePreset();

        foreach (var preset in ScalePresets)
        {
            preset.PropertyChanged += OnScalePresetPropertyChanged;
        }
        ScalePresets.CollectionChanged += OnScalePresetsChanged;
    }

    private void ResetFileFormats(IEnumerable<FileFormatModel> formats)
    {
        FileFormats.CollectionChanged -= OnFileFormatsChanged;
        foreach (var item in FileFormats)
        {
            item.PropertyChanged -= OnFileFormatItemChanged;
        }

        FileFormats.Clear();
        foreach (var format in formats)
        {
            FileFormats.Add(new FileFormatItem(format.DisplayName, format.Extensions.ToArray(), format.IsEnabled));
        }

        FileFormats.CollectionChanged += OnFileFormatsChanged;
        foreach (var item in FileFormats)
        {
            item.PropertyChanged += OnFileFormatItemChanged;
        }
    }

    private void ResetExifDisplayItems(IEnumerable<ExifDisplayModel> items)
    {
        ExifDisplayItems.CollectionChanged -= OnExifDisplayItemsChanged;
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged -= OnExifDisplayItemChanged;
        }

        ExifDisplayItems.Clear();
        foreach (var item in items)
        {
            ExifDisplayItems.Add(new ExifDisplayItem(item.DisplayName, item.PropertyName, item.IsEnabled));
        }

        ExifDisplayItems.CollectionChanged += OnExifDisplayItemsChanged;
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged += OnExifDisplayItemChanged;
        }
    }

    private void ResetHotkeys(IEnumerable<HotkeyModel> hotkeys)
    {
        Hotkeys.CollectionChanged -= OnHotkeysChanged;
        foreach (var item in Hotkeys)
        {
            item.PropertyChanged -= OnHotkeyItemChanged;
        }

        Hotkeys.Clear();
        foreach (var hotkey in hotkeys)
        {
            Hotkeys.Add(new HotkeyItem(
                hotkey.Name,
                hotkey.Command,
                hotkey.DisplaySymbol,
                hotkey.Tooltip,
                hotkey.IsDisplay,
                FromGestureModel(hotkey.Primary),
                FromGestureModel(hotkey.Secondary)));
        }

        Hotkeys.CollectionChanged += OnHotkeysChanged;
        foreach (var item in Hotkeys)
        {
            item.PropertyChanged += OnHotkeyItemChanged;
        }
    }

    private static GestureModel? ToGestureModel(AppGesture? gesture)
    {
        if (gesture == null) return null;
        if (gesture.Key != null)
        {
            return new GestureModel
            {
                Kind = GestureKind.Key,
                Key = gesture.Key.Key.ToString(),
                Modifiers = gesture.Key.KeyModifiers
            };
        }
        if (gesture.Mouse != null)
        {
            return new GestureModel
            {
                Kind = GestureKind.Mouse,
                MouseAction = gesture.Mouse.Action,
                Modifiers = gesture.Mouse.Modifiers
            };
        }
        return null;
    }

    private static AppGesture? FromGestureModel(GestureModel? model)
    {
        if (model == null) return null;

        switch (model.Kind)
        {
            case GestureKind.Key:
                if (!string.IsNullOrWhiteSpace(model.Key) && Enum.TryParse<Key>(model.Key, true, out var key))
                {
                    return AppGesture.FromKey(new KeyGesture(key, model.Modifiers));
                }
                break;
            case GestureKind.Mouse:
                if (model.MouseAction.HasValue)
                {
                    return AppGesture.FromMouse(new MouseGestureEx(model.MouseAction.Value, model.Modifiers));
                }
                break;
        }

        return null;
    }

    private void OnScalePresetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ScalePreset preset in e.NewItems)
            {
                preset.PropertyChanged += OnScalePresetPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (ScalePreset preset in e.OldItems)
            {
                preset.PropertyChanged -= OnScalePresetPropertyChanged;
            }
        }

        RequestSave();
    }

    private void OnScalePresetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScalePreset.Value) || e.PropertyName == nameof(ScalePreset.Text))
        {
            RequestSave();
        }
    }
}
