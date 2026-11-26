using System.ComponentModel;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Tools;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生 EXIF 详情页（UIKit 版）：直接继承 <see cref="UITableViewController"/>（<see cref="UITableViewStyle.InsetGrouped"/>），
/// 顶部展示不含扩展名的文件名与原生 <see cref="UISegmentedControl"/> 格式切换（单格式只显示非交互扩展名），
/// 下方以原生分组标题 + AutoLayout 自适应键值行展示全部元数据（中文名 / 英文名 / Tag ID / 值），
/// 并通过系统 <see cref="UIContextMenuConfiguration"/> 提供长按拷贝值、名称与整行。
/// </summary>
internal sealed class iOSExifDetailView : UITableViewController
{
    private const string CellReuseId = "ExifTagCell";
    private const string HeaderReuseId = "ExifGroupHeader";

    private readonly Action? _onClose;
    private HeaderView? _header;
    private ExifDetailViewModel? _viewModel;
    private double _lastHeaderWidth = -1;
    private bool _disposed;

    /// <summary>原生表格（验收读取分区/行数与渲染文本）。</summary>
    internal UITableView Table => TableView;

    /// <summary>当前绑定的 EXIF 详情 VM。</summary>
    internal ExifDetailViewModel? ViewModel => _viewModel;

    /// <summary>当前分组数（未加载为 0）。</summary>
    internal int GroupCount => _viewModel?.Groups?.Count ?? 0;

    /// <summary>当前标签行数（未加载为 0）。</summary>
    internal int TagCount => _viewModel?.Groups?.Sum(group => group.Tags.Count) ?? 0;

    /// <summary>多格式时的原生分段选择器；单格式时为 null 或隐藏。</summary>
    internal UISegmentedControl? FormatControl => _header?.FormatControl;

    /// <summary>文件名标签在表格坐标系中的当前 frame。</summary>
    internal CGRect FileNameFrame => _header?.FileNameFrame ?? CGRect.Empty;

    public iOSExifDetailView(Action? onClose = null) : base(UITableViewStyle.InsetGrouped)
    {
        _onClose = onClose;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = "EXIF 详情";
        if (_onClose != null)
        {
            NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => _onClose());
        }

        TableView.BackgroundColor = UIColor.SystemGroupedBackground;
        TableView.CellLayoutMarginsFollowReadableWidth = false;
        TableView.AllowsSelection = false;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 56;
        TableView.SectionHeaderHeight = UITableView.AutomaticDimension;
        TableView.EstimatedSectionHeaderHeight = 36;
        TableView.RegisterClassForCellReuse(typeof(ExifTagCell), CellReuseId);
        TableView.RegisterClassForHeaderFooterViewReuse(typeof(UITableViewHeaderFooterView), HeaderReuseId);

