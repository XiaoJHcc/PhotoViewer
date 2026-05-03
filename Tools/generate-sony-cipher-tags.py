#!/usr/bin/env python3
"""
Tools/generate-sony-cipher-tags.py

从 ExifTool Perl 源码 (Sony.pm) 生成 Sony 加密 MakerNote tag 解析映射 C# 文件。
Sony 0x94xx / 0x9050 等 tag 使用 (b³ % 249) 替换密码加密，本脚本提取解密后的
二进制偏移量→字段名映射，用于在 C# 中解码这些加密数据块。

用法:
    python3 Tools/generate-sony-cipher-tags.py [--ref <git-ref>]

输出:
    PhotoViewer/Core/Exif/Sony/SonyCipherTags.Generated.cs
"""

import re
import sys
import urllib.request
import urllib.error
from datetime import datetime, timezone
from pathlib import Path
from argparse import ArgumentParser
from typing import Optional

EXIFTOOL_RAW = "https://raw.githubusercontent.com/exiftool/exiftool/{ref}/lib/Image/ExifTool/Sony.pm"
REPO_ROOT = Path(__file__).parent.parent
OUTPUT_PATH = REPO_ROOT / "PhotoViewer" / "Core" / "Exif" / "Sony" / "SonyCipherTags.Generated.cs"

# ─── 数据结构 ────────────────────────────────────────────────────────────────

class SharedVar:
    """Perl 共享变量定义 (my %varName = (...))"""
    def __init__(self, name: str):
        self.name = name
        self.tag_name: str = ""
        self.format: Optional[str] = None
        self.print_conv: Optional[dict[int, str]] = None

class FieldDef:
    """子表中的一个字段定义"""
    def __init__(self, offset: int, name: str, fmt: Optional[str] = None,
                 print_conv: Optional[dict[int, str]] = None,
                 value_formula: Optional[str] = None):
        self.offset = offset
        self.name = name
        self.format = fmt
        self.print_conv = print_conv
        self.value_formula = value_formula

class SubTable:
    """一个加密子表 (如 Tag9400c)"""
    def __init__(self, table_name: str, default_format: str = "int8u"):
        self.table_name = table_name
        self.default_format = default_format
        self.fields: list[FieldDef] = []

class VariantRule:
    """变体选择规则"""
    def __init__(self, first_bytes: Optional[list[int]], model_pattern: Optional[str],
                 table_name: str):
        self.first_bytes = first_bytes      # 加密首字节匹配 ($$valPt)
        self.model_pattern = model_pattern  # 相机型号正则 ($$self{Model})
        self.table_name = table_name

# ─── Perl 源码解析 ───────────────────────────────────────────────────────────

# 匹配共享变量定义: my %varName = (
_SHARED_VAR_RE = re.compile(r'^my\s+%(\w+)\s*=\s*\(')
# 匹配 Name => 'TagName'
_NAME_RE = re.compile(r"Name\s*=>\s*['\"]([A-Za-z]\w*)['\"]")
# 匹配 Format => 'int16u' 等
_FORMAT_RE = re.compile(r"Format\s*=>\s*['\"](\w[\w\[\]]*)['\"]")
# 匹配数字 tag 偏移: 0xHEXH => { ... } 或 0xHEXH => 'name'
_OFFSET_RE = re.compile(r'^\s+(0x[0-9a-fA-F]{1,6})\s*=>')
# 匹配 hash 引用: { %varName }
_HASH_REF_RE = re.compile(r'\{\s*%(\w+)\s*\}')
# 匹配子表定义: %Image::ExifTool::Sony::TagXXX = (
_SUBTABLE_DEF_RE = re.compile(r'^%Image::ExifTool::Sony::(\w+)\s*=\s*\(')
# 匹配 ProcessEnciphered
_PROCESS_ENCIPHERED_RE = re.compile(r'PROCESS_PROC\s*=>\s*\\&ProcessEnciphered')
# 匹配 FORMAT => 'xxx' (表级默认格式)
_TABLE_FORMAT_RE = re.compile(r"^\s+FORMAT\s*=>\s*['\"](\w+)['\"]")
# 匹配 PrintConv 中的整数映射: N => 'Value'
_PRINTCONV_ENTRY_RE = re.compile(r"^\s+(\d+)\s*=>\s*['\"]([^'\"]+)['\"]")
# 匹配 ValueConv 公式
_VALUECONV_RE = re.compile(r"ValueConv\s*=>\s*['\"]([^'\"]+)['\"]")
# 匹配 $$valPt 首字节条件: /^[\xHH\xHH...]/ 或 /^\xHH/
_VALPT_BYTES_RE = re.compile(r"\$\$valPt\s*=~\s*/\^\[?((?:\\x[0-9a-fA-F]{2})+)\]?/")
# 匹配 $$self{Model} 条件
_MODEL_COND_RE = re.compile(r"\$\$self\{Model\}\s*=~\s*/([^/]+)/")
_MODEL_NOTMATCH_RE = re.compile(r"\$\$self\{Model\}\s*!~\s*/([^/]+)/")
# 匹配 SubDirectory => { TagTable => 'Image::ExifTool::Sony::XXX' }
_SUBTABLE_REF_RE = re.compile(r"TagTable\s*=>\s*['\"]Image::ExifTool::Sony::(\w+)['\"]")
# 匹配 %unknownCipherData 或 Name => 'Sony_0x...' 的 fallback 条目
_UNKNOWN_CIPHER_RE = re.compile(r'%unknownCipherData|Sony_0x')


def fetch_sony_pm(ref: str) -> Optional[str]:
    """获取 ExifTool Sony.pm 源文件"""
    url = EXIFTOOL_RAW.format(ref=ref)
    try:
        with urllib.request.urlopen(url, timeout=60) as resp:
            return resp.read().decode("utf-8", errors="replace")
    except Exception as e:
        print(f"[ERROR] 获取 Sony.pm 失败: {e}", file=sys.stderr)
        return None


