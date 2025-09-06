using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Windows.Input;
using Avalonia.Input;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class ControlViewModel : ReactiveObject
{
    private readonly MainViewModel Main;

    public ControlViewModel(MainViewModel mainViewModel)
    {
        Main = mainViewModel;
        
        // 初始化命令
        OnPrevious = ReactiveCommand.Create(ExecutePrevious);
        OnNext = ReactiveCommand.Create(ExecuteNext);
        OnFit = ReactiveCommand.Create(ExecuteFit);
        
        // 监听设置变化
        Main.Settings.Hotkeys.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(EnabledControls));
        foreach (var hotkey in Main.Settings.Hotkeys)
        {
            hotkey.PropertyChanged += (s, e) => 
            {
                if (e.PropertyName == nameof(SettingsViewModel.HotkeyItem.IsEnabled))
                {
                    this.RaisePropertyChanged(nameof(EnabledControls));
                }
            };
        }
    }

    // 命令
    public ReactiveCommand<Unit, Unit> OnPrevious { get; }
    public ReactiveCommand<Unit, Unit> OnNext { get; }
    public ReactiveCommand<Unit, Unit> OnFit { get; }

    // 启用的控件列表
    public IEnumerable<SettingsViewModel.HotkeyItem> EnabledControls => 
        Main.Settings.Hotkeys.Where(h => h.IsEnabled);

    // 所有快捷键（用于全局监听）
    public IEnumerable<SettingsViewModel.HotkeyItem> AllHotkeys => Main.Settings.Hotkeys;

    // 根据命令名称获取命令
    public ICommand? GetCommandByName(string commandName)
    {
        return commandName switch
        {
            "Previous" => OnPrevious,
            "Next" => OnNext,
            "Fit" => OnFit,
            _ => null
        };
    }

    private void ExecutePrevious()
    {
        // 实现上一张逻辑
        if (Main.HasPreviousFile())
        {
            var currentIndex = Main.FilteredFiles.IndexOf(Main.CurrentFile);
            Main.CurrentFile = Main.FilteredFiles[currentIndex - 1];
        }
    }

    private void ExecuteNext()
    {
        // 实现下一张逻辑
        if (Main.HasNextFile())
        {
            var currentIndex = Main.FilteredFiles.IndexOf(Main.CurrentFile);
            Main.CurrentFile = Main.FilteredFiles[currentIndex + 1];
        }
    }

    private void ExecuteFit()
    {
        // 实现缩放适应逻辑
        Main.ImageViewModel.FitToScreen();
    }

    // 处理全局快捷键输入
    public bool HandleKeyInput(KeyGesture keyGesture)
    {
        var command = Main.Settings.GetCommandByHotkey(keyGesture);
        if (command != null)
        {
            var commandToExecute = GetCommandByName(command);
            if (commandToExecute?.CanExecute(null) == true)
            {
                commandToExecute.Execute(null);
                return true;
            }
        }
        
        return false;
    }
}