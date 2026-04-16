#!/usr/bin/env python3
"""
Tools/generate-exiftool-values.py

从 ExifTool Perl 源码自动生成 tag 值 → 可读名称映射（PrintConv）的 C# 文件。
用于将厂商 Makernote 中 MetadataExtractor 无法解码的数值（如 SonyRawFileType=4）
翻译为人类可读的字符串（如 "Sony Lossless Compressed RAW 2"）。

只提取简单的 int/string → string 哈希映射，跳过：
  - Perl 代码引用（sub { ... }、$val 表达式等）
  - 外部哈希引用（\%someHash）
  - 多层嵌套结构

用法:
    python3 Tools/generate-exiftool-values.py [--ref <git-ref>]

参数:
    --ref   ExifTool GitHub 仓库的 git ref（分支/tag/commit），默认 master

输出:
    PhotoViewer/Core/ExifToolValues.Generated.cs
"""

import re
import sys
import urllib.request
import urllib.error
from datetime import datetime, timezone
from pathlib import Path
from argparse import ArgumentParser
from typing import Optional

EXIFTOOL_RAW = "https://raw.githubusercontent.com/exiftool/exiftool/{ref}/lib/Image/ExifTool/{file}.pm"

# (C# 模块键名, ExifTool .pm 文件名)
MODULES: list[tuple[str, str]] = [
    ("Sony",      "Sony"),
    ("Nikon",     "Nikon"),
    ("Canon",     "Canon"),
    ("Fujifilm",  "FujiFilm"),
    ("Panasonic", "Panasonic"),
    ("Olympus",   "Olympus"),
    ("Pentax",    "Pentax"),
    ("Sigma",     "Sigma"),
    ("Minolta",   "Minolta"),
    ("Samsung",   "Samsung"),
    ("Apple",     "Apple"),
    ("Reconyx",   "Reconyx"),
    ("Ricoh",     "Ricoh"),
    ("Casio",     "Casio"),
    ("Kodak",     "Kodak"),
    ("DJI",       "DJI"),
    ("Kyocera",   "KyoceraRaw"),
    ("Sanyo",     "Sanyo"),
    ("FLIR",      "FLIR"),
    ("Exif",      "Exif"),
]

REPO_ROOT = Path(__file__).parent.parent
OUTPUT_PATH = REPO_ROOT / "PhotoViewer" / "Core" / "ExifToolValues.Generated.cs"

# 匹配 tag ID 行
_TAG_ID_RE = re.compile(r'^\s+(0x[0-9a-fA-F]{1,6}|\b\d{1,5}\b)\s*=>')
# 匹配 Name => 'TagName'
_NAME_RE = re.compile(r'Name\s*=>\s*[\'"]([A-Za-z][A-Za-z0-9_\s\-/]{0,80})[\'"]')
# 匹配 PrintConv => { 开始
_PRINT_CONV_START_RE = re.compile(r'PrintConv\s*=>\s*\{')
# 匹配简单键值对: int => 'string' 或 'string' => 'string'
_SIMPLE_KV_RE = re.compile(
    r"""^\s*
    (?:                             # 键
      (0x[0-9a-fA-F]+)              # 十六进制整数
      | (\d+)                       # 十进制整数
      | '([^']*)'                   # 单引号字符串键
    )
    \s*=>\s*
    '([^']*)'                       # 值（单引号字符串）
    """, re.VERBOSE
)
# 匹配 OTHER => sub { ... }（跳过）
_OTHER_RE = re.compile(r'^\s*OTHER\s*=>')
# 匹配 BITMASK => { ... }（跳过）
_BITMASK_RE = re.compile(r'^\s*BITMASK\s*=>')
# 匹配 PrintConv => \%（外部引用，跳过）
_PRINT_CONV_REF_RE = re.compile(r'PrintConv\s*=>\s*\\')
# 匹配 PrintConv => '...' 或 PrintConv => sub { ... }（代码引用，跳过）
_PRINT_CONV_CODE_RE = re.compile(r"PrintConv\s*=>\s*(?:'|\"|sub\s*\{|\\&)")
# 匹配 PrintConv => [ （数组形式，跳过）
_PRINT_CONV_ARRAY_RE = re.compile(r'PrintConv\s*=>\s*\[')