def _find_matching_paren(lines: list[str], start_line: int) -> int:
    """从 start_line 开始，找到匹配的右括号 ); 所在行号"""
    depth = 0
    for i in range(start_line, len(lines)):
        for ch in lines[i]:
            if ch == '(':
                depth += 1
            elif ch == ')':
                depth -= 1
                if depth <= 0:
                    return i
    return len(lines) - 1


def _parse_print_conv(lines: list[str], start: int, end: int,
                      lookup_tables: Optional[dict[str, dict[int, str]]] = None
                      ) -> Optional[dict[int, str]]:
    """解析 PrintConv => { N => 'Value', ... } 映射，
    支持 PrintConv => \\%varName 引用"""
    result: dict[int, str] = {}
    in_conv = False
    depth = 0
    for i in range(start, end + 1):
        line = lines[i]
        if not in_conv:
            if 'PrintConv' in line and '=>' in line:
                # PrintConv => \%varName 引用
                ref_m = re.search(r'PrintConv\s*=>\s*\\%(\w+)', line)
                if ref_m and lookup_tables:
                    var_name = ref_m.group(1)
                    return lookup_tables.get(var_name)
                if ref_m:
                    return None  # 引用但无可用查找表
                # PrintConv => \&funcName 引用
                if '\\&' in line:
                    return None
                if '{' in line[line.index('PrintConv'):]:
                    in_conv = True
                    depth = 0
                    for ch in line[line.index('{'):]:
                        if ch == '{': depth += 1
                        elif ch == '}': depth -= 1
            continue
        for ch in line:
            if ch == '{': depth += 1
            elif ch == '}':
                depth -= 1
                if depth <= 0:
                    return result if result else None
        m = _PRINTCONV_ENTRY_RE.match(line)
        if m:
            result[int(m.group(1))] = m.group(2)
    return result if result else None


def _detect_value_formula(lines: list[str], start: int, end: int) -> Optional[str]:
    """检测已知的 ValueConv 公式模式"""
    for i in range(start, end + 1):
        m = _VALUECONV_RE.search(lines[i])
        if m:
            formula = m.group(1)
            if '2**(16' in formula or '2 ** (16' in formula:
                if '100' in formula:
                    return "SonyISO"          # 100 * 2^(16 - val/256)
                return "ExposureTime16"       # 2^(16 - val/256)
            if '2 ** (6' in formula or '2**(6' in formula:
                return "ExposureTime6"        # 2^(6 - val/8)
            # 注意: SonyFNumber 必须在 Divide256 之前检测 (都含 /256)
            if '- 16) / 2' in formula or '-16)/2' in formula:
                return "SonyFNumber"          # 2^((val/256 - 16) / 2)
            if '$val - 20' in formula or '$val-20' in formula:
                return "Subtract20"           # val - 20 (temperature)
            if '$val / 10' in formula or '$val/10' in formula:
                if '10.24' in formula:
                    return "Percentage1024"    # val / 10.24
                return "Divide10"             # val / 10 (focal length)
            if '$val / 16' in formula or '$val/16' in formula:
                return "Divide16"
            if '$val / 256' in formula or '$val/256' in formula:
                return "Divide256"            # val / 256 (stops)
    return None


def parse_lookup_tables(content: str) -> dict[str, dict[int, str]]:
    """解析 Perl 中的大型查找表 (如 %sonyLensTypes2)"""
    lines = content.split('\n')
    result: dict[str, dict[int, str]] = {}

    # 需要解析的查找表名称
    target_tables = ['sonyLensTypes2', 'sonyLensTypes']

    for table_name in target_tables:
        # 匹配 %sonyLensTypes2 = ( 或 my %sonyLensTypes2 = (
        pattern = re.compile(rf'(?:my\s+)?%{re.escape(table_name)}\s*=\s*\(')
        for i, line in enumerate(lines):
            if pattern.search(line):
                end = _find_matching_paren(lines, i)
                table: dict[int, str] = {}
                for j in range(i, end + 1):
                    m = _PRINTCONV_ENTRY_RE.match(lines[j])
                    if m:
                        table[int(m.group(1))] = m.group(2)
                if table:
                    result[table_name] = table
                break

    return result


def parse_shared_vars(content: str) -> dict[str, SharedVar]:
    """解析所有 my %varName = (...) 共享变量定义"""
    lines = content.split('\n')
    result: dict[str, SharedVar] = {}

    i = 0
    while i < len(lines):
        m = _SHARED_VAR_RE.match(lines[i])
        if m:
            var_name = m.group(1)
            sv = SharedVar(var_name)
            end = _find_matching_paren(lines, i)

            # 提取 Name
            for j in range(i, min(end + 1, i + 15)):
                nm = _NAME_RE.search(lines[j])
                if nm:
                    sv.tag_name = nm.group(1)
                    break

            # 提取 Format
            for j in range(i, min(end + 1, i + 15)):
                fm = _FORMAT_RE.search(lines[j])
                if fm:
                    sv.format = fm.group(1)
                    break

            # 提取 PrintConv
            sv.print_conv = _parse_print_conv(lines, i, end)

            # 提取 ValueConv
            # (shared vars usually don't have ValueConv, but check anyway)

            if sv.tag_name:
                result[var_name] = sv

            i = end + 1
            continue
        i += 1

    return result


