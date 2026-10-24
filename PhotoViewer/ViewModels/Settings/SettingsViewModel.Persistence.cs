using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Settings;

public partial class SettingsViewModel
{
    private static readonly HashSet<string> PersistedPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(LayoutMode),
        nameof(ShowZoomIndicator),
        nameof(ScalePresets),
        nameof(ShowRating),
        nameof(SafeSetRating),
        nameof(SameNameAsOnePhoto),
        nameof(FileFormats),
        nameof(ExifDisplayItems),
        nameof(Hotkeys),
        nameof(UseAppleKeyboard),
        nameof(MapCommandTarget),
        nameof(MapOptionTarget),
        nameof(MapControlTarget),
        nameof(SimilarityThreshold),
        nameof(SimilarityMaxResults),
        nameof(AnalysisImmediateCompute),
        nameof(BitmapCacheMaxCount),
        nameof(BitmapCacheMaxMemory),
        nameof(PreloadForwardCount),
        nameof(PreloadBackwardCount),
        nameof(VisibleCenterPreloadCount),
        nameof(VisibleCenterDelayMs),
        nameof(NativePreloadParallelism),
        nameof(CpuPreloadParallelism),
    };

    private string? _persistenceStatus;

    /// <summary>
    /// 设置页顶部的一行持久化状态：读取/恢复失败或保存失败时非空（SETTINGS-14）。
    /// draft 会话不显示源设置的持久化状态。
    /// </summary>
    public string? PersistenceStatus
    {
        get => _persistenceStatus;
        private set => this.RaiseAndSetIfChanged(ref _persistenceStatus, value);
    }

    public void RefreshPersistenceStatus()
    {
        if (_disposed) return;
        SetPersistenceStatus(BuildPersistenceStatus());
    }

    private string? BuildPersistenceStatus()
    {
        if (_session is not null || _repository is null) return null;

        var load = _repository.LastLoadError;
        var save = _repository.LastSaveError is { } error ? $"保存设置失败：{error}" : null;
        if (load is null) return save;
        if (save is null) return load;
        return $"{load}；{save}";
    }

    private void SetPersistenceStatus(string? status)
    {
        if (_disposed) return;

        if (UiDispatcher.CheckAccess())
        {
            PersistenceStatus = status;
            return;
        }

        UiDispatcher.Post(() =>
        {
            if (!_disposed) PersistenceStatus = status;
        });
    }

    private void RequestSave()
    {
        if (_isRestoring || !_initialized || _disposed) return;

        if (_session is not null)
        {
            _session.Draft = CreateSnapshot();
            return;
        }

        if (_repository is null) return;
        if (!_repositoryReady)
        {
            _hasLocalEdits = true;
            return;
        }

        QueueRepositoryCommit();
    }

    private void QueueRepositoryCommit()
    {
        if (_disposed || _repository is null || !_repositoryReady) return;

        var snapshot = CreateSnapshot();
        lock (_saveGate)
        {
            _pendingSnapshot = snapshot;
            _saveRequested = true;
            if (_saveLoopRunning) return;
            _saveLoopRunning = true;
        }

        _ = SaveLoopAsync();
    }

    private async System.Threading.Tasks.Task SaveLoopAsync()
    {
        var repository = _repository!;
        while (true)
        {
            AppSettings? snapshot;
            lock (_saveGate)
            {
                if (_disposed || !_saveRequested || _pendingSnapshot is null)
                {
                    _saveRequested = false;
                    _pendingSnapshot = null;
                    _saveLoopRunning = false;
                    return;
                }

                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
                _saveRequested = false;
            }

            try
            {
                await repository.CommitAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetPersistenceStatus($"保存设置失败：{ex.Message}");
            }

            RefreshPersistenceStatus();
        }
    }

    /// <summary>导出当前界面状态对应的不可变设置快照。</summary>
    public AppSettings CreateSnapshot()
    {
        return new AppSettings
        {
            LayoutMode = LayoutMode,
            ShowZoomIndicator = ShowZoomIndicator,
            ScalePresets = ScalePresets.Select(preset => preset.Value).ToImmutableArray(),
            ShowRating = ShowRating,
            SafeSetRating = SafeSetRating,
            SameNameAsOnePhoto = SameNameAsOnePhoto,
            FileFormats = FileFormats
                .Select(item => new FileFormatSetting(item.Id, item.IsEnabled))
                .ToImmutableArray(),
            ExifFields = ExifDisplayItems
                .Select(item => new ExifFieldSetting(item.PropertyName, item.IsEnabled))
                .ToImmutableArray(),
            Hotkeys = Hotkeys
                .Select(item => new HotkeySetting(
                    item.Command,
                    item.IsDisplay,
                    item.PrimaryHotkey,
                    item.SecondaryHotkey))
                .ToImmutableArray(),
            UseAppleKeyboard = UseAppleKeyboard,
            MapCommandTarget = MapCommandTarget,
            MapOptionTarget = MapOptionTarget,
            MapControlTarget = MapControlTarget,
            SimilarityThreshold = SimilarityThreshold,
            SimilarityMaxResults = SimilarityMaxResults,
            AnalysisImmediateCompute = AnalysisImmediateCompute,
            BitmapCacheMaxCount = BitmapCacheMaxCount,
            BitmapCacheMaxMemory = BitmapCacheMaxMemory,
            PreloadForwardCount = PreloadForwardCount,
            PreloadBackwardCount = PreloadBackwardCount,
            VisibleCenterPreloadCount = VisibleCenterPreloadCount,
            VisibleCenterDelayMs = VisibleCenterDelayMs,
            NativePreloadParallelism = NativePreloadParallelism,
            CpuPreloadParallelism = CpuPreloadParallelism,
        };
    }

    /// <summary>用快照显式回填所有界面字段（0/false 不当作缺失）；不触发保存。</summary>
    public void ApplySnapshot(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _isRestoring = true;
        try
        {
            LayoutMode = settings.LayoutMode;
            ShowZoomIndicator = settings.ShowZoomIndicator;
            ResetScalePresets(
                settings.ScalePresets.IsDefaultOrEmpty
                    ? SettingsCatalog.DefaultScalePresets
                    : settings.ScalePresets);
            ShowRating = settings.ShowRating;
            SafeSetRating = settings.SafeSetRating;
            SameNameAsOnePhoto = settings.SameNameAsOnePhoto;
            ResetFileFormats(settings.FileFormats.IsDefault
                ? SettingsCatalog.DefaultFileFormats
                : settings.FileFormats);
            ResetExifDisplayItems(settings.ExifFields.IsDefault
                ? SettingsCatalog.DefaultExifFields
                : settings.ExifFields);
            ResetHotkeys(settings.Hotkeys.IsDefault
                ? SettingsCatalog.DefaultHotkeys
                : settings.Hotkeys);
            UseAppleKeyboard = settings.UseAppleKeyboard;
            MapCommandTarget = settings.MapCommandTarget;
            MapOptionTarget = settings.MapOptionTarget;
            MapControlTarget = settings.MapControlTarget;
            BitmapCacheMaxCount = settings.BitmapCacheMaxCount;
            BitmapCacheMaxMemory = settings.BitmapCacheMaxMemory;
            PreloadForwardCount = settings.PreloadForwardCount;
            PreloadBackwardCount = settings.PreloadBackwardCount;
            VisibleCenterPreloadCount = settings.VisibleCenterPreloadCount;
            VisibleCenterDelayMs = settings.VisibleCenterDelayMs;
            NativePreloadParallelism = settings.NativePreloadParallelism;
            CpuPreloadParallelism = settings.CpuPreloadParallelism;
            SimilarityThreshold = settings.SimilarityThreshold;
            SimilarityMaxResults = settings.SimilarityMaxResults;
            AnalysisImmediateCompute = settings.AnalysisImmediateCompute;
            CheckHotkeyConflicts();
        }
        finally
        {
            _isRestoring = false;
        }

        UpdateSelectedFormats();
        UpdateEnabledExifItems();
        if (_session is not null)
            _session.Draft = CreateSnapshot();
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
            var percent = value * 100;
            if (!double.IsFinite(percent)) percent = 100;
            percent = Math.Clamp(percent, ScalePresetPercentMin, ScalePresetPercentMax);
            var text = percent.ToString("0.###", CultureInfo.InvariantCulture);
            ScalePresets.Add(new ScalePreset(text));
        }

        SortScalePreset();
        this.RaisePropertyChanged(nameof(CanRemoveScalePreset));
    }

    private void ResetFileFormats(IEnumerable<FileFormatSetting> formats)
    {
        FileFormats.CollectionChanged -= OnFileFormatsChanged;
        foreach (var item in FileFormats)
        {
            item.PropertyChanged -= OnFileFormatItemChanged;
        }

        FileFormats.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var format in formats)
        {
            if (format is null
                || string.IsNullOrWhiteSpace(format.Id)
                || !seen.Add(format.Id)
                || !SettingsCatalog.TryGetFileFormat(format.Id, out var definition))
            {
                continue;
            }

            FileFormats.Add(new FileFormatItem(
                definition.Id,
                definition.DisplayName,
                definition.Extensions.ToArray(),
                format.IsEnabled));
        }

        foreach (var definition in SettingsCatalog.FileFormats)
        {
            if (seen.Add(definition.Id))
            {
                FileFormats.Add(new FileFormatItem(
                    definition.Id,
                    definition.DisplayName,
                    definition.Extensions.ToArray(),
                    definition.IsEnabledByDefault));
            }
        }

        FileFormats.CollectionChanged += OnFileFormatsChanged;
        foreach (var item in FileFormats)
        {
            item.PropertyChanged += OnFileFormatItemChanged;
        }
    }

    private void ResetExifDisplayItems(IEnumerable<ExifFieldSetting> items)
    {
        ExifDisplayItems.CollectionChanged -= OnExifDisplayItemsChanged;
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged -= OnExifDisplayItemChanged;
        }

        ExifDisplayItems.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.Id)
                || !seen.Add(item.Id)
                || !SettingsCatalog.TryGetExifField(item.Id, out var definition))
            {
                continue;
            }

            ExifDisplayItems.Add(new ExifDisplayItem(
                definition.DisplayName,
                definition.Id,
                item.IsEnabled));
        }

        foreach (var definition in SettingsCatalog.ExifFields)
        {
            if (seen.Add(definition.Id))
            {
                ExifDisplayItems.Add(new ExifDisplayItem(
                    definition.DisplayName,
                    definition.Id,
                    definition.IsEnabledByDefault));
            }
        }

        ExifDisplayItems.CollectionChanged += OnExifDisplayItemsChanged;
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged += OnExifDisplayItemChanged;
        }
    }

    private void ResetHotkeys(IEnumerable<HotkeySetting> hotkeys)
    {
        Hotkeys.CollectionChanged -= OnHotkeysChanged;
        foreach (var item in Hotkeys)
        {
            item.PropertyChanged -= OnHotkeyItemChanged;
        }

        Hotkeys.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hotkey in hotkeys)
        {
            if (hotkey is null
                || string.IsNullOrWhiteSpace(hotkey.CommandId)
                || !seen.Add(hotkey.CommandId)
                || !SettingsCatalog.TryGetCommand(hotkey.CommandId, out var definition))
            {
                continue;
            }

            Hotkeys.Add(new HotkeyItem(
                definition.Name,
                definition.Id,
                definition.Icon,
                definition.Tooltip,
                hotkey.IsDisplay,
                hotkey.PrimaryGesture,
                hotkey.SecondaryGesture));
        }

        foreach (var definition in SettingsCatalog.Commands)
        {
            if (seen.Add(definition.Id))
            {
                Hotkeys.Add(new HotkeyItem(
                    definition.Name,
                    definition.Id,
                    definition.Icon,
                    definition.Tooltip,
                    definition.IsVisibleByDefault,
                    definition.PrimaryGesture,
                    definition.SecondaryGesture));
            }
        }

        Hotkeys.CollectionChanged += OnHotkeysChanged;
        foreach (var item in Hotkeys)
        {
            item.PropertyChanged += OnHotkeyItemChanged;
        }
    }

    private void OnScalePresetsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
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

        this.RaisePropertyChanged(nameof(CanRemoveScalePreset));
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