        _header = new HeaderView(SelectFormat);
        if (_viewModel != null)
        {
            _header.Show(_viewModel);
        }
        TableView.TableHeaderView = _header;
        UpdateHeaderLayout(force: true);
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        UpdateHeaderLayout(force: false);
    }

    /// <summary>绑定 EXIF 详情 VM 并按当前状态重建内容；VM 实例变化时切换订阅。</summary>
    internal void Show(ExifDetailViewModel viewModel)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
        if (IsViewLoaded)
        {
            _header?.Show(_viewModel);
            UpdateHeaderLayout(force: true);
            TableView.ReloadData();
        }
    }

    public override nint NumberOfSections(UITableView tableView) => GroupCount;

    public override nint RowsInSection(UITableView tableView, nint section)
        => _viewModel?.Groups is { } groups && section >= 0 && section < groups.Count
            ? groups[(int)section].Tags.Count
            : 0;

    public override UIView GetViewForHeader(UITableView tableView, nint section)
    {
        var header = tableView.DequeueReusableHeaderFooterView(HeaderReuseId)
            ?? new UITableViewHeaderFooterView((NSString)HeaderReuseId);
        if (_viewModel?.Groups is { } groups && section >= 0 && section < groups.Count)
        {
            var config = UIListContentConfiguration.ProminentInsetGroupedHeaderConfiguration;
            config.Text = groups[(int)section].Name;
            header.ContentConfiguration = config;
        }
        return header;
    }

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = (ExifTagCell)tableView.DequeueReusableCell(CellReuseId, indexPath);
        var tag = _viewModel!.Groups![(int)indexPath.Section].Tags[(int)indexPath.Row];
        cell.Configure(tag);
        return cell;
    }

    /// <summary>TOOLS-03：通过系统长按上下文菜单提供原生拷贝值 / 名称 / 整行。</summary>
    public override UIContextMenuConfiguration? GetContextMenuConfiguration(
        UITableView tableView, NSIndexPath indexPath, CGPoint point)
    {
        if (TryGetTag(indexPath) is not { } tag) return null;
        return UIContextMenuConfiguration.Create(
            identifier: null,
            previewProvider: null,
            actionProvider: _ => BuildCopyMenu(tag));
    }

    /// <summary>验收缝：按行索引构造与长按相同的原生拷贝菜单。</summary>
    internal UIMenu? BuildCopyMenuForChecks(NSIndexPath indexPath)
        => TryGetTag(indexPath) is { } tag ? BuildCopyMenu(tag) : null;

    internal static UIMenu BuildCopyMenu(MetadataTag tag)
    {
        var copyValue = UIAction.Create("拷贝值", UIImage.GetSystemImage("doc.on.doc"), null, _ =>
            UIPasteboard.General.String = tag.Value);
        var copyName = UIAction.Create("拷贝名称", UIImage.GetSystemImage("tag"), null, _ =>
            UIPasteboard.General.String = tag.Name);
        var copyLine = UIAction.Create("拷贝整行", UIImage.GetSystemImage("list.bullet.rectangle"), null, _ =>
        {
            string label = string.IsNullOrWhiteSpace(tag.ChineseName)
                ? $"{tag.TagIdHex} {tag.Name}"
                : $"{tag.TagIdHex} {tag.Name} ({tag.ChineseName})";
            UIPasteboard.General.String = $"{label}: {tag.Value}";
        });
        return UIMenu.Create(tag.Name, [copyValue, copyName, copyLine]);
    }

    private MetadataTag? TryGetTag(NSIndexPath indexPath)
    {
        if (_viewModel?.Groups is not { } groups) return null;
        int section = (int)indexPath.Section;
        int row = (int)indexPath.Row;
        if (section < 0 || section >= groups.Count) return null;
        var tags = groups[section].Tags;
        return row >= 0 && row < tags.Count ? tags[row] : null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
        UiDispatcher.Post(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _viewModel) || !IsViewLoaded) return;
            _header?.Show(_viewModel);
            UpdateHeaderLayout(force: true);
            TableView.ReloadData();
        });
    }

    private void SelectFormat(int index)
    {
        if (_viewModel == null || index < 0 || index >= _viewModel.Formats.Count) return;
        _viewModel.SelectedFormat = _viewModel.Formats[index];
        _header?.Show(_viewModel);
        UpdateHeaderLayout(force: true);
        TableView.ReloadData();
    }

    private void UpdateHeaderLayout(bool force)
    {
        if (_header == null) return;
        double width = (double)TableView.Bounds.Width;
        if (width <= 1) return;
        if (!force && Math.Abs(width - _lastHeaderWidth) < .5) return;
        _lastHeaderWidth = width;

        double left = (double)TableView.LayoutMargins.Left;
        double right = (double)TableView.LayoutMargins.Right;
        if (left + right <= 0) left = right = 16;
        _header.SetHorizontalMargins(left, right);

        var target = new CGSize((nfloat)width, UIView.UILayoutFittingCompressedSize.Height);
        var size = _header.SystemLayoutSizeFittingSize(
            target,
            (float)UILayoutPriority.Required,
            (float)UILayoutPriority.FittingSizeLevel);
        double height = Math.Max(1, Math.Ceiling((double)size.Height));
        if (Math.Abs((double)_header.Frame.Width - width) > .5
            || Math.Abs((double)_header.Frame.Height - height) > .5)
        {
            _header.Frame = new CGRect(0, 0, (nfloat)width, (nfloat)height);
            TableView.TableHeaderView = _header;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel = null;
            }
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 原生 EXIF 元数据行：保留系统 <see cref="UITableViewStyle.InsetGrouped"/> 默认背景、圆角与分隔线，
    /// 左侧以动态字体呈现中文名（缺失时英文名）与「英文名 · Tag ID」，右侧对齐呈现最多 3 行的值。
    /// </summary>
    [Register("PViOSExifTagCell")]
    internal sealed class ExifTagCell : UITableViewCell
    {
        private readonly UILabel _titleLabel;
        private readonly UILabel _metaLabel;
        private readonly UILabel _valueLabel;

        internal UILabel TitleLabel => _titleLabel;
        internal UILabel MetaLabel => _metaLabel;
        internal UILabel ValueLabel => _valueLabel;

        [Export("initWithStyle:reuseIdentifier:")]
        public ExifTagCell(UITableViewCellStyle style, string reuseIdentifier) : base(style, reuseIdentifier)
        {
            SelectionStyle = UITableViewCellSelectionStyle.None;
            ContentView.BackgroundColor = UIColor.Clear;

            _titleLabel = new UILabel
            {
                Font = UIFont.PreferredSubheadline!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.Label,
                Lines = 0,
                LineBreakMode = UILineBreakMode.WordWrap,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            var metaFont = UIFontMetrics.DefaultMetrics.GetScaledFont(
                UIFont.MonospacedDigitSystemFontOfSize(11, UIFontWeight.Regular)!);
            _metaLabel = new UILabel
            {
                Font = metaFont,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.SecondaryLabel,
                Lines = 0,
                LineBreakMode = UILineBreakMode.WordWrap,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            _valueLabel = new UILabel
            {
                Font = UIFont.PreferredSubheadline!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.Label,
                TextAlignment = UITextAlignment.Right,
                Lines = ExifTableGeometry.ValueMaxLines,
                LineBreakMode = UILineBreakMode.TailTruncation,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            var keyStack = new UIStackView([_titleLabel, _metaLabel])
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            keyStack.SetContentCompressionResistancePriority((float)UILayoutPriority.DefaultHigh, UILayoutConstraintAxis.Horizontal);
            _valueLabel.SetContentHuggingPriority((float)UILayoutPriority.DefaultHigh, UILayoutConstraintAxis.Horizontal);
            _valueLabel.SetContentCompressionResistancePriority((float)UILayoutPriority.DefaultHigh + 1, UILayoutConstraintAxis.Horizontal);

            ContentView.AddSubview(keyStack);
            ContentView.AddSubview(_valueLabel);

            var minHeight = ContentView.HeightAnchor.ConstraintGreaterThanOrEqualTo(44);
            minHeight.Priority = 999;

            var maxValueWidth = _valueLabel.WidthAnchor.ConstraintLessThanOrEqualTo(
                ContentView.LayoutMarginsGuide.WidthAnchor, (nfloat)0.56);
            maxValueWidth.Priority = 999;

            var maxKeyWidth = keyStack.WidthAnchor.ConstraintLessThanOrEqualTo(
                ContentView.LayoutMarginsGuide.WidthAnchor, (nfloat)0.58);
            maxKeyWidth.Priority = 998;

            NSLayoutConstraint.ActivateConstraints(
            [
                minHeight,
                maxValueWidth,
                maxKeyWidth,

                keyStack.LeadingAnchor.ConstraintEqualTo(ContentView.LayoutMarginsGuide.LeadingAnchor),
                keyStack.TopAnchor.ConstraintGreaterThanOrEqualTo(ContentView.TopAnchor, 10),
                keyStack.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -10),
                keyStack.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),

                _valueLabel.LeadingAnchor.ConstraintGreaterThanOrEqualTo(keyStack.TrailingAnchor, 12),
                _valueLabel.TrailingAnchor.ConstraintEqualTo(ContentView.LayoutMarginsGuide.TrailingAnchor),
                _valueLabel.TopAnchor.ConstraintGreaterThanOrEqualTo(ContentView.TopAnchor, 10),
                _valueLabel.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -10),
                _valueLabel.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            ]);
        }

        internal void Configure(MetadataTag tag)
        {
            bool hasChinese = !string.IsNullOrWhiteSpace(tag.ChineseName);
            _titleLabel.Text = hasChinese ? tag.ChineseName : tag.Name;
            _metaLabel.Text = hasChinese ? $"{tag.Name} · {tag.TagIdHex}" : tag.TagIdHex;
            _valueLabel.Text = tag.Value;
            AccessibilityLabel = hasChinese ? $"{tag.ChineseName}，{tag.Name}" : tag.Name;
            AccessibilityValue = tag.Value;
            AccessibilityHint = "长按可拷贝标签值或名称";
        }
    }

    /// <summary>
    /// 顶部文件名与原生格式切换头：单格式仅显示扩展名文字，多格式使用原生 <see cref="UISegmentedControl"/>，
    /// 加载中展示系统 <see cref="UIActivityIndicatorView"/> 与提示文本。
    /// </summary>
    private sealed class HeaderView : UIView
    {
        private readonly Action<int> _onSelectFormat;
        private readonly UILabel _fileName;
        private readonly UILabel _singleFormat;
        private readonly UISegmentedControl _segmented;
        private readonly UIStackView _loadingRow;
        private readonly UIActivityIndicatorView _spinner;
        private readonly NSLayoutConstraint _leading;
        private readonly NSLayoutConstraint _trailing;
        private string[] _formatLabels = [];

        internal UISegmentedControl? FormatControl => _segmented.Hidden ? null : _segmented;
        internal CGRect FileNameFrame => _fileName.ConvertRectToView(_fileName.Bounds, this);

        internal HeaderView(Action<int> onSelectFormat)
        {
            _onSelectFormat = onSelectFormat;
            BackgroundColor = UIColor.Clear;

            _fileName = new UILabel
            {
                Font = UIFont.PreferredHeadline!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.Label,
                TextAlignment = UITextAlignment.Center,
                Lines = 1,
                LineBreakMode = UILineBreakMode.MiddleTruncation,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            _singleFormat = new UILabel
            {
                Font = UIFont.PreferredFootnote!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.SecondaryLabel,
                TextAlignment = UITextAlignment.Center,
                Lines = 1,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            _segmented = new UISegmentedControl
            {
                Hidden = true,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _segmented.ValueChanged += (_, _) => _onSelectFormat((int)_segmented.SelectedSegment);

            _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            var loadingLabel = new UILabel
            {
                Text = "加载中…",
                Font = UIFont.PreferredFootnote!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _loadingRow = new UIStackView([_spinner, loadingLabel])
            {
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Center,
                Spacing = 8,
                Hidden = true,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            var stack = new UIStackView([_fileName, _singleFormat, _segmented, _loadingRow])
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Center,
                Spacing = 8,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            AddSubview(stack);

            _leading = stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 16);
            _trailing = stack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -16);
            NSLayoutConstraint.ActivateConstraints(
            [
                stack.TopAnchor.ConstraintEqualTo(TopAnchor, 12),
                stack.BottomAnchor.ConstraintEqualTo(BottomAnchor, -8),
                _leading,
                _trailing,
                _fileName.WidthAnchor.ConstraintLessThanOrEqualTo(stack.WidthAnchor),
            ]);
        }

        internal void SetHorizontalMargins(double left, double right)
        {
            _leading.Constant = (nfloat)left;
            _trailing.Constant = (nfloat)(-right);
        }

        internal void Show(ExifDetailViewModel? viewModel)
        {
            _fileName.Text = viewModel?.BaseFileName ?? string.Empty;
            bool loading = viewModel?.IsLoading ?? false;
            _loadingRow.Hidden = !loading;
            if (loading) _spinner.StartAnimating();
            else _spinner.StopAnimating();

            if (viewModel == null)
            {
                _singleFormat.Hidden = true;
                _segmented.Hidden = true;
                return;
            }

            if (viewModel.HasMultipleFormats)
            {
                _singleFormat.Hidden = true;
                _segmented.Hidden = false;
                var labels = viewModel.Formats.Select(entry => entry.Label).ToArray();
                if (!_formatLabels.SequenceEqual(labels))
                {
                    _formatLabels = labels;
                    _segmented.RemoveAllSegments();
                    for (int index = 0; index < labels.Length; index++)
                    {
                        _segmented.InsertSegment(labels[index], (nint)index, false);
                    }
                }
                int selected = viewModel.Formats.ToList().FindIndex(entry => entry.IsSelected);
                if (selected >= 0 && _segmented.SelectedSegment != selected)
                {
                    _segmented.SelectedSegment = selected;
                }
            }
            else
            {
                _segmented.Hidden = true;
                _singleFormat.Hidden = false;
                _singleFormat.Text = viewModel.SelectedFormat.Label;
            }
        }
    }
}