def parse_sub_tables(content: str, shared_vars: dict[str, SharedVar],
                     lookup_tables: Optional[dict[str, dict[int, str]]] = None
                     ) -> dict[str, SubTable]:
    """解析所有加密子表定义"""
    lines = content.split('\n')
    result: dict[str, SubTable] = {}

    i = 0
    while i < len(lines):
        m = _SUBTABLE_DEF_RE.match(lines[i])
        if not m:
            i += 1
            continue

        table_name = m.group(1)
        end = _find_matching_paren(lines, i)

        # 检查是否是 ProcessEnciphered 表
        header_end = min(i + 20, end)
        is_cipher = False
        for j in range(i, header_end):
            if _PROCESS_ENCIPHERED_RE.search(lines[j]):
                is_cipher = True
                break

        if not is_cipher:
            i = end + 1
            continue

        # 提取默认 FORMAT
        default_fmt = "int8u"
        for j in range(i, header_end):
            fm = _TABLE_FORMAT_RE.match(lines[j])
            if fm:
                default_fmt = fm.group(1)
                break

        st = SubTable(table_name, default_fmt)

        # 提取字段定义
        j = i + 1
        while j <= end:
            om = _OFFSET_RE.match(lines[j])
            if not om:
                j += 1
                continue

            offset = int(om.group(1), 16)
            rest = lines[j][om.end():].strip()

            # 跳过表头属性 (如 PROCESS_PROC, FORMAT, etc.)
            if offset > 0xFFFF:
                j += 1
                continue

            # 找到这个条目的范围
            entry_end = j
            if '{' in rest or '[' in rest:
                entry_end = _find_matching_paren(lines, j)

            # Case 1: { %sharedVar }
            hm = _HASH_REF_RE.search(rest)
            if hm:
                var_name = hm.group(1)
                sv = shared_vars.get(var_name)
                if sv and sv.tag_name:
                    field = FieldDef(offset, sv.tag_name, sv.format, sv.print_conv)
                    st.fields.append(field)
                j = entry_end + 1
                continue

            # Case 2: { Name => 'FieldName', ... }
            # Also handle [{ Name => ... }, ...] (take first variant)
            field_name = None
            field_format = None
            field_print_conv = None
            field_formula = None

            # 更精确地查找字段属性 (只在第一个变体内搜索)
            first_variant_end = entry_end
            for k in range(j + 1, entry_end + 1):
                if '},{' in lines[k] or lines[k].strip() == '},{':
                    first_variant_end = k
                    break

            for k in range(j, min(first_variant_end + 1, j + 30)):
                if field_name is None:
                    nm = _NAME_RE.search(lines[k])
                    if nm:
                        field_name = nm.group(1)
                if field_format is None:
                    fm = _FORMAT_RE.search(lines[k])
                    if fm:
                        field_format = fm.group(1)

            if field_name:
                # 检查是否是 SubDirectory (子目录引用，跳过)
                is_subdir = False
                for k in range(j, min(entry_end + 1, j + 15)):
                    if 'SubDirectory' in lines[k]:
                        is_subdir = True
                        break

                if not is_subdir:
                    field_print_conv = _parse_print_conv(lines, j, first_variant_end, lookup_tables)
                    field_formula = _detect_value_formula(lines, j, first_variant_end)
                    field = FieldDef(offset, field_name, field_format,
                                    field_print_conv, field_formula)
                    st.fields.append(field)
                else:
                    # SubDirectory 条目 - 特殊处理 ISOInfo 等
                    sub_ref = None
                    for k in range(j, min(entry_end + 1, j + 10)):
                        sm = _SUBTABLE_REF_RE.search(lines[k])
                        if sm:
                            sub_ref = sm.group(1)
                            break
                    if sub_ref == "ISOInfo" or field_name == "ISOInfo":
                        # ISOInfo 子目录: 3 个固定字段 (ISOSetting, ISOAutoMin, ISOAutoMax)
                        # 在 Tag9401 中，偏移量因版本不同而变化
                        # 记录为特殊的 ISOInfo 条目
                        field = FieldDef(offset, "_ISOInfo_", None, None, "ISOInfo")
                        st.fields.append(field)

            j = entry_end + 1
            continue

        if st.fields:
            result[table_name] = st

        i = end + 1

    return result


def parse_variant_dispatch(content: str, sub_tables: dict[str, SubTable]) -> dict[int, list[VariantRule]]:
    """从主表 (%Image::ExifTool::Sony::Main) 解析变体选择规则，
    并补充未在主表中出现的单变体 tag"""
    lines = content.split('\n')
    result: dict[int, list[VariantRule]] = {}

    # 找到主表
    main_start = -1
    for i, line in enumerate(lines):
        if '%Image::ExifTool::Sony::Main' in line and '=' in line:
            main_start = i
            break
    if main_start < 0:
        return result

    main_end = _find_matching_paren(lines, main_start)

    # 扫描主表中的 0x9xxx 和 0x2010 条目
    target_tags = set()
    for tag_id in [0x2010, 0x900b, 0x9050,
                   0x9400, 0x9401, 0x9402, 0x9403, 0x9404, 0x9405, 0x9406,
                   0x940a, 0x940b, 0x940c, 0x940d, 0x940e, 0x940f,
                   0x9411, 0x9416]:
        target_tags.add(tag_id)

    i = main_start
    while i <= main_end:
        om = _OFFSET_RE.match(lines[i])
        if not om:
            i += 1
            continue

        tag_id_str = om.group(1)
        tag_id = int(tag_id_str, 16) if tag_id_str.startswith('0x') else int(tag_id_str)
        if tag_id not in target_tags:
            i += 1
            continue

        # 找到条目结尾
        entry_end = _find_matching_paren(lines, i)

        variants: list[VariantRule] = []

        # 扫描条目中所有的 SubDirectory 引用
        # 每个引用代表一个变体，向上搜索其 Condition
        j = i
        current_variant_start = i  # 当前变体块的起始行
        while j <= entry_end:
            line = lines[j]

            # 跟踪变体边界: },{ 或 },{
            if j > i and (line.strip().startswith('},{') or line.strip() == '},{'):
                current_variant_start = j

            sub_m = _SUBTABLE_REF_RE.search(line)
            if sub_m:
                sub_table = sub_m.group(1)

                # 跳过 fallback/unknown 变体
                is_fallback = False
                for k in range(max(current_variant_start, j - 5), min(j + 3, entry_end + 1)):
                    if _UNKNOWN_CIPHER_RE.search(lines[k]):
                        is_fallback = True
                        break
                if is_fallback:
                    j += 1
                    continue

                # 在当前变体块中搜索条件
                first_bytes: Optional[list[int]] = None
                model_pattern: Optional[str] = None
                search_start = max(i, current_variant_start)
                for k in range(search_start, j + 1):
                    # $$valPt =~ /^[...]/
                    vp_m = _VALPT_BYTES_RE.search(lines[k])
                    if vp_m:
                        hex_bytes = re.findall(r'\\x([0-9a-fA-F]{2})', vp_m.group(1))
                        first_bytes = [int(h, 16) for h in hex_bytes]
                    # $$self{Model} =~ /.../
                    if '$$self{Model}' in lines[k]:
                        mm = _MODEL_COND_RE.search(lines[k])
                        mn = _MODEL_NOTMATCH_RE.search(lines[k])
                        if mn:
                            model_pattern = "!" + mn.group(1)
                        elif mm:
                            model_pattern = mm.group(1)

                variants.append(VariantRule(first_bytes, model_pattern, sub_table))
            j += 1

        if variants:
            result[tag_id] = variants

        i = entry_end + 1

    # 补充: 对于有子表但未出现在变体映射中的 tag，
    # 根据子表名推断 tag ID (如 Tag9402 → 0x9402)
    tag_name_to_id = {
        "Tag9402": 0x9402, "Tag9406": 0x9406, "Tag9406b": 0x9406,
        "Tag940a": 0x940a, "Tag940c": 0x940c, "Tag940e": 0x940e,
        "AFInfo": 0x940e,
    }
    for table_name, st in sub_tables.items():
        inferred_id = tag_name_to_id.get(table_name)
        if inferred_id is None:
            # 尝试从名称推断: Tag9050a → 0x9050
            m = re.match(r'^Tag([0-9a-fA-F]{4})[a-z]?$', table_name)
            if m:
                inferred_id = int(m.group(1), 16)
        if inferred_id is None:
            continue
        if inferred_id not in result:
            result[inferred_id] = [VariantRule(None, None, table_name)]
        elif not any(r.table_name == table_name for r in result[inferred_id]):
            # 子表存在但未在变体中被引用 — 作为额外变体追加
            result[inferred_id].append(VariantRule(None, None, table_name))

    return result


