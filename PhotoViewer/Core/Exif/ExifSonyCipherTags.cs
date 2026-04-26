using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MetadataExtractor.Formats.Exif.Makernotes;

namespace PhotoViewer.Core;

/// <summary>
/// Exif Sony 加密 MakerNote tag 解码器。
/// 处理 0x94xx / 0x9050 等使用 (b³ % 249) 替换密码加密的二进制数据块，
/// 将其解密后按 ExifTool 定义的偏移量解析为可读字段。
/// </summary>
internal static partial class ExifSonyCipherTags
{
    /// <summary>值读取格式</summary>
    internal enum FieldFormat
    {
        Int8u,
        Int8s,
        Int16u,
        Int16s,
        Int32u,
        Int32s,
        Rational32u,
    }

    /// <summary>已知的值转换公式</summary>
    internal enum ValueFormula
    {
        None,
        /// <summary>100 * 2^(16 - val/256) — Sony ISO</summary>
        SonyISO,
        /// <summary>2^(16 - val/256) — 曝光时间 (16bit编码)</summary>
        ExposureTime16,
        /// <summary>2^(6 - val/8) — 曝光时间 (8bit编码)</summary>
        ExposureTime6,
        /// <summary>val - 20 — 温度</summary>
        Subtract20,
        /// <summary>val / 10 — 焦距等</summary>
        Divide10,
        /// <summary>val / 16</summary>
        Divide16,
        /// <summary>val / 10.24 — 百分比</summary>
        Percentage1024,
        /// <summary>2^((val/256 - 16) / 2) — Sony 光圈值</summary>
        SonyFNumber,
        /// <summary>val / 256 — 档位数</summary>
        Divide256,
        /// <summary>val &amp; 0x00FFFFFF — 快门次数 (高字节清零)</summary>
        Mask24bit,
        /// <summary>(val - 32) / 1.8 — 华氏→摄氏</summary>
        BatteryTemp,
        /// <summary>"val%" — 百分比直接显示</summary>
        Percentage,
        /// <summary>val &amp; 0x7F — FocusMode 掩码</summary>
        Mask0x7F,
        /// <summary>ISOInfo 子目录 (特殊处理)</summary>
        ISOInfo,
    }

    // ─── 公开接口 ────────────────────────────────────────────────────────

