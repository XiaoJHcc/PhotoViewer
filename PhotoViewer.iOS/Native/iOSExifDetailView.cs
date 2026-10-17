using System.ComponentModel;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Tools;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生 EXIF 详情页（UIKit 版）：顶部居中显示不含后缀的文件名与格式切换，下方按元数据分组渲染圆角卡片，
/// 每行 7 列（ID 60 点、1 点分隔线、名称/中文名/值按 2:2:3 分配，见共享 <see cref="ExifTableGeometry"/>）。
/// 长分组列表用 <see cref="UITableView"/> 承载以获得原生虚拟化；窄宽度下列仍保持 2:2:3，由文本换行与
/// 值列最多 3 行尾部截断自适应。数据全部来自共享 <see cref="ExifDetailViewModel"/>
/// （含惰性加载、异步补全与当前格式状态），本视图只读不写业务状态。
/// </summary>
internal sealed class iOSExifDetailView : UIViewController
{
    /// <summary>页面/卡片左右外边距（旧壳 StackPanel Margin 12 与卡片 Margin 12,4）。</summary>
    private const double SideMargin = 12;

    /// <summary>文件名行的上外边距（旧壳 Margin 12,12,12,4）。</summary>
    private const double HeaderTopMargin = 12;

    /// <summary>文件名行的下外边距。</summary>
    private const double HeaderBottomMargin = 4;

    /// <summary>加载提示的上下外边距（旧壳 Margin 12,12）。</summary>
    private const double LoadingMargin = 12;

    /// <summary>分组标题的上外边距（旧壳 Margin 12,18,12,6）。</summary>
    private const double GroupHeaderTopMargin = 18;

    /// <summary>分组标题的下外边距。</summary>
    private const double GroupHeaderBottomMargin = 6;

    /// <summary>卡片的上外边距（旧壳 Margin 12,4）。</summary>
    private const double CardTopMargin = 4;

    /// <summary>卡片的下外边距。</summary>
    private const double CardBottomMargin = 4;

    /// <summary>测量换行高度的宽度上限。</summary>
    private const double MeasureLimit = 10000;

    /// <summary>格式按钮左右内边距（旧壳 ListBoxItem 内容 Margin 8,4）。</summary>
    private const double FormatPaddingX = 8;

    /// <summary>格式按钮上下内边距。</summary>
    private const double FormatPaddingY = 4;

    /// <summary>格式按钮之间的间距（旧壳 StackPanel Spacing 4）。</summary>
    private const double FormatSpacing = 4;

    /// <summary>文件名与格式切换之间的间距（旧壳 StackPanel Spacing 8）。</summary>
    private const double HeaderSpacing = 8;

    /// <summary>分组标题颜色 #EEE。</summary>
    private static readonly UIColor GroupHeaderColor = UIColor.FromRGB(0xEE, 0xEE, 0xEE);

    /// <summary>卡片底色 #2a2a2a。</summary>
    private static readonly UIColor CardColor = UIColor.FromRGB(0x2A, 0x2A, 0x2A);

    /// <summary>Tag ID 单元格底色 #222。</summary>
    private static readonly UIColor IdCellColor = UIColor.FromRGB(0x22, 0x22, 0x22);

    /// <summary>分隔线颜色 #3a3a3a。</summary>
    private static readonly UIColor DividerColor = UIColor.FromRGB(0x3A, 0x3A, 0x3A);

    /// <summary>Tag ID 文字颜色 #555。</summary>
    private static readonly UIColor IdTextColor = UIColor.FromRGB(0x55, 0x55, 0x55);

    /// <summary>英文名称颜色 #AAA。</summary>
    private static readonly UIColor NameColor = UIColor.FromRGB(0xAA, 0xAA, 0xAA);

    /// <summary>中文译名颜色 #888。</summary>
    private static readonly UIColor ChineseColor = UIColor.FromRGB(0x88, 0x88, 0x88);

    /// <summary>值颜色 #FFF。</summary>
    private static readonly UIColor ValueColor = UIColor.FromRGB(0xFF, 0xFF, 0xFF);