def apply_variant_corrections(variants: dict[int, list[VariantRule]]) -> None:
    """补充自动解析可能遗漏的变体条件 (基于 ExifTool 已知数据)。
    Perl 中的条件表达式格式多变（跨行、q{}、or 链），自动解析可能遗漏，
    这里用硬编码方式确保关键 tag 的变体条件正确。"""

    # 0x9400: 加密首字节选择 (3 个世代)
    _set_variants(variants, 0x9400, [
        VariantRule([0x07, 0x09, 0x0a, 0x5e, 0xe7, 0x04], None, "Tag9400a"),
        VariantRule([0x0c], None, "Tag9400b"),
        VariantRule([0x23, 0x24, 0x26, 0x28, 0x31, 0x32, 0x33, 0x41], None, "Tag9400c"),
    ])

    # 0x9404: 加密首字节选择 (与 0x9400 类似但字节略有不同)
    _set_variants(variants, 0x9404, [
        VariantRule([0x04, 0x07, 0x09, 0x0a, 0x05, 0x5e, 0xe7], None, "Tag9404a"),
        VariantRule([0x0c], None, "Tag9404b"),
        VariantRule([0x23, 0x24, 0x26, 0x28, 0x31, 0x32, 0x33, 0x41], None, "Tag9404c"),
    ])

    # 0x9405: 加密首字节选择
    _set_variants(variants, 0x9405, [
        VariantRule([0x1b, 0x40, 0x7d], None, "Tag9405a"),
        VariantRule([0x23, 0x24, 0x26, 0x28, 0x31, 0x32, 0x33, 0x41], None, "Tag9405b"),
    ])

    # 0x9050: 型号条件选择 (ExifTool: 4 个世代)
    _set_variants(variants, 0x9050, [
        VariantRule(None, r"!^(DSC-|Stellar|ILCE-(1\b|6[1-7]00|6400|7C\b|7M[3-5]|7RM[2-5]|7SM[23]|7C[MR]|9\b|9M[23])|ILCA-99M2|ILME-|ZV-)", "Tag9050a"),
        VariantRule(None, r"^(ILCE-(6[1-6]00|6400|7C\b|7M3|7RM[234]A?|7SM2|9\b|9M2)|ILCA-99M2|ZV-E10\b)", "Tag9050b"),
        VariantRule(None, r"^(ILCE-(1\b|7M4|7RM5|7SM3)|ILME-FX3)", "Tag9050c"),
        VariantRule(None, r"^(ILCE-(6700|7CM2|7CR|7M5|1M2|9M3)|ILME-FX2|ZV-E1\b|ZV-E10M2)", "Tag9050d"),
    ])

    # 0x2010: 型号条件选择 (多世代，简化处理)
    _set_variants(variants, 0x2010, [
        VariantRule(None, r"^(SLT-|HV)", "Tag2010b"),
        VariantRule(None, r"^(NEX-[56]|NEX-3N|ILCE-3000|ILCE-3500)", "Tag2010c"),
        VariantRule(None, r"^(NEX-VG)", "Tag2010d"),
        VariantRule(None, r"^(ILCE-(5000|5100|6000|6300|6500|7|7M2|7R|7S|QX1))", "Tag2010e"),
        VariantRule(None, r"^(ILCA-(68|77M2))", "Tag2010f"),
        VariantRule(None, r"^(ILCE-(6100|6400|6600|7C|7M3|7M4|7RM3|7RM4|7RM5|7SM3|9|9M2|9M3))", "Tag2010g"),
        VariantRule(None, r"^(ILCE-(7CR|7CM2|1))", "Tag2010h"),
        VariantRule(None, r"^(ZV-|ILME-)", "Tag2010i"),
    ])

    # 0x940e: 首字节选择
    _set_variants(variants, 0x940e, [
        VariantRule([0x23, 0x24, 0x26, 0x28, 0x31, 0x32, 0x33, 0x41], None, "AFInfo"),
        VariantRule(None, None, "Tag940e"),
    ])

    # 0x9406: 加密首字节选择 (ExifTool: $$valPt 检查)
    _set_variants(variants, 0x9406, [
        VariantRule([0x01, 0x08, 0x1B], None, "Tag9406"),
        VariantRule([0x40], None, "Tag9406b"),
    ])


