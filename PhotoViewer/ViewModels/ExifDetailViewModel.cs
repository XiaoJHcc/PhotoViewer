using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

/// <summary>
/// EXIF 详情中可切换的文件格式条目（如 ARW、JPG、HIF 等）
/// </summary>
public class FormatEntry : ReactiveObject
{
    /// <summary>格式标签，如 "ARW"、"JPG"</summary>
    public string Label { get; }

    /// <summary>对应的存储文件</summary>
    public IStorageFile File { get; internal set; }

    private List<MetadataGroup>? _groups;
    /// <summary>该格式的元数据分组列表；null 表示尚未加载</summary>
    public List<MetadataGroup>? Groups
    {
        get => _groups;
        internal set => this.RaiseAndSetIfChanged(ref _groups, value);
    }

    private bool _isLoading;
    /// <summary>是否正在加载 EXIF 数据</summary>
    public bool IsLoading
    {
        get => _isLoading;
        internal set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private bool _isSelected;
    /// <summary>是否当前选中（供格式选择 UI 绑定）</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <param name="label">格式标签（文件扩展名大写）</param>
    /// <param name="file">对应存储文件</param>
    /// <param name="preloaded">已预加载的 EXIF 数据（主文件可直接传入）</param>
    public FormatEntry(string label, IStorageFile file, ExifData? preloaded = null)
    {
        Label = label;
        File = file;
        _groups = preloaded?.AllMetadata;
    }
}

/// <summary>
/// EXIF 详情页 ViewModel，支持在主文件与同名伴侣文件（如 RAW）之间切换格式。
/// RAW 格式优先排在最前并默认选中；伴侣文件的 EXIF 按需加载。
/// </summary>
public class ExifDetailViewModel : ReactiveObject
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARW", "CR2", "CR3", "NEF", "NRW", "RAF", "ORF", "RW2", "PEF",
        "DNG", "SRW", "X3F", "3FR", "IIQ", "MOS", "ERF", "MEF", "MRW"
    };

    private List<FormatEntry> _formats = new();
    /// <summary>全部可切换的格式列表（RAW 优先）</summary>
    public List<FormatEntry> Formats
    {
        get => _formats;
        private set
        {
            this.RaiseAndSetIfChanged(ref _formats, value);
            this.RaisePropertyChanged(nameof(HasMultipleFormats));
        }
    }

    private string _baseFileName = "";
    /// <summary>不含后缀的文件名，显示在格式选择器左侧</summary>
    public string BaseFileName
    {
        get => _baseFileName;
        private set => this.RaiseAndSetIfChanged(ref _baseFileName, value);
    }

    /// <summary>是否有多个格式可切换</summary>
    public bool HasMultipleFormats => Formats.Count > 1;

    // 用于使异步加载任务作废的代数计数器
    private int _loadGeneration = 0;

    private FormatEntry _selectedFormat;
    /// <summary>当前选中的格式；设置时自动按需加载 EXIF</summary>
    public FormatEntry SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (value == null || _selectedFormat == value) return;
            _selectedFormat.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedFormat, value);
            value.IsSelected = true;
            this.RaisePropertyChanged(nameof(Groups));
            this.RaisePropertyChanged(nameof(IsLoading));
            if (value.Groups == null && !value.IsLoading)
                _ = LoadFormatAsync(value, _loadGeneration);
        }
    }

    /// <summary>当前格式的元数据分组列表（加载中时为 null）</summary>
    public List<MetadataGroup>? Groups => SelectedFormat.Groups;

    /// <summary>当前格式是否正在加载</summary>
    public bool IsLoading => SelectedFormat.IsLoading;

    /// <param name="imageFile">当前图片文件（其 ExifData 应已加载），及其 HiddenFiles 伴侣文件</param>
    public ExifDetailViewModel(ImageFile imageFile)
    {
        _baseFileName = Path.GetFileNameWithoutExtension(imageFile.Name);
        var all = BuildFormats(imageFile);
        _formats = all;

        // 默认选 RAW；无 RAW 时选第一个
        _selectedFormat = all.FirstOrDefault(f => RawExtensions.Contains(f.Label)) ?? all[0];
        _selectedFormat.IsSelected = true;

        // 若默认选中的格式未加载，立即触发加载
        if (_selectedFormat.Groups == null)
            _ = LoadFormatAsync(_selectedFormat, _loadGeneration);
    }

    /// <summary>
    /// 切换到新图片文件，保留当前选中的格式标签（若新图片包含该格式），
    /// 否则回退到 RAW 优先的默认格式。不替换 DataContext，因此不会重置滚动位置。
    /// </summary>
    /// <param name="imageFile">新的图片文件</param>
    public void UpdateToFile(ImageFile imageFile)
    {
        _loadGeneration++; // 令所有进行中的异步加载失效
        var gen = _loadGeneration;
        var previousLabel = _selectedFormat.Label;
        var newFormats = BuildFormats(imageFile);

        var newLabels = newFormats.Select(f => f.Label).ToList();
        var oldLabels = _formats.Select(f => f.Label).ToList();
        bool formatSetChanged = !newLabels.SequenceEqual(oldLabels);

        List<FormatEntry> effectiveFormats;
        if (!formatSetChanged)
        {
            // 格式标签集合不变：原地更新现有 FormatEntry 对象，避免 ListBox 重建导致选中闪烁
            for (int i = 0; i < _formats.Count; i++)
            {
                _formats[i].File = newFormats[i].File;     // 更新文件引用
                _formats[i].Groups = newFormats[i].Groups; // 主文件已预加载，伴侣文件置 null
                _formats[i].IsLoading = false;
            }
            effectiveFormats = _formats;
        }
        else
        {
            effectiveFormats = newFormats;
        }

        var newSelected = effectiveFormats.FirstOrDefault(f => f.Label == previousLabel)
            ?? effectiveFormats.FirstOrDefault(f => RawExtensions.Contains(f.Label))
            ?? effectiveFormats[0];

        // 直接更新字段，绕过 setter，避免触发额外的 Groups 通知
        if (_selectedFormat != newSelected)
        {
            _selectedFormat.IsSelected = false;
            _selectedFormat = newSelected;
            newSelected.IsSelected = true;
            this.RaisePropertyChanged(nameof(SelectedFormat));
        }

        BaseFileName = Path.GetFileNameWithoutExtension(imageFile.Name);

        if (formatSetChanged)
            Formats = newFormats;

        this.RaisePropertyChanged(nameof(Groups));
        this.RaisePropertyChanged(nameof(IsLoading));

        // 触发所有 Groups 为 null 的格式条目按需加载（常见于伴侣文件）
        foreach (var entry in effectiveFormats.Where(e => e.Groups == null && !e.IsLoading))
            _ = LoadFormatAsync(entry, gen);
    }

    private static List<FormatEntry> BuildFormats(ImageFile imageFile)
    {
        var primaryExt = GetExtension(imageFile.Name);
        var primary = new FormatEntry(primaryExt, imageFile.File, imageFile.ExifData);

        var companions = imageFile.HiddenFiles
            .Select(f => new FormatEntry(GetExtension(f.Name), f))
            .ToList();

        var all = new List<FormatEntry> { primary }.Concat(companions).ToList();

        // RAW 格式优先，同级按扩展名字母排序
        all.Sort((a, b) =>
        {
            var aRaw = RawExtensions.Contains(a.Label);
            var bRaw = RawExtensions.Contains(b.Label);
            if (aRaw != bRaw) return aRaw ? -1 : 1;
            return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });

        return all;
    }

    private async Task LoadFormatAsync(FormatEntry entry, int generation)
    {
        var file = entry.File; // 捕获当前文件引用，防止后续 UpdateToFile 修改后读取错误文件
        entry.IsLoading = true;
        if (_selectedFormat == entry)
            this.RaisePropertyChanged(nameof(IsLoading));

        try
        {
            var exifData = await Task.Run(async () =>
            {
                try { return await ExifLoader.LoadExifDataAsync(file); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ExifDetail] 加载失败 {entry.Label}: {ex.Message}");
                    return null;
                }
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_loadGeneration != generation) return; // 已过期，丢弃结果
                entry.Groups = exifData?.AllMetadata ?? new List<MetadataGroup>();
                entry.IsLoading = false;
                if (_selectedFormat == entry)
                {
                    this.RaisePropertyChanged(nameof(Groups));
                    this.RaisePropertyChanged(nameof(IsLoading));
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExifDetail] 异常 {entry.Label}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => { entry.IsLoading = false; });
        }
    }

    private static string GetExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(ext) ? fileName : ext;
    }
}

