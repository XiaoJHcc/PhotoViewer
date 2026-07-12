using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatasetBuilder;

/// <summary>
/// 数据集入库清单（manifest）：声明要入库哪些文件夹、写到哪个独立数据集库、是否提取增强视图。
/// 清单驱动让"入了什么"可复现、可审计、可脚本化（Plan-3-1 M1 数据地基）。
/// </summary>
public sealed class IngestManifest
{
    /// <summary>独立数据集库文件路径（绝对或相对清单文件目录）。绝不写产品 photos.db。</summary>
    [JsonPropertyName("dbPath")]
    public string DbPath { get; set; } = "";

    /// <summary>增强视图配置；null 或 enabled=false 时不提取增强 CLS（仅原片路）。</summary>
    [JsonPropertyName("enhance")]
    public EnhanceOptions? Enhance { get; set; }

    /// <summary>要入库的文件夹条目（逐条带标签）。</summary>
    [JsonPropertyName("folders")]
    public List<FolderEntry> Folders { get; set; } = new();

    /// <summary>
    /// 可选：精修成品清单文件路径（每行一个"直出原片文件名去扩展名"）。命中的指纹 is_retouched=1。
    /// 缺省则 is_retouched 全 NULL（Plan-3-1 §1.1 精修回溯匹配未落实时不阻塞入库）。
    /// </summary>
    [JsonPropertyName("retouchedList")]
    public string? RetouchedList { get; set; }

    /// <summary>解码并发数；缺省 CPU/2。ONNX 推理内部另有单闸串行化。</summary>
    [JsonPropertyName("concurrency")]
    public int? Concurrency { get; set; }

    /// <summary>从 JSON 文件加载清单。</summary>
    /// <param name="path">清单文件路径。</param>
    /// <returns>反序列化后的清单对象。</returns>
    public static IngestManifest Load(string path)
    {
        var json = System.IO.File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        return JsonSerializer.Deserialize<IngestManifest>(json, options)
            ?? throw new System.InvalidOperationException($"清单为空或格式错误: {path}");
    }
}

/// <summary>增强视图开关。</summary>
public sealed class EnhanceOptions
{
    /// <summary>是否提取增强 CLS（第二 model_id 行）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>单个入库文件夹条目 + 标签。</summary>
public sealed class FolderEntry
{
    /// <summary>文件夹绝对路径。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>是否递归子目录；缺省 true（用户按选片习惯建的子文件夹结构会被 source_rel_path 记录）。</summary>
    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; } = true;

    /// <summary>事件/日期标签（如 "2026-1-10 祥睦桥 P2"），落进 photos.event_label。</summary>
    [JsonPropertyName("eventLabel")]
    public string? EventLabel { get; set; }

    /// <summary>题材标签（如 "风光"），落进 photos.subject_label，供题材配比子集选取。</summary>
    [JsonPropertyName("subjectLabel")]
    public string? SubjectLabel { get; set; }
}