def _set_variants(variants: dict[int, list[VariantRule]], tag_id: int,
                   rules: list[VariantRule]) -> None:
    """设置变体规则（仅当对应子表存在时）"""
    if tag_id not in variants:
        return
    # 过滤: 只保留实际存在于 variants 中的子表
    existing = {r.table_name for r in variants[tag_id]}
    filtered = [r for r in rules if r.table_name in existing]
    if filtered:
        variants[tag_id] = filtered


def apply_field_corrections(sub_tables: dict[str, SubTable],
                             lookup_tables: dict[str, dict[int, str]]) -> None:
    """修正自动解析器在多变体 Perl 条目中可能错取的字段属性，
    并补充关键 PrintConv 映射和缺失字段。"""

    # ─── PrintConv 表 ─────────────────────────────────────────────

    pc_quality2 = {0: "JPEG", 1: "RAW", 2: "RAW + JPEG", 3: "JPEG + MPO"}
    pc_quality2_heif = {1: "JPEG", 2: "RAW", 3: "RAW + JPEG", 4: "HEIF", 6: "RAW + HEIF"}
    pc_focus_mode = {0: "Manual", 2: "AF-S", 3: "AF-C", 4: "AF-A", 6: "DMF", 7: "AF-D"}
    pc_exposure_program = {
        0: "P", 1: "A", 2: "S", 3: "M",
        4: "Auto", 5: "iAuto", 6: "Superior Auto", 7: "iAuto+",
        8: "Portrait", 9: "Landscape", 10: "Twilight", 11: "Twilight Portrait",
        12: "Sunset", 14: "Action", 16: "Sports",
        17: "Handheld Night Shot", 18: "Anti Motion Blur", 19: "High Sensitivity",
        21: "Beach", 22: "Snow", 23: "Fireworks",
        26: "Underwater", 27: "Gourmet", 28: "Pet", 29: "Macro",
        30: "HDR", 33: "Sweep Panorama", 36: "Background Defocus",
        43: "Cont. Priority AE", 45: "Document", 46: "Party",
    }
    pc_lens_format = {0: "Unknown", 1: "APS-C", 2: "Full-frame"}
    pc_lens_mount = {0: "Unknown", 1: "A-mount", 2: "E-mount"}
    pc_aps_c_capture = {0: "Off", 1: "On"}
    pc_release_mode2 = {
        0: "Normal", 1: "Continuous", 2: "Bracketing",
        3: "WB Bracketing", 5: "Burst", 6: "Capture During Movie",
        7: "Sweep Panorama", 8: "Anti-Motion Blur",
        9: "HDR", 13: "3D Sweep Panorama",
        16: "3D Image", 17: "Burst 2", 18: "iAuto+",
        19: "Speed Priority", 20: "Multi Frame NR",
        23: "Bracketing (Single)", 26: "Continuous Low",
        27: "High Sensitivity", 28: "Smile Shutter",
        146: "Movie Capture (Single)",
    }
    pc_flash_status = {
        0: "No Flash", 2: "Flash Inhibited",
        64: "Built-in Flash", 65: "Built-in Flash Fired",
        128: "External Flash", 129: "External Flash Fired",
    }
    pc_camera_orientation = {1: "Horizontal", 3: "Rotate 180", 6: "Rotate 90 CW", 8: "Rotate 270 CW"}
    pc_shutter_type = {7: "Electronic", 23: "Mechanical"}
    pc_creative_style = {
        0: "Standard", 1: "Vivid", 2: "Neutral", 3: "Portrait", 4: "Landscape",
        5: "B&W", 6: "Clear", 7: "Deep", 8: "Light", 9: "Sunset",
        10: "Night View", 11: "Autumn Leaves", 13: "Sepia",
        15: "FL", 16: "VV2", 17: "IN", 18: "SH", 255: "Off",
    }
    pc_picture_profile = {
        0: "Standard (PP2)", 1: "Portrait", 3: "Night View",
        4: "B&W/Sepia", 5: "Clear", 6: "Deep", 7: "Light", 8: "Vivid", 9: "Real",
        10: "Movie (PP1)", 22: "ITU709 (PP3/PP4)", 24: "Cine1 (PP5)", 25: "Cine2 (PP6)",
        26: "Cine3", 27: "Cine4", 28: "S-Log2 (PP7)", 29: "ITU709 (800%)",
        31: "S-Log3 (PP8/PP9)", 33: "HLG2 (PP10)", 34: "HLG3",
        36: "Off", 37: "FL", 38: "VV2", 39: "IN", 40: "SH", 48: "FL2", 49: "FL3",
    }
    pc_hi_iso_nr = {0: "Off", 1: "Low", 2: "Normal", 3: "High"}
    pc_long_exp_nr = {0: "Off", 1: "On"}

    # ─── 字段修正列表 ────────────────────────────────────────────

    corrections: list[tuple[str, int, Optional[str], Optional[str], Optional[str], Optional[dict[int,str]]]] = [
        # (表名, 偏移, 字段名覆盖, 格式覆盖, 公式覆盖, PrintConv覆盖)

        # ── Tag9416 ──
        ("Tag9416", 0x0006, "SonyExposureTime2", "int16u", "ExposureTime16", None),
        ("Tag9416", 0x000a, "SonyExposureTime2", "int16u", "ExposureTime16", None),
        ("Tag9416", 0x0010, "SonyFNumber2", "int16u", "SonyFNumber", None),
        ("Tag9416", 0x0012, "SonyMaxApertureValue", "int16u", "SonyFNumber", None),
        ("Tag9416", 0x0035, "ExposureProgram", "int8u", None, pc_exposure_program),
        ("Tag9416", 0x0048, "LensMount", "int8u", None, pc_lens_mount),
        ("Tag9416", 0x0049, "LensFormat", "int8u", None, pc_lens_format),
        ("Tag9416", 0x004A, "LensMount", "int8u", None, pc_lens_mount),
        ("Tag9416", 0x004B, None, "int16u", None, lookup_tables.get("sonyLensTypes2")),

        # ── Tag9400c (当前机型的基本信息表) ──
        ("Tag9400c", 0x0029, "CameraOrientation", "int8u", None, pc_camera_orientation),
        ("Tag9400c", 0x002a, "Quality2", "int8u", None, pc_quality2_heif),

        # ── Tag9402 ──
        ("Tag9402", 0x0004, "FocusMode", "int8u", "Mask0x7F", pc_focus_mode),

        # ── Tag9405b (曝光/风格信息) ──
        ("Tag9405b", 0x0004, None, "int16u", "SonyISO", None),
        ("Tag9405b", 0x0006, None, "int16u", "SonyISO", None),
        ("Tag9405b", 0x000A, "StopsAboveBaseISO", "int16u", "Divide256", None),
        ("Tag9405b", 0x0014, "SonyFNumber", "int16u", "SonyFNumber", None),
        ("Tag9405b", 0x0016, "SonyMaxApertureValue", "int16u", "SonyFNumber", None),
        ("Tag9405b", 0x0042, "HighISONoiseReduction", "int8u", None, pc_hi_iso_nr),
        ("Tag9405b", 0x0044, "LongExposureNoiseReduction", "int8u", None, pc_long_exp_nr),
        ("Tag9405b", 0x0048, "ExposureProgram", "int8u", None, pc_exposure_program),
        ("Tag9405b", 0x004a, "CreativeStyle", "int8u", None, pc_creative_style),
        ("Tag9405b", 0x005d, "LensFormat", "int8u", None, pc_lens_format),
        ("Tag9405b", 0x005e, "LensMount", "int8u", None, pc_lens_mount),

        # ── Tag9050b (A7III 等中期机型) ──
        ("Tag9050b", 0x0026, "Shutter", "int16u", "ExposureTime16", None),
        ("Tag9050b", 0x0039, "FlashStatus", "int8u", "None", pc_flash_status),
        ("Tag9050b", 0x003A, "ShutterCount", "int32u", "Mask24bit", None),
        ("Tag9050b", 0x0046, "SonyExposureTime", "int16u", "ExposureTime16", None),
        ("Tag9050b", 0x0048, "SonyFNumber", "int16u", "SonyFNumber", None),
        ("Tag9050b", 0x004b, "ReleaseMode2", "int8u", None, pc_release_mode2),
        ("Tag9050b", 0x0052, "ShutterCount2", "int32u", "Mask24bit", None),
        ("Tag9050b", 0x0058, "ShutterCount2", "int32u", "Mask24bit", None),
        ("Tag9050b", 0x0105, "LensMount", "int8u", None, pc_lens_mount),
        ("Tag9050b", 0x0106, "LensFormat", "int8u", None, pc_lens_format),
        ("Tag9050b", 0x0114, "APS-CSizeCapture", "int8u", None, pc_aps_c_capture),
        ("Tag9050b", 0x019F, None, "int32u", None, None),
        ("Tag9050b", 0x01CB, None, "int32u", None, None),
        ("Tag9050b", 0x01CD, None, "int32u", None, None),

        # ── Tag9050a (老一代机型) ──
        ("Tag9050a", 0x0020, "Shutter", "int16u", "ExposureTime16", None),
        ("Tag9050a", 0x0031, "FlashStatus", "int8u", "None", pc_flash_status),
        ("Tag9050a", 0x003C, "SonyFNumber", "int16u", "SonyFNumber", None),
        ("Tag9050a", 0x004C, "ShutterCount2", "int32u", "Mask24bit", None),
        ("Tag9050a", 0x01A0, None, "int32u", None, None),
        ("Tag9050a", 0x01AA, None, "int32u", None, None),
        ("Tag9050a", 0x01BD, None, "int32u", None, None),

        # ── Tag9050c/d (新一代机型) ──
        ("Tag9050c", 0x0026, "Shutter", "int16u", "ExposureTime16", None),
        ("Tag9050c", 0x0039, "FlashStatus", "int8u", "None", pc_flash_status),
        ("Tag9050c", 0x0048, "SonyFNumber", "int16u", "SonyFNumber", None),
        ("Tag9050c", 0x0066, "SonyExposureTime", "int16u", "ExposureTime16", None),
        ("Tag9050c", 0x0068, "SonyFNumber", "int16u", "SonyFNumber", None),
        ("Tag9050d", 0x000A, "ShutterCount", "int32u", "Mask24bit", None),
        ("Tag9050d", 0x001A, "SonyExposureTime", "int16u", "ExposureTime16", None),
        ("Tag9050d", 0x001C, "SonyFNumber", "int16u", "SonyFNumber", None),
        ("Tag9050d", 0x001F, "ReleaseMode2", "int8u", None, pc_release_mode2),

        # ── Tag9406 / Tag9406b (电池信息) ──
        ("Tag9406", 0x0005, "BatteryTemperature", "int8u", "BatteryTemp", None),
        ("Tag9406", 0x0007, "BatteryLevel", "int8u", "Percentage", None),
        ("Tag9406b", 0x0005, "BatteryLevel", "int8u", "Percentage", None),
        ("Tag9406b", 0x0007, "BatteryLevel2", "int8u", "Percentage", None),

        # ── Tag2010 关键字段 ──
        ("Tag2010f", 0x113C, None, "int16u", "SonyISO", None),
        ("Tag2010g", 0x0344, None, "int16u", "SonyISO", None),
        ("Tag2010g", 0x0237, "PictureProfile", "int8u", None, pc_picture_profile),
        ("Tag2010h", 0x0346, None, "int16u", "SonyISO", None),
        ("Tag2010i", 0x0320, None, "int16u", "SonyISO", None),
    ]

    for table_name, offset, name_override, fmt_override, formula_override, pc_override in corrections:
        if table_name not in sub_tables:
            continue
        st = sub_tables[table_name]
        found = False
        for field in st.fields:
            if field.offset == offset:
                if name_override:
                    field.name = name_override
                if fmt_override:
                    field.format = fmt_override
                if formula_override:
                    field.value_formula = formula_override
                if pc_override is not None:
                    field.print_conv = pc_override
                found = True
                break
        # 如果字段不存在则添加 (用于补齐缺失字段)
        if not found and name_override:
            st.fields.append(FieldDef(
                offset, name_override,
                fmt_override or st.default_format,
                pc_override, formula_override or "None",
            ))
            st.fields.sort(key=lambda f: f.offset)


