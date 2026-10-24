using System.Text.Json.Serialization;

namespace PhotoViewer.Core.Settings;

/// <summary>settings.json（schemaVersion 2）的源生成序列化上下文。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SettingsDocumentDto))]
[JsonSerializable(typeof(GestureDto))]
internal sealed partial class SettingsDocumentJsonContext : JsonSerializerContext
{
}