    /// <summary>
    /// 尝试解码一个 Sony 加密 MakerNote tag。
    /// </summary>
    /// <param name="directory">Sony MakerNote 目录</param>
    /// <param name="tagId">Tag ID (如 0x9400)</param>
    /// <param name="cameraModel">相机型号 (如 "ILCE-7M3")，用于部分 tag 的变体选择</param>
    /// <returns>解码后的字段列表；无法解码时返回 null</returns>
    public static List<MetadataTag>? Decode(SonyType1MakernoteDirectory directory, int tagId, string? cameraModel)
    {
        // 获取原始加密字节
        var obj = directory.GetObject(tagId);
        if (obj is not byte[] raw || raw.Length < 2)
            return null;

        // 查找变体规则
        if (!Variants.TryGetValue(tagId, out var rules))
            return null;

        // 选择匹配的子表
        string? tableName = SelectVariant(rules, raw, cameraModel);
        if (tableName == null)
            return null;

        // 查找字段定义
        if (!FieldDefs.TryGetValue(tableName, out var fields))
            return null;

        // 解密数据
        var decrypted = Decipher(raw);

        // 解析字段
        var result = new List<MetadataTag>();
        bool isoInfoDecoded = false;
        var seenNames = new HashSet<string>();

        foreach (var (offset, name, format, printConv, formula) in fields)
        {
            // 跳过占位符字段 (自动生成的无名字段)
            if (name.Contains('_') && name.StartsWith("Tag"))
                continue;

            // 跳过噪声字段 (矫正参数、内部数据等)
            if (IsHiddenField(name))
                continue;

            if (name == "_ISOInfo_")
            {
                // Tag9401: 使用版本字节选择正确的 ISOInfo 偏移量
                // 所有 _ISOInfo_ 条目只处理一次
                if (!isoInfoDecoded)
                {
                    isoInfoDecoded = true;
                    int isoOffset = FindISOInfoOffset(decrypted);
                    if (isoOffset >= 0)
                        DecodeISOInfo(decrypted, isoOffset, result, tagId);
                }
                continue;
            }

            // Rational32u 特殊处理: 读取为分数
            if (format == FieldFormat.Rational32u)
            {
                var ratStr = ReadRational32u(decrypted, offset, name);
                if (ratStr != null && seenNames.Add(name))
                {
                    result.Add(new MetadataTag
                    {
                        TagId = tagId,
                        Name = name,
                        ChineseName = ExifChinese.GetChineseName(name),
                        Value = ratStr
                    });
                }
                continue;
            }

            var value = ReadField(decrypted, offset, format);
            if (value == null) continue;

            string displayValue = FormatValue(value.Value, formula, printConv);

            // 去重: 同名字段只保留第一个
            if (!seenNames.Add(name))
                continue;

            result.Add(new MetadataTag
            {
                TagId = tagId,
                Name = name,
                ChineseName = ExifChinese.GetChineseName(name),
                Value = displayValue
            });
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>获取所有支持解码的 tag ID 列表</summary>
    public static IEnumerable<int> SupportedTagIds => Variants.Keys;

    // ─── 内部实现 ────────────────────────────────────────────────────────

    /// <summary>解密数据 (使用查找表)</summary>
    private static byte[] Decipher(byte[] encrypted)
    {
        var result = new byte[encrypted.Length];
        for (int i = 0; i < encrypted.Length; i++)
        {
            result[i] = encrypted[i] < 249 ? DecipherTable[encrypted[i]] : encrypted[i];
        }
        return result;
    }

    /// <summary>选择匹配的子表变体</summary>
    private static string? SelectVariant(
        (byte[]? FirstBytes, string? ModelPattern, string TableName)[] rules,
        byte[] rawEncrypted,
        string? cameraModel)
    {
        byte firstByte = rawEncrypted[0];

        foreach (var (firstBytes, modelPattern, tableName) in rules)
        {
            // 首字节匹配 (检查加密数据)
            if (firstBytes != null)
            {
                bool match = false;
                foreach (var b in firstBytes)
                {
                    if (firstByte == b) { match = true; break; }
                }
                if (!match) continue;
                return tableName;
            }

            // 型号匹配
            if (modelPattern != null && cameraModel != null)
            {
                bool negate = modelPattern.StartsWith('!');
                string pattern = negate ? modelPattern[1..] : modelPattern;
                try
                {
                    bool isMatch = Regex.IsMatch(cameraModel, pattern, RegexOptions.None,
                        TimeSpan.FromMilliseconds(100));
                    if (negate ? !isMatch : isMatch)
                        return tableName;
                }
                catch
                {
                    // 正则无效，跳过
                }
                continue;
            }

            // 无条件匹配 (fallback)
            if (firstBytes == null && modelPattern == null)
                return tableName;
        }

        // 如果只有一个规则且无条件，直接使用
        if (rules.Length == 1)
            return rules[0].TableName;

        return null;
    }

    /// <summary>从解密后的数据中读取一个字段值</summary>
    private static long? ReadField(byte[] data, int offset, FieldFormat format)
    {
        try
        {
            return format switch
            {
                FieldFormat.Int8u => offset < data.Length ? data[offset] : null,
                FieldFormat.Int8s => offset < data.Length ? (sbyte)data[offset] : null,
                FieldFormat.Int16u => offset + 1 < data.Length
                    ? BitConverter.ToUInt16(data, offset) : null,
                FieldFormat.Int16s => offset + 1 < data.Length
                    ? BitConverter.ToInt16(data, offset) : null,
                FieldFormat.Int32u => offset + 3 < data.Length
                    ? BitConverter.ToUInt32(data, offset) : null,
                FieldFormat.Int32s => offset + 3 < data.Length
                    ? BitConverter.ToInt32(data, offset) : null,
                FieldFormat.Rational32u => offset + 3 < data.Length
                    ? BitConverter.ToUInt32(data, offset) : null, // 仅用于 fallback; 正常应走 ReadRational32u
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取 Rational32u (uint16 分子 + uint16 分母)，格式化为曝光时间等</summary>
    private static string? ReadRational32u(byte[] data, int offset, string fieldName)
    {
        if (offset + 3 >= data.Length) return null;
        try
        {
            uint num = BitConverter.ToUInt16(data, offset);
            uint den = BitConverter.ToUInt16(data, offset + 2);
            if (den == 0) return num == 0 ? "0" : null;
            double val = (double)num / den;

            // 曝光时间字段：格式化为分数
            if (fieldName.Contains("ExposureTime", StringComparison.OrdinalIgnoreCase))
            {
                if (val >= 1) return $"{val:F1} s";
                if (num == 0) return "0";
                return $"{num}/{den} s";
            }
            // 其他 rational 字段
            return val % 1 == 0 ? $"{val:F0}" : $"{val:F2}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>将原始值转换为显示字符串</summary>
    private static string FormatValue(long rawValue, ValueFormula formula, Dictionary<int, string>? printConv)
    {
        // 先应用值公式
        string result;
        switch (formula)
        {
            case ValueFormula.SonyISO:
                if (rawValue == 0) { result = "Auto"; break; }
                var iso = 100.0 * Math.Pow(2, 16.0 - rawValue / 256.0);
                result = $"{iso:F0}";
                break;

            case ValueFormula.ExposureTime16:
                if (rawValue == 0) { result = "Bulb"; break; }
                var et16 = Math.Pow(2, 16.0 - rawValue / 256.0);
                result = et16 >= 1 ? $"{et16:F1} s" : $"1/{1.0 / et16:F0} s";
                break;

            case ValueFormula.ExposureTime6:
                if (rawValue == 0) { result = "Bulb"; break; }
                var et6 = Math.Pow(2, 6.0 - rawValue / 8.0);
                result = et6 >= 1 ? $"{et6:F1} s" : $"1/{1.0 / et6:F0} s";
                break;

            case ValueFormula.Subtract20:
                result = $"{rawValue - 20} C";
                break;

            case ValueFormula.Divide10:
            {
                var d10 = rawValue / 10.0;
                result = d10 % 1 == 0 ? $"{d10:F0}" : $"{d10:F1}";
                break;
            }

            case ValueFormula.Divide16:
                result = $"{rawValue / 16.0:F1}";
                break;

            case ValueFormula.Percentage1024:
                result = $"{rawValue / 10.24:F0}%";
                break;

            case ValueFormula.SonyFNumber:
                var fnum = Math.Pow(2, (rawValue / 256.0 - 16.0) / 2.0);
                result = $"f/{fnum:F1}";
                break;

            case ValueFormula.Divide256:
                result = $"{rawValue / 256.0:F1}";
                break;

            case ValueFormula.Mask24bit:
                result = (rawValue & 0x00FFFFFF).ToString();
                break;

            case ValueFormula.BatteryTemp:
            {
                var celsius = (rawValue - 32) / 1.8;
                result = $"{celsius:F1} °C";
                break;
            }

            case ValueFormula.Percentage:
                result = $"{rawValue}%";
                break;

            case ValueFormula.Mask0x7F:
            {
                long masked = rawValue & 0x7F;
                if (printConv != null && printConv.TryGetValue((int)masked, out var label0x7F))
                    return label0x7F;
                result = masked.ToString();
                break;
            }

            default:
                result = rawValue.ToString();
                break;
        }

        // 再应用 PrintConv 映射 (仅在无公式或公式为 None 时)
        if (formula == ValueFormula.None && printConv != null)
        {
            if (printConv.TryGetValue((int)rawValue, out var label))
                result = label;
        }

        return result;
    }

    /// <summary>判断字段是否应被隐藏 (矫正参数、内部数据等无用户可读价值的字段)</summary>
    private static bool IsHiddenField(string name)
    {
        return name.EndsWith("CorrParams", StringComparison.Ordinal)
            || name.EndsWith("CorrParamsPresent", StringComparison.Ordinal)
            || name.StartsWith("Tag9", StringComparison.Ordinal)
            || name == "TempTest1"
            || name == "SonyTimeMinSec"
            || name == "ModelReleaseYear"
            || name == "LensSpecFeatures"
            || name == "InternalSerialNumber"
            || name == "BatteryLevelGrip1"
            || name == "BatteryLevelGrip2"
            || name == "ShotNumberSincePowerUp"
            || name == "LensMount2"
            || name == "LensType3"
            || name == "Battery2"
            || name == "BatteryLevel2"
            || name == "ShutterType";
    }

    /// <summary>ISO 设置值映射 (ExifTool isoSetting2010)</summary>
    private static readonly Dictionary<int, string> _isoSettings = new()
    {
        [0] = "Auto", [5] = "25", [7] = "40", [8] = "50", [9] = "64",
        [10] = "80", [11] = "100", [12] = "125", [13] = "160", [14] = "200",
        [15] = "250", [16] = "320", [17] = "400", [18] = "500", [19] = "640",
        [20] = "800", [21] = "1000", [22] = "1250", [23] = "1600", [24] = "2000",
        [25] = "2500", [26] = "3200", [27] = "4000", [28] = "5000", [29] = "6400",
        [30] = "8000", [31] = "10000", [32] = "12800", [33] = "16000", [34] = "20000",
        [35] = "25600", [36] = "32000", [37] = "40000", [38] = "51200", [39] = "64000",
        [40] = "80000", [41] = "102400", [42] = "128000", [43] = "160000",
        [44] = "204800", [45] = "256000", [46] = "320000", [47] = "409600",
    };

    /// <summary>解码 ISOInfo 子目录 (ISOSetting, ISOAutoMin, ISOAutoMax)</summary>
    private static void DecodeISOInfo(byte[] data, int offset, List<MetadataTag> result, int tagId)
    {
        // ISOInfo: 3 个 int8u 字段，间隔 2 字节 (offset+0, offset+2, offset+4)
        var names = new[] { "ISOSetting", "ISOAutoMin", "ISOAutoMax" };
        var offsets = new[] { 0, 2, 4 };
        for (int i = 0; i < 3; i++)
        {
            int pos = offset + offsets[i];
            if (pos >= data.Length) break;
            int val = data[pos];
            _isoSettings.TryGetValue(val, out var label);
            result.Add(new MetadataTag
            {
                TagId = tagId,
                Name = names[i],
                ChineseName = ExifChinese.GetChineseName(names[i]),
                Value = label ?? val.ToString()
            });
        }
    }

    /// <summary>
    /// Tag9401 版本字节 → ISOInfo 偏移量映射。
    /// 版本字节位于 Tag9401 解密后的 offset 0x0000。
    /// </summary>
    private static readonly (int[] versions, int offset)[] _isoInfoVersionMap =
    {
        (new[] { 181 }, 0x03E2),
        (new[] { 185, 186, 187 }, 0x03F4),
        (new[] { 109, 133, 148, 149 }, 0x044E),
        (new[] { 152 }, 0x0453),
        (new[] { 19, 22, 25 }, 0x0498),
        (new[] { 24 }, 0x049D),
        (new[] { 21 }, 0x049E),
        (new[] { 20 }, 0x04A1),
        (new[] { 18 }, 0x04BA),
        (new[] { 160 }, 0x059D),
        (new[] { 156, 158 }, 0x0634),
        (new[] { 159 }, 0x0636),
        (new[] { 154 }, 0x064C),
        (new[] { 155 }, 0x0653),
        (new[] { 157 }, 0x0678),
        (new[] { 161, 162 }, 0x06B8),
        (new[] { 164, 166 }, 0x06DE),
        (new[] { 163 }, 0x06E7),
    };

    /// <summary>根据 Tag9401 版本字节查找 ISOInfo 的正确偏移量</summary>
    private static int FindISOInfoOffset(byte[] decryptedData)
    {
        if (decryptedData.Length < 1) return -1;
        int version = decryptedData[0];

        foreach (var (versions, offset) in _isoInfoVersionMap)
        {
            foreach (var v in versions)
            {
                if (v == version) return offset;
            }
        }
        return -1;
    }
}