    /// <summary>文件名颜色 #CCC。</summary>
    private static readonly UIColor FileNameColor = UIColor.FromRGB(0xCC, 0xCC, 0xCC);

    /// <summary>格式切换文字颜色 #AAA。</summary>
    private static readonly UIColor FormatTextColor = UIColor.FromRGB(0xAA, 0xAA, 0xAA);

    /// <summary>格式选中底色 #505050。</summary>
    private static readonly UIColor FormatSelectedColor = UIColor.FromRGB(0x50, 0x50, 0x50);

    /// <summary>加载提示颜色 #666。</summary>
    private static readonly UIColor LoadingColor = UIColor.FromRGB(0x66, 0x66, 0x66);

    /// <summary>文件名 14 点字体。</summary>
    private static readonly UIFont FileNameFont = UIFont.SystemFontOfSize(14);

    /// <summary>分组标题 16 点粗体。</summary>
    private static readonly UIFont GroupHeaderFont = UIFont.BoldSystemFontOfSize(16);

    /// <summary>单元格 13 点字体。</summary>
    private static readonly UIFont CellFont = UIFont.SystemFontOfSize(13);

    /// <summary>Tag ID 11 点等宽字体。</summary>
    private static readonly UIFont IdFont = UIFont.FromName("Menlo", 11) ?? UIFont.SystemFontOfSize(11);

    /// <summary>格式切换 13 点字体。</summary>
    private static readonly UIFont FormatFont = UIFont.SystemFontOfSize(13);

    private readonly UITableView _table;
    private readonly HeaderView _header;
    private readonly TableSource _source;
    private ExifDetailViewModel? _viewModel;
    private bool _disposed;

    /// <summary>原生表格（验收读取分区/行数与渲染文本）。</summary>
    internal UITableView Table => _table;

    /// <summary>当前绑定的 EXIF 详情 VM。</summary>
    internal ExifDetailViewModel? ViewModel => _viewModel;

    /// <summary>当前分组数（未加载为 0）。</summary>
    internal int GroupCount => _viewModel?.Groups?.Count ?? 0;

    /// <summary>当前标签行数（未加载为 0）。</summary>
    internal int TagCount => _viewModel?.Groups?.Sum(group => group.Tags.Count) ?? 0;

    /// <summary>多格式时的格式切换按钮（验收触发真实点击）。</summary>
    internal IReadOnlyList<UIButton> FormatButtons => _header.FormatButtons;

    /// <summary>建立表格、文件名/格式切换头与数据源。</summary>
    public iOSExifDetailView() : base(null, null)
    {
        Title = "EXIF 详情";
        View!.BackgroundColor = UIColor.FromRGB(0x12, 0x12, 0x12);
        _table = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
        {
            BackgroundColor = UIColor.FromRGB(0x12, 0x12, 0x12),
            SeparatorStyle = UITableViewCellSeparatorStyle.None,
            RowHeight = 0,
            EstimatedRowHeight = 0,
            SectionHeaderTopPadding = 0,
            ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never,
            AllowsSelection = false,
        };
        _table.RegisterClassForCellReuse(typeof(RowCell), TableSource.CellId);
        _header = new HeaderView(SelectFormat);
        _table.TableHeaderView = _header;
        _table.TableFooterView = new UIView(new CGRect(0, 0, 1, 24)) { BackgroundColor = UIColor.Clear };
        _source = new TableSource(this);
        _table.Source = _source;
        View.AddSubview(_table);
    }

