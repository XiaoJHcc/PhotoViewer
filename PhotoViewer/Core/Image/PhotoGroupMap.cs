using System.Collections.Immutable;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

public sealed record PhotoFormatRule(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    bool IsEnabled = true)
{
    public bool Contains(string extension) => Extensions.Any(
        item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
}

public sealed class PhotoGroup
{
    internal PhotoGroup(
        string id,
        ImageFile representative,
        IEnumerable<ImageFile> files,
        IEnumerable<string> companionFormatNames)
    {
        Id = id;
        Representative = representative;
        Files = files.ToImmutableArray();
        Companions = Files.Where(file => !ReferenceEquals(file, representative)).ToImmutableArray();
        CompanionFormatNames = companionFormatNames.ToImmutableArray();
    }

    public string Id { get; }

    public ImageFile Representative { get; }

    public IReadOnlyList<ImageFile> Files { get; }

    public IReadOnlyList<ImageFile> Companions { get; }

    public IReadOnlyList<string> CompanionFormatNames { get; }

    public string DisplayName => Companions.Count == 0
        ? Representative.Name
        : $"{Representative.Name}({string.Join('/', CompanionFormatNames)})";

    public bool HasRatingConflict => Files.Any(file => file.Rating != Representative.Rating);
}

public sealed class PhotoGroupMap
{
    private readonly ImmutableDictionary<StorageItemId, PhotoGroup> _groupsByFile;

    private PhotoGroupMap(
        ImmutableArray<PhotoGroup> groups,
        ImmutableDictionary<StorageItemId, PhotoGroup> groupsByFile)
    {
        Groups = groups;
        _groupsByFile = groupsByFile;
    }

    public static PhotoGroupMap Empty { get; } = new(
        ImmutableArray<PhotoGroup>.Empty,
        ImmutableDictionary<StorageItemId, PhotoGroup>.Empty);

    public IReadOnlyList<PhotoGroup> Groups { get; }

    public IEnumerable<ImageFile> Representatives => Groups.Select(group => group.Representative);

    public static PhotoGroupMap Build(
        IEnumerable<ImageFile> source,
        bool mergeSameName,
        IEnumerable<PhotoFormatRule> formatRules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(formatRules);
        var formats = formatRules.Where(rule => rule.IsEnabled).ToArray();
        var allFiles = source.ToArray();
        // 组信息的唯一真源：先清空所有源文件的背链，再只给本次参与分组的文件回填。
        // 被格式筛选掉的文件因此不会残留上一次的组与显示名。
        foreach (var file in allFiles)
        {
            file.Group = null;
        }

        var files = allFiles.Where(file => FindFormat(file.Name, formats) != null).ToArray();
        if (files.Length == 0) return Empty;

        var groups = new List<PhotoGroup>();
        if (mergeSameName)
        {
            foreach (var sameName in files.GroupBy(
                file => Path.GetFileNameWithoutExtension(file.Name),
                StringComparer.OrdinalIgnoreCase))
            {
                var members = sameName.ToArray();
                var representative = members
                    .Select((file, index) => (file, index))
                    .OrderBy(item => FormatPriority(item.file.Name, formats))
                    .ThenBy(item => item.index)
                    .First().file;
                groups.Add(CreateGroup(members, representative, formats));
            }
        }
        else
        {
            groups.AddRange(files.Select(file => CreateGroup([file], file, formats)));
        }

        var byFile = groups
            .SelectMany(group => group.Files.Select(file => (file, group)))
            .ToImmutableDictionary(item => item.file.File.StorageId, item => item.group);

        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                file.Group = group;
            }
        }

        return new PhotoGroupMap(groups.ToImmutableArray(), byFile);
    }

    public PhotoGroup? GroupFor(ImageFile? file) =>
        file != null && _groupsByFile.TryGetValue(file.File.StorageId, out var group) ? group : null;

    public ImageFile? RepresentativeFor(ImageFile? file) => GroupFor(file)?.Representative;

    private static PhotoGroup CreateGroup(
        ImageFile[] members,
        ImageFile representative,
        PhotoFormatRule[] formats)
    {
        var companionNames = members
            .Where(file => !ReferenceEquals(file, representative))
            .Select(file => FindFormat(file.Name, formats)?.DisplayName.ToUpperInvariant())
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        var id = string.Join("\n", members
            .Select(file => file.File.StorageId.Value)
            .OrderBy(value => value, StringComparer.Ordinal));
        return new PhotoGroup(id, representative, members, companionNames);
    }

    private static int FormatPriority(string name, PhotoFormatRule[] formats)
    {
        var index = Array.FindIndex(formats, rule => rule.Contains(Path.GetExtension(name)));
        return index < 0 ? int.MaxValue : index;
    }

    private static PhotoFormatRule? FindFormat(string name, PhotoFormatRule[] formats) =>
        formats.FirstOrDefault(rule => rule.Contains(Path.GetExtension(name)));
}