# ─── 解密表计算 ──────────────────────────────────────────────────────────────

def compute_decipher_table() -> list[int]:
    """计算解密查找表: decipher[encrypted_byte] = original_byte"""
    decipher = list(range(256))  # 249-255 保持不变
    for b in range(249):
        c = pow(b, 3, 249)
        decipher[c] = b
    return decipher


# ─── C# 代码生成 ─────────────────────────────────────────────────────────────

def _cs_byte_array(values: list[int], per_line: int = 16) -> str:
    """生成 C# byte[] 初始化列表"""
    lines = []
    for i in range(0, len(values), per_line):
        chunk = values[i:i + per_line]
        lines.append("            " + ", ".join(f"0x{v:02X}" for v in chunk) + ",")
    return "\n".join(lines)


def _cs_dict_literal(d: dict[int, str], indent: int = 16) -> str:
    """生成 C# Dictionary<int, string> 初始化"""
    pad = " " * indent
    entries = []
    for k, v in sorted(d.items()):
        v_escaped = v.replace('\\', '\\\\').replace('"', '\\"')
        entries.append(f'{pad}[{k}] = "{v_escaped}",')
    return "\n".join(entries)


def _format_to_enum(fmt: str) -> str:
    """将 ExifTool 格式字符串转为 C# 枚举名"""
    mapping = {
        "int8u": "Int8u",
        "int8s": "Int8s",
        "int16u": "Int16u",
        "int16s": "Int16s",
        "int32u": "Int32u",
        "int32s": "Int32s",
        "rational32u": "Rational32u",
    }
    # 处理带数组的格式如 "int8u[5]", "int16u[3]"
    base = fmt.split('[')[0] if '[' in fmt else fmt
    return mapping.get(base, "Int8u")