def fetch_pm(file_name: str, ref: str) -> Optional[str]:
    url = EXIFTOOL_RAW.format(ref=ref, file=file_name)
    try:
        with urllib.request.urlopen(url, timeout=30) as resp:
            return resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        print(f"[SKIP] HTTP {e.code}: {file_name}.pm", file=sys.stderr)
        return None
    except Exception as e:
        print(f"[ERROR] {file_name}.pm: {e}", file=sys.stderr)
        return None


def _parse_tag_id(raw: str) -> Optional[int]:
    """解析十六进制或十进制 tag ID"""
    try:
        return int(raw, 16) if raw.lower().startswith('0x') else int(raw)
    except ValueError:
        return None


def _find_closing_brace(lines: list[str], start: int, max_lines: int = 60) -> int:
    """从 start 行开始找到 PrintConv 哈希的关闭括号位置"""
    depth = 0
    for i in range(start, min(start + max_lines, len(lines))):
        line = lines[i]
        depth += line.count('{') - line.count('}')
        if depth <= 0:
            return i
    return start + max_lines


def extract_print_conv(lines: list[str], start_line: int) -> Optional[dict[str, str]]:
    """
    从 PrintConv => { 所在行开始，提取简单键值对。
    返回 { rawValue: displayString } 映射，跳过无法解析的复杂结构。
    """
    end = _find_closing_brace(lines, start_line)
    result: dict[str, str] = {}

    for i in range(start_line, end + 1):
        line = lines[i]

        # 跳过 OTHER => sub { }
        if _OTHER_RE.match(line):
            continue
        # 跳过 BITMASK => { }
        if _BITMASK_RE.match(line):
            continue

        m = _SIMPLE_KV_RE.match(line)
        if m:
            hex_key, dec_key, str_key, value = m.groups()
            if hex_key:
                key = str(int(hex_key, 16))
            elif dec_key:
                key = dec_key
            elif str_key:
                key = str_key
            else:
                continue

            # 清理注释后缀
            value = value.strip()
            if value:
                result[key] = value

    # 只保留至少有 1 个条目的映射，且过滤掉过大的表（如镜头数据库）
    if len(result) < 1 or len(result) > 200:
        return None

    return result


def extract_all_values(content: str) -> dict[int, tuple[str, dict[str, str]]]:
    """
    从 .pm 文件提取所有 tagId → (tagName, {rawValue → displayString}) 映射。
    只处理顶层 tag 表中的简单 PrintConv 哈希。
    """
    results: dict[int, tuple[str, dict[str, str]]] = {}
    lines = content.split('\n')
    n = len(lines)

    current_tag_id: Optional[int] = None
    current_tag_name: Optional[str] = None
    in_tag_block = False
    block_depth = 0

    i = 0
    while i < n:
        line = lines[i]

        # 检测新的 tag ID 行
        m = _TAG_ID_RE.match(line)
        if m:
            raw_id = m.group(1)
            tag_id = _parse_tag_id(raw_id)
            if tag_id is not None:
                current_tag_id = tag_id
                current_tag_name = None
                rest = line[m.end():].strip()

                # 检查是否是 hash 块开头
                if '{' in rest:
                    in_tag_block = True
                    block_depth = line.count('{') - line.count('}')
                else:
                    in_tag_block = False
                    block_depth = 0

                i += 1
                continue

        # 在 tag 块内查找 Name 和 PrintConv
        if in_tag_block and current_tag_id is not None:
            # 追踪大括号深度
            block_depth += line.count('{') - line.count('}')

            # 查找 Name
            if current_tag_name is None:
                nm = _NAME_RE.search(line)
                if nm:
                    current_tag_name = nm.group(1).strip()

            # 跳过不可解析的 PrintConv 形式
            if (_PRINT_CONV_REF_RE.search(line) or
                _PRINT_CONV_CODE_RE.search(line) or
                _PRINT_CONV_ARRAY_RE.search(line)):
                pass
            elif _PRINT_CONV_START_RE.search(line):
                # 找到可解析的 PrintConv => { ... }
                pc = extract_print_conv(lines, i)
                if pc and current_tag_name:
                    # 不覆盖已有的（保留第一个找到的）
                    if current_tag_id not in results:
                        results[current_tag_id] = (current_tag_name, pc)

            # 块结束
            if block_depth <= 0:
                in_tag_block = False
                current_tag_id = None
                current_tag_name = None

        i += 1

    return results


