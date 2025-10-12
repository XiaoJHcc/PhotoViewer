using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using PhotoViewer.Core;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ControlView : UserControl
{
    public ControlView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 监听全局键盘事件
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            topLevel.KeyDown += OnGlobalKeyDown;
        }
    }

    private void OnControlButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && 
            button.DataContext is SettingsViewModel.HotkeyItem hotkeyItem &&
            DataContext is ControlViewModel controlViewModel)
        {
            var command = controlViewModel.GetCommandByName(hotkeyItem.Command);
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ControlViewModel viewModel) return;

        // 新增：星级快捷键 (` -> 0, 主键盘1-5 -> 1-5，小键盘0-5 -> 0-5)
        if (e.KeyModifiers == KeyModifiers.None && viewModel.ShowRating)
        {
            int? rating = null;
            switch (e.Key)
            {
                case Key.OemTilde: rating = 0; break;      // ` 键
                case Key.D1: rating = 1; break;
                case Key.D2: rating = 2; break;
                case Key.D3: rating = 3; break;
                case Key.D4: rating = 4; break;
                case Key.D5: rating = 5; break;
                case Key.NumPad0: rating = 0; break;
                case Key.NumPad1: rating = 1; break;
                case Key.NumPad2: rating = 2; break;
                case Key.NumPad3: rating = 3; break;
                case Key.NumPad4: rating = 4; break;
                case Key.NumPad5: rating = 5; break;
            }

            if (rating.HasValue)
            {
                viewModel.SetRating(rating.Value);
                e.Handled = true;
                return;
            }
        }

        // 基于 PhysicalKey 标准化当前按键，区分主键盘 +/- 与小键盘 +/-，修复 iOS/macOS 键位混淆
        var normalizedKey = NormalizeKey(e);
        var mods = AppleKeyboardMapping.ApplyForRuntime(e.KeyModifiers);

        // 仅匹配键盘类手势（鼠标手势在 ImageView 内处理）
        var matchedHotkey = viewModel.AllHotkeys?.FirstOrDefault(h =>
            AreKeyGestureMatch(h.PrimaryHotkey, normalizedKey, mods) ||
            AreKeyGestureMatch(h.SecondaryHotkey, normalizedKey, mods));

        if (matchedHotkey != null)
        {
            var command = viewModel.GetCommandByName(matchedHotkey.Command);
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnRatingButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is string ratingStr &&
            int.TryParse(ratingStr, out int rating) &&
            DataContext is ControlViewModel controlViewModel)
        {
            controlViewModel.SetRating(rating);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 移除全局键盘事件监听
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            topLevel.KeyDown -= OnGlobalKeyDown;
        }
        base.OnDetachedFromVisualTree(e);
    }

    // 使用 PhysicalKey 将当前事件标准化为正确的逻辑键
    private static Key NormalizeKey(KeyEventArgs e)
    {
        switch (e.PhysicalKey)
        {
            case PhysicalKey.Equal:
                return Key.OemPlus;           // "=" 键位（Shift 为 "+")
            case PhysicalKey.Minus:
                return Key.OemMinus;          // "-" 键位
            case PhysicalKey.NumPadAdd:
                return OperatingSystem.IsIOS() ? Key.OemPlus : Key.Add;      // iOS 并入 OemPlus
            case PhysicalKey.NumPadSubtract:
                return OperatingSystem.IsIOS() ? Key.OemMinus : Key.Subtract; // iOS 并入 OemMinus
        }

        if (OperatingSystem.IsIOS())
        {
            if (e.Key == Key.Add) return Key.OemPlus;
            if (e.Key == Key.Subtract) return Key.OemMinus;
        }

        return e.Key;
    }

    // iOS 下将 Add/Subtract 视为 OemPlus/OemMinus 用于比较
    private static Key NormalizeKeyForCompare(Key key)
    {
        if (OperatingSystem.IsIOS())
        {
            if (key == Key.Add) return Key.OemPlus;
            if (key == Key.Subtract) return Key.OemMinus;
        }
        return key;
    }

    // 匹配逻辑：先比对修饰键，再比对键值；iOS 下做等价合并
    private static bool AreGestureMatch(KeyGesture? stored, Key normalizedKey, KeyModifiers mods)
    {
        if (stored == null) return false;
        if (stored.KeyModifiers != mods) return false;

        var left = NormalizeKeyForCompare(stored.Key);
        var right = NormalizeKeyForCompare(normalizedKey);
        return left == right;
    }

    // 键盘匹配：仅当存储为键盘手势时才参与
    private static bool AreKeyGestureMatch(AppGesture? stored, Key normalizedKey, KeyModifiers mods)
    {
        if (stored?.Key == null) return false;
        if (stored.Key.KeyModifiers != mods) return false;

        var left = NormalizeKeyForCompare(stored.Key.Key);
        var right = NormalizeKeyForCompare(normalizedKey);
        return left == right;
    }
}

// 星级颜色转换器
public class StarColorConverter : IValueConverter
{
    public static readonly StarColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int currentRating && 
            parameter is string starIndexStr && 
            int.TryParse(starIndexStr, out int starIndex))
        {
            return currentRating >= starIndex ? 
                new SolidColorBrush(Colors.Gold) : 
                new SolidColorBrush(Colors.Gray);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// EXIF 值转换器
public class ExifValueConverter : IMultiValueConverter
{
    public static readonly ExifValueConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count != 2 || values[0] is not ExifData exifData || values[1] is not string propertyName)
            return "--";

        try
        {
            return propertyName switch
            {
                "Aperture" => exifData.Aperture != null ? 
                    Converters.ApertureConverter.Instance.Convert(exifData.Aperture, targetType, parameter, culture) : "--",
                "ExposureTime" => exifData.ExposureTime != null ? 
                    Converters.ExposureTimeConverter.Instance.Convert(exifData.ExposureTime, targetType, parameter, culture) : "--",
                "Iso" => exifData.Iso?.ToString() ?? "--",
                "EquivFocalLength" => exifData.EquivFocalLength != null ? 
                    Converters.FocalLengthConverter.Instance.Convert(exifData.EquivFocalLength, targetType, parameter, culture) : "--",
                "FocalLength" => exifData.FocalLength != null ? 
                    Converters.FocalLengthConverter.Instance.Convert(exifData.FocalLength, targetType, parameter, culture) : "--",
                "CameraMake" => exifData.CameraMake ?? "--",
                "CameraModel" => exifData.CameraModel ?? "--",
                "LensModel" => exifData.LensModel ?? "--",
                "DateTimeOriginal" => exifData.DateTimeOriginal != null ? 
                    Converters.DateTimeConverter.Instance.Convert(exifData.DateTimeOriginal, targetType, parameter, culture) : "--",
                "ExposureBias" => exifData.ExposureBias != null ? 
                    Converters.ExposureBiasConverter.Instance.Convert(exifData.ExposureBias, targetType, parameter, culture) : "--",
                "WhiteBalance" => exifData.WhiteBalance ?? "--",
                "Flash" => exifData.Flash ?? "--",
                _ => "--"
            };
        }
        catch
        {
            return "--";
        }
    }
}