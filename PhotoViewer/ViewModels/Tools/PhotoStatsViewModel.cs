using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Tools;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>
/// 照片数据统计工具 ViewModel：管理文件夹列表、文件筛选模式与导出进度。
/// </summary>
public class PhotoStatsViewModel : ReactiveObject
{
    /// <summary>当前选中的文件夹路径列表。</summary>
    public ObservableCollection<string> Folders { get; } = new();

    private string _patterns = "*.HIF,*.JPG";
    /// <summary>逗号分隔的文件筛选通配符，如 "*.HIF,*.JPG"。</summary>
    public string Patterns
    {
        get => _patterns;
        set => this.RaiseAndSetIfChanged(ref _patterns, value);
    }

    private bool _isRunning;
    /// <summary>是否正在执行扫描/导出任务。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(CanExport));
        }
    }

    private string _statusText = "请添加文件夹后点击「导出 CSV」。";
    /// <summary>显示给用户的状态/进度文本。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>当前是否可执行导出操作（未在运行中且至少有一个文件夹）。</summary>
    public bool CanExport => !_isRunning && Folders.Count > 0;

    private CancellationTokenSource? _cts;

    /// <summary>初始化照片数据统计工具 ViewModel，监听文件夹列表变化以更新 CanExport。</summary>
    public PhotoStatsViewModel()
    {
        Folders.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(CanExport));
    }

    /// <summary>
    /// 添加文件夹路径（已存在则忽略，路径比较不区分大小写）。
    /// </summary>
    /// <param name="path">文件夹的本地路径。</param>
    public void AddFolder(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            !Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(path);
        }
    }

    /// <summary>从列表中移除指定文件夹路径。</summary>
    public void RemoveFolder(string path) => Folders.Remove(path);

    /// <summary>
    /// 扫描所有选中文件夹并将 EXIF 统计写入 CSV 文件。
    /// </summary>
    /// <param name="outputPath">CSV 输出文件的完整路径。</param>
    public async Task ExportAsync(string outputPath)
    {
        if (!CanExport) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        try
        {
            StatusText = "扫描文件中…";
            var files = await Task.Run(
                () => PhotoStatsService.EnumerateFiles(Folders, Patterns).ToList(),
                token);

            if (files.Count == 0)
            {
                StatusText = "未找到匹配的文件。";
                return;
            }

            var progress = new Progress<(int current, int total)>(p =>
                StatusText = $"处理中… {p.current} / {p.total}");

            var rows = await Task.Run(() =>
            {
                var result = new List<PhotoStatsRow>(files.Count);
                for (int i = 0; i < files.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(PhotoStatsService.ReadStats(files[i]));
                    var idx = i + 1;
                    ((IProgress<(int, int)>)progress).Report((idx, files.Count));
                }
                return result;
            }, token);

            await Task.Run(() => PhotoStatsService.WriteCsv(rows, outputPath), token);
            StatusText = $"完成，已导出 {rows.Count} 条记录。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>取消正在进行的导出任务。</summary>
    public void CancelExport() => _cts?.Cancel();
}