    /// <summary>绑定 EXIF 详情 VM 并按当前状态重建内容；VM 实例变化时切换订阅。</summary>
    /// <param name="viewModel">共享 EXIF 详情 VM。</param>
    internal void Show(ExifDetailViewModel viewModel)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
        _header.Show(_viewModel);
        _table.ReloadData();
        LayoutHeader();
    }

    /// <summary>表格铺满页面并在尺寸变化后重排文件名头。</summary>
    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        _table.Frame = View!.Bounds;
        LayoutHeader();
    }

    /// <summary>VM 内容变化（格式切换、EXIF 异步到达、切换照片）后合并重载，跨线程通知统一回到主线程。</summary>
    /// <param name="sender">事件源（EXIF 详情 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
        UiDispatcher.Post(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
            _header.Show(_viewModel);
            _table.ReloadData();
            LayoutHeader();
        });
    }

    /// <summary>切换格式：写回共享 VM（触发惰性加载）后立即重载。</summary>
    /// <param name="entry">目标格式条目。</param>
    private void SelectFormat(FormatEntry entry)
    {
        if (_viewModel == null) return;
        _viewModel.SelectedFormat = entry;
        _header.Show(_viewModel);
        _table.ReloadData();
        LayoutHeader();
    }

    /// <summary>按当前宽度重新计算并挂上文件名/格式切换头（表格头不会自动布局）。</summary>
    private void LayoutHeader()
    {
        double width = Math.Max(1, (double)_table.Bounds.Width);
        double height = _header.RequiredHeight(width);
        if (Math.Abs((double)_header.Frame.Height - height) > .01 || Math.Abs((double)_header.Frame.Width - width) > .01)
            _header.Frame = new CGRect(0, 0, width, height);
        _table.TableHeaderView = _header;
        _header.SetNeedsLayout();
    }

    /// <summary>单条元数据的行高：名称/中文名/值在 2:2:3 列宽下换行后的最大高度 + 上下内边距。</summary>
    /// <param name="tag">元数据标签。</param>
    /// <param name="cardWidth">卡片宽度（表格宽 - 两侧外边距）。</param>
    /// <returns>行高。</returns>
    internal static double RowHeight(MetadataTag tag, double cardWidth)
    {
        if (cardWidth <= 0) return ExifTableGeometry.CellPaddingY * 2 + 18;
        var columns = ExifTableGeometry.Compute(cardWidth);
        double nameHeight = MeasureHeight(tag.Name, CellFont, columns.NameTextWidth, 0);
        double chineseHeight = MeasureHeight(tag.ChineseName ?? "", CellFont, columns.ChineseTextWidth, 0);
        double valueHeight = MeasureHeight(tag.Value, CellFont, columns.ValueTextWidth, ExifTableGeometry.ValueMaxLines);
        return Math.Max(nameHeight, Math.Max(chineseHeight, valueHeight)) + ExifTableGeometry.CellPaddingY * 2;
    }

    /// <summary>量出文本在给定宽度下换行后的高度；<paramref name="maxLines"/> 为 0 时不限行数。</summary>
    /// <param name="text">目标文本。</param>
    /// <param name="font">字体。</param>
    /// <param name="width">可用宽度。</param>
    /// <param name="maxLines">最多显示行数。</param>
    /// <returns>文本高度（点）。</returns>
    private static double MeasureHeight(string text, UIFont font, double width, int maxLines)
    {
        double lineHeight = Math.Ceiling((double)font.LineHeight);
        if (width <= 0 || string.IsNullOrEmpty(text)) return lineHeight;
        using var measure = new UILabel
        {
            Text = text,
            Font = font,
            Lines = 0,
            LineBreakMode = UILineBreakMode.WordWrap,
        };
        double height = (double)measure.SizeThatFits(new CGSize(width, MeasureLimit)).Height;
        if (maxLines > 0) height = Math.Min(height, lineHeight * maxLines);
        return Math.Max(1, Math.Ceiling(height));
    }

    /// <summary>释放订阅并拆除表格数据源。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
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
            _table.Source = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>表格数据源：每个元数据分组一个 section（分组标题 + 圆角卡片行）。</summary>
    private sealed class TableSource(iOSExifDetailView owner) : UITableViewSource
    {
        /// <summary>行复用标识。</summary>
        internal const string CellId = "ExifRow";

        /// <summary>分组数。</summary>
        /// <param name="tableView">表格。</param>
        /// <returns>分组数。</returns>
        public override nint NumberOfSections(UITableView tableView) => owner.GroupCount;

        /// <summary>分组内的标签行数。</summary>
        /// <param name="tableView">表格。</param>
        /// <param name="section">分组索引。</param>
        /// <returns>行数。</returns>
        public override nint RowsInSection(UITableView tableView, nint section)
            => owner._viewModel?.Groups is { } groups && section >= 0 && section < groups.Count ? groups[(int)section].Tags.Count : 0;

        /// <summary>复用行并按当前标签配置（首/末行决定卡片圆角）。</summary>
        /// <param name="tableView">表格。</param>
        /// <param name="indexPath">行索引。</param>
        /// <returns>已配置的单元格。</returns>
        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (RowCell)tableView.DequeueReusableCell(CellId, indexPath);
            var group = owner._viewModel!.Groups![(int)indexPath.Section];
            cell.Configure(group.Tags[(int)indexPath.Row], indexPath.Row == 0, indexPath.Row == group.Tags.Count - 1);
            return cell;
        }

        /// <summary>行高取共享 7 列几何下的换行测量值。</summary>
        /// <param name="tableView">表格。</param>
        /// <param name="indexPath">行索引。</param>
        /// <returns>行高。</returns>
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        {
            var group = owner._viewModel!.Groups![(int)indexPath.Section];
            double cardWidth = Math.Max(1, (double)tableView.Bounds.Width - SideMargin * 2);
            return (nfloat)(RowHeight(group.Tags[(int)indexPath.Row], cardWidth) + ExifTableGeometry.DividerThickness);
        }

        /// <summary>分组标题：左上 16 点粗体 #EEE，带旧壳的 18/6 上下外边距。</summary>
        /// <param name="tableView">表格。</param>
        /// <param name="section">分组索引。</param>
        /// <returns>分组标题视图。</returns>
        public override UIView GetViewForHeader(UITableView tableView, nint section)
        {
            var group = owner._viewModel!.Groups![(int)section];
            var host = new UIView { BackgroundColor = UIColor.Clear };
            var label = new UILabel(new CGRect(SideMargin, GroupHeaderTopMargin, Math.Max(1, (double)tableView.Bounds.Width - SideMargin * 2), GroupHeaderFont.LineHeight))
            {
                Text = group.Name,
                Font = GroupHeaderFont,
                TextColor = GroupHeaderColor,
                BackgroundColor = UIColor.Clear,
            };
            host.AddSubview(label);
            return host;
        }

        /// <summary>分组标题高度。</summary>
        /// <param name="tableView">表格。</param>
        /// <param name="section">分组索引。</param>
        /// <returns>高度。</returns>
        public override nfloat GetHeightForHeader(UITableView tableView, nint section)
            => (nfloat)(GroupHeaderTopMargin + Math.Ceiling((double)GroupHeaderFont.LineHeight) + GroupHeaderBottomMargin + CardTopMargin);
    }

    /// <summary>元数据行：卡片 #2a2a2a 圆角 6，7 列（ID / 名称 / 中文名 / 值）与 1 点分隔线。</summary>
    [Register("PViOSExifRowCell")]
    internal sealed class RowCell : UITableViewCell
    {
        private static readonly nfloat IdColumnWidth = (nfloat)ExifTableGeometry.IdColumnWidth;
        private static readonly nfloat DividerWidth = (nfloat)ExifTableGeometry.DividerThickness;

        private readonly UIView _card = new();
        private readonly UIView _idCell = new();
        private readonly UILabel _idText;
        private readonly UILabel _name;
        private readonly UILabel _chinese;
        private readonly UILabel _value;
        private readonly UIView[] _dividers = [new UIView(), new UIView(), new UIView()];
        private readonly UIView _rowDivider = new();

        /// <summary>按原生复用池创建行。</summary>
        /// <param name="style">单元格样式。</param>
        /// <param name="reuseIdentifier">复用标识。</param>
        [Export("initWithStyle:reuseIdentifier:")]
        public RowCell(UITableViewCellStyle style, string reuseIdentifier) : base(style, reuseIdentifier)
        {
            BackgroundColor = UIColor.Clear;
            ContentView.BackgroundColor = UIColor.Clear;
            _card.BackgroundColor = CardColor;
            _card.Layer.CornerRadius = 6;
            _card.ClipsToBounds = true;
            _idCell.BackgroundColor = IdCellColor;
            _idText = Label(IdFont, IdTextColor, UITextAlignment.Center, 1, UILineBreakMode.TailTruncation);
            _name = Label(CellFont, NameColor, UITextAlignment.Left, 0, UILineBreakMode.WordWrap);
            _chinese = Label(CellFont, ChineseColor, UITextAlignment.Left, 0, UILineBreakMode.WordWrap);
            _value = Label(CellFont, ValueColor, UITextAlignment.Left, ExifTableGeometry.ValueMaxLines, UILineBreakMode.TailTruncation);
            foreach (var divider in _dividers) divider.BackgroundColor = DividerColor;
            _rowDivider.BackgroundColor = DividerColor;
            _card.AddSubview(_idCell);
            _idCell.AddSubview(_idText);
            _card.AddSubview(_name);
            _card.AddSubview(_chinese);
            _card.AddSubview(_value);
            foreach (var divider in _dividers) _card.AddSubview(divider);
            _card.AddSubview(_rowDivider);
            ContentView.AddSubview(_card);
        }

        /// <summary>按标签内容与首/末行状态配置（首/末行给卡片加对应圆角）。</summary>
        /// <param name="tag">元数据标签。</param>
        /// <param name="first">分组首行。</param>
        /// <param name="last">分组末行。</param>
        internal void Configure(MetadataTag tag, bool first, bool last)
        {
            _idText.Text = tag.TagIdHex;
            _name.Text = tag.Name;
            _chinese.Text = tag.ChineseName ?? "";
            _value.Text = tag.Value;
            nfloat corner = 6;
            _card.Layer.CornerRadius = corner;
            _card.Layer.MaskedCorners = (first ? CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner : 0)
                | (last ? CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner : 0);
            _rowDivider.Hidden = last;
            SetNeedsLayout();
        }

        /// <summary>按共享 7 列几何摆放 ID 单元格、三条列分隔线、三列文本与行分隔线。</summary>
        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            double width = Math.Max(1, (double)ContentView.Bounds.Width);
            double cardWidth = Math.Max(1, width - SideMargin * 2);
            var card = new CGRect(SideMargin, 0, cardWidth, (double)ContentView.Bounds.Height);
            _card.Frame = card;
            var columns = ExifTableGeometry.Compute(cardWidth);
            double idHeight = Math.Min((double)_idText.Font.LineHeight, (double)card.Height);
            _idCell.Frame = new CGRect(0, 0, ExifTableGeometry.IdColumnWidth, (nfloat)card.Height);
            _idText.Frame = new CGRect(0, ((nfloat)card.Height - idHeight) / 2, IdColumnWidth, idHeight);

            double x = ExifTableGeometry.IdColumnWidth;
            _dividers[0].Frame = new CGRect(x, 0, DividerWidth, (nfloat)card.Height);
            x += ExifTableGeometry.DividerThickness;
            _name.Frame = new CGRect(x + ExifTableGeometry.CellPaddingX, ExifTableGeometry.CellPaddingY,
                columns.NameTextWidth, Math.Max(1, (double)card.Height - ExifTableGeometry.CellPaddingY * 2));
            x += columns.NameColumn;
            _dividers[1].Frame = new CGRect(x, 0, DividerWidth, (nfloat)card.Height);
            x += ExifTableGeometry.DividerThickness;
            _chinese.Frame = new CGRect(x + ExifTableGeometry.CellPaddingX, ExifTableGeometry.CellPaddingY,
                columns.ChineseTextWidth, Math.Max(1, (double)card.Height - ExifTableGeometry.CellPaddingY * 2));
            x += columns.ChineseColumn;
            _dividers[2].Frame = new CGRect(x, 0, DividerWidth, (nfloat)card.Height);
            x += ExifTableGeometry.DividerThickness;
            _value.Frame = new CGRect(x + ExifTableGeometry.CellPaddingX, ExifTableGeometry.CellPaddingY,
                columns.ValueTextWidth, Math.Max(1, (double)card.Height - ExifTableGeometry.CellPaddingY * 2));
            _rowDivider.Frame = new CGRect(0, (nfloat)card.Height - DividerWidth, (nfloat)cardWidth, DividerWidth);
        }

        /// <summary>只读文本标签；值列超过三行时尾部截断。</summary>
        /// <param name="font">字体。</param>
        /// <param name="color">颜色。</param>
        /// <param name="alignment">对齐方式。</param>
        /// <param name="lines">行数上限；0 表示不限。</param>
        /// <param name="mode">换行模式。</param>
        /// <returns>标签。</returns>
        private static UILabel Label(UIFont font, UIColor color, UITextAlignment alignment, int lines, UILineBreakMode mode) => new()
        {
            Font = font,
            TextColor = color,
            TextAlignment = alignment,
            Lines = lines,
            LineBreakMode = mode,
            BackgroundColor = UIColor.Clear,
        };
    }

    /// <summary>文件名 + 格式切换头：单格式显示文字标签，多格式显示扁平按钮（选中 #505050、圆角 6）。</summary>
    private sealed class HeaderView : UIView
    {
        private readonly UILabel _fileName;
        private readonly UILabel _formatLabel;
        private readonly UILabel _loading;
        private readonly Action<FormatEntry> _select;
        private readonly List<UIButton> _buttons = [];
        private bool _showLoading;

        /// <summary>构造三个常驻标签。</summary>
        /// <param name="select">格式切换回调。</param>
        public HeaderView(Action<FormatEntry> select)
        {
            _select = select;
            BackgroundColor = UIColor.Clear;
            _fileName = SingleLineLabel("", FileNameFont, FileNameColor);
            _formatLabel = SingleLineLabel("", FormatFont, ChineseColor);
            _loading = SingleLineLabel("加载中...", FormatFont, LoadingColor);
            _loading.TextAlignment = UITextAlignment.Center;
            AddSubview(_fileName);
            AddSubview(_formatLabel);
            AddSubview(_loading);
        }

        /// <summary>多格式时的格式切换按钮（验收触发真实点击）。</summary>
        internal IReadOnlyList<UIButton> FormatButtons => _buttons;

        /// <summary>绑定 VM 当前状态：文件名、格式切换按钮与加载提示。</summary>
        /// <param name="viewModel">共享 EXIF 详情 VM。</param>
        internal void Show(ExifDetailViewModel? viewModel)
        {
            foreach (var button in _buttons)
            {
                button.RemoveFromSuperview();
                button.Dispose();
            }
            _buttons.Clear();
            _fileName.Text = viewModel?.BaseFileName ?? "";
            _showLoading = viewModel?.IsLoading ?? false;
            _loading.Hidden = !_showLoading;
            _formatLabel.Hidden = true;
            if (viewModel == null) return;
            if (viewModel.HasMultipleFormats)
            {
                foreach (var entry in viewModel.Formats)
                {
                    var button = new UIButton(UIButtonType.Custom);
                    button.SetTitle(entry.Label, UIControlState.Normal);
                    button.SetTitleColor(FormatTextColor, UIControlState.Normal);
                    button.TitleLabel!.Font = FormatFont;
                    button.BackgroundColor = entry.IsSelected ? FormatSelectedColor : UIColor.Clear;
                    button.Layer.CornerRadius = 6;
                    var capture = entry;
                    button.TouchUpInside += (_, _) => _select(capture);
                    AddSubview(button);
                    _buttons.Add(button);
                }
            }
            else
            {
                _formatLabel.Hidden = false;
                _formatLabel.Text = viewModel.SelectedFormat.Label;
            }
        }

        /// <summary>给定宽度下头部需要的高度（文件名行 + 可选加载提示）。</summary>
        /// <param name="width">表格宽。</param>
        /// <returns>头部高度。</returns>
        internal double RequiredHeight(double width)
        {
            double lineHeight = Math.Ceiling((double)FileNameFont.LineHeight);
            double formatHeight = Math.Ceiling((double)FormatFont.LineHeight) + FormatPaddingY * 2;
            double height = HeaderTopMargin + Math.Max(lineHeight, formatHeight) + HeaderBottomMargin;
            if (_showLoading) height += LoadingMargin + Math.Ceiling((double)FormatFont.LineHeight) + LoadingMargin;
            return Math.Max(1, height);
        }

        /// <summary>文件名与格式切换整体按旧壳口径居中，加载提示占下一行。</summary>
        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            double width = Math.Max(1, (double)Bounds.Width);
            double lineHeight = Math.Ceiling((double)FileNameFont.LineHeight);
            double formatHeight = Math.Ceiling((double)FormatFont.LineHeight) + FormatPaddingY * 2;
            double headerHeight = Math.Max(lineHeight, formatHeight);
            double fileNameWidth = MeasureWidth(_fileName);
            double formatWidth = 0;
            if (_buttons.Count > 0)
            {
                for (int index = 0; index < _buttons.Count; index++)
                    formatWidth += MeasureWidth(_buttons[index]) + FormatPaddingX * 2 + (index > 0 ? FormatSpacing : 0);
            }
            else if (!_formatLabel.Hidden)
            {
                formatWidth = MeasureWidth(_formatLabel);
            }
            double spacing = formatWidth > 0 ? HeaderSpacing : 0;
            double groupWidth = Math.Min(fileNameWidth + spacing + formatWidth, Math.Max(1, width - SideMargin * 2));
            double nameWidth = Math.Max(1, groupWidth - spacing - formatWidth);
            double start = Math.Max(SideMargin, (width - groupWidth) / 2);
            _fileName.Frame = new CGRect(start, HeaderTopMargin + (headerHeight - lineHeight) / 2, nameWidth, lineHeight);
            double x = start + nameWidth + spacing;
            for (int index = 0; index < _buttons.Count; index++)
            {
                double size = MeasureWidth(_buttons[index]) + FormatPaddingX * 2;
                _buttons[index].Frame = new CGRect(x, HeaderTopMargin + (headerHeight - formatHeight) / 2, size, formatHeight);
                x += size + FormatSpacing;
            }
            if (_buttons.Count == 0)
            {
                double labelWidth = Math.Max(1, Math.Min(formatWidth, width - x - SideMargin));
                _formatLabel.Frame = new CGRect(x, HeaderTopMargin + (headerHeight - lineHeight) / 2, labelWidth, lineHeight);
            }
            double loadingHeight = Math.Ceiling((double)FormatFont.LineHeight);
            double loadingTop = HeaderTopMargin + headerHeight + HeaderBottomMargin;
            _loading.Frame = new CGRect(SideMargin, loadingTop + LoadingMargin, Math.Max(1, width - SideMargin * 2), loadingHeight);
        }

        /// <summary>单行文本的自然宽度。</summary>
        /// <param name="label">目标标签或按钮标题。</param>
        /// <returns>宽度（点）。</returns>
        private static double MeasureWidth(UILabel label)
            => Math.Ceiling((double)label.SizeThatFits(new CGSize(MeasureLimit, MeasureLimit)).Width);

        /// <summary>单行文本的自然宽度（按钮标题）。</summary>
        /// <param name="button">目标按钮。</param>
        /// <returns>宽度（点）。</returns>
        private static double MeasureWidth(UIButton button)
            => Math.Ceiling((double)button.TitleLabel!.SizeThatFits(new CGSize(MeasureLimit, MeasureLimit)).Width);

        /// <summary>创建单行标签。</summary>
        /// <param name="text">初始文本。</param>
        /// <param name="font">字体。</param>
        /// <param name="color">颜色。</param>
        /// <returns>标签。</returns>
        private static UILabel SingleLineLabel(string text, UIFont font, UIColor color) => new()
        {
            Text = text,
            Font = font,
            TextColor = color,
            Lines = 1,
            LineBreakMode = UILineBreakMode.TailTruncation,
            BackgroundColor = UIColor.Clear,
        };
    }
}
