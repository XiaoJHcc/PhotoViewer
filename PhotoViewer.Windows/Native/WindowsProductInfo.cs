using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 发行版本与产品身份事实（编译期能力 + 构建期身份元数据）。
/// 显示名、规范包 ID、内部产品键与分发名来自根 <c>Directory.Build.props</c> 按
/// <c>PhotoViewerEdition</c> 派生的 <see cref="AssemblyMetadataAttribute"/>，这里不再各写一套映射；
/// DINO 能力与标准版/AI 版的界面差异仍由 <c>PHOTOVIEWER_DINO</c> 编译符号决定。
/// 两个产品是并列安装身份：包 ID、单实例键、私有运行状态各自独立，只有适合共用的产品族数据按设计共享。
/// </summary>
internal static class WindowsProductInfo
{
    /// <summary>AI 版规范包 ID 的后缀；标准版使用无后缀 ID。</summary>
    internal const string AiPackageIdSuffix = ".AI";

#if PHOTOVIEWER_DINO
    /// <summary>诊断工具页名与直达菜单名。</summary>
    internal const string DiagnosticName = "DINO 诊断";

    /// <summary>工具窗口分类键（导航图标查表用）。</summary>
    internal const string DiagnosticCategoryKey = "Dino";

    /// <summary>发行版标识，写进验收报告。</summary>
    internal const string EditionName = "ai";

    /// <summary>当前产物是否内置 DINO。</summary>
    internal const bool HasDino = true;
#else
    /// <summary>诊断工具页名：标准版保留三瓦片 CV 诊断，不得继续叫 DINO 诊断（EDITION-04）。</summary>
    internal const string DiagnosticName = "CV 诊断";

    /// <summary>工具窗口分类键（导航图标查表用）。</summary>
    internal const string DiagnosticCategoryKey = "Cv";

    /// <summary>发行版标识，写进验收报告。</summary>
    internal const string EditionName = "standard";

    /// <summary>当前产物是否内置 DINO。</summary>
    internal const bool HasDino = false;
#endif

    private static readonly IReadOnlyDictionary<string, string> Identity = ReadIdentity();

    /// <summary>窗口标题末段、版本资源产品名与任务栏名称（空格分隔）。</summary>
    internal static string DisplayName => Value("PhotoViewerDisplayName");

    /// <summary>全平台规范包 ID（MSIX Identity.Name、AUMID 的包族前缀）。</summary>
    internal static string PackageId => Value("PhotoViewerPackageId");

    /// <summary>内部产品键（<c>photoviewer.standard</c> / <c>photoviewer.ai</c>）。</summary>
    internal static string ProductId => Value("PhotoViewerProductId");

    /// <summary>分发文件名前缀（横线分隔）。</summary>
    internal static string DistributionName => Value("PhotoViewerDistributionName");

    /// <summary>另一产品的规范包 ID；用于确认单实例键与激活目标不会跨版转发。</summary>
    internal static string SiblingPackageId => PackageId.EndsWith(AiPackageIdSuffix, StringComparison.Ordinal)
        ? PackageId[..^AiPackageIdSuffix.Length]
        : PackageId + AiPackageIdSuffix;

    /// <summary>
    /// 本产品的私有运行状态目录：日志、界面状态与临时诊断各版独立。
    /// 产品族共享数据（通用设置、CV/DINO 特征库）不在这里，按共享存储规则另行接入。
    /// </summary>
    internal static string PrivateStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DisplayName);

    private static string Value(string key)
        => Identity.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new InvalidOperationException($"程序集缺少产品身份元数据 {key}（csproj 已声明）；无法确定发行版本。");

    private static IReadOnlyDictionary<string, string> ReadIdentity()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in typeof(WindowsProductInfo).Assembly
                     .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key is { Length: > 0 } key && attribute.Value is { } value)
            {
                values[key] = value;
            }
        }

        return values;
    }
}