def _formula_to_enum(formula: Optional[str]) -> str:
    """将公式名转为 C# 枚举名"""
    if formula is None:
        return "None"
    return formula  # 枚举名和 Python 名相同


def generate_cs(decipher: list[int],
                sub_tables: dict[str, SubTable],
                variants: dict[int, list[VariantRule]],
                ref: str) -> str:
    """生成完整的 C# 文件内容"""
    now = datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')
    total_fields = sum(len(st.fields) for st in sub_tables.values())
    total_tables = len(sub_tables)

    out: list[str] = []
    out.append("// <auto-generated>")
    out.append("// 由 Tools/generate-sony-cipher-tags.py 从 ExifTool Sony.pm 自动生成。")
    out.append(f"// ExifTool git ref : {ref}")
    out.append(f"// 生成时间         : {now}")
    out.append(f"// 子表数           : {total_tables}  字段总数: {total_fields}")
    out.append("// 请勿手动编辑，运行脚本以同步最新版本。")
    out.append("// </auto-generated>")
    out.append("")
    out.append("#nullable enable")
    out.append("")
    out.append("using System.Collections.Generic;")
    out.append("")
    out.append("namespace PhotoViewer.Core;")
    out.append("")
    out.append("internal static partial class SonyCipherTags")
    out.append("{")

    # 1. 解密表
    out.append("    /// <summary>解密查找表: DecipherTable[encrypted] = original, 基于 (b³ % 249)</summary>")
    out.append("    private static readonly byte[] DecipherTable =")
    out.append("    {")
    out.append(_cs_byte_array(decipher))
    out.append("    };")
    out.append("")

    # 2. 变体选择规则
    out.append("    /// <summary>")
    out.append("    /// 加密首字节 → 子表变体映射。")
    out.append("    /// Key = 主 tag ID, Value = (加密首字节集合, 子表名) 列表")
    out.append("    /// </summary>")
    out.append("    private static readonly Dictionary<int, (byte[]? FirstBytes, string? ModelPattern, string TableName)[]> Variants = new()")
    out.append("    {")

    for tag_id in sorted(variants.keys()):
        rules = variants[tag_id]
        out.append(f"        [0x{tag_id:04X}] = new (byte[]?, string?, string)[]")
        out.append("        {")
        for rule in rules:
            fb = "null"
            if rule.first_bytes:
                fb = "new byte[] { " + ", ".join(f"0x{b:02X}" for b in rule.first_bytes) + " }"
            mp = "null"
            if rule.model_pattern:
                mp_escaped = rule.model_pattern.replace('\\', '\\\\').replace('"', '\\"')
                mp = f'"{mp_escaped}"'
            out.append(f'            ({fb}, {mp}, "{rule.table_name}"),')
        out.append("        },")

    out.append("    };")
    out.append("")

    # 3. 收集所有 PrintConv 字典 (去重)
    print_conv_map: dict[str, dict[int, str]] = {}
    pc_counter = 0
    field_pc_refs: dict[tuple[str, int], str] = {}  # (table_name, offset) → pc_var_name

    for table_name, st in sorted(sub_tables.items()):
        for field in st.fields:
            if field.print_conv:
                # 检查是否已有相同的 PrintConv
                pc_key = str(sorted(field.print_conv.items()))
                existing = None
                for k, v in print_conv_map.items():
                    if str(sorted(v.items())) == pc_key:
                        existing = k
                        break
                if existing:
                    field_pc_refs[(table_name, field.offset)] = existing
                else:
                    pc_name = f"_pc{pc_counter}"
                    pc_counter += 1
                    print_conv_map[pc_name] = field.print_conv
                    field_pc_refs[(table_name, field.offset)] = pc_name

    # 输出 PrintConv 字典
    if print_conv_map:
        out.append("    // ─── PrintConv 值映射表 ─────────────────────────────────────────")
        out.append("")
        for pc_name, pc_dict in sorted(print_conv_map.items()):
            out.append(f"    private static readonly Dictionary<int, string> {pc_name} = new()")
            out.append("    {")
            out.append(_cs_dict_literal(pc_dict))
            out.append("    };")
            out.append("")

    # 4. 子表字段定义
    out.append("    // ─── 子表字段定义 ────────────────────────────────────────────────")
    out.append("")
    out.append("    /// <summary>")
    out.append("    /// 子表字段定义。")
    out.append("    /// Key = 子表名称, Value = (偏移量, 字段名, 格式, PrintConv引用, 值公式) 列表")
    out.append("    /// </summary>")
    out.append("    private static readonly Dictionary<string, (int Offset, string Name, FieldFormat Format, Dictionary<int, string>? PrintConv, ValueFormula Formula)[]> FieldDefs = new()")
    out.append("    {")

    for table_name in sorted(sub_tables.keys()):
        st = sub_tables[table_name]
        if not st.fields:
            continue
        out.append(f'        ["{table_name}"] = new (int, string, FieldFormat, Dictionary<int, string>?, ValueFormula)[]')
        out.append("        {")
        for field in sorted(st.fields, key=lambda f: f.offset):
            fmt_enum = _format_to_enum(field.format or st.default_format)
            pc_ref = field_pc_refs.get((table_name, field.offset), "null")
            formula_enum = _formula_to_enum(field.value_formula)
            name_escaped = field.name.replace('"', '\\"')
            out.append(f'            (0x{field.offset:04X}, "{name_escaped}", FieldFormat.{fmt_enum}, {pc_ref}, ValueFormula.{formula_enum}),')
        out.append("        },")

    out.append("    };")
    out.append("}")
    out.append("")

    return "\n".join(out)


