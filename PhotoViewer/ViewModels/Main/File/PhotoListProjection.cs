using System;
using System.Collections.Generic;
using System.Linq;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>一次列表投影的结果：组信息与筛选排序后的代表文件列表。</summary>
/// <param name="Groups">按当前设置构建的同名合并组；每个参与分组的文件都回链到自己的组。</param>
/// <param name="Items">排序后的代表文件列表（已按格式与星级筛选）。</param>
public sealed record PhotoListProjectionResult(
    PhotoGroupMap Groups,
    IReadOnlyList<ImageFile> Items);

/// <summary>
/// 文件列表投影：格式过滤、同名合并、代表选择、星级筛选与排序的唯一实现。
/// 只读 <see cref="SettingsViewModel"/> 与文件对象，不持有列表状态，供
/// <see cref="ThumbnailListViewModel.ApplyFilter"/> 与打开文件求首图共用。
/// </summary>
public static class PhotoListProjection
{
    /// <summary>星级筛选键 "All"（不过滤）。</summary>
    public const string AllRatings = "All";

    /// <summary>
    /// 计算筛选后的文件列表。除回填 <see cref="ImageFile.Group"/> 外不修改文件对象，
    /// 尤其不会写入旧的隐藏文件/隐藏星级缓存，也不会写显示名（显示名由组派生）。
    /// </summary>
    /// <param name="allFiles">全部文件（<see cref="FolderViewModel.AllFiles"/> 的快照）。</param>
    /// <param name="settings">设置来源：文件格式列表、同名合并开关。</param>
    /// <param name="ratingKey">星级筛选键（与筛选条一致，如 "All" / "Eq3" / "Gt2" / "Rejected" / "Conflict"）。</param>
    /// <param name="sortMode">排序方式。</param>
    /// <param name="sortOrder">排序方向。</param>
    /// <param name="retainedFile">即使被星级筛选排除也暂时保留的文件（评分后保持滚动位置）。</param>
    public static PhotoListProjectionResult Project(
        IReadOnlyList<ImageFile> allFiles,
        SettingsViewModel settings,
        string ratingKey = AllRatings,
        SortMode sortMode = SortMode.Name,
        SortOrder sortOrder = SortOrder.Ascending,
        ImageFile? retainedFile = null)
    {
        ArgumentNullException.ThrowIfNull(allFiles);
        ArgumentNullException.ThrowIfNull(settings);

        var groups = PhotoGroupMap.Build(
            allFiles,
            settings.SameNameAsOnePhoto,
            BuildFormatRules(settings));

        var items = groups.Representatives
            .Where(file => ReferenceEquals(file, retainedFile) || MatchesRating(file, ratingKey))
            .ToList();

        return new PhotoListProjectionResult(groups, SortItems(items, sortMode, sortOrder));
    }

    /// <summary>
    /// 按筛选条口径排序（FILTER-02：名称固定用序数忽略大小写）。
    /// 供仅排序变化的路径复用，保持与 <see cref="Project"/> 同一语义。
    /// </summary>
    internal static List<ImageFile> SortItems(
        List<ImageFile> files,
        SortMode sortMode,
        SortOrder sortOrder)
    {
        IEnumerable<ImageFile> sorted = sortMode switch
        {
            SortMode.Name => sortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),

            SortMode.Date => sortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue)
                : files.OrderByDescending(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue),

            SortMode.Size => sortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.FileSize)
                : files.OrderByDescending(f => f.FileSize),

            _ => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };
        return sorted.ToList();
    }

    private static bool MatchesRating(ImageFile file, string ratingKey)
    {
        var rating = file.Rating;
        var state = file.RatingState;
        // 数字与“以上”只按代表文件的内嵌值命中；代表为 Invalid/Conflict 时不命中任何数字档。
        // 冲突是独立维度：数字命中不消除冲突，冲突也不改写数字结果。
        return ratingKey switch
        {
            "None" => HasNumericValue(state) && rating == 0,
            "Rejected" => state == XmpRatingState.Rejected,
            "Eq1" => HasNumericValue(state) && rating == 1,
            "Eq2" => HasNumericValue(state) && rating == 2,
            "Eq3" => HasNumericValue(state) && rating == 3,
            "Eq4" => HasNumericValue(state) && rating == 4,
            "Eq5" => HasNumericValue(state) && rating == 5,
            "Gt1" => HasNumericValue(state) && rating >= 1,
            "Gt2" => HasNumericValue(state) && rating >= 2,
            "Gt3" => HasNumericValue(state) && rating >= 3,
            "Gt4" => HasNumericValue(state) && rating >= 4,
            "Conflict" => file.HasRatingConflict,
            _ => true
        };
    }

    private static bool HasNumericValue(XmpRatingState state) =>
        state is not (XmpRatingState.Invalid or XmpRatingState.Conflict);

    private static PhotoFormatRule[] BuildFormatRules(SettingsViewModel settings) =>
        settings.FileFormats
            .Select(item => new PhotoFormatRule(item.Id, item.DisplayName, item.Extensions, item.IsEnabled))
            .ToArray();
}