def _cs_hex(tag_id: int) -> str:
    if tag_id <= 0xFFFF:
        return f"0x{tag_id:04x}"
    return f"0x{tag_id:08x}"


def _escape(s: str) -> str:
    return s.replace('\\', '\\\\').replace('"', '\\"')


def generate_cs(module_values: list[tuple[str, dict[int, tuple[str, dict[str, str]]]]], ref: str) -> str:
    now = datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')
    total_tags = sum(len(v) for _, v in module_values)
    total_entries = sum(
        sum(len(pc) for _, pc in v.values())
        for _, v in module_values
    )

    out: list[str] = [
        "// <auto-generated>",
        "// 由 Tools/generate-exiftool-values.py 从 ExifTool Perl 源码 PrintConv 自动生成。",
        f"// ExifTool git ref : {ref}",
        f"// 生成时间         : {now}",
        f"// 模块数           : {len(module_values)}  Tag 数: {total_tags}  值条目数: {total_entries}",
        "// 请勿手动编辑，运行脚本以同步最新版本。",
        "// </auto-generated>",
        "",
        "using System.Collections.Generic;",
        "",
        "namespace PhotoViewer.Core;",
        "",
        "internal static partial class ExifToolValues",
        "{",
        "    /// <summary>",
        "    /// 自动生成的值映射表。",
        "    /// 结构: 模块名 → tagId → (tagName, rawValue → displayString)",
        "    /// </summary>",
        "    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, (string TagName, IReadOnlyDictionary<string, string> Values)>> _tables =",
        "        new Dictionary<string, IReadOnlyDictionary<int, (string TagName, IReadOnlyDictionary<string, string> Values)>>",
        "        {",
    ]

    for module_name, values in module_values:
        if not values:
            continue
        out.append(f'            ["{_escape(module_name)}"] = new Dictionary<int, (string TagName, IReadOnlyDictionary<string, string> Values)>')
        out.append("            {")
        for tag_id, (tag_name, pc) in sorted(values.items()):
            entries = ", ".join(
                f'["{_escape(k)}"] = "{_escape(v)}"'
                for k, v in sorted(pc.items(), key=lambda x: (x[0].lstrip('-').isdigit(), int(x[0]) if x[0].lstrip('-').isdigit() else 0, x[0]))
            )
            out.append(f'                [{_cs_hex(tag_id)}] = ("{_escape(tag_name)}", new Dictionary<string, string> {{ {entries} }}),')
        out.append("            },")

    out += [
        "        };",
        "}",
        "",
    ]
    return '\n'.join(out)


def main() -> None:
    parser = ArgumentParser(description="从 ExifTool Perl 源码生成 C# tag 值映射文件")
    parser.add_argument('--ref', default='master',
                        help='ExifTool GitHub 仓库的 git ref（默认: master）')
    args = parser.parse_args()
    ref = args.ref

    print(f"ExifTool ref: {ref}")
    print(f"输出路径: {OUTPUT_PATH.relative_to(REPO_ROOT)}\n")

    module_values = []
    for module_name, file_name in MODULES:
        print(f"  Fetching {file_name}.pm ...", end=' ', flush=True)
        content = fetch_pm(file_name, ref)
        if content is None:
            continue
        values = extract_all_values(content)
        module_values.append((module_name, values))
        total_entries = sum(len(pc) for _, pc in values.values())
        print(f"{len(values)} tags, {total_entries} value entries")

    cs_content = generate_cs(module_values, ref)
    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT_PATH.write_text(cs_content, encoding='utf-8')

    total_tags = sum(len(v) for _, v in module_values)
    total_entries = sum(
        sum(len(pc) for _, pc in v.values())
        for _, v in module_values
    )
    print(f"\n✓ 生成完成，共 {len(module_values)} 个模块，{total_tags} 条 tag，{total_entries} 个值条目")


if __name__ == '__main__':
    main()
