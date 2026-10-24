using System.Reactive.Concurrency;
using ReactiveUI.Builder;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;

namespace CoreContractCheck;

internal static class PhotoGroupChecks
{
    public static void Run(Checks c, string root)
    {
        c.Section("同名组与列表投影");
        UiDispatcher.Initialize(new InlineDispatcher());
        var reactiveBuilder = ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(ImmediateScheduler.Instance);
        reactiveBuilder.BuildApp();
        var jpg = new ImageFile(new LocalFileRef(Path.Combine(root, "a.jpg")));
        var heif = new ImageFile(new LocalFileRef(Path.Combine(root, "a.hif")));
        var raw = new ImageFile(new LocalFileRef(Path.Combine(root, "a.arw")));
        var other = new ImageFile(new LocalFileRef(Path.Combine(root, "b.jpg")));
        var rules = new[]
        {
            new PhotoFormatRule("jpg", "JPG", [".jpg"]),
            new PhotoFormatRule("heif", "HEIF", [".heif", ".hif"]),
            new PhotoFormatRule("raw", "RAW", [".arw"]),
        };

        var map = PhotoGroupMap.Build([raw, jpg, heif, other], true, rules);
        c.Equal("同名文件形成两个组", 2, map.Groups.Count);
        var group = map.GroupFor(raw)!;
        c.Equal("JPG 优先成为代表", jpg, group.Representative);
        c.Equal("两个隐藏伴侣", 2, group.Companions.Count);
        c.Equal("隐藏 RAW 回映射代表", jpg, map.RepresentativeFor(raw));
        c.True("显示名包含伴侣格式", group.DisplayName.Contains("HEIF", StringComparison.Ordinal)
            && group.DisplayName.Contains("RAW", StringComparison.Ordinal));

        jpg.UpdateCachedRating(4);
        raw.UpdateCachedRating(2);
        c.True("代表与伴侣评分冲突", group.HasRatingConflict);
        heif.UpdateCachedRating(4);
        c.True("任一伴侣不同即冲突", group.HasRatingConflict);

        var split = PhotoGroupMap.Build([raw, jpg, heif, other], false, rules);
        c.Equal("关闭同名合并后独立成组", 4, split.Groups.Count);
        c.True("独立组无伴侣", split.Groups.All(item => item.Companions.Count == 0));

        var disabledJpg = new[]
        {
            new PhotoFormatRule("jpg", "JPG", [".jpg"], false),
            rules[1], rules[2],
        };
        var withoutJpg = PhotoGroupMap.Build([raw, jpg, heif, other], true, disabledJpg);
        c.Equal("禁用 JPG 后 HEIF 成为代表", heif, withoutJpg.GroupFor(raw)!.Representative);
        c.Equal("被禁用格式不参与当前组", null, withoutJpg.GroupFor(jpg));
        c.Equal("被禁用文件无旧组背链", null, jpg.Group);

        using var settings = new SettingsViewModel();
        settings.SameNameAsOnePhoto = true;
        var projection = PhotoListProjection.Project([raw, jpg, heif, other], settings, "Eq4");
        c.Equal("星级筛选保留符合的代表", 1, projection.Items.Count);
        c.Equal("筛选结果同样使用组代表", jpg, projection.Items[0]);
        var conflict = PhotoListProjection.Project([raw, jpg, heif, other], settings, "Conflict");
        c.Equal("冲突筛选与组语义一致", jpg, conflict.Items.Single());
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public IScheduler MainThreadScheduler { get; } = ImmediateScheduler.Instance;
        public bool CheckAccess() => true;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Task.FromResult(func());
    }
}
