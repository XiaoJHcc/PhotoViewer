using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PhotoViewer.Windows.Native;

internal static class WindowsAcceptanceFixtures
{
    private const string SwitchPrefix = "--pv-acceptance-fixtures";
    internal const string SwitchName = SwitchPrefix + "=";
    internal const string ReportFileName = "fixture-checks.json";

    private static readonly string[] ColorFiles =
    [
        "jpeg/color-srgb.jpg",
        "jpeg/color-p3.jpg",
        "jpeg/color-adobe.jpg",
        "jpeg/color-untagged-adobe.jpg",
        "heif/color-srgb.heic",
        "heif/color-p3.heic",
        "raw/A7C01489.ARW",
        "raw/A7C01495.ARW",
    ];

    private static readonly string[] RoundtripFiles =
    [
        "jpeg/color-srgb.jpg",
        "jpeg/color-p3.jpg",
        "jpeg/color-adobe.jpg",
        "jpeg/color-untagged-adobe.jpg",
        .. Enumerable.Range(1, 8).Select(index => $"jpeg/orientation-{index}.jpg"),
    ];

    private static string[] s_required = [];
    private static string[] s_missing = [];

    internal static string? RootDirectory { get; private set; }

    internal static void Initialize(IReadOnlyList<string> args)
    {
        RootDirectory = null;
        s_required = RequiredFor(args);
        s_missing = [];
        var declared = args.Where(arg => arg.StartsWith(SwitchPrefix, StringComparison.Ordinal)).ToArray();
        if (declared.Length > 1)
        {
            throw new InvalidOperationException(
                $"{SwitchName}<Fixtures 绝对路径> 只能出现一次，实际 {declared.Length} 次: {string.Join(' ', declared)}");
        }

        if (declared.Length == 0) return;
        var argument = declared[0];
        if (!argument.StartsWith(SwitchName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{SwitchPrefix} 缺少 '=' 与素材根: {argument}");
        }

        var value = argument[SwitchName.Length..].Trim().Trim('"');
        if (value.Length == 0)
        {
            throw new InvalidOperationException($"{SwitchName}<Fixtures 绝对路径> 取值为空");
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException($"{SwitchName}<Fixtures 绝对路径> 必须是绝对路径: {value}");
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    internal static string? Validate()
    {
        s_missing = [];
        if (RootDirectory is not { } root)
        {
            if (s_required.Length == 0) return null;
            s_missing = s_required;
            return $"{SwitchName}<Fixtures 绝对路径> 未指定：本模式需要 {s_required.Length} 个只读验收素材"
                + $"({string.Join(", ", s_required)})";
        }

        if (!Directory.Exists(root))
        {
            return $"{SwitchName}<Fixtures 绝对路径> 不是已存在目录: {root}";
        }

        s_missing = s_required.Where(relative => !File.Exists(Path.Combine(root, relative))).ToArray();
        return s_missing.Length == 0
            ? null
            : $"{SwitchName}<Fixtures 绝对路径> 校验失败：素材根 {root} 缺少 {s_missing.Length} 个必需素材: "
                + string.Join(", ", s_missing);
    }

    internal static string GetFile(string relativePath) => Resolve(relativePath, isDirectory: false);

    internal static string GetDirectory(string relativePath) => Resolve(relativePath, isDirectory: true);

    internal static bool IsInsideRoot(string path)
    {
        if (RootDirectory is not { } root) return false;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static string DescribePreflight()
        => $"acceptance fixtures: root={RootDirectory ?? "none"} required={s_required.Length}";

    internal static void WriteReport(string outputDirectory, string? failure)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, ReportFileName),
            JsonSerializer.Serialize(
                new
                {
                    argument = $"{SwitchPrefix}=<Fixtures 绝对路径>",
                    root = RootDirectory,
                    required = s_required,
                    missing = s_missing,
                    status = failure == null ? "PASS" : "FAIL",
                    detail = failure,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string[] RequiredFor(IReadOnlyList<string> args)
    {
        var required = new List<string>();
#if PHOTOVIEWER_DINO
        if (args.Contains("--pv-native-smoke")) required.Add("jpeg-large/color-p3-large.jpg");
#endif
        if (args.Contains("--pv-native-settings")) required.Add("jpeg/color-srgb.jpg");
        if (args.Contains("--pv-native-color")) required.AddRange(ColorFiles);
        if (args.Any(arg => arg.StartsWith("--pv-native-edition-roundtrip", StringComparison.Ordinal)))
        {
            required.AddRange(RoundtripFiles);
        }

        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Resolve(string relativePath, bool isDirectory)
    {
        var root = RootDirectory ?? throw new InvalidOperationException(
            $"{SwitchName}<Fixtures 绝对路径> 未指定，无法读取验收素材 {relativePath}");
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"验收素材必须是素材根内的相对路径: {relativePath}");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsInsideRoot(full))
        {
            throw new InvalidOperationException($"验收素材路径越出素材根 {root}: {relativePath}");
        }

        if (!(isDirectory ? Directory.Exists(full) : File.Exists(full)))
        {
            throw new InvalidOperationException($"素材根 {root} 缺少验收素材: {relativePath}");
        }

        return full;
    }
}