# ─── 主流程 ──────────────────────────────────────────────────────────────────

def main() -> None:
    parser = ArgumentParser(description="从 ExifTool Sony.pm 生成加密 tag 解析映射")
    parser.add_argument('--ref', default='master',
                        help='ExifTool GitHub 仓库的 git ref（默认: master）')
    args = parser.parse_args()
    ref: str = args.ref

    print(f"ExifTool ref: {ref}")
    print(f"输出路径: {OUTPUT_PATH.relative_to(REPO_ROOT)}\n")

    # 1. 获取源文件
    print("  获取 Sony.pm ...", end=' ', flush=True)
    content = fetch_sony_pm(ref)
    if content is None:
        sys.exit(1)
    print(f"{len(content)} bytes")

    # 2. 解析共享变量
    print("  解析共享变量 ...", end=' ', flush=True)
    shared_vars = parse_shared_vars(content)
    print(f"{len(shared_vars)} 个")

    # 2.5 解析查找表 (LensType 等)
    print("  解析查找表 ...", end=' ', flush=True)
    lookup_tables = parse_lookup_tables(content)
    for name, table in sorted(lookup_tables.items()):
        print(f"\n    {name}: {len(table)} entries", end='')
    print()

    # 3. 解析子表
    print("  解析加密子表 ...", end=' ', flush=True)
    sub_tables = parse_sub_tables(content, shared_vars, lookup_tables)
    total_fields = sum(len(st.fields) for st in sub_tables.values())
    print(f"{len(sub_tables)} 个表, {total_fields} 个字段")
    for name, st in sorted(sub_tables.items()):
        print(f"    {name}: {len(st.fields)} fields (default: {st.default_format})")

    # 4. 解析变体选择
    print("  解析变体选择 ...", end=' ', flush=True)
    variants = parse_variant_dispatch(content, sub_tables)

    # 4.1 补充自动解析遗漏的变体条件 (基于 ExifTool 已知数据)
    apply_variant_corrections(variants)

    # 4.2 修正已知的字段属性 (多变体条目中的格式/公式/名称偏差)
    print("  修正字段属性 ...", end=' ', flush=True)
    apply_field_corrections(sub_tables, lookup_tables)
    print("done")
    print(f"{len(variants)} 个 tag")
    for tag_id, rules in sorted(variants.items()):
        tables = [r.table_name for r in rules]
        print(f"    0x{tag_id:04X}: {', '.join(tables)}")

    # 5. 计算解密表
    print("  计算解密表 ...", end=' ', flush=True)
    decipher = compute_decipher_table()
    print("done")

    # 6. 过滤: 只保留在 variants 中被引用的子表
    referenced_tables = set()
    for rules in variants.values():
        for rule in rules:
            referenced_tables.add(rule.table_name)
    # 也保留其他有意义的表
    for name in list(sub_tables.keys()):
        if name not in referenced_tables and not name.startswith("Tag"):
            del sub_tables[name]

    # 7. 生成 C#
    print("\n  生成 C# 代码 ...", end=' ', flush=True)
    cs_content = generate_cs(decipher, sub_tables, variants, ref)
    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT_PATH.write_text(cs_content, encoding='utf-8')
    print("done")

    print(f"\n✓ 生成完成: {OUTPUT_PATH.relative_to(REPO_ROOT)}")
    print(f"  {len(sub_tables)} 个子表, {total_fields} 个字段定义")


if __name__ == '__main__':
    main()
