using System.Text.Json.Serialization;

namespace PhotoViewer.Core.Settings;

/// <summary>
/// 设置模型序列化上下文。
/// 使用源生成避免运行时反射序列化带来的裁剪警告。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SettingsModel))]
public sealed partial class SettingsJsonContext : JsonSerializerContext
{
}