#!/usr/bin/env python3
"""扫描 Windows win-x64 MSIX 或单文件 EXE，核对产品身份与双版本发行契约。

用法：
  python Tools/check-windows-package.py <包.msix> --edition standard|ai --output <报告.json>
        [--version 0.9.0] [--build-number 90] [--display-name "PhotoViewer AI"]
        [--package-id cc.xiaojh.PhotoViewer.AI] [--product-id photoviewer.ai]
        [--application-id PhotoViewerAI] [--publisher "CN=xiaojh.cc"] [--require-signature]
  python Tools/check-windows-package.py <发布.exe> --edition standard|ai --output <报告.json>
        [--app-assembly PhotoViewer-0.9.0-win-x64]
  python Tools/check-windows-package.py --self-test

产品身份默认值与根 Directory.Build.props 的 edition 派生映射一致；发布脚本把 MSBuild 求值出的
实际身份显式传进来，扫描器只按实际产物核对，不靠构建参数推断。只做本地文件检查：
不联网、不执行包、不签名。

MSIX：包是 OPC/ZIP。核对 Identity（Name = 规范包 ID、Publisher、四段 Version、
ProcessorArchitecture = x64）、Properties/DisplayName、Application Id/Executable/EntryPoint、
VisualElements 显示名与 logo 资源、文件关联名与扩展名、runFullTrust 能力，并由 Publisher
计算 Package Family Name 与 AUMID，确认标准版与 AI 版不同。载荷侧复用 EXE 的检查：
主程序集 PE/CLI 元数据（程序集名、版本、PhotoViewerEdition / AppBuildNumber /
PhotoViewerPackageId / PhotoViewerProductId / PhotoViewerDisplayName）、嵌入资源、
根目录 pvhevc.dll / resources.pri / VC++ CRT / HEIF 原生库，以及分版推理资产。
AppxBlockMap.xml 用于交叉核对包内文件清单与未压缩长度；AppxSignature.p7x 只按存在性
判定签名（正式分发可加 --require-signature），签名链与哈希校验留给 Windows 安装验收。

单文件 EXE：只支持 .NET bundle v6（实测 SDK 9.0.305 / 10.0.201 与本仓 0.9.0 发布包一致）。
apphost 内带 40 字节 bundle marker：[int64 头部偏移][32 字节签名 8b1202b9…b36aae]。该 32 字节
签名取自本机 SDK 的 Microsoft.NET.HostModel.dll 与 apphost.exe 模板，并在真实发布包中核对过
（均唯一命中，且签名前 8 字节就是 bundle 头部偏移）。布局：
  apphost PE → 各文件数据 → bundle 头部 → 文件条目表 → EOF
头部：+0 主版本 6、+1 次版本、+2..+7 保留、+8 条目数（uint32）、+12 varint(len)+包标识
（.NET 9 为 32 字符、.NET 10 为 12 字符），之后 deps.json 偏移/长度、runtimeconfig.json
偏移/长度、flags（bit0=NetcoreApp3CompatMode，与压缩无关）各 int64，因此头部大小是
53+len(包标识)。压缩只按条目 compressedSize 判断，未压缩的原生格式与关闭压缩的包都要能解析。
定位优先用 marker 的头部偏移直达；marker 缺失时改用「尾部条目链倒推 + 头部反查」。两条路径
都要求条目表正向解析到文件末尾，并与头部条目数、deps/runtimeconfig 偏移长度、压缩 flag 交叉
核对。marker 存在但指向的头部无效按硬错误处理（真实 host 同样只认 marker）。

条目布局：偏移 int64、未压缩长度 int64、压缩后长度 int64、类型 uint8、
路径长度（7-bit 编码）+ UTF-8 路径。压缩后长度为 0 表示原样存储，否则为 raw deflate。
扫描逐一校验：长度与解压结果一致、数据不越界/不重叠、路径为安全相对路径、路径不重复，
并计算逐文件未压缩/压缩大小与 SHA-256。

EXE 身份：必须是 x64 PE32+；读取其 PE 版本资源（ProductName / FileVersion / ProductVersion /
OriginalFilename）与 deps.json 推导出的真实主程序集；再解析该程序集的 PE/CLI 元数据：
Assembly 表（程序集名 + 版本）、ManifestResource 表（嵌入资源名、字节数、SHA-256）、
AssemblyMetadata（PhotoViewerEdition、AppBuildNumber、PhotoViewerPackageId、
PhotoViewerProductId、PhotoViewerDisplayName，缺任何一项都是硬错误）。
EXE 文件名可任意改名（含中文与空格），不参与身份判定，只记录实际文件名。
主程序集名只能是 PhotoViewer[-AI]-<version>-win-x64.dll。
版本号与构建号缺省取根 Directory.Build.props，也可显式传入。

standard：不得包含 .onnx 文件或嵌入资源、ONNX Runtime（托管/原生）、DirectML、MathNet 数值库、
          DINOv3 许可证；嵌入资源按元数据表逐条核对字节与哈希，不做文件名扫描。
ai：      必须包含 Microsoft.ML.OnnxRuntime.dll、MathNet.Numerics.dll、onnxruntime.dll、
          DirectML.dll、DINOv3-LICENSE.md，以及唯一模型 dinov3_vits16.onnx
          （86582598 bytes，SHA-256 b1579b92…，ModelId dinov3_vits16_f32_518_v2）。
          模型可在主程序集的 ManifestResource
          （LogicalName PhotoViewer.Windows.NativeAssets.Assets.Models.dinov3_vits16.onnx），
          也可为包内文件 Assets/Models/dinov3_vits16.onnx；两处同时出现视为重复模型。
两种形态都需要：根目录唯一一份 pvhevc.dll（不得再有 NativeLibs/win-x64 副本）、根目录
resources.pri、四个 VC++ CRT、HEIF 解码原生库与 pvhevc 许可说明；EXE 另需自包含
net10.0/win-x64 的 deps.json / runtimeconfig.json；缺少应用资源包 <主程序集>.pri 时只告警。
两版都拒绝开发期文件混入：NativeSrc/、NativeLibs/、AGENTS.md、App.xaml、app.manifest、
*.ps1、PortableSingleFile.targets、项目文件与源码扩展名，也拒绝重新引入 WPF/WinForms 桌面运行时。

报告固定写入 format / edition / expectations / identity / embeddedResources / modelContract /
summary / files / errors / warnings / passed；EXE 另含 exePath / exe / bundle，MSIX 另含
packagePath / package / msix。summary 按分类给出文件数、字节与聚合 SHA-256。
检查失败时仍写报告并以 1 退出。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
import zlib
from pathlib import Path

# 产品显示名（AI 版含空格）：EXE 版本资源 ProductName / FileDescription 与窗口标题同值。
PRODUCT_NAMES = {"standard": "PhotoViewer", "ai": "PhotoViewer AI"}
# 打包命名（程序集名、EXE 文件名）仍用横线。
PACKAGE_NAMES = {"standard": "PhotoViewer", "ai": "PhotoViewer-AI"}
# 全平台规范包 ID（与根 Directory.Build.props 的派生映射一致；扫描默认值，可用参数覆盖）。
PACKAGE_IDS = {"standard": "cc.xiaojh.PhotoViewer", "ai": "cc.xiaojh.PhotoViewer.AI"}
# 内部产品键（文件关联名与数据归属的机器标识）。
PRODUCT_IDS = {"standard": "photoviewer.standard", "ai": "photoviewer.ai"}
# Windows Application Id：AUMID = PackageFamilyName + "!" + ApplicationId。
APPLICATION_IDS = {"standard": "PhotoViewer", "ai": "PhotoViewerAI"}
DEFAULT_PUBLISHER = "CN=xiaojh.cc"
AI_PACKAGE_ID_SUFFIX = ".AI"

# MSIX 结构常量。
MSIX_MANIFEST = "AppxManifest.xml"
MSIX_BLOCKMAP = "AppxBlockMap.xml"
MSIX_SIGNATURE = "AppxSignature.p7x"
MSIX_CONTENT_TYPES = "[Content_Types].xml"
MSIX_STRUCTURAL_FILES = (MSIX_BLOCKMAP, MSIX_SIGNATURE, MSIX_CONTENT_TYPES)
MSIX_LOGOS = (
    "Assets/StoreLogo.png",
    "Assets/Square150x150Logo.png",
    "Assets/Square44x44Logo.png",
    "Assets/Square71x71Logo.png",
    "Assets/Wide310x150Logo.png",
)
MSIX_ENTRY_POINT = "Windows.FullTrustApplication"
UAP10_NAMESPACE = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
# 与 WindowsFilePicker 的通用图片扩展名一致：MSIX 文件关联必须覆盖这些类型。
FILE_ASSOCIATION_EXTENSIONS = (
    ".jpg", ".jpeg", ".png", ".heic", ".heif", ".hif", ".avif", ".tif", ".tiff",
    ".webp", ".bmp", ".gif", ".arw", ".cr2", ".cr3", ".nef", ".nrw", ".raf", ".rw2",
    ".orf", ".dng", ".pef", ".srw",
)

MODEL_NAME = "dinov3_vits16.onnx"
MODEL_BYTES = 86_582_598
MODEL_SHA256 = "b1579b92faa070106c613a6a502abe41d507588cab236727396bb2a2505cfc01"
MODEL_ID = "dinov3_vits16_f32_518_v2"
DINO_LICENSE_NAME = "dinov3-license.md"
DINO_LICENSE_DISPLAY = "DINOv3-LICENSE.md"

ONNX_MANAGED = "Microsoft.ML.OnnxRuntime.dll"
MATHNET_MANAGED = "MathNet.Numerics.dll"
ONNX_NATIVE = "onnxruntime.dll"
DIRECTML_NATIVE = "DirectML.dll"

PVHEVC = "pvhevc.dll"
PVHEVC_LICENSE = "pvhevc-FFmpeg-LICENSE.txt"
PVHEVC_NOTE = "pvhevc.txt"
APP_PRI_FALLBACK = "resources.pri"
CRT_FILES = ("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll")
HEIF_NATIVE = "libheif.dll"
HEIF_PLUGINS = ("libde265.dll", "libx265.dll", "aom.dll")

# .NET apphost 的 bundle marker：[int64 头部偏移][32 字节签名]（共 40 字节）。
# 32 字节签名取自本机 SDK 的 Microsoft.NET.HostModel.dll、apphost.exe 模板与真实发布包，
# 三者均唯一命中；真实包中签名前 8 字节即 bundle 头部偏移。
BUNDLE_SIGNATURE = bytes.fromhex(
    "8b1202b96a612038727b930214d7a03213f5b9e6efae3318"
    "ee3b2dce24b36aae")
MARKER_BYTES = 8 + len(BUNDLE_SIGNATURE)
MARKER_SEARCH_LIMIT = 64 << 20
MAX_HEADER_BYTES = 512
MARKER_CANDIDATE_LIMIT = 8
BUNDLE_MAJOR_VERSIONS = (6,)
BUNDLE_TYPE_NAMES = {
    0: "content",
    1: "assembly",
    2: "native",
    3: "deps-json",
    4: "runtimeconfig-json",
    5: "symbols",
}
BUNDLE_HEADER_TRAILER = 40
# bundle 头 flags bit0 是 NetcoreApp3CompatMode（IncludeAllContentForSelfExtract），
# 与压缩无关：压缩只由各条目 compressedSize 是否为 0 决定。
NETCOREAPP3_COMPAT_FLAG = 0x1

PE_MACHINE_X64 = 0x8664
PE_MACHINE_X86 = 0x014C

TAIL_WINDOW_INITIAL = 1 << 20
TAIL_WINDOW_MAX = 64 << 20
HEAD_SEARCH_SPAN = 4096
SHORT_PATH_LIMIT = 300
MODEL_LOOKS_LIKE_LIMIT = 256 * 1024
COPY_CHUNK = 1 << 20


class ScanError(Exception):
    """无法继续扫描的结构性错误。"""


# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------

def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def human(size: int) -> str:
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024
    return f"{size} B"


def read_root_props() -> dict:
    """读取根 Directory.Build.props 的 AppVersion / AppBuildNumber。"""
    props = Path(__file__).resolve().parent.parent / "Directory.Build.props"
    values: dict = {}
    try:
        text = props.read_text(encoding="utf-8")
    except OSError:
        return values
    for key in ("AppVersion", "AppBuildNumber"):
        match = re.search(rf"<{key}>([^<]+)</{key}>", text)
        if match:
            values[key] = match.group(1).strip()
    return values


def read_identity_props() -> dict:
    """读取根 Directory.Build.props 的产品身份映射（扫描器默认值的来源）。"""
    props = Path(__file__).resolve().parent.parent / "Directory.Build.props"
    result: dict[str, dict[str, str | None]] = {}
    try:
        root = ET.parse(props).getroot()
    except (OSError, ET.ParseError):
        return result
    identity_group = None
    for group in root.findall("PropertyGroup"):
        condition = group.get("Condition") or ""
        if "'$(PhotoViewerEdition)' == 'standard'" in condition:
            identity_group = group
            break
    if identity_group is None:
        return result
    names = {
        "packageId": "PhotoViewerPackageId",
        "productId": "PhotoViewerProductId",
        "displayName": "PhotoViewerDisplayName",
        "distributionName": "PhotoViewerDistributionName",
        "enableDino": "PhotoViewerEnableDino",
    }
    for edition in ("standard", "ai"):
        values: dict[str, str | None] = {}
        for key, element_name in names.items():
            found = None
            for element in identity_group.findall(element_name):
                condition = element.get("Condition") or ""
                if condition and f"'{edition}'" not in condition:
                    continue
                found = (element.text or "").strip().replace(
                    "$(PhotoViewerEdition)", edition)
                break
            values[key] = found or None
        result[edition] = values
    return result


def publisher_id(publisher: str) -> str:
    """按 MSIX 规则把 Publisher 归约为 Package Family Name 的 13 字符发布者 ID。

    取 Publisher 字符串 UTF-16LE 编码的 SHA-256 前 8 字节，右侧补一个 0 位后按
    Crockford base32（"0123456789ABCDEFGHJKMNPQRSTVWXYZ"）从高位分 13 组编码。
    """
    digest = hashlib.sha256(publisher.encode("utf-16-le")).digest()[:8]
    alphabet = "0123456789abcdefghjkmnpqrstvwxyz"
    value = int.from_bytes(digest, "big") << 1
    return "".join(alphabet[(value >> (5 * index)) & 0x1F] for index in range(12, -1, -1))


def expected_sibling_package_id(package_id: str) -> str:
    """另一产品的规范包 ID：标准版无后缀，AI 版固定 .AI。"""
    if package_id.endswith(AI_PACKAGE_ID_SUFFIX):
        return package_id[: -len(AI_PACKAGE_ID_SUFFIX)]
    return package_id + AI_PACKAGE_ID_SUFFIX


class PackageFile:
    """MSIX 载荷文件在契约检查里的最小视图（与 bundle 条目同名接口）。"""

    __slots__ = ("path", "name")

    def __init__(self, path: str):
        self.path = path
        self.name = path.rsplit("/", 1)[-1]


def xml_local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def xml_children(element, name: str) -> list:
    return [child for child in list(element) if xml_local(child.tag) == name]


def xml_child(element, name: str):
    matches = xml_children(element, name)
    return matches[0] if matches else None


def read_at(data: bytes, offset: int, size: int, label: str) -> bytes:
    if offset < 0 or size < 0 or offset + size > len(data):
        raise ScanError(f"{label}：读取越界（offset={offset} size={size} total={len(data)}）")
    return data[offset:offset + size]


def encode_varint(value: int) -> bytes:
    out = bytearray()
    while True:
        chunk = value & 0x7F
        value >>= 7
        if value:
            out.append(chunk | 0x80)
        else:
            out.append(chunk)
            return bytes(out)


def decode_varint(data: bytes, offset: int, limit: int, label: str) -> tuple[int, int]:
    value = 0
    shift = 0
    pos = offset
    while True:
        if pos >= limit:
            raise ScanError(f"{label}：长度前缀越界")
        byte = data[pos]
        value |= (byte & 0x7F) << shift
        shift += 7
        pos += 1
        if not byte & 0x80:
            return value, pos
        if shift > 28:
            raise ScanError(f"{label}：长度前缀过长")


def validate_bundle_path(path: str) -> str | None:
    """返回错误说明；None 表示路径安全可用。"""
    if not path:
        return "路径为空"
    if len(path) > 4096:
        return "路径过长"
    if path.startswith("/") or path.startswith("\\"):
        return "不允许绝对路径"
    if "\\" in path:
        return "必须使用 / 分隔且不得含反斜杠"
    if ":" in path:
        return "不允许盘符或备用数据流"
    if any(ord(ch) < 0x20 for ch in path):
        return "含控制字符"
    if any(ch in '<>"|?*' for ch in path):
        return "含 Windows 非法字符"
    parts = path.split("/")
    for part in parts:
        if part in ("", ".", ".."):
            return "含空段、. 或 .."
        if part.endswith((" ", ".")):
            return "段尾为空格或点"
    return None


# ---------------------------------------------------------------------------
# PE 与版本资源
# ---------------------------------------------------------------------------

class PeImage:
    """按需读取的 PE 头解析结果（读文件或读内存切片均可）。"""

    def __init__(self, data: bytes, label: str):
        self.data = data
        self.label = label
        if len(data) < 0x40 or data[:2] != b"MZ":
            raise ScanError(f"{label}：不是 PE 文件（缺少 MZ 头）")
        e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
        if e_lfanew < 0x40 or e_lfanew + 24 > len(data):
            raise ScanError(f"{label}：e_lfanew 越界（{e_lfanew:#x}）")
        if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            raise ScanError(f"{label}：缺少 PE 签名")
        coff = e_lfanew + 4
        (machine, section_count, _ts, _sym, _nsym, opt_size,
         _chars) = struct.unpack_from("<HHIIIHH", data, coff)
        opt = coff + 20
        if opt + opt_size > len(data) or opt_size < 2:
            raise ScanError(f"{label}：可选头越界")
        magic = struct.unpack_from("<H", data, opt)[0]
        if magic == 0x20B:
            pe32_plus = True
            dir_base = opt + 112
        elif magic == 0x10B:
            pe32_plus = False
            dir_base = opt + 96
        else:
            raise ScanError(f"{label}：未知可选头 magic {magic:#x}")
        dir_count = 0
        if (108 if pe32_plus else 92) + 4 <= opt_size:
            dir_count = struct.unpack_from(
                "<I", data, opt + (108 if pe32_plus else 92))[0]
        directories = []
        for index in range(min(dir_count, 16)):
            pos = dir_base + index * 8
            if pos + 8 > len(data):
                break
            directories.append(struct.unpack_from("<II", data, pos))
        while len(directories) < 16:
            directories.append((0, 0))
        section_table = opt + opt_size
        sections = []
        for index in range(section_count):
            pos = section_table + index * 40
            if pos + 40 > len(data):
                raise ScanError(f"{label}：节表越界")
            name = data[pos:pos + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, va, raw_size, raw_ptr = struct.unpack_from("<IIII", data, pos + 8)
            sections.append({"name": name, "va": va, "vsize": vsize,
                             "rawSize": raw_size, "rawOffset": raw_ptr})
        self.machine = machine
        self.pe32_plus = pe32_plus
        self.sections = sections
        self.directories = directories
        self.header_end = section_table + section_count * 40
        self.image_end = max(
            [item["rawOffset"] + item["rawSize"] for item in sections],
            default=self.header_end,
        )

    def rva_to_offset(self, rva: int) -> int:
        for section in self.sections:
            span = max(section["vsize"], section["rawSize"])
            if section["va"] <= rva < section["va"] + span:
                return section["rawOffset"] + (rva - section["va"])
        raise ScanError(f"{self.label}：RVA {rva:#x} 不属于任何节")

    def version_info(self) -> dict | None:
        rsrc_rva, _rsrc_size = self.directories[2]
        if not rsrc_rva:
            return None
        base = self.rva_to_offset(rsrc_rva)
        blob = self._find_version_blob(base)
        if blob is None:
            return None
        return decode_version_blob(blob, self.label)

    def _find_version_blob(self, base: int) -> bytes | None:
        data = self.data

        def entries(offset: int):
            pos = base + offset
            if pos + 16 > len(data):
                raise ScanError(f"{self.label}：资源目录越界")
            named, ids = struct.unpack_from("<HH", data, pos + 12)
            out = []
            for index in range(named + ids):
                item = pos + 16 + index * 8
                if item + 8 > len(data):
                    raise ScanError(f"{self.label}：资源目录项越界")
                name, target = struct.unpack_from("<II", data, item)
                out.append((name, target))
            return out

        try:
            for name, target in entries(0):
                if name & 0x80000000 or name != 16:
                    continue
                if not target & 0x80000000:
                    continue
                for _name2, target2 in entries(target & 0x7FFFFFFF):
                    if not target2 & 0x80000000:
                        continue
                    for _name3, target3 in entries(target2 & 0x7FFFFFFF):
                        if target3 & 0x80000000:
                            continue
                        entry = base + target3
                        if entry + 16 > len(data):
                            raise ScanError(f"{self.label}：资源数据项越界")
                        rva, size, _cp = struct.unpack_from("<III", data, entry)
                        offset = self.rva_to_offset(rva)
                        return read_at(data, offset, size, self.label)
        except ScanError:
            return None
        return None


def _align4(value: int) -> int:
    return (value + 3) & ~3


def _version_block(blob: bytes, pos: int, label: str) -> tuple[str, int, int, int, int]:
    if pos + 6 > len(blob):
        raise ScanError(f"{label}：版本资源块越界")
    length, value_length, vtype = struct.unpack_from("<HHH", blob, pos)
    cursor = pos + 6
    key = []
    while True:
        if cursor + 2 > len(blob):
            raise ScanError(f"{label}：版本资源键越界")
        char = struct.unpack_from("<H", blob, cursor)[0]
        cursor += 2
        if char == 0:
            break
        key.append(chr(char))
    end = pos + length
    if length <= 0 or end > len(blob):
        raise ScanError(f"{label}：版本资源块长度非法（{length}）")
    return "".join(key), value_length, vtype, cursor, end


def decode_version_blob(blob: bytes, label: str) -> dict:
    info: dict = {"strings": {}, "fixed": {}}
    key, _value_length, _vtype, cursor, end = _version_block(blob, 0, label)
    info["rootKey"] = key
    cursor = _align4(cursor)
    if blob[cursor:cursor + 4] == b"\xbd\x04\xef\xfe":
        (_signature, _struct_version, file_ms, file_ls,
         product_ms, product_ls) = struct.unpack_from("<IIIIII", blob, cursor)
        info["fixed"] = {
            "fileVersion": f"{file_ms >> 16}.{file_ms & 0xFFFF}.{file_ls >> 16}.{file_ls & 0xFFFF}",
            "productVersion": (f"{product_ms >> 16}.{product_ms & 0xFFFF}."
                               f"{product_ls >> 16}.{product_ls & 0xFFFF}"),
        }
        cursor += 52

    def walk(pos: int, limit: int, depth: int) -> None:
        while pos + 6 <= limit:
            name, value_length, vtype, value_pos, block_end = _version_block(blob, pos, label)
            if vtype == 1 and depth < 3:
                walk(_align4(value_pos), block_end, depth + 1)
            elif depth == 3:
                start = _align4(value_pos)
                raw = blob[start:start + value_length * 2]
                info["strings"][name] = raw.decode("utf-16-le", "replace").rstrip("\0")
            if block_end <= pos:
                break
            pos = _align4(block_end)

    walk(cursor, end, 1)
    return info


# ---------------------------------------------------------------------------
# 托管 PE 元数据（Assembly 表 / ManifestResource 表 / AssemblyMetadata）
# ---------------------------------------------------------------------------

CODED_INDEXES = {
    "TypeDefOrRef": (2, (2, 1, 0x1B)),
    "HasConstant": (2, (4, 8, 0x17)),
    "HasCustomAttribute": (5, (6, 4, 1, 2, 8, 9, 0x0A, 0, 0x0E, 0x17, 0x14, 0x11,
                                 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2B, 0x2C)),
    "HasFieldMarshal": (1, (4, 8)),
    "HasDeclSecurity": (2, (2, 6, 0x20)),
    "MemberRefParent": (3, (2, 1, 0x1A, 6, 0x1B)),
    "HasSemantics": (1, (0x14, 0x17)),
    "MethodDefOrRef": (1, (6, 0x0A)),
    "MemberForwarded": (1, (4, 6)),
    "Implementation": (2, (0x26, 0x23, 0x27)),
    "CustomAttributeType": (3, (None, None, 6, 0x0A, None)),
    "ResolutionScope": (2, (0, 0x1A, 0x23, 1)),
    "TypeOrMethodDef": (1, (2, 6)),
}

TABLE_SCHEMAS = {
    0x00: (("Generation", "u2"), ("Name", "str"), ("Mvid", "guid"),
           ("EncId", "guid"), ("EncBaseId", "guid")),
    0x01: (("ResolutionScope", "c:ResolutionScope"), ("Name", "str"), ("Namespace", "str")),
    0x02: (("Flags", "u4"), ("Name", "str"), ("Namespace", "str"),
           ("Extends", "c:TypeDefOrRef"), ("FieldList", "t:0x04"), ("MethodList", "t:0x06")),
    0x03: (("Field", "t:0x04"),),
    0x04: (("Flags", "u2"), ("Name", "str"), ("Signature", "blob")),
    0x05: (("Method", "t:0x06"),),
    0x06: (("RVA", "u4"), ("ImplFlags", "u2"), ("Flags", "u2"), ("Name", "str"),
           ("Signature", "blob"), ("ParamList", "t:0x08")),
    0x07: (("Param", "t:0x08"),),
    0x08: (("Flags", "u2"), ("Sequence", "u2"), ("Name", "str")),
    0x09: (("Class", "t:0x02"), ("Interface", "c:TypeDefOrRef")),
    0x0A: (("Class", "c:MemberRefParent"), ("Name", "str"), ("Signature", "blob")),
    0x0B: (("Type", "u1"), ("Padding", "u1"), ("Parent", "c:HasConstant"), ("Value", "blob")),
    0x0C: (("Parent", "c:HasCustomAttribute"), ("Type", "c:CustomAttributeType"),
           ("Value", "blob")),
    0x0D: (("Parent", "c:HasFieldMarshal"), ("NativeType", "blob")),
    0x0E: (("Action", "u2"), ("Parent", "c:HasDeclSecurity"), ("PermissionSet", "blob")),
    0x0F: (("PackingSize", "u2"), ("ClassSize", "u4"), ("Parent", "t:0x02")),
    0x10: (("Offset", "u4"), ("Field", "t:0x04")),
    0x11: (("Signature", "blob"),),
    0x12: (("Parent", "t:0x02"), ("EventList", "t:0x14")),
    0x13: (("Event", "t:0x14"),),
    0x14: (("EventFlags", "u2"), ("Name", "str"), ("EventType", "c:TypeDefOrRef")),
    0x15: (("Parent", "t:0x02"), ("PropertyList", "t:0x17")),
    0x16: (("Property", "t:0x17"),),
    0x17: (("Flags", "u2"), ("Name", "str"), ("Type", "blob")),
    0x18: (("Semantics", "u2"), ("Method", "t:0x06"), ("Association", "c:HasSemantics")),
    0x19: (("Class", "t:0x02"), ("MethodBody", "c:MethodDefOrRef"),
           ("MethodDeclaration", "c:MethodDefOrRef")),
    0x1A: (("Name", "str"),),
    0x1B: (("Signature", "blob"),),
    0x1C: (("MappingFlags", "u2"), ("MemberForwarded", "c:MemberForwarded"),
           ("ImportName", "str"), ("ImportScope", "t:0x1A")),
    0x1D: (("RVA", "u4"), ("Field", "t:0x04")),
    0x1E: (("Token", "u4"), ("FuncCode", "u4")),
    0x1F: (("Token", "u4"),),
    0x20: (("HashAlgId", "u4"), ("MajorVersion", "u2"), ("MinorVersion", "u2"),
           ("BuildNumber", "u2"), ("RevisionNumber", "u2"), ("Flags", "u4"),
           ("PublicKey", "blob"), ("Name", "str"), ("Culture", "str")),
    0x21: (("Processor", "u4"),),
    0x22: (("OSPlatformId", "u4"), ("OSMajorVersion", "u4"), ("OSMinorVersion", "u4")),
    0x23: (("MajorVersion", "u2"), ("MinorVersion", "u2"), ("BuildNumber", "u2"),
           ("RevisionNumber", "u2"), ("Flags", "u4"), ("PublicKeyOrToken", "blob"),
           ("Name", "str"), ("Culture", "str"), ("HashValue", "blob")),
    0x24: (("Processor", "u4"), ("AssemblyRef", "t:0x23")),
    0x25: (("OSPlatformId", "u4"), ("OSMajorVersion", "u4"), ("OSMinorVersion", "u4"),
           ("AssemblyRef", "t:0x23")),
    0x26: (("Flags", "u4"), ("Name", "str"), ("HashValue", "blob")),
    0x27: (("Flags", "u4"), ("TypeDefId", "u4"), ("TypeName", "str"),
           ("TypeNamespace", "str"), ("Implementation", "c:Implementation")),
    0x28: (("Offset", "u4"), ("Flags", "u4"), ("Name", "str"),
           ("Implementation", "c:Implementation")),
    0x29: (("NestedClass", "t:0x02"), ("EnclosingClass", "t:0x02")),
    0x2A: (("Number", "u2"), ("Flags", "u2"), ("Owner", "c:TypeOrMethodDef"),
           ("Name", "str")),
    0x2B: (("Method", "c:MethodDefOrRef"), ("Instantiation", "blob")),
    0x2C: (("Owner", "t:0x2A"), ("Constraint", "c:TypeDefOrRef")),
}

SIMPLE_SIZES = {"u1": 1, "u2": 2, "u4": 4}


class ManagedAssembly:
    """主程序集的 PE + CLI 元数据读取器（不依赖 System.Reflection）。"""

    def __init__(self, data: bytes, label: str):
        self.data = data
        self.label = label
        self.pe = PeImage(data, label)
        cli_rva, cli_size = self.pe.directories[14]
        if not cli_rva or cli_size < 72:
            raise ScanError(f"{label}：不是托管程序集（缺少 CLI 头）")
        self.cli_offset = self.pe.rva_to_offset(cli_rva)
        header = read_at(data, self.cli_offset, 72, label)
        (self.runtime_major, self.runtime_minor, md_rva, md_size,
         flags, _entry, res_rva, res_size) = struct.unpack_from("<HHIIIIII", header, 4)
        if not md_rva:
            raise ScanError(f"{label}：缺少元数据目录")
        self.metadata_rva = md_rva
        self.metadata_size = md_size
        self.flags = flags
        self.resources_rva = res_rva
        self.resources_size = res_size
        self.metadata_offset = self.pe.rva_to_offset(md_rva)
        self.resources_offset = self.pe.rva_to_offset(res_rva) if res_rva else 0
        self._read_root()
        self._read_tables()

    # -- 元数据根与流 --
    def _read_root(self) -> None:
        data = self.data
        base = self.metadata_offset
        if base + 32 > len(data):
            raise ScanError(f"{self.label}：元数据根越界")
        if struct.unpack_from("<I", data, base)[0] != 0x424A5342:
            raise ScanError(f"{self.label}：元数据缺少 BSJB 签名")
        version_length = struct.unpack_from("<I", data, base + 12)[0]
        cursor = _align4(base + 16 + version_length)
        if cursor + 4 > len(data):
            raise ScanError(f"{self.label}：元数据流头越界")
        _flags, count = struct.unpack_from("<HH", data, cursor)
        cursor += 4
        self.streams: dict[str, tuple[int, int]] = {}
        for _ in range(count):
            if cursor + 8 > len(data):
                raise ScanError(f"{self.label}：元数据流表越界")
            offset, size = struct.unpack_from("<II", data, cursor)
            cursor += 8
            name_end = cursor
            while name_end < len(data) and data[name_end] != 0:
                name_end += 1
            if name_end >= len(data):
                raise ScanError(f"{self.label}：元数据流名越界")
            name = data[cursor:name_end].decode("ascii", "replace")
            cursor = _align4(name_end + 1)
            self.streams[name] = (base + offset, size)

    def _read_tables(self) -> None:
        location = self.streams.get("#~") or self.streams.get("#-")
        if location is None:
            raise ScanError(f"{self.label}：缺少 #~ 表流")
        offset, size = location
        data = self.data
        if offset + 24 > len(data):
            raise ScanError(f"{self.label}：表流头越界")
        heap_sizes = data[offset + 6]
        self.str4 = bool(heap_sizes & 1)
        self.guid4 = bool(heap_sizes & 2)
        self.blob4 = bool(heap_sizes & 4)
        valid, _sorted = struct.unpack_from("<QQ", data, offset + 8)
        cursor = offset + 24
        self.index_rows: dict[int, int] = {}
        for table in range(64):
            if valid >> table & 1:
                if cursor + 4 > len(data):
                    raise ScanError(f"{self.label}：表行数越界")
                self.index_rows[table] = struct.unpack_from("<I", data, cursor)[0]
                cursor += 4
        sizes: dict[int, int] = {}
        for table in sorted(self.index_rows):
            total = 0
            for _name, kind in TABLE_SCHEMAS[table]:
                total += self._column_size(kind)
            sizes[table] = total
        self.row_size = sizes
        self.table_data: dict[int, tuple[int, int]] = {}
        for table in sorted(self.index_rows):
            self.table_data[table] = (cursor, sizes[table])
            cursor += sizes[table] * self.index_rows[table]
        self.tables_end = cursor
        if self.tables_end > offset + size:
            raise ScanError(f"{self.label}：表流长度与行数不一致")
        # 常用表引用
        self.type_ref_rows: list[dict] = []
        if 0x01 in self.index_rows:
            for index, pos in self._rows(0x01):
                row = self._row(0x01, pos)
                self.type_ref_rows.append({
                    "rid": index,
                    "name": self.strings(row["Name"]),
                    "namespace": self.strings(row["Namespace"]),
                    "scope": self._decode_coded("ResolutionScope", row["ResolutionScope"]),
                })
        self.member_ref_rows: list[dict] = []
        if 0x0A in self.index_rows:
            for index, pos in self._rows(0x0A):
                row = self._row(0x0A, pos)
                self.member_ref_rows.append({
                    "rid": index,
                    "name": self.strings(row["Name"]),
                    "parent": self._decode_coded("MemberRefParent", row["Class"]),
                })

    def _column_size(self, kind: str) -> int:
        if kind in SIMPLE_SIZES:
            return SIMPLE_SIZES[kind]
        if kind == "str":
            return 4 if self.str4 else 2
        if kind == "guid":
            return 4 if self.guid4 else 2
        if kind == "blob":
            return 4 if self.blob4 else 2
        if kind.startswith("t:"):
            target = int(kind[2:], 16)
            return 4 if self.index_rows.get(target, 0) > 0xFFFF else 2
        if kind.startswith("c:"):
            bits, referenced = CODED_INDEXES[kind[2:]]
            largest = max((self.index_rows.get(t, 0) for t in referenced if t is not None),
                          default=0)
            return 4 if largest >= (1 << (16 - bits)) else 2
        raise ScanError(f"未知列类型 {kind}")

    def _rows(self, table: int):
        if table not in self.index_rows:
            return
        start, size = self.table_data[table]
        for index in range(self.index_rows[table]):
            yield index + 1, start + index * size

    def _row(self, table: int, pos: int) -> dict:
        data = self.data
        row: dict = {}
        cursor = pos
        for name, kind in TABLE_SCHEMAS[table]:
            if kind == "u1":
                row[name] = data[cursor]
                cursor += 1
            elif kind == "u2":
                row[name] = struct.unpack_from("<H", data, cursor)[0]
                cursor += 2
            elif kind == "u4":
                row[name] = struct.unpack_from("<I", data, cursor)[0]
                cursor += 4
            elif kind == "str":
                width = 4 if self.str4 else 2
                row[name] = struct.unpack_from("<I" if width == 4 else "<H", data, cursor)[0]
                cursor += width
            elif kind == "guid":
                width = 4 if self.guid4 else 2
                row[name] = struct.unpack_from("<I" if width == 4 else "<H", data, cursor)[0]
                cursor += width
            elif kind == "blob":
                width = 4 if self.blob4 else 2
                row[name] = struct.unpack_from("<I" if width == 4 else "<H", data, cursor)[0]
                cursor += width
            else:
                width = self._column_size(kind)
                row[name] = struct.unpack_from("<I" if width == 4 else "<H", data, cursor)[0]
                cursor += width
        return row

    def _decode_coded(self, coded: str, value: int):
        bits, referenced = CODED_INDEXES[coded]
        tag = value & ((1 << bits) - 1)
        rid = value >> bits
        if tag >= len(referenced) or referenced[tag] is None or rid == 0:
            return None
        return {"table": referenced[tag], "rid": rid}

    # -- 堆 --
    def strings(self, index: int) -> str:
        if not index:
            return ""
        offset, size = self.streams["#Strings"]
        start = offset + index
        end = self.data.find(b"\0", start, offset + size)
        if end < 0:
            end = offset + size
        return self.data[start:end].decode("utf-8", "replace")

    def blobs(self, index: int) -> bytes:
        if not index:
            return b""
        offset, size = self.streams["#Blob"]
        pos = offset + index
        if pos >= offset + size:
            raise ScanError(f"{self.label}：Blob 索引越界（{index}）")
        first = self.data[pos]
        if first & 0x80 == 0:
            length, pos = first, pos + 1
        elif first & 0xC0 == 0x80:
            length, pos = ((first & 0x3F) << 8) | self.data[pos + 1], pos + 2
        else:
            length = (((first & 0x1F) << 24) | (self.data[pos + 1] << 16)
                      | (self.data[pos + 2] << 8) | self.data[pos + 3])
            pos += 4
        return read_at(self.data, pos, length, f"{self.label}：#Blob")

    # -- 语义 --
    def assembly(self) -> dict | None:
        if not self.index_rows.get(0x20):
            return None
        row = self._row(0x20, self.table_data[0x20][0])
        return {
            "name": self.strings(row["Name"]),
            "version": (f"{row['MajorVersion']}.{row['MinorVersion']}."
                        f"{row['BuildNumber']}.{row['RevisionNumber']}"),
            "culture": self.strings(row["Culture"]),
        }

    def manifest_resources(self) -> list[dict]:
        resources = []
        for _index, pos in self._rows(0x28):
            row = self._row(0x28, pos)
            name = self.strings(row["Name"])
            implementation = self._decode_coded("Implementation", row["Implementation"])
            item = {
                "name": name,
                "offset": row["Offset"],
                "flags": row["Flags"],
                "implementation": implementation,
                "bytes": None,
                "sha256": None,
                "head": b"",
            }
            if implementation is None:
                if not self.resources_rva:
                    raise ScanError(f"{self.label}：嵌入资源 {name} 无资源目录")
                pos_res = self.resources_offset + row["Offset"]
                length = struct.unpack_from("<I", read_at(
                    self.data, pos_res, 4, self.label))[0]
                if row["Offset"] + 4 + length > self.resources_size:
                    raise ScanError(
                        f"{self.label}：嵌入资源 {name} 越过 CLI 资源目录"
                        f"（offset={row['Offset']} bytes={length} "
                        f"size={self.resources_size}）")
                view = memoryview(self.data)[pos_res + 4:pos_res + 4 + length]
                item["bytes"] = length
                item["sha256"] = hashlib.sha256(view).hexdigest()
                item["head"] = bytes(view[:MODEL_LOOKS_LIKE_LIMIT])
            resources.append(item)
        return resources

    def assembly_metadata(self) -> dict:
        """读取 AssemblyMetadataAttribute("键","值")，取自 Assembly 行的自定义特性。"""
        values: dict[str, str] = {}
        type_ref = next(
            (item for item in self.type_ref_rows
             if item["name"] == "AssemblyMetadataAttribute"
             and item["namespace"] == "System.Reflection"),
            None,
        )
        if type_ref is None:
            return values
        member_ref = next(
            (item for item in self.member_ref_rows
             if item["name"] == ".ctor" and item["parent"]
             and item["parent"]["table"] == 0x01 and item["parent"]["rid"] == type_ref["rid"]),
            None,
        )
        if member_ref is None:
            return values
        for _index, pos in self._rows(0x0C):
            row = self._row(0x0C, pos)
            parent = self._decode_coded("HasCustomAttribute", row["Parent"])
            if not parent or parent["table"] != 0x20 or parent["rid"] != 1:
                continue
            attr_type = self._decode_coded("CustomAttributeType", row["Type"])
            if not attr_type or attr_type["table"] != 0x0A or attr_type["rid"] != member_ref["rid"]:
                continue
            blob = self.blobs(row["Value"])
            parsed = parse_metadata_attribute(blob)
            if parsed:
                values[parsed[0]] = parsed[1]
        return values


def parse_metadata_attribute(blob: bytes) -> tuple[str, str] | None:
    if len(blob) < 4 or struct.unpack_from("<H", blob, 0)[0] != 1:
        return None
    cursor = 2

    def ser_string(pos: int) -> tuple[str | None, int]:
        if pos >= len(blob):
            raise ScanError("自定义特性字符串越界")
        if blob[pos] == 0xFF:
            return None, pos + 1
        length, next_pos = decode_varint(blob, pos, len(blob), "自定义特性字符串")
        raw = read_at(blob, next_pos, length, "自定义特性字符串")
        return raw.decode("utf-8", "replace"), next_pos + length

    key, cursor = ser_string(cursor)
    value, cursor = ser_string(cursor)
    if key is None or value is None:
        return None
    return key, value

# ---------------------------------------------------------------------------
# 单文件 bundle（.NET 6–10 bundle v6 尾部头 + 条目表）
# ---------------------------------------------------------------------------

class BundleEntry:
    __slots__ = ("path", "type", "offset", "size", "compressed_size",
                 "record_offset", "record_end")

    def __init__(self, path: str, type_: int, offset: int, size: int,
                 compressed_size: int, record_offset: int, record_end: int):
        self.path = path
        self.type = type_
        self.offset = offset
        self.size = size
        self.compressed_size = compressed_size
        self.record_offset = record_offset
        self.record_end = record_end

    @property
    def stored_size(self) -> int:
        return self.compressed_size or self.size

    @property
    def compressed(self) -> bool:
        return self.compressed_size > 0

    @property
    def name(self) -> str:
        return self.path.rsplit("/", 1)[-1]

    def key(self) -> tuple:
        return (self.path, self.type, self.offset, self.size, self.compressed_size)


def _entry_at(data: bytes, base: int, local: int, total: int,
              label: str) -> BundleEntry | None:
    """从尾部窗口按条目布局解析一条记录；结构性不合法时返回 None。"""
    if local < 0 or local + 26 > len(data):
        return None
    offset, size, compressed_size = struct.unpack_from("<qqq", data, local)
    type_ = data[local + 24]
    if type_ > 5:
        return None
    try:
        path_length, path_pos = decode_varint(data, local + 25, len(data), label)
    except ScanError:
        return None
    if path_length == 0 or path_length > 4096 or path_pos + path_length > len(data):
        return None
    raw_path = data[path_pos:path_pos + path_length]
    try:
        path = raw_path.decode("utf-8")
    except UnicodeDecodeError:
        return None
    if validate_bundle_path(path) is not None:
        return None
    if offset < 0 or size < 0 or compressed_size < 0:
        return None
    stored = compressed_size or size
    if offset + stored > total:
        return None
    record_offset = base + local
    record_end = base + path_pos + path_length
    return BundleEntry(path, type_, offset, size, compressed_size,
                       record_offset, record_end)


def _previous_candidates(data: bytes, base: int, boundary: int, total: int,
                         next_offset: int | None, label: str) -> list[BundleEntry]:
    """倒推一条记录：条目紧接在 boundary 之前结束。"""
    local_boundary = boundary - base
    if local_boundary <= 26 or local_boundary > len(data):
        return []
    found: list[BundleEntry] = []
    for length in range(1, min(SHORT_PATH_LIMIT, local_boundary - 26) + 1):
        path_start = local_boundary - length
        if length < 0x80:
            if data[path_start - 1] != length:
                continue
        elif data[path_start - 2:path_start] != encode_varint(length):
            continue
        raw_path = data[path_start:local_boundary]
        try:
            path = raw_path.decode("utf-8")
        except UnicodeDecodeError:
            continue
        if validate_bundle_path(path) is not None:
            continue
        entry = _entry_at(data, base, path_start - len(encode_varint(length)) - 25,
                          total, label)
        if entry is None or entry.path != path or entry.record_end != boundary:
            continue
        if entry.offset + entry.stored_size > boundary:
            continue
        if next_offset is not None and entry.offset + entry.stored_size > next_offset:
            continue
        if entry.compressed_size and entry.compressed_size > entry.size:
            continue
        found.append(entry)
    if not found:
        for length in range(SHORT_PATH_LIMIT + 1, min(4096, local_boundary - 26) + 1):
            path_start = local_boundary - length
            if data[path_start - 2:path_start] != encode_varint(length):
                continue
            raw_path = data[path_start:local_boundary]
            try:
                path = raw_path.decode("utf-8")
            except UnicodeDecodeError:
                continue
            if validate_bundle_path(path) is not None:
                continue
            entry = _entry_at(data, base, path_start - len(encode_varint(length)) - 25,
                              total, label)
            if entry is None or entry.path != path or entry.record_end != boundary:
                continue
            if entry.offset + entry.stored_size > boundary:
                continue
            if next_offset is not None and entry.offset + entry.stored_size > next_offset:
                continue
            found.append(entry)
    found.sort(key=lambda item: (-(item.offset + item.stored_size), item.path))
    return found[:8]


def _parse_manifest_forward(data: bytes, base: int, total: int,
                            start: int, label: str) -> list[BundleEntry] | None:
    entries: list[BundleEntry] = []
    pos = start
    while pos < total:
        entry = _entry_at(data, base, pos - base, total, label)
        if entry is None or entry.record_end <= pos:
            return None
        entries.append(entry)
        pos = entry.record_end
    return entries if pos == total else None


def _header_at(data: bytes, base: int, header_start: int, total: int,
               label: str) -> tuple[dict, int] | None:
    """直接解析给定位置的 bundle 头部；结构不合法返回 None。"""
    local = header_start - base
    if local < 0 or local + 53 > len(data) or header_start + 53 > total:
        return None
    if data[local] not in BUNDLE_MAJOR_VERSIONS:
        return None
    if data[local + 1] != 0 or any(data[local + 2:local + 8]):
        return None
    file_count = struct.unpack_from("<I", data, local + 8)[0]
    if file_count == 0:
        return None
    try:
        identifier_length, cursor = decode_varint(data, local + 12, len(data), label)
    except ScanError:
        return None
    if identifier_length == 0 or identifier_length > 128:
        return None
    header_size = (cursor - local) + identifier_length + BUNDLE_HEADER_TRAILER
    if local + header_size > len(data) or header_start + header_size > total:
        return None
    fields_at = cursor + identifier_length
    if fields_at + BUNDLE_HEADER_TRAILER > len(data):
        return None
    try:
        identifier = data[cursor:cursor + identifier_length].decode("ascii")
    except UnicodeDecodeError:
        return None
    (deps_offset, deps_size, runtime_offset, runtime_size,
     flags) = struct.unpack_from("<qqqqQ", data, fields_at)
    return {
        "headerOffset": header_start,
        "headerBytes": header_size,
        "entriesOffset": header_start + header_size,
        "formatMajor": data[local],
        "formatMinor": data[local + 1],
        "fileCount": file_count,
        "bundleId": identifier,
        "depsJson": {"offset": deps_offset, "bytes": deps_size},
        "runtimeConfigJson": {"offset": runtime_offset, "bytes": runtime_size},
        "flags": flags,
        "netcoreApp3CompatMode": bool(flags & NETCOREAPP3_COMPAT_FLAG),
    }, header_size


def _find_bundle_header(data: bytes, base: int, manifest_start: int,
                        entry_count: int, label: str) -> tuple[dict | None, list[str]]:
    """在条目表之前反查可变长头部，并要求其条目数、条目表位置完全吻合。"""
    problems: list[str] = []
    lowest = max(base, manifest_start - HEAD_SEARCH_SPAN)
    for header_start in range(manifest_start - 54, lowest - 1, -1):
        parsed = _header_at(data, base, header_start, base + len(data), label)
        if parsed is None:
            continue
        header, _size = parsed
        if header["entriesOffset"] != manifest_start:
            continue
        if header["fileCount"] != entry_count:
            continue
        return header, problems
    problems.append(
        "bundle 头部不可解析：在条目表之前 "
        f"{HEAD_SEARCH_SPAN} 字节内未找到满足「条目数 {entry_count}、包标识长度与尾随字段」一致的头部"
    )
    return None, problems


def _discover_manifest(data: bytes, base: int, total: int,
                       label: str) -> tuple[int, list[BundleEntry], dict | None, list[str]] | None:
    """倒推尾部条目链，得到条目表起点；条目链必须能正向解析到文件末尾。"""
    stack: list[tuple[int, int | None, tuple[BundleEntry, ...]]] = [(total, None, ())]
    attempts = 0
    notes: list[str] = []
    while stack and attempts < 20000:
        boundary, next_offset, chain = stack.pop()
        attempts += 1
        candidates = _previous_candidates(data, base, boundary, total, next_offset, label)
        if not candidates:
            if not chain:
                continue
            manifest_start = boundary
            entries = list(reversed(chain))
            forward = _parse_manifest_forward(data, base, total, manifest_start, label)
            if forward is None:
                continue
            if [item.key() for item in forward] != [item.key() for item in entries]:
                continue
            header, problems = _find_bundle_header(
                data, base, manifest_start, len(entries), label)
            notes.extend(problems)
            return manifest_start, entries, header, notes
        for candidate in candidates:
            if len(chain) >= 20000:
                break
            stack.append((candidate.record_offset, candidate.offset, chain + (candidate,)))
    return None


def _find_bundle_markers(handle, size: int) -> list[dict]:
    """在 apphost 区域搜索 bundle marker：[int64 头部偏移][32 字节签名]。"""
    limit = min(size, MARKER_SEARCH_LIMIT)
    chunk = 1 << 20
    overlap = MARKER_BYTES - 1
    offset = 0
    buffer = b""
    markers: list[dict] = []
    while offset < limit and len(markers) < MARKER_CANDIDATE_LIMIT:
        handle.seek(offset)
        data = handle.read(min(chunk, limit - offset))
        if not data:
            break
        window = buffer + data
        base = offset - len(buffer)
        pos = window.find(BUNDLE_SIGNATURE)
        while pos >= 0:
            marker_offset = base + pos - 8
            if marker_offset >= 0:
                header_offset = struct.unpack_from("<Q", window, pos - 8)[0]
                markers.append({
                    "markerOffset": marker_offset,
                    "headerOffset": header_offset,
                    "signature": BUNDLE_SIGNATURE.hex(),
                })
                if len(markers) >= MARKER_CANDIDATE_LIMIT:
                    break
            pos = window.find(BUNDLE_SIGNATURE, pos + 1)
        buffer = window[-overlap:]
        offset += len(data)
    return markers


class SingleFileBundle:
    """解析 .NET 单文件 EXE 的 bundle 清单，并提供按条目读取/哈希。"""

    def __init__(self, path: Path):
        self.path = path
        self.label = path.name
        try:
            self.size = path.stat().st_size
        except OSError as exc:
            raise ScanError(f"无法读取 {path}：{exc}") from exc
        self.entries: list[BundleEntry] = []
        self.manifest_start = 0
        self.header_offset = 0
        self.header: dict | None = None
        self.marker: dict | None = None
        self.locator: str | None = None
        self.notes: list[str] = []

    def parse(self) -> None:
        with self.path.open("rb") as handle:
            markers = _find_bundle_markers(handle, self.size)
        marker_error = None
        for marker in markers:
            try:
                self._load_from_marker(marker)
            except ScanError as exc:
                marker_error = f"bundle marker 指向的头部无效：{exc}"
                continue
            self.marker = marker
            self.locator = "signature-marker"
            return

        result = self._discover_from_tail()
        if result is None:
            message = ("无法定位单文件 bundle 清单：尾部条目链不能解析到文件末尾。"
                       "该文件可能不是 .NET 单文件发布、已被截断，或使用了不支持的 bundle 格式")
            if marker_error:
                raise ScanError(f"{marker_error}；{message}")
            raise ScanError(message)
        self.manifest_start, self.entries, self.header, notes = result
        self.notes.extend(notes)
        self.header_offset = (self.header or {}).get("headerOffset", self.manifest_start)
        self.locator = "footer-entries"
        if marker_error:
            self.notes.append(marker_error)
        elif self.marker is None:
            self.notes.append(
                "本包未找到 .NET apphost 的 32 字节 bundle marker，已改用条目链反推；"
                "真实 apphost 应带 marker")

    def _load_from_marker(self, marker: dict) -> None:
        header_offset = marker["headerOffset"]
        if header_offset < 0 or header_offset + 53 > self.size:
            raise ScanError(f"记录的头部偏移 {header_offset} 越界（文件 {self.size} 字节）")
        with self.path.open("rb") as handle:
            handle.seek(header_offset)
            head = handle.read(MAX_HEADER_BYTES)
            if len(head) < 53:
                raise ScanError("头部读取不完整")
            parsed = _header_at(head, header_offset, header_offset, self.size, self.label)
            if parsed is None:
                raise ScanError("头部格式版本、包标识或尾随字段不合法")
            header, header_size = parsed
            entries_start = header_offset + header_size
            handle.seek(entries_start)
            data = handle.read(self.size - entries_start)
        entries = _parse_manifest_forward(data, entries_start, self.size,
                                         entries_start, self.label)
        if entries is None:
            raise ScanError("条目表不能从头部之后连续解析到文件末尾")
        if header["fileCount"] != len(entries):
            raise ScanError(
                f"头部条目数 {header['fileCount']} 与实际条目 {len(entries)} 不一致")
        for entry in entries:
            if entry.offset + entry.stored_size > header_offset:
                raise ScanError(
                    f"{entry.path}：数据越过 bundle 头部起点 "
                    f"（{entry.offset}+{entry.stored_size} > {header_offset}）")
        self.header = header
        self.entries = entries
        self.manifest_start = entries_start
        self.header_offset = header_offset

    def _discover_from_tail(self):
        window = min(self.size, TAIL_WINDOW_INITIAL)
        with self.path.open("rb") as handle:
            while True:
                base = self.size - window
                handle.seek(base)
                data = handle.read(window)
                result = _discover_manifest(data, base, self.size, self.label)
                if result is not None or window >= TAIL_WINDOW_MAX or window >= self.size:
                    return result
                window = min(self.size, window * 4)

    # -- 读取 --
    def _read_segment(self, entry: BundleEntry, chunk: int = COPY_CHUNK):
        limit = entry.stored_size
        with self.path.open("rb") as handle:
            handle.seek(entry.offset)
            remaining = limit
            if not entry.compressed:
                while remaining:
                    data = handle.read(min(chunk, remaining))
                    if not data:
                        raise ScanError(f"{entry.path}：数据被截断（剩余 {remaining} 字节）")
                    remaining -= len(data)
                    yield data
                return
            inflater = zlib.decompressobj(-15)
            while remaining:
                data = handle.read(min(chunk, remaining))
                if not data:
                    raise ScanError(f"{entry.path}：压缩数据被截断（剩余 {remaining} 字节）")
                remaining -= len(data)
                try:
                    produced = inflater.decompress(data)
                except zlib.error as exc:
                    raise ScanError(f"{entry.path}：raw deflate 解压失败（{exc}）") from exc
                if produced:
                    yield produced
            try:
                trailer = inflater.flush()
            except zlib.error as exc:
                raise ScanError(f"{entry.path}：raw deflate 收尾失败（{exc}）") from exc
            if trailer:
                yield trailer
            if not inflater.eof:
                raise ScanError(f"{entry.path}：压缩数据未正常结束（缺少 deflate 结束标记）")
            if inflater.unused_data:
                raise ScanError(
                    f"{entry.path}：压缩数据在结束标记后还有 {len(inflater.unused_data)} 字节冗余")

    def hash_entry(self, entry: BundleEntry, head_size: int = 8192) -> dict:
        digest = hashlib.sha256()
        total = 0
        head = bytearray()
        for chunk in self._read_segment(entry):
            digest.update(chunk)
            total += len(chunk)
            if len(head) < head_size:
                head.extend(chunk[:head_size - len(head)])
        if total != entry.size:
            raise ScanError(
                f"{entry.path}：解压长度 {total} 与条目声明 {entry.size} 不一致")
        return {"sha256": digest.hexdigest(), "bytes": total, "head": bytes(head)}

    def read_entry(self, entry: BundleEntry) -> bytes:
        if not entry.compressed:
            with self.path.open("rb") as handle:
                handle.seek(entry.offset)
                data = handle.read(entry.size)
            if len(data) != entry.size:
                raise ScanError(
                    f"{entry.path}：读取长度 {len(data)} 与条目声明 {entry.size} 不一致")
            return data
        chunks = list(self._read_segment(entry))
        data = b"".join(chunks)
        if len(data) != entry.size:
            raise ScanError(
                f"{entry.path}：解压长度 {len(data)} 与条目声明 {entry.size} 不一致")
        return data


# ---------------------------------------------------------------------------
# 扫描
# ---------------------------------------------------------------------------

def load_host_pe(path: Path, label: str) -> PeImage:
    """读取 apphost PE（头部 + .rsrc 都在 apphost 内，按需扩大读取窗口）。"""
    with path.open("rb") as handle:
        prefix = handle.read(1 << 16)
        pe = PeImage(prefix, label)
        needed = max(pe.image_end, 1 << 16, pe.header_end)
        if needed <= len(prefix):
            return pe
        handle.seek(0)
        data = handle.read(needed)
    return PeImage(data, label)


def sniff_head(head: bytes) -> dict:
    """按前若干字节判断原生/托管与架构，用于分类。"""
    info = {"kind": "unknown"}
    if len(head) < 0x40 or head[:2] != b"MZ":
        return info
    try:
        e_lfanew = struct.unpack_from("<I", head, 0x3C)[0]
        if e_lfanew + 24 > len(head) or head[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            return info
        machine = struct.unpack_from("<H", head, e_lfanew + 4)[0]
        opt_size = struct.unpack_from("<H", head, e_lfanew + 20)[0]
        opt = e_lfanew + 24
        magic = struct.unpack_from("<H", head, opt)[0]
        pe32_plus = magic == 0x20B
        dir_base = opt + (112 if pe32_plus else 96)
        dir_count = 0
        if dir_base - 4 + 4 <= len(head):
            dir_count = struct.unpack_from("<I", head, dir_base - 4)[0]
        has_cli = False
        if dir_count > 14 and dir_base + 14 * 8 + 8 <= len(head):
            cli_rva, cli_size = struct.unpack_from("<II", head, dir_base + 14 * 8)
            has_cli = bool(cli_rva and cli_size)
        info = {
            "kind": "managed" if has_cli else "native",
            "machine": machine,
            "pe32Plus": pe32_plus,
            "hasCli": has_cli,
        }
    except struct.error:
        return info
    return info


def categorize_name(name: str, head: dict) -> str:
    lowered = name.lower()
    if lowered.endswith(".onnx"):
        return "model"
    if "onnxruntime" in lowered or "microsoft.ml.onnxruntime" in lowered:
        if lowered.endswith(".dll") and lowered.startswith("microsoft.ml.onnxruntime"):
            return "onnx-runtime-managed"
        if lowered.endswith((".dll", ".lib")):
            return "onnx-runtime-native"
        return "onnx-runtime-other"
    if "directml" in lowered:
        return "directml"
    if lowered == MATHNET_MANAGED.lower():
        return "mathnet-managed"
    if lowered == PVHEVC:
        return "hevc-native"
    if lowered in CRT_FILES or lowered.startswith(("msvcp140", "vcruntime140", "concrt140")):
        return "crt"
    if lowered in HEIF_PLUGINS or lowered == HEIF_NATIVE:
        return "heif-native"
    if lowered.endswith(".pri"):
        return "resources-pri"
    if lowered.endswith(".winmd"):
        return "winmd"
    if lowered.endswith(".dll"):
        if head.get("kind") == "managed":
            return "managed"
        if head.get("kind") == "native":
            return "native"
        return "dll-unknown"
    if lowered.endswith((".ico", ".png", ".jpg", ".jpeg", ".svg", ".bmp")):
        return "image-resource"
    if lowered.endswith((".md", ".txt", ".json", ".xml", ".targets", ".props", ".ps1",
                         ".c", ".h", ".cs", ".pdb")):
        return "text-metadata"
    return "other"


def categorize(entry: BundleEntry, head: dict) -> str:
    return categorize_name(entry.name, head)


def looks_like_onnx_model(name: str, size: int | None, head: bytes) -> bool:
    """识别嵌入资源里的 ONNX 模型：名称后缀，或体积与内容特征同时成立。"""
    if name.lower().endswith(".onnx"):
        return True
    if not size or size < 1 << 20 or not head:
        return False
    lowered = head.lower()
    return b"ai.onnx" in lowered or b"onnx" in lowered[:4096]

# ---------------------------------------------------------------------------
# 契约检查
# ---------------------------------------------------------------------------

def build_expectations(edition: str, version: str, build_number: str,
                       product_name: str | None = None,
                       app_assembly: str | None = None,
                       model: dict | None = None,
                       package_id: str | None = None,
                       product_id: str | None = None,
                       display_name: str | None = None,
                       application_id: str | None = None,
                       publisher: str | None = None) -> dict:
    display = display_name or product_name or PRODUCT_NAMES[edition]
    product = product_id or PRODUCT_IDS[edition]
    package = package_id or PACKAGE_IDS[edition]
    app_id = application_id or APPLICATION_IDS[edition]
    sibling = expected_sibling_package_id(package)
    owner = publisher or DEFAULT_PUBLISHER
    assembly = app_assembly or f"{PACKAGE_NAMES[edition]}-{version}-win-x64"
    return {
        "edition": edition,
        "productName": display,
        "displayName": display,
        "version": version,
        "buildNumber": build_number,
        "appAssemblyName": assembly,
        "appExecutable": f"{assembly}.exe",
        "assemblyVersion": f"{version}.0",
        "packageId": package,
        "productId": product,
        "applicationId": app_id,
        "fileAssociationName": f"{product}.image",
        "siblingPackageId": sibling,
        "siblingApplicationId": (APPLICATION_IDS["ai"] if app_id == APPLICATION_IDS["standard"]
                                 else APPLICATION_IDS["standard"]),
        "publisher": owner,
        "packageVersion": f"{version}.0",
        "packageFamilyName": f"{package}_{publisher_id(owner)}",
        "aumid": f"{package}_{publisher_id(owner)}!{app_id}",
        "siblingAumid": f"{sibling}_{publisher_id(owner)}!"
                       f"{(APPLICATION_IDS['ai'] if app_id == APPLICATION_IDS['standard'] else APPLICATION_IDS['standard'])}",
        "model": model or {"name": MODEL_NAME, "bytes": MODEL_BYTES, "sha256": MODEL_SHA256},
        "tfmPrefix": "net10.0",
        "rid": "win-x64",
    }


DEV_PAYLOAD_DIRECTORIES = (("nativesrc/", "NativeSrc/ 原生源码"),
                           ("nativelibs/", "NativeLibs/ 冗余副本"))
DEV_PAYLOAD_NAMES = {
    "agents.md": "模块文档 AGENTS.md",
    "app.manifest": "项目清单文件 app.manifest",
    "app.xaml": "项目源文件 App.xaml",
    "app.xaml.cs": "项目源文件 App.xaml.cs",
    "portablesinglefile.targets": "单文件打包配置 PortableSingleFile.targets",
}
DEV_PAYLOAD_EXTENSIONS = {
    ".ps1": "PowerShell 脚本",
    ".psm1": "PowerShell 脚本",
    ".cs": "C# 源码",
    ".c": "原生源码",
    ".h": "原生头文件",
    ".cc": "原生源码",
    ".cpp": "原生源码",
    ".csproj": "项目文件",
    ".vcxproj": "项目文件",
    ".sln": "解决方案文件",
    ".props": "MSBuild 配置",
    ".targets": "MSBuild 打包配置",
    ".resx": "资源源文件",
    ".editorconfig": "编辑器配置",
}


def check_dev_payload(entries: list[BundleEntry], findings: list[str]) -> None:
    """拒绝开发期文件混入发布包（csproj/打包脚本已过滤，这里是产物侧硬门）。"""
    for entry in entries:
        lowered = entry.path.lower()
        name = entry.name.lower()
        reason = None
        for directory, label in DEV_PAYLOAD_DIRECTORIES:
            if lowered.startswith(directory) or f"/{directory}" in lowered:
                reason = label
                break
        if reason is None and name in DEV_PAYLOAD_NAMES:
            reason = DEV_PAYLOAD_NAMES[name]
        if reason is None:
            for extension, label in DEV_PAYLOAD_EXTENSIONS.items():
                if name.endswith(extension):
                    reason = label
                    break
        if reason:
            findings.append(f"发布包不得包含开发期文件（{reason}）：{entry.path}")


def validate_entries(entries: list[BundleEntry], host_image_end: int,
                     manifest_start: int, findings: list[str],
                     warnings: list[str]) -> None:
    seen: dict[str, BundleEntry] = {}
    seen_folded: dict[str, BundleEntry] = {}
    ranges: list[tuple[int, int, BundleEntry]] = []
    for entry in entries:
        problem = validate_bundle_path(entry.path)
        if problem:
            findings.append(f"{entry.path}：路径不安全（{problem}）")
        if entry.type not in BUNDLE_TYPE_NAMES:
            findings.append(f"{entry.path}：未知条目类型 {entry.type}")
        stored = entry.stored_size
        if entry.offset < host_image_end:
            findings.append(
                f"{entry.path}：数据起点 {entry.offset} 落在 apphost PE 内（末尾 {host_image_end}）")
        if entry.offset + stored > manifest_start:
            findings.append(
                f"{entry.path}：数据越过条目表起点（{entry.offset}+{stored} > {manifest_start}）")
        if entry.compressed_size and entry.compressed_size > entry.size:
            findings.append(
                f"{entry.path}：压缩长度 {entry.compressed_size} 大于未压缩长度 {entry.size}")
        if entry.path in seen:
            findings.append(f"重复条目：{entry.path}")
        seen[entry.path] = entry
        folded = entry.path.casefold()
        if folded in seen_folded and seen_folded[folded].path != entry.path:
            findings.append(
                "条目路径仅大小写不同，Windows 下会互相覆盖："
                f"{seen_folded[folded].path} / {entry.path}")
        seen_folded.setdefault(folded, entry)
        if stored:
            ranges.append((entry.offset, entry.offset + stored, entry))
    ranges.sort(key=lambda item: (item[0], item[1]))
    previous: tuple[int, int, BundleEntry] | None = None
    for item in ranges:
        if previous is not None and item[0] < previous[1]:
            findings.append(
                f"数据区间重叠：{previous[2].path}（{previous[0]}..{previous[1]}）与 "
                f"{item[2].path}（{item[0]}..{item[1]}）")
        previous = item
    if entries and ranges:
        first_start = min(item[0] for item in ranges)
        if first_start > host_image_end:
            warnings.append(
                f"第一个文件数据起点 {first_start} 与 apphost 末尾 {host_image_end} 之间有间隙")


def model_name_matches(name: str, expected: str) -> bool:
    lowered = name.lower()
    target = expected.lower()
    return lowered == target or lowered.endswith("/" + target) or lowered.endswith("." + target)


def check_shared_requirements(entries: list[BundleEntry], expectations: dict,
                              findings: list[str], warnings: list[str]) -> None:
    by_name: dict[str, list[BundleEntry]] = {}
    for entry in entries:
        by_name.setdefault(entry.name.lower(), []).append(entry)

    desktop_prefixes = (
        "presentationframework", "presentationcore", "presentationnative_cor3",
        "wpfgfx_cor3", "system.windows.forms", "windowsformsintegration", "system.xaml",
    )
    for entry in entries:
        if entry.name.lower().startswith(desktop_prefixes):
            findings.append(f"发布包不得重新引入 WPF/WinForms 桌面运行时：{entry.path}")

    hevc = by_name.get(PVHEVC.lower(), [])
    rooted = [entry for entry in hevc if entry.path.lower() == PVHEVC]
    nested = [entry for entry in hevc if "/" in entry.path]
    if not rooted:
        findings.append(f"缺少根目录 {PVHEVC}（HEIF 快速解码原生库）")
    if nested:
        listed = "、".join(entry.path for entry in nested)
        findings.append(
            f"{PVHEVC} 只应有一份且位于根目录，实际另有嵌套副本：{listed}")
    if len(hevc) > 1:
        findings.append(
            f"{PVHEVC} 应恰好一份，实际 {len(hevc)} 份："
            + "、".join(entry.path for entry in hevc))

    if not any(entry.path.lower() == APP_PRI_FALLBACK for entry in entries):
        findings.append(f"缺少根目录 {APP_PRI_FALLBACK}（EXE 改名后的资源回退名）")
    app_pri = expectations["appAssemblyName"].lower() + ".pri"
    if not any(entry.path.lower() == app_pri for entry in entries):
        warnings.append(f"未找到应用资源包 {expectations['appAssemblyName']}.pri（只依赖 {APP_PRI_FALLBACK}）")

    for name in CRT_FILES:
        matches = by_name.get(name, [])
        rooted_matches = [entry for entry in matches if entry.path.lower() == name]
        if not rooted_matches:
            findings.append(f"缺少根目录 VC++ CRT：{name}")
        if len(matches) > 1:
            findings.append(
                f"VC++ CRT {name} 出现 {len(matches)} 份：" + "、".join(e.path for e in matches))

    if not by_name.get(HEIF_NATIVE.lower()):
        findings.append(f"缺少 HEIF 解码原生库 {HEIF_NATIVE}")
    for name in HEIF_PLUGINS:
        if not by_name.get(name):
            warnings.append(f"未包含 HEIF 解码插件 {name}（移除前需确认 libheif 格式支持）")

    if not by_name.get(PVHEVC_LICENSE.lower()):
        findings.append(f"缺少根目录 {PVHEVC_LICENSE}（HEVC 解码库许可说明）")
    elif len(by_name[PVHEVC_LICENSE.lower()]) > 1:
        listed = "、".join(entry.path for entry in by_name[PVHEVC_LICENSE.lower()])
        warnings.append(f"{PVHEVC_LICENSE} 出现多份：{listed}")
    if not by_name.get(PVHEVC_NOTE.lower()):
        warnings.append(f"未包含 {PVHEVC_NOTE}（pvhevc 构建说明）")

    pdbs = [entry for entry in entries if entry.name.lower().endswith(".pdb")]
    if pdbs:
        warnings.append("包内包含符号文件：" + "、".join(entry.path for entry in pdbs[:5]))


def check_standard(entries: list[BundleEntry], embedded: list[dict],
                   findings: list[str]) -> None:
    def reasons_for(name_lower: str) -> list[str]:
        reasons = []
        if name_lower.endswith(".onnx"):
            reasons.append("ONNX 模型")
        if "onnxruntime" in name_lower or name_lower.startswith("microsoft.ml.onnxruntime"):
            reasons.append("ONNX Runtime 组件")
        if "directml" in name_lower:
            reasons.append("DirectML 组件")
        if name_lower == MATHNET_MANAGED.lower():
            reasons.append("MathNet 数值库")
        if name_lower.endswith(DINO_LICENSE_NAME):
            reasons.append("DINO 模型许可证")
        return reasons

    for entry in entries:
        reasons = reasons_for(entry.name.lower())
        if reasons:
            findings.append(f"标准版不得包含 {'、'.join(reasons)}：{entry.path}")

    for resource in embedded:
        lower = resource["name"].lower()
        if resource["implementation"] is not None:
            continue
        reasons = reasons_for(lower)
        if looks_like_onnx_model(resource["name"], resource["bytes"], resource["head"]):
            if "ONNX 模型" not in reasons:
                reasons.append("ONNX 模型")
        if reasons:
            findings.append(
                f"标准版不得在程序集中嵌入 {'、'.join(reasons)}："
                f"{resource['name']}（{resource['bytes']} bytes，SHA-256 {resource['sha256']}）")


def collect_models(embedded: list[dict], records: list[dict]) -> list[dict]:
    """汇总包内 ONNX 模型候选：程序集嵌入资源与 bundle 文件两处都算。"""
    candidates = []
    for resource in embedded:
        if resource["implementation"] is not None:
            continue
        if looks_like_onnx_model(resource["name"], resource["bytes"], resource["head"]):
            candidates.append({
                "location": "embedded-resource",
                "path": resource["name"],
                "name": resource["name"],
                "bytes": resource["bytes"],
                "sha256": resource["sha256"],
            })
    for record in records:
        if record["fileName"].lower().endswith(".onnx"):
            candidates.append({
                "location": "bundle-file",
                "path": record["path"],
                "name": record["fileName"],
                "bytes": record["bytes"],
                "sha256": record["sha256"],
            })
    return candidates


def check_ai(entries: list[BundleEntry], embedded: list[dict], candidates: list[dict],
             expectations: dict, findings: list[str]) -> None:
    by_name: dict[str, list[BundleEntry]] = {}
    for entry in entries:
        by_name.setdefault(entry.name.lower(), []).append(entry)
    for required in (ONNX_MANAGED, MATHNET_MANAGED, ONNX_NATIVE, DIRECTML_NATIVE):
        matches = by_name.get(required.lower(), [])
        if not matches:
            findings.append(f"AI 版缺少必需组件：{required}")
        elif len(matches) > 1:
            findings.append(
                f"AI 版 {required} 出现 {len(matches)} 份：" + "、".join(e.path for e in matches))

    contract = expectations["model"]
    if not candidates:
        findings.append(
            f"AI 版缺少模型 {contract['name']}（期望 {contract['bytes']} bytes，"
            f"SHA-256 {contract['sha256']}）")
    elif len(candidates) > 1:
        findings.append(
            "AI 版只应包含一份模型，实际 "
            + f"{len(candidates)} 份：" + "、".join(item["path"] for item in candidates))
    else:
        found = candidates[0]
        if not model_name_matches(found["name"], contract["name"]):
            findings.append(
                f"AI 模型名称不符：期望 {contract['name']}，实际 {found['name']}（{found['path']}）")
        if found["bytes"] != contract["bytes"] or found["sha256"] != contract["sha256"]:
            findings.append(
                f"AI 模型契约不匹配（期望 {contract['name']}，{contract['bytes']} bytes，"
                f"SHA-256 {contract['sha256']}）：{found['path']} 实际 {found['bytes']} bytes，"
                f"SHA-256 {found['sha256']}")

    licenses = [entry for entry in entries if entry.name.lower().endswith(DINO_LICENSE_NAME)]
    licenses.extend(
        {"path": f"<assembly>!{resource['name']}"}
        for resource in embedded
        if (resource["implementation"] is None
            and resource["name"].lower().endswith(DINO_LICENSE_NAME)))
    if not licenses:
        findings.append(f"AI 版缺少 DINOv3 许可证 {DINO_LICENSE_DISPLAY}")
    elif len(licenses) > 1:
        findings.append(
            f"AI 版只应包含一份 {DINO_LICENSE_DISPLAY}，实际 {len(licenses)} 份："
            + "、".join(item["path"] for item in licenses))


def _app_product_version(version_info: dict | None) -> str | None:
    info = version_info or {}
    return (info.get("strings", {}).get("ProductVersion")
            or info.get("fixed", {}).get("productVersion"))


def check_identity(host_pe: PeImage | None, version_info: dict | None,
                   assembly: dict | None, assembly_version_info: dict | None,
                   assembly_metadata: dict, expectations: dict,
                   exe_name: str, app_assembly_path: str | None,
                   deps_name: str | None, runtime_name: str | None,
                   runtime_config: dict | None, deps_document: dict | None,
                   findings: list[str], warnings: list[str]) -> dict:
    identity: dict = {
        "exeFileName": exe_name,
        "renamedExe": exe_name != f"{expectations['appAssemblyName']}.exe",
        "machine": None,
        "pe32Plus": None,
        "x64": False,
        "versionResource": version_info,
        "appAssembly": {
            "path": app_assembly_path,
            "name": (assembly or {}).get("name"),
            "assemblyVersion": (assembly or {}).get("version"),
            "fileVersion": (assembly_version_info or {}).get("fixed", {}).get("fileVersion"),
            "productVersion": _app_product_version(assembly_version_info),
            "editionMetadata": assembly_metadata.get("PhotoViewerEdition"),
            "buildNumberMetadata": assembly_metadata.get("AppBuildNumber"),
            "packageIdMetadata": assembly_metadata.get("PhotoViewerPackageId"),
            "productIdMetadata": assembly_metadata.get("PhotoViewerProductId"),
            "displayNameMetadata": assembly_metadata.get("PhotoViewerDisplayName"),
            "assemblyMetadata": dict(sorted(assembly_metadata.items())),
        },
        "runtimeConfigJson": runtime_name,
        "depsJson": deps_name,
        "checks": [],
        "verified": False,
    }

    def check(field: str, label: str, expected, actual) -> None:
        ok = expected == actual
        identity["checks"].append({
            "field": field, "label": label, "expected": expected,
            "actual": actual, "ok": bool(ok),
        })
        if not ok:
            findings.append(f"{label}不符：期望 {expected}，实际 {actual}")

    if host_pe is not None:
        identity["machine"] = hex(host_pe.machine)
        identity["pe32Plus"] = host_pe.pe32_plus
        identity["x64"] = host_pe.machine == PE_MACHINE_X64 and host_pe.pe32_plus
        if host_pe.machine != PE_MACHINE_X64:
            findings.append(
                f"EXE 不是 x64（machine {host_pe.machine:#x}，期望 {PE_MACHINE_X64:#x}）")
        if not host_pe.pe32_plus:
            findings.append("EXE 不是 PE32+（不是 64 位 PE）")

    expected_version = expectations["version"]
    expected_file_version = f"{expected_version}.0"
    strings = (version_info or {}).get("strings", {})
    fixed = (version_info or {}).get("fixed", {})
    if version_info:
        check("exe.productName", "EXE 版本资源产品名", expectations["productName"],
              strings.get("ProductName"))
        check("exe.fileVersion", "EXE 版本资源文件版本", expected_file_version,
              fixed.get("fileVersion"))
        product_version = strings.get("ProductVersion") or fixed.get("productVersion")
        identity["productVersion"] = product_version
        if not (product_version or "").startswith(expected_version):
            findings.append(
                f"EXE 版本资源产品版本不符：期望以 {expected_version} 开头，实际 {product_version}")
        else:
            identity["checks"].append({
                "field": "exe.productVersion", "label": "EXE 版本资源产品版本",
                "expected": f"{expected_version}*", "actual": product_version, "ok": True,
            })
        original = strings.get("OriginalFilename")
        if original:
            check("exe.originalFilename", "EXE 版本资源原始文件名",
                  f"{expectations['appAssemblyName']}.dll", original)
    else:
        warnings.append("EXE 缺少 PE 版本资源，身份只能依据主程序集元数据")

    app = identity["appAssembly"]
    check("app.name", "主程序集名", expectations["appAssemblyName"], app["name"])
    check("app.assemblyVersion", "主程序集元数据版本", expectations["assemblyVersion"],
          app["assemblyVersion"])
    if app["fileVersion"]:
        check("app.fileVersion", "主程序集文件版本", expected_file_version, app["fileVersion"])
    informational = app["productVersion"]
    if informational:
        if not informational.startswith(expected_version):
            findings.append(
                f"主程序集产品版本不符：期望以 {expected_version} 开头，实际 {informational}")
        else:
            identity["checks"].append({
                "field": "app.productVersion", "label": "主程序集产品版本",
                "expected": f"{expected_version}*", "actual": informational, "ok": True,
            })
    else:
        warnings.append("主程序集缺少版本资源，产品版本未核实")

    edition_metadata = app["editionMetadata"]
    if edition_metadata is None:
        findings.append(
            "主程序集缺少 AssemblyMetadata(PhotoViewerEdition)：无法确认发行版本，"
            "发布包必须带该元数据（csproj 已声明）")
    else:
        check("app.edition", "主程序集版本标识", expectations["edition"], edition_metadata)

    build_metadata = app["buildNumberMetadata"]
    if build_metadata is None:
        findings.append(
            "主程序集缺少 AssemblyMetadata(AppBuildNumber)：无法确认构建号，"
            "发布包必须带该元数据（csproj 已声明）")
    else:
        check("app.buildNumber", "主程序集构建号", str(expectations["buildNumber"]),
              str(build_metadata))

    # 产品身份按实际程序集元数据核对：标准版与 AI 版的包 ID / 内部产品键 / 显示名不能互换。
    identity_fields = (
        ("PhotoViewerPackageId", "packageId", "app.packageId", "主程序集规范包 ID"),
        ("PhotoViewerProductId", "productId", "app.productId", "主程序集内部产品键"),
        ("PhotoViewerDisplayName", "displayName", "app.displayName", "主程序集产品显示名"),
    )
    identity_metadata_ok = True
    for metadata_key, expectation_key, field, label in identity_fields:
        actual = assembly_metadata.get(metadata_key)
        if actual is None:
            identity_metadata_ok = False
            findings.append(
                f"主程序集缺少 AssemblyMetadata({metadata_key})：无法确认产品身份，"
                "发布包必须带该元数据（csproj 已声明）")
        else:
            check(field, label, expectations[expectation_key], actual)

    expected_deps = f"{expectations['appAssemblyName']}.deps.json"
    expected_runtime = f"{expectations['appAssemblyName']}.runtimeconfig.json"
    check("bundle.depsJson", "deps.json 文件名", expected_deps, deps_name)
    check("bundle.runtimeConfigJson", "runtimeconfig.json 文件名", expected_runtime, runtime_name)

    tfm = None
    if isinstance(runtime_config, dict):
        options = runtime_config.get("runtimeOptions") or {}
        tfm = options.get("tfm")
        identity["tfm"] = tfm
        if not isinstance(tfm, str) or not tfm.startswith(expectations["tfmPrefix"]):
            findings.append(
                f"runtimeconfig.json 目标框架不符：期望 {expectations['tfmPrefix']}，实际 {tfm}")
        else:
            identity["checks"].append({
                "field": "bundle.tfm", "label": "runtimeconfig.json 目标框架",
                "expected": f"{expectations['tfmPrefix']}*", "actual": tfm, "ok": True,
            })
        if not options.get("includedFrameworks") and not options.get("framework"):
            warnings.append("runtimeconfig.json 既无 includedFrameworks 也无 framework")
    elif runtime_name:
        warnings.append("未能解析 runtimeconfig.json")

    target = None
    if isinstance(deps_document, dict):
        runtime_target = deps_document.get("runtimeTarget") or {}
        target = runtime_target.get("name")
        identity["depsRuntimeTarget"] = target
        # deps.json 的 runtimeTarget 形如 ".NETCoreApp,Version=v10.0/win-x64"
        tfm_match = re.match(r"net(\d+)\.(\d+)", expectations["tfmPrefix"])
        version_token = (f"Version=v{tfm_match.group(1)}.{tfm_match.group(2)}"
                         if tfm_match else expectations["tfmPrefix"])
        if not isinstance(target, str) or not target.lower().endswith(
                "/" + expectations["rid"].lower()):
            findings.append(
                f"deps.json runtimeTarget 不是 {expectations['rid']}：{target}")
        elif version_token not in target:
            findings.append(
                f"deps.json runtimeTarget 不是 {version_token}：{target}")
        else:
            identity["checks"].append({
                "field": "bundle.depsRuntimeTarget", "label": "deps.json 运行时目标",
                "expected": f"*{version_token}/{expectations['rid']}",
                "actual": target, "ok": True,
            })

    identity["metadataVerified"] = bool(edition_metadata is not None
                                        and build_metadata is not None
                                        and identity_metadata_ok)
    identity["verified"] = (bool(identity["checks"])
                            and all(item["ok"] for item in identity["checks"])
                            and identity["metadataVerified"])
    return identity


def build_summary(records: list[dict]) -> dict:
    totals: dict[str, dict] = {}
    digest = hashlib.sha256()
    for record in sorted(records, key=lambda item: item["path"]):
        bucket = totals.setdefault(record["category"] or "unreadable", {
            "category": record["category"] or "unreadable", "fileCount": 0,
            "bytes": 0, "storedBytes": 0})
        bucket["fileCount"] += 1
        bucket["bytes"] += record["bytes"] or 0
        bucket["storedBytes"] += record["storedBytes"] or 0
        digest.update(f"{record['path']}\0{record.get('sha256') or ''}\n".encode("utf-8"))
    categories = sorted(totals.values(), key=lambda item: (-item["bytes"], item["category"]))
    return {
        "categories": categories,
        "aggregateSha256": digest.hexdigest(),
        "modelFiles": [item for item in records if item["category"] == "model"],
        "onnxRuntimeFiles": [
            item for item in records if item["category"].startswith("onnx-runtime")],
        "directMlFiles": [item for item in records if item["category"] == "directml"],
        "mathNetFiles": [item for item in records if item["category"] == "mathnet-managed"],
        "hevcNative": [item for item in records if item["category"] == "hevc-native"],
        "heifNative": [item for item in records if item["category"] == "heif-native"],
        "crtFiles": [item for item in records if item["category"] == "crt"],
        "priFiles": [item for item in records if item["category"] == "resources-pri"],
        "dinoLicenses": [
            item for item in records
            if item["fileName"].lower().endswith(DINO_LICENSE_NAME)],
    }

def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(COPY_CHUNK), b""):
            digest.update(chunk)
    return digest.hexdigest()


def scan(exe: Path, expectations: dict) -> dict:
    """扫描单个单文件 EXE，返回报告字典（errors 非空即未通过）。"""
    findings: list[str] = []
    warnings: list[str] = []
    report: dict = {
        "format": "exe-bundle",
        "edition": expectations["edition"],
        "exePath": str(exe),
        "exe": {},
        "expectations": expectations,
        "bundle": {},
        "identity": {},
        "embeddedResources": [],
        "modelContract": None,
        "summary": {"categories": [], "aggregateSha256": None},
        "files": [],
        "errors": findings,
        "warnings": warnings,
        "passed": False,
    }
    if not exe.is_file():
        findings.append(f"发布包不存在或不是普通文件：{exe}")
        return report

    report["exePath"] = str(exe.resolve())
    report["exe"] = {
        "fileName": exe.name,
        "bytes": exe.stat().st_size,
        "sha256": sha256_file(exe),
    }

    bundle = SingleFileBundle(exe)
    try:
        bundle.parse()
    except ScanError as exc:
        findings.append(str(exc))
        return report

    host_pe: PeImage | None = None
    try:
        host_pe = load_host_pe(exe, exe.name)
    except ScanError as exc:
        findings.append(f"EXE PE 头解析失败：{exc}")

    validate_entries(bundle.entries, host_pe.image_end if host_pe else 0,
                     bundle.manifest_start, findings, warnings)
    check_dev_payload(bundle.entries, findings)
    max_data_end = max(
        (entry.offset + entry.stored_size for entry in bundle.entries), default=0)

    header = bundle.header
    marker = bundle.marker
    bundle_report: dict = {
        "locator": bundle.locator,
        "formatMajor": (header or {}).get("formatMajor"),
        "formatMinor": (header or {}).get("formatMinor"),
        "bundleId": (header or {}).get("bundleId"),
        "signature": (marker or {}).get("signature"),
        "marker": marker,
        "headerOffset": (header or {}).get("headerOffset"),
        "headerBytes": (header or {}).get("headerBytes"),
        "headerVerified": header is not None,
        "markerVerified": (marker is not None and header is not None
                           and marker["headerOffset"] == header["headerOffset"]),
        "manifestOffset": bundle.manifest_start,
        "manifestBytes": exe.stat().st_size - bundle.manifest_start,
        "fileCount": len(bundle.entries),
        "flags": (header or {}).get("flags"),
        "netcoreApp3CompatMode": (header or {}).get("netcoreApp3CompatMode"),
        "compressionInUse": sum(1 for entry in bundle.entries if entry.compressed) > 0,
        "hostImageEnd": host_pe.image_end if host_pe else None,
        "dataEnd": max_data_end,
    }
    report["bundle"] = bundle_report
    for note in bundle.notes:
        if note.startswith("本包未找到"):
            warnings.append(note)
        else:
            findings.append(note)

    # 条目类型 / deps / runtimeconfig / 主程序集
    deps_entries = [entry for entry in bundle.entries if entry.type == 3]
    runtime_entries = [entry for entry in bundle.entries if entry.type == 4]
    deps_entry = deps_entries[0] if len(deps_entries) == 1 else None
    runtime_entry = runtime_entries[0] if len(runtime_entries) == 1 else None
    if len(deps_entries) != 1:
        findings.append(f"deps.json 条目数应为 1，实际 {len(deps_entries)}")
    if len(runtime_entries) != 1:
        findings.append(f"runtimeconfig.json 条目数应为 1，实际 {len(runtime_entries)}")

    base_name = None
    if deps_entry is not None and deps_entry.path.lower().endswith(".deps.json"):
        base_name = deps_entry.path[:-len(".deps.json")]
    app_entry = None
    if base_name:
        candidates = [entry for entry in bundle.entries
                      if entry.type == 1 and entry.path == f"{base_name}.dll"]
        if len(candidates) == 1:
            app_entry = candidates[0]
        else:
            findings.append(
                f"未找到唯一的主程序集 {base_name}.dll（实际 {len(candidates)} 个）")

    if header is not None:
        if header["headerOffset"] < max_data_end:
            findings.append(
                f"bundle 头部与文件数据重叠：头部起点 {header['headerOffset']} < 数据末尾 {max_data_end}")
        if deps_entry is not None:
            if (header["depsJson"]["offset"] != deps_entry.offset
                    or header["depsJson"]["bytes"] != deps_entry.size):
                findings.append(
                    "bundle 头记录的 deps.json 与条目不一致："
                    f"头部 {header['depsJson']}，条目 offset={deps_entry.offset} "
                    f"size={deps_entry.size}")
        if runtime_entry is not None:
            if (header["runtimeConfigJson"]["offset"] != runtime_entry.offset
                    or header["runtimeConfigJson"]["bytes"] != runtime_entry.size):
                findings.append(
                    "bundle 头记录的 runtimeconfig.json 与条目不一致："
                    f"头部 {header['runtimeConfigJson']}，条目 offset={runtime_entry.offset} "
                    f"size={runtime_entry.size}")

    # 主程序集元数据
    managed: ManagedAssembly | None = None
    assembly: dict | None = None
    assembly_version_info: dict | None = None
    assembly_metadata: dict = {}
    embedded: list[dict] = []
    app_sha = None
    app_data: bytes | None = None
    if app_entry is not None:
        try:
            app_data = bundle.read_entry(app_entry)
            app_sha = sha256_bytes(app_data)
            managed = ManagedAssembly(app_data, app_entry.path)
            assembly = managed.assembly()
            assembly_version_info = managed.pe.version_info()
            assembly_metadata = managed.assembly_metadata()
            embedded = managed.manifest_resources()
        except ScanError as exc:
            findings.append(f"主程序集解析失败：{exc}")

    # runtimeconfig / deps 文档
    runtime_config = None
    deps_document = None
    if runtime_entry is not None:
        try:
            raw = bundle.read_entry(runtime_entry)
            runtime_config = json.loads(raw.decode("utf-8-sig"))
        except (ScanError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            findings.append(f"runtimeconfig.json 解析失败：{exc}")
    if deps_entry is not None:
        try:
            raw = bundle.read_entry(deps_entry)
            deps_document = json.loads(raw.decode("utf-8-sig"))
        except (ScanError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            findings.append(f"deps.json 解析失败：{exc}")

    # 逐文件大小与 SHA-256
    records: list[dict] = []
    for entry in bundle.entries:
        record = {
            "path": entry.path,
            "fileName": entry.name,
            "type": entry.type,
            "typeName": BUNDLE_TYPE_NAMES.get(entry.type),
            "bytes": entry.size,
            "storedBytes": entry.stored_size,
            "compressed": entry.compressed,
            "sha256": None,
            "category": "unreadable",
        }
        if app_entry is not None and entry is app_entry and app_sha is not None:
            record["sha256"] = app_sha
            record["head"] = (app_data or b"")[:8192]
        else:
            try:
                digest = bundle.hash_entry(entry)
            except ScanError as exc:
                findings.append(str(exc))
                records.append(record)
                continue
            record["sha256"] = digest["sha256"]
            record["head"] = digest["head"]
        record["category"] = categorize(entry, sniff_head(record.pop("head")))
        records.append(record)

    report["files"] = [
        {key: record[key] for key in
         ("path", "type", "typeName", "bytes", "storedBytes", "compressed", "sha256", "category")}
        for record in records
    ]
    summary = build_summary(records)
    report["summary"] = summary
    report["bundle"]["compressedFileCount"] = sum(1 for r in records if r["compressed"])
    report["bundle"]["uncompressedBytes"] = sum(r["bytes"] or 0 for r in records)
    report["bundle"]["storedBytes"] = sum(r["storedBytes"] or 0 for r in records)
    if deps_entry is not None:
        deps_record = next((r for r in records if r["path"] == deps_entry.path), None)
        report["bundle"]["depsJson"] = {
            "path": deps_entry.path,
            "bytes": deps_entry.size,
            "sha256": deps_record["sha256"] if deps_record else None,
        }
    if runtime_entry is not None:
        runtime_record = next((r for r in records if r["path"] == runtime_entry.path), None)
        report["bundle"]["runtimeConfigJson"] = {
            "path": runtime_entry.path,
            "bytes": runtime_entry.size,
            "sha256": runtime_record["sha256"] if runtime_record else None,
        }
    if deps_entry is not None and runtime_entry is not None:
        # bundle 头部记录的偏移交叉核对已在上面完成，这里回填实际条目供报告比对
        report["bundle"]["depsEntryOffset"] = deps_entry.offset
        report["bundle"]["runtimeConfigEntryOffset"] = runtime_entry.offset

    report["embeddedResources"] = [
        {
            "name": resource["name"],
            "bytes": resource["bytes"],
            "sha256": resource["sha256"],
            "linked": resource["implementation"] is not None,
            "modelLike": (resource["implementation"] is None and looks_like_onnx_model(
                resource["name"], resource["bytes"], resource["head"])),
        }
        for resource in embedded
    ]

    # 身份
    report["identity"] = check_identity(
        host_pe,
        host_pe.version_info() if host_pe else None,
        assembly,
        assembly_version_info,
        assembly_metadata,
        expectations,
        exe.name,
        app_entry.path if app_entry else None,
        deps_entry.path if deps_entry else None,
        runtime_entry.path if runtime_entry else None,
        runtime_config,
        deps_document,
        findings,
        warnings,
    )
    report["identity"]["appAssembly"]["sha256"] = app_sha

    # 版本契约
    check_shared_requirements(bundle.entries, expectations, findings, warnings)
    candidates = collect_models(embedded, records)
    if expectations["edition"] == "standard":
        check_standard(bundle.entries, embedded, findings)
        satisfied = not candidates
    else:
        check_ai(bundle.entries, embedded, candidates, expectations, findings)
        contract = expectations["model"]
        satisfied = (len(candidates) == 1
                     and model_name_matches(candidates[0]["name"], contract["name"])
                     and candidates[0]["bytes"] == contract["bytes"]
                     and candidates[0]["sha256"] == contract["sha256"])
    report["modelContract"] = {
        "expected": expectations["model"],
        "found": candidates,
        "satisfied": satisfied,
    }

    report["passed"] = not findings
    return report


def _msix_manifest_root(zf: zipfile.ZipFile, findings: list[str]):
    try:
        raw = zf.read(MSIX_MANIFEST)
    except KeyError:
        findings.append(f"MSIX 包缺少根目录 {MSIX_MANIFEST}")
        return None
    try:
        root = ET.fromstring(raw)
    except ET.ParseError as exc:
        findings.append(f"{MSIX_MANIFEST} 解析失败：{exc}")
        return None
    if xml_local(root.tag) != "Package":
        findings.append(f"{MSIX_MANIFEST} 根元素不是 Package：{xml_local(root.tag)}")
        return None
    return root


def _msix_identity_checks(root, expectations: dict, findings: list[str]) -> dict:
    checks: list[dict] = []

    def check(field: str, label: str, expected, actual) -> None:
        ok = expected == actual
        checks.append({"field": field, "label": label, "expected": expected,
                       "actual": actual, "ok": bool(ok)})
        if not ok:
            findings.append(f"{label}不符：期望 {expected}，实际 {actual}")

    identity: dict = {"checks": checks, "verified": False}
    identity_el = xml_child(root, "Identity")
    if identity_el is None:
        findings.append("MSIX 清单缺少 Identity")
        return identity

    name = identity_el.get("Name")
    publisher = identity_el.get("Publisher")
    version = identity_el.get("Version")
    architecture = identity_el.get("ProcessorArchitecture")
    identity.update({
        "packageId": name,
        "publisher": publisher,
        "version": version,
        "architecture": architecture,
    })
    check("msix.identity.name", "MSIX 包身份 Identity.Name", expectations["packageId"], name)
    if name == expectations["siblingPackageId"]:
        findings.append(
            f"MSIX 包身份与发行版本互换：{expectations['edition']} 包不得使用 "
            f"{expectations['siblingPackageId']}")
    check("msix.identity.publisher", "MSIX 签名主体 Publisher", expectations["publisher"], publisher)
    check("msix.identity.version", "MSIX 包版本 Identity.Version",
          expectations["packageVersion"], version)
    if version and not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        findings.append(f"MSIX 包版本必须是四段数字：{version}")
    check("msix.identity.architecture", "MSIX 包架构", "x64", architecture)

    properties = xml_child(root, "Properties")
    display = xml_child(properties, "DisplayName") if properties is not None else None
    check("msix.properties.displayName", "MSIX 包显示名", expectations["displayName"],
          (display.text or "").strip() if display is not None else None)

    applications = xml_child(root, "Applications")
    application_els = xml_children(applications, "Application") if applications is not None else []
    if len(application_els) != 1:
        findings.append(f"MSIX 清单必须恰好有一个 Application，实际 {len(application_els)} 个")
        return identity
    application = application_els[0]
    application_id = application.get("Id")
    executable = application.get("Executable")
    entry_point = application.get("EntryPoint")
    identity.update({
        "applicationId": application_id,
        "executable": executable,
        "entryPoint": entry_point,
    })
    check("msix.application.id", "MSIX Application Id（AUMID 末段）",
          expectations["applicationId"], application_id)
    check("msix.application.executable", "MSIX 主可执行文件",
          expectations["appExecutable"], executable)
    check("msix.application.entryPoint", "MSIX 入口点", MSIX_ENTRY_POINT, entry_point)
    for attribute in ("TrustLevel", "RuntimeBehavior"):
        if application.get(f"{{{UAP10_NAMESPACE}}}{attribute}") is None:
            findings.append(f"MSIX Application 缺少 uap10:{attribute}（桌面全信任包必需）")

    visual = xml_child(application, "VisualElements")
    if visual is None:
        findings.append("MSIX Application 缺少 uap:VisualElements")
    else:
        visual_display = visual.get("DisplayName")
        identity["visualDisplayName"] = visual_display
        check("msix.visual.displayName", "MSIX 应用入口显示名",
              expectations["displayName"], visual_display)
        for attribute in ("Square150x150Logo", "Square44x44Logo"):
            if not visual.get(attribute):
                findings.append(f"MSIX VisualElements 缺少 {attribute}")
        default_tile = xml_child(visual, "DefaultTile")
        if default_tile is not None:
            for attribute in ("Wide310x150Logo", "Square71x71Logo"):
                if not default_tile.get(attribute):
                    findings.append(f"MSIX DefaultTile 缺少 {attribute}")

    extensions = xml_child(application, "Extensions")
    extension_els = xml_children(extensions, "Extension") if extensions is not None else []
    associations = []
    for extension in extension_els:
        associations.extend(xml_children(extension, "FileTypeAssociation"))
    if len(associations) != 1:
        findings.append(f"MSIX 必须恰好有一个文件关联，实际 {len(associations)} 个")
    else:
        association = associations[0]
        association_name = association.get("Name")
        identity["fileAssociationName"] = association_name
        check("msix.association.name", "MSIX 文件关联名",
              expectations["fileAssociationName"], association_name)
        if association_name and not re.fullmatch(r"[a-z0-9.-]{1,64}", association_name):
            findings.append(
                f"MSIX 文件关联名必须是小写字母/数字/点/横线且不超过 64 字符：{association_name}")
        association_display = xml_child(association, "DisplayName")
        if association_display is not None:
            check("msix.association.displayName", "MSIX 文件关联显示名",
                  expectations["displayName"], (association_display.text or "").strip())
        supported = xml_child(association, "SupportedFileTypes")
        declared = {
            (item.text or "").strip().lower()
            for item in (xml_children(supported, "FileType") if supported is not None else [])
        }
        identity["fileTypes"] = sorted(declared)
        missing = [extension for extension in FILE_ASSOCIATION_EXTENSIONS
                   if extension not in declared]
        if missing:
            findings.append("MSIX 文件关联缺少扩展名：" + "、".join(missing))
        extra = sorted(declared - set(FILE_ASSOCIATION_EXTENSIONS))
        if extra:
            findings.append("MSIX 文件关联出现未登记扩展名：" + "、".join(extra))

    capabilities = xml_child(root, "Capabilities")
    capability_names = {
        item.get("Name") for item in (xml_children(capabilities, "Capability")
                                      if capabilities is not None else [])
    }
    if "runFullTrust" not in capability_names:
        findings.append("MSIX 桌面全信任包缺少 rescap:runFullTrust")

    publisher_hash = publisher_id(publisher) if publisher else None
    identity["publisherHash"] = publisher_hash
    if name and publisher_hash:
        family = f"{name}_{publisher_hash}"
        identity["packageFamilyName"] = family
        identity["aumid"] = f"{family}!{application_id}" if application_id else None
        identity["siblingAumid"] = expectations["siblingAumid"]
        if identity["aumid"] == expectations["siblingAumid"]:
            findings.append(f"标准版与 AI 版 AUMID 相同：{identity['aumid']}")
    identity["verified"] = bool(checks) and all(item["ok"] for item in checks)
    return identity


def _msix_payload(zf: zipfile.ZipFile, findings: list[str]) -> list[dict]:
    records: list[dict] = []
    for info in zf.infolist():
        if info.is_dir():
            continue
        zip_path = info.filename
        # APPX/OPC 的条目名可能用反斜杠；契约统一按正斜杠比较。
        path = zip_path.replace("\\", "/")
        problem = validate_bundle_path(path)
        if problem:
            findings.append(f"{path}：MSIX 条目路径不安全（{problem}）")
        digest = hashlib.sha256()
        head = bytearray()
        try:
            with zf.open(info, "r") as handle:
                while True:
                    chunk = handle.read(COPY_CHUNK)
                    if not chunk:
                        break
                    digest.update(chunk)
                    if len(head) < 64 * 1024:
                        head.extend(chunk[: 64 * 1024 - len(head)])
        except (OSError, zlib.error) as exc:
            findings.append(f"{path}：读取失败（{exc}）")
            continue
        record = {
            "path": path,
            "zipPath": zip_path,
            "fileName": path.rsplit("/", 1)[-1],
            "type": 0,
            "typeName": "package",
            "bytes": info.file_size,
            "storedBytes": info.compress_size,
            "compressed": info.compress_type != zipfile.ZIP_STORED,
            "sha256": digest.hexdigest(),
            "category": categorize_name(path.rsplit("/", 1)[-1], sniff_head(bytes(head))),
        }
        records.append(record)
    return records


def _check_msix_blockmap(zf: zipfile.ZipFile, records: list[dict],
                         findings: list[str], warnings: list[str]) -> dict | None:
    if MSIX_CONTENT_TYPES not in zf.namelist():
        findings.append(f"MSIX 包缺少根目录 {MSIX_CONTENT_TYPES}")
    if MSIX_BLOCKMAP not in zf.namelist():
        findings.append(f"MSIX 包缺少根目录 {MSIX_BLOCKMAP}（包完整性/增量信息）")
        return None
    try:
        root = ET.fromstring(zf.read(MSIX_BLOCKMAP))
    except (ET.ParseError, KeyError) as exc:
        findings.append(f"{MSIX_BLOCKMAP} 解析失败：{exc}")
        return None
    listed: dict[str, int] = {}
    block_count = 0
    for element in root.iter():
        if xml_local(element.tag) != "File":
            continue
        name = (element.get("Name") or "").replace("\\", "/")
        try:
            size = int(element.get("Size") or 0)
        except ValueError:
            size = -1
        listed[name] = size
        block_count += sum(1 for child in list(element) if xml_local(child.tag) == "Block")
    payload = {record["path"]: record["bytes"] for record in records
               if record["path"] not in MSIX_STRUCTURAL_FILES
               and not record["path"].startswith("AppxMetadata/")}
    for name, size in sorted(listed.items()):
        if name not in payload:
            findings.append(f"{MSIX_BLOCKMAP} 列出的文件不在包内：{name}")
        elif size != payload[name]:
            findings.append(
                f"{MSIX_BLOCKMAP} 记录的未压缩长度与包内不一致：{name}（{size} != {payload[name]}）")
    for name in sorted(payload):
        if name not in listed:
            findings.append(f"包内文件未出现在 {MSIX_BLOCKMAP}：{name}")
    return {"fileCount": len(listed), "blockCount": block_count}


def _msix_app_assembly(records: list[dict], zf: zipfile.ZipFile,
                       findings: list[str]) -> tuple[dict | None, ManagedAssembly | None,
                                                      list[dict], bytes | None, str | None]:
    deps = [record for record in records
            if record["path"].lower().endswith(".deps.json") and "/" not in record["path"]]
    if len(deps) != 1:
        findings.append(f"MSIX 根目录 deps.json 条目数应为 1，实际 {len(deps)}")
        return None, None, [], None, None
    base_name = deps[0]["path"][: -len(".deps.json")]
    candidates = [record for record in records if record["path"] == f"{base_name}.dll"]
    if len(candidates) != 1:
        findings.append(f"MSIX 未找到唯一的主程序集 {base_name}.dll（实际 {len(candidates)} 个）")
        return None, None, [], None, None
    payload = zf.read(candidates[0]["zipPath"])
    try:
        managed = ManagedAssembly(payload, candidates[0]["path"])
    except ScanError as exc:
        findings.append(f"MSIX 主程序集解析失败：{exc}")
        return None, None, [], payload, candidates[0]["path"]
    return (managed.assembly(), managed, managed.manifest_resources(),
            payload, candidates[0]["path"])


def scan_msix(msix: Path, expectations: dict, require_signature: bool = False) -> dict:
    """扫描单个 MSIX，按实际清单与载荷核对身份、版本、架构与分版资源。"""
    findings: list[str] = []
    warnings: list[str] = []
    report: dict = {
        "format": "msix",
        "edition": expectations["edition"],
        "packagePath": str(msix),
        "package": {},
        "expectations": expectations,
        "msix": {},
        "identity": {},
        "embeddedResources": [],
        "modelContract": None,
        "summary": {"categories": [], "aggregateSha256": None},
        "files": [],
        "errors": findings,
        "warnings": warnings,
        "passed": False,
    }
    if not msix.is_file():
        findings.append(f"发布包不存在或不是普通文件：{msix}")
        return report
    report["packagePath"] = str(msix.resolve())
    report["package"] = {
        "fileName": msix.name,
        "bytes": msix.stat().st_size,
        "sha256": sha256_file(msix),
    }
    if not zipfile.is_zipfile(msix):
        findings.append("MSIX 不是有效的 ZIP/OPC 包")
        return report

    with zipfile.ZipFile(msix) as zf:
        broken = zf.testzip()
        if broken is not None:
            findings.append(f"MSIX 压缩条目损坏：{broken}")
            return report

        root = _msix_manifest_root(zf, findings)
        identity = _msix_identity_checks(root, expectations, findings) if root is not None else {}
        signature_present = MSIX_SIGNATURE in zf.namelist()
        identity["signaturePresent"] = signature_present
        identity["signed"] = signature_present
        identity["signatureVerified"] = False
        if not signature_present:
            message = "MSIX 未签名（缺少 AppxSignature.p7x），只能做结构校验，不能安装或分发"
            if require_signature:
                findings.append(message)
            else:
                warnings.append(message)

        records = _msix_payload(zf, findings)
        report["files"] = [
            {key: record[key] for key in
             ("path", "type", "typeName", "bytes", "storedBytes", "compressed", "sha256",
              "category")}
            for record in records
        ]
        report["summary"] = build_summary(records)
        blockmap = _check_msix_blockmap(zf, records, findings, warnings)

        manifest_el = xml_child(root, "Identity") if root is not None else None
        package_files = [PackageFile(record["path"]) for record in records]
        check_shared_requirements(package_files, expectations, findings, warnings)
        check_dev_payload(package_files, findings)

        present_paths = {record["path"] for record in records}
        for logo in MSIX_LOGOS:
            if logo not in present_paths:
                findings.append(f"MSIX 清单引用的包资源缺失：{logo}")
        executable = identity.get("executable") if identity else None
        if executable:
            matches = [record for record in records if record["path"] == executable]
            if not matches:
                findings.append(f"MSIX 主可执行文件不在包内：{executable}")
            else:
                try:
                    host_pe = PeImage(zf.read(matches[0]["zipPath"]), executable)
                    if host_pe.machine != PE_MACHINE_X64 or not host_pe.pe32_plus:
                        findings.append(f"MSIX 主可执行文件不是 x64 PE32+：{executable}")
                    else:
                        identity["executableMachine"] = hex(host_pe.machine)
                except ScanError as exc:
                    findings.append(f"MSIX 主可执行文件 PE 解析失败：{exc}")

        assembly, managed, embedded, app_data, app_path = _msix_app_assembly(
            records, zf, findings)
        if managed is not None and assembly is not None:
            metadata = managed.assembly_metadata()
            checks = identity.setdefault("checks", []) if identity else []
            app_info = {
                "path": app_path,
                "name": assembly.get("name"),
                "assemblyVersion": assembly.get("version"),
                "editionMetadata": metadata.get("PhotoViewerEdition"),
                "buildNumberMetadata": metadata.get("AppBuildNumber"),
                "packageIdMetadata": metadata.get("PhotoViewerPackageId"),
                "productIdMetadata": metadata.get("PhotoViewerProductId"),
                "displayNameMetadata": metadata.get("PhotoViewerDisplayName"),
                "assemblyMetadata": dict(sorted(metadata.items())),
                "sha256": sha256_bytes(app_data) if app_data is not None else None,
            }
            identity["appAssembly"] = app_info

            def app_check(field: str, label: str, expected, actual) -> None:
                ok = expected == actual
                checks.append({"field": field, "label": label, "expected": expected,
                               "actual": actual, "ok": bool(ok)})
                if not ok:
                    findings.append(f"{label}不符：期望 {expected}，实际 {actual}")

            app_check("app.name", "主程序集名", expectations["appAssemblyName"], app_info["name"])
            app_check("app.assemblyVersion", "主程序集元数据版本",
                      expectations["assemblyVersion"], app_info["assemblyVersion"])
            for metadata_key, expectation_key, label in (
                ("PhotoViewerEdition", "edition", "主程序集版本标识"),
                ("PhotoViewerPackageId", "packageId", "主程序集规范包 ID"),
                ("PhotoViewerProductId", "productId", "主程序集内部产品键"),
                ("PhotoViewerDisplayName", "displayName", "主程序集产品显示名"),
                ("AppBuildNumber", "buildNumber", "主程序集构建号"),
            ):
                actual = metadata.get(metadata_key)
                if actual is None:
                    findings.append(
                        f"主程序集缺少 AssemblyMetadata({metadata_key})：MSIX 身份无法与产品身份对应")
                else:
                    app_check(f"app.{expectation_key}", label,
                              str(expectations[expectation_key]), str(actual))
            identity["verified"] = (bool(checks) and all(item["ok"] for item in checks)
                                    and bool(identity.get("verified")))
        else:
            identity["verified"] = False

        report["identity"] = identity
        report["embeddedResources"] = [
            {"name": resource["name"], "bytes": resource["bytes"],
             "sha256": resource["sha256"], "linked": resource["implementation"] is not None,
             "modelLike": (resource["implementation"] is None and looks_like_onnx_model(
                 resource["name"], resource["bytes"], resource["head"]))}
            for resource in embedded
        ]
        candidates = collect_models(embedded, records)
        if expectations["edition"] == "standard":
            check_standard(package_files, embedded, findings)
            satisfied = not candidates
        else:
            check_ai(package_files, embedded, candidates, expectations, findings)
            contract = expectations["model"]
            satisfied = (len(candidates) == 1
                         and model_name_matches(candidates[0]["name"], contract["name"])
                         and candidates[0]["bytes"] == contract["bytes"]
                         and candidates[0]["sha256"] == contract["sha256"])
        report["modelContract"] = {
            "expected": expectations["model"],
            "found": candidates,
            "satisfied": satisfied,
        }
        report["msix"] = {
            "blockMap": blockmap,
            "identity": {key: (manifest_el.get(key) if manifest_el is not None else None)
                         for key in ("Name", "Publisher", "Version", "ProcessorArchitecture")},
            "signaturePresent": signature_present,
        }

    report["passed"] = not findings
    return report


def write_report(report: dict, output: Path) -> int:
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                          encoding="utf-8")
    except OSError as exc:
        print(f"无法写入报告 {output}：{exc}", file=sys.stderr)
        return 1
    return 0


def print_report(report: dict, output: Path | None) -> None:
    print(f"edition：{report['edition']}")
    print(f"格式：{report.get('format', 'exe-bundle')}")
    if report.get("format") == "msix":
        package = report.get("package") or {}
        print(f"msix：{report.get('packagePath')}")
        print(f"文件：{package.get('fileName')}  {human(package.get('bytes') or 0)}  "
              f"sha256 {package.get('sha256')}")
        identity = report.get("identity") or {}
        print(
            f"身份：{identity.get('packageId')} {identity.get('version')} "
            f"{identity.get('architecture')} / 显示名 {identity.get('visualDisplayName')} / "
            f"AUMID {identity.get('aumid')} / 关联 {identity.get('fileAssociationName')} / "
            f"签名 {identity.get('signed')}")
        blockmap = (report.get("msix") or {}).get("blockMap") or {}
        print(f"blockmap：{blockmap.get('fileCount')} 文件 / {blockmap.get('blockCount')} 块")
    else:
        print(f"exe：{report['exePath']}")
        exe = report.get("exe") or {}
        if exe:
            print(f"文件：{exe.get('fileName')}  {human(exe.get('bytes') or 0)}  "
                  f"sha256 {exe.get('sha256')}")
        bundle = report.get("bundle") or {}
        if bundle:
            print(
                f"bundle：bundle v{bundle.get('formatMajor')} / 条目 {bundle.get('fileCount')} / "
                f"压缩条目 {bundle.get('compressedFileCount')} / "
                f"未压缩 {human(bundle.get('uncompressedBytes') or 0)} / "
                f"包内 {human(bundle.get('storedBytes') or 0)}")
            print(
                f"头部：{bundle.get('locator')} offset={bundle.get('headerOffset')} "
                f"verified={bundle.get('headerVerified')} bundleId={bundle.get('bundleId')}")
    identity = report.get("identity") or {}
    app = identity.get("appAssembly") or {}
    if app:
        print(
            f"程序集：{app.get('name')} / 程序集版本 {app.get('assemblyVersion')} / "
            f"版本标识 {app.get('editionMetadata')} / 构建号 {app.get('buildNumberMetadata')} / "
            f"包 ID {app.get('packageIdMetadata')}")
    if report.get("modelContract"):
        model = report["modelContract"]
        found = model.get("found") or []
        print(f"模型：满足={model.get('satisfied')} 命中 {len(found)} 处")
        for item in found:
            print(f"  {item['location']}  {item['path']}  {item['bytes']} bytes  {item['sha256']}")
    categories = (report.get("summary") or {}).get("categories") or []
    if categories:
        print("分类体积：")
        for category in categories:
            print(f"  {category['category']:<20} {category['fileCount']:>5}  "
                  f"{human(category['bytes']):>12}  包内 {human(category['storedBytes']):>12}")
    if output is not None:
        print(f"报告：{output}")
    if report.get("errors"):
        print("包扫描未通过：", file=sys.stderr)
        for message in report["errors"]:
            print(f"  - {message}", file=sys.stderr)
    else:
        print("包扫描通过")
    if report.get("warnings"):
        print("警告：", file=sys.stderr)
        for message in report["warnings"]:
            print(f"  - {message}", file=sys.stderr)

# ---------------------------------------------------------------------------
# 合成单文件包（自检用；不是真实编译产物）
# ---------------------------------------------------------------------------

def _pad4(data: bytes) -> bytes:
    return data + b"\0" * ((4 - len(data) % 4) % 4)


def _utf16z(text: str) -> bytes:
    return (text + "\0").encode("utf-16-le")


def _version_info_block(key: str, value: bytes = b"", value_units: int = 0,
                        vtype: int = 1, children: bytes = b"") -> bytes:
    head = struct.pack("<HHH", 0, value_units, vtype) + _utf16z(key)
    body = _pad4(head) + _pad4(value) + children
    return struct.pack("<HHH", len(body), value_units, vtype) + body[6:]


def _fixed_file_info(file_version: str, product_version: str) -> bytes:
    def parts(text: str) -> tuple[int, int]:
        numbers = [int(part) for part in re.split(r"[.+]", text)[:4] if part.isdigit()]
        while len(numbers) < 4:
            numbers.append(0)
        return ((numbers[0] << 16) | numbers[1]), ((numbers[2] << 16) | numbers[3])

    file_ms, file_ls = parts(file_version)
    product_ms, product_ls = parts(product_version)
    return b"".join((
        b"\xbd\x04\xef\xfe",
        struct.pack("<I", 0x10000),
        struct.pack("<II", file_ms, file_ls),
        struct.pack("<II", product_ms, product_ls),
        struct.pack("<II", 0x3F, 0),
        struct.pack("<I", 0x40004),
        struct.pack("<I", 1),
        struct.pack("<I", 0),
        struct.pack("<II", 0, 0),
    ))


def build_version_resource(strings: dict, file_version: str,
                           product_version: str, base_rva: int) -> tuple[bytes, int]:
    """合成 RT_VERSION 资源节：目录树 + VS_VERSIONINFO。返回 (节字节, 大小)。"""
    string_blocks = b"".join(
        _version_info_block(key, value=_utf16z(value), value_units=len(value) + 1, vtype=1)
        for key, value in strings.items()
    )
    language_block = _version_info_block("040904B0", vtype=1, children=string_blocks)
    string_table = _version_info_block("StringFileInfo", vtype=1, children=language_block)
    fixed = _fixed_file_info(file_version, product_version)
    blob = _version_info_block("VS_VERSION_INFO", value=fixed, value_units=52, vtype=0,
                               children=string_table)

    data_offset = 0x58
    blob = _pad4(blob)
    tree = bytearray()
    tree += struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1)
    tree += struct.pack("<II", 16, 0x80000000 | 0x18)
    tree += struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1)
    tree += struct.pack("<II", 1, 0x80000000 | 0x30)
    tree += struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1)
    tree += struct.pack("<II", 0x409, 0x48)
    tree += struct.pack("<IIII", base_rva + data_offset, len(blob), 1200, 0)
    assert len(tree) == data_offset, len(tree)
    section = bytes(tree) + blob
    section = section + b"\0" * ((0x200 - len(section) % 0x200) % 0x200)
    return section, len(blob)


def build_pe(machine: int, pe32_plus: bool, sections: list[tuple[str, bytes, int]],
             rsrc_index: int | None = None, cli_index: int | None = None,
             cli_rva: int = 0) -> bytes:
    """合成 PE 文件；sections 为 (名称, 原始数据, RVA)。"""
    file_align = 0x200
    header_size = 0x400
    opt_size = (112 if pe32_plus else 96) + 16 * 8
    offset = header_size
    table = []
    for name, data, rva in sections:
        raw_size = ((len(data) + file_align - 1) // file_align) * file_align
        table.append((name, data, rva, offset, raw_size))
        offset += raw_size
    image_size = max((rva + len(data) for _name, data, rva in sections), default=0x1000)
    image_size = (image_size + 0x1000 - 1) & ~0xFFF

    out = bytearray(header_size)
    out[0:2] = b"MZ"
    struct.pack_into("<I", out, 0x3C, 0x80)
    out[0x80:0x84] = b"PE\0\0"
    coff = 0x84
    struct.pack_into("<HHIIIHH", out, coff, machine, len(sections), 0, 0, 0, opt_size, 0x22)
    opt = coff + 20
    struct.pack_into("<H", out, opt, 0x20B if pe32_plus else 0x10B)
    struct.pack_into("<I", out, opt + 56, image_size)
    struct.pack_into("<I", out, opt + 60, header_size)
    if pe32_plus:
        struct.pack_into("<Q", out, opt + 24, 0x140000000)
        struct.pack_into("<I", out, opt + 32, 0x1000)
        struct.pack_into("<I", out, opt + 36, file_align)
        struct.pack_into("<H", out, opt + 68, 3)
        struct.pack_into("<Q", out, opt + 72, 0x100000)
        struct.pack_into("<Q", out, opt + 80, 0x1000)
        struct.pack_into("<Q", out, opt + 88, 0x100000)
        struct.pack_into("<Q", out, opt + 96, 0x1000)
        struct.pack_into("<I", out, opt + 108, 16)
        dir_base = opt + 112
    else:
        struct.pack_into("<I", out, opt + 28, 0x400000)
        struct.pack_into("<I", out, opt + 32, 0x1000)
        struct.pack_into("<I", out, opt + 36, file_align)
        struct.pack_into("<H", out, opt + 40, 6)
        struct.pack_into("<H", out, opt + 48, 6)
        struct.pack_into("<I", out, opt + 56, image_size)
        struct.pack_into("<I", out, opt + 60, header_size)
        struct.pack_into("<I", out, opt + 92, 16)
        dir_base = opt + 96
    if rsrc_index is not None:
        name, data, rva, raw_offset, raw_size = table[rsrc_index]
        struct.pack_into("<II", out, dir_base + 2 * 8, rva, raw_size)
    if cli_index is not None:
        name, data, rva, raw_offset, raw_size = table[cli_index]
        struct.pack_into("<II", out, dir_base + 14 * 8, cli_rva or rva, raw_size)

    section_table = opt + opt_size
    for index, (name, data, rva, raw_offset, raw_size) in enumerate(table):
        pos = section_table + index * 40
        out[pos:pos + 8] = name.encode("ascii").ljust(8, b"\0")[:8]
        struct.pack_into("<IIII", out, pos + 8, len(data), rva, raw_size, raw_offset)
        struct.pack_into("<I", out, pos + 36, 0x40000040)
    for name, data, rva, raw_offset, raw_size in table:
        out.extend(b"\0" * (raw_offset - len(out)))
        out[raw_offset:raw_offset + len(data)] = data
        out.extend(b"\0" * (raw_offset + raw_size - len(out)))
    return bytes(out)


def build_host_pe(product_name: str, file_version: str, product_version: str,
                  original_filename: str, machine: int = PE_MACHINE_X64,
                  pe32_plus: bool = True) -> bytes:
    strings = {
        "CompanyName": "xiaojh.cc",
        "FileDescription": product_name,
        "FileVersion": file_version,
        "InternalName": original_filename,
        "OriginalFilename": original_filename,
        "ProductName": product_name,
        "ProductVersion": product_version,
        "Assembly Version": file_version,
    }
    rsrc_rva = 0x2000
    rsrc, _size = build_version_resource(strings, file_version, product_version, rsrc_rva)
    # .bnd 节模拟 apphost 里的 40 字节 bundle marker 占位（[int64 头部偏移][32 字节签名]），
    # 由 build_bundle 在算出头部偏移后回填。
    marker_section = b"\0" * 0x200
    return build_pe(machine, pe32_plus,
                    [(".text", b"\xc3" * 16, 0x1000),
                     (".rsrc", rsrc, rsrc_rva),
                     (".bnd", marker_section, 0x3000)],
                    rsrc_index=1)


def find_marker_section(host: bytes) -> int | None:
    """取合成 apphost 里 .bnd 节的原始偏移（marker 回填位置）。"""
    try:
        pe = PeImage(host, "host")
    except ScanError:
        return None
    for section in pe.sections:
        if section["name"] == ".bnd":
            return section["rawOffset"]
    return None


def build_managed_assembly(assembly_name: str, version: str,
                           metadata: dict, resources: list[tuple[str, bytes]]) -> bytes:
    """合成托管程序集：CLI 头 + 最小元数据（Module/TypeRef/MemberRef/CustomAttribute/
    Assembly/AssemblyRef/ManifestResource）+ 嵌入资源目录。"""
    metadata = dict(metadata)
    edition = metadata.get("PhotoViewerEdition")
    if edition in PACKAGE_IDS:
        # 真实 csproj 无条件写这些身份元数据；合成包按 metadata 里的 edition 补齐，
        # 只有用例显式给错值时才覆盖。
        metadata.setdefault("PhotoViewerPackageId", PACKAGE_IDS[edition])
        metadata.setdefault("PhotoViewerProductId", PRODUCT_IDS[edition])
        metadata.setdefault("PhotoViewerDisplayName", PRODUCT_NAMES[edition])
        metadata.setdefault("PhotoViewerDistributionName", PACKAGE_NAMES[edition])
    file_name = assembly_name + ".dll"
    numbers = [int(part) for part in version.split(".")]
    while len(numbers) < 4:
        numbers.append(0)

    strings = bytearray(b"\0")
    string_index: dict[str, int] = {}

    def add_string(text: str) -> int:
        if not text:
            return 0
        if text in string_index:
            return string_index[text]
        index = len(strings)
        strings.extend(text.encode("utf-8") + b"\0")
        string_index[text] = index
        return index

    blobs = bytearray(b"\0")
    blob_index: dict[bytes, int] = {}

    def add_blob(data: bytes) -> int:
        if not data:
            return 0
        if data in blob_index:
            return blob_index[data]
        index = len(blobs)
        blobs.extend(encode_varint(len(data)) + data)
        blob_index[data] = index
        return index

    def attribute_blob(key: str, value: str) -> bytes:
        return b"".join((
            b"\x01\x00",
            encode_varint(len(key)) + key.encode("utf-8"),
            encode_varint(len(value)) + value.encode("utf-8"),
            b"\x00\x00",
        ))

    ctor_signature = add_blob(b"\x20\x02\x01\x0e\x0e")
    resource_blob = bytearray()
    resource_rows = []
    for resource_name, payload in resources:
        resource_rows.append((len(resource_blob), add_string(resource_name)))
        resource_blob.extend(struct.pack("<I", len(payload)))
        resource_blob.extend(payload)

    attribute_rows = [(add_blob(attribute_blob(key, value)))
                      for key, value in metadata.items()]

    module_name = add_string(file_name)
    type_ref_name = add_string("AssemblyMetadataAttribute")
    type_ref_namespace = add_string("System.Reflection")
    ctor_name = add_string(".ctor")
    assembly_name_index = add_string(assembly_name)
    assembly_ref_name = add_string("System.Runtime")

    tables = [
        (0x00, [struct.pack("<HHHHH", 0, module_name, 1, 0, 0)]),
        (0x01, [struct.pack("<HHH", (1 << 2) | 2, type_ref_name, type_ref_namespace)]),
        (0x0A, [struct.pack("<HHH", (1 << 3) | 1, ctor_name, ctor_signature)]),
        (0x0C, [struct.pack("<HHH", (1 << 5) | 14, (1 << 3) | 3, blob)
                 for blob in attribute_rows]),
        (0x20, [struct.pack("<IHHHHIHHH", 0x8004, numbers[0], numbers[1], numbers[2],
                            numbers[3], 0, 0, assembly_name_index, 0)]),
        (0x23, [struct.pack("<HHHHIHHHH", 0, 0, 0, 0, 0, 0, assembly_ref_name, 0, 0)]),
        (0x28, [struct.pack("<IIHH", offset, 1, name_index, 0)
                for offset, name_index in resource_rows]),
    ]

    valid = 0
    for table_id, _rows in tables:
        valid |= 1 << table_id
    table_stream = bytearray()
    table_stream += struct.pack("<I", 0)
    table_stream += struct.pack("<BBBB", 2, 0, 0, 1)
    table_stream += struct.pack("<QQ", valid, 0)
    for _table_id, rows in tables:
        table_stream += struct.pack("<I", len(rows))
    for _table_id, rows in tables:
        for row in rows:
            table_stream += row
    while len(table_stream) % 4:
        table_stream += bytes([0])

    guid_stream = bytes(range(1, 17))
    us_stream = b"\0"
    streams = [
        ("#~", bytes(table_stream)),
        ("#Strings", bytes(strings)),
        ("#US", us_stream),
        ("#GUID", guid_stream),
        ("#Blob", bytes(blobs)),
    ]
    root_header_size = 32
    header_length = 0
    entries_layout = []
    for name, payload in streams:
        padded = name.encode("ascii") + b"\0"
        while len(padded) % 4:
            padded += b"\0"
        entries_layout.append((padded, len(payload)))
        header_length += 8 + len(padded)
    cursor = root_header_size + header_length
    stream_headers = bytearray()
    for padded, size in entries_layout:
        stream_headers += struct.pack("<II", cursor, size)
        stream_headers += padded
        cursor += size

    metadata_root = bytearray()
    metadata_root += struct.pack("<IHHII", 0x424A5342, 1, 1, 0, 12)
    metadata_root += b"v4.0.30319\0\0"
    metadata_root += struct.pack("<HH", 0, len(streams))
    metadata_root += stream_headers
    for _name, payload in streams:
        metadata_root += payload
    while len(metadata_root) % 4:
        metadata_root += b"\0"

    section_rva = 0x2000
    cli_size = 72
    metadata_offset = cli_size
    resources_offset = metadata_offset + len(metadata_root)
    resources_rva = section_rva + resources_offset if resource_blob else 0
    section = bytearray()
    section += struct.pack(
        "<IHH" + "I" * 16,
        cli_size, 2, 5,
        section_rva + metadata_offset, len(metadata_root),
        1, 0,
        resources_rva, len(resource_blob),
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    assert len(section) == 72, len(section)
    section += metadata_root
    section += resource_blob
    while len(section) % 0x200:
        section += b"\0"
    return build_pe(PE_MACHINE_X86, False, [(".text", bytes(section), section_rva)],
                    cli_index=0, cli_rva=section_rva)


def build_bundle(host: bytes, files: list[dict], bundle_id: str = "0123456789ab",
                 header_count: int | None = None,
                 flags: int = NETCOREAPP3_COMPAT_FLAG,
                 marker_offset: int | None = None,
                 marker_header_offset: int | None = None) -> tuple[bytes, dict]:
    """按真实 bundle v6 尾部布局合成单文件 EXE。files 元素：path/type/data/compress。"""
    out = bytearray(host)
    records = []
    for item in files:
        while len(out) % 8:
            out.append(0)
        offset = len(out)
        payload = item["data"]
        stored = payload
        compressed_size = 0
        if item.get("compress"):
            compressor = zlib.compressobj(9, zlib.DEFLATED, -15)
            candidate = compressor.compress(payload) + compressor.flush()
            if len(candidate) < len(payload):
                stored = candidate
                compressed_size = len(candidate)
        out.extend(stored)
        records.append({"path": item["path"], "type": item["type"], "offset": offset,
                        "size": len(payload), "compressedSize": compressed_size})
    deps = next((item for item in records if item["type"] == 3), None)
    runtime = next((item for item in records if item["type"] == 4), None)
    if deps is None or runtime is None:
        raise ScanError("合成包必须包含 deps.json 与 runtimeconfig.json")
    header = bytearray()
    header += struct.pack("<BB", 6, 0) + b"\0" * 6
    header += struct.pack("<I", len(files) if header_count is None else header_count)
    header += encode_varint(len(bundle_id)) + bundle_id.encode("ascii")
    header += struct.pack("<qqqqQ", deps["offset"], deps["size"],
                          runtime["offset"], runtime["size"], flags)
    assert len(header) == 53 + len(bundle_id)
    out.extend(header)
    manifest_start = len(out) - len(header)
    layout = {"manifestStart": manifest_start, "records": {}, "markerOffset": marker_offset}
    for item in records:
        record_offset = len(out)
        out += struct.pack("<qqq", item["offset"], item["size"], item["compressedSize"])
        out.append(item["type"])
        path_bytes = item["path"].encode("utf-8")
        out += encode_varint(len(path_bytes))
        out += path_bytes
        layout["records"][item["path"]] = {"recordOffset": record_offset,
                                           "sizeFieldOffset": record_offset + 8}
    if marker_offset is not None:
        out[marker_offset:marker_offset + MARKER_BYTES] = (
            struct.pack("<Q", manifest_start if marker_header_offset is None
                        else marker_header_offset) + BUNDLE_SIGNATURE)
    return bytes(out), layout


def make_exe(target: Path, *, edition: str, version: str, build_number: str,
             product_name: str | None = None, app_assembly: str | None = None,
             files: list[dict] | None = None,
             assembly: bytes | None = None, metadata: dict | None = None,
             resources: list[tuple[str, bytes]] | None = None,
             machine: int = PE_MACHINE_X64, pe32_plus: bool = True,
             header_count: int | None = None,
             bundle_flags: int = NETCOREAPP3_COMPAT_FLAG,
             embed_marker: bool = True,
             marker_header_offset: int | None = None,
             host_product: str | None = None,
             host_original: str | None = None) -> tuple[Path, dict]:
    """构造一条完整合成包；返回 (路径, 布局信息)。"""
    product = product_name or PRODUCT_NAMES[edition]
    assembly_name = app_assembly or f"{PACKAGE_NAMES[edition]}-{version}-win-x64"
    assembly_version = f"{version}.0"
    meta = {"PhotoViewerEdition": edition, "AppBuildNumber": str(build_number)}
    if metadata is not None:
        meta = dict(metadata)
    payload = assembly
    if payload is None:
        payload = build_managed_assembly(assembly_name, assembly_version, meta,
                                        resources or [])
    host = build_host_pe(host_product or product, assembly_version,
                         f"{version}+0123456789abcdef", host_original or f"{assembly_name}.dll",
                         machine=machine, pe32_plus=pe32_plus)
    marker_offset = find_marker_section(host) if embed_marker else None
    if marker_offset is None and marker_header_offset is not None:
        raise ScanError("要求写入损坏 marker，但宿主没有 marker 占位节")
    entries = files
    if entries is None:
        entries = standard_files(assembly_name, payload)
    data, layout = build_bundle(host, entries, header_count=header_count,
                                flags=bundle_flags,
                                marker_offset=marker_offset,
                                marker_header_offset=marker_header_offset)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    return target, layout


def runtimeconfig_bytes() -> bytes:
    return json.dumps({
        "runtimeOptions": {
            "tfm": "net10.0",
            "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.0"},
                                   {"name": "Microsoft.WindowsDesktop.App", "version": "10.0.0"}],
            "configProperties": {"System.GC.Server": True},
        }
    }, indent=2).encode("utf-8")


def deps_bytes(assembly_name: str) -> bytes:
    return json.dumps({
        "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64",
                          "signature": ""},
        "compilationOptions": {},
        "targets": {".NETCoreApp,Version=v10.0/win-x64": {}},
        "libraries": {assembly_name: {"type": "project", "serviceable": False,
                                      "sha512": ""}},
    }, indent=2).encode("utf-8")


_NATIVE_STUB = build_pe(PE_MACHINE_X64, True, [(".text", b"\xc3" * 8, 0x1000)])
_MANAGED_STUB = build_managed_assembly("Stub", "1.0.0.0", {}, [])


def standard_files(assembly_name: str, assembly: bytes) -> list[dict]:
    return [
        {"path": f"{assembly_name}.dll", "type": 1, "data": assembly, "compress": True},
        {"path": f"{assembly_name}.deps.json", "type": 3,
         "data": deps_bytes(assembly_name), "compress": True},
        {"path": f"{assembly_name}.runtimeconfig.json", "type": 4,
         "data": runtimeconfig_bytes(), "compress": True},
        {"path": "pvhevc.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "pvhevc.txt", "type": 0, "data": b"pvhevc build note", "compress": True},
        {"path": "pvhevc-FFmpeg-LICENSE.txt", "type": 0,
         "data": b"FFmpeg LGPL license", "compress": True},
        {"path": "resources.pri", "type": 0, "data": b"PRI\0fallback", "compress": True},
        {"path": f"{assembly_name}.pri", "type": 0, "data": b"PRI\0app", "compress": True},
        {"path": "libheif.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "libde265.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "libx265.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "aom.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "msvcp140.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "msvcp140_1.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "vcruntime140.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "vcruntime140_1.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "icon.ico", "type": 2, "data": b"\x00\x00\x01\x00\x01\x00", "compress": False},
        {"path": "System.Private.CoreLib.dll", "type": 1, "data": _MANAGED_STUB,
         "compress": True},
        {"path": "DirectNStandard.dll", "type": 1, "data": _MANAGED_STUB, "compress": True},
    ]


def ai_files(assembly_name: str, assembly: bytes, license_in_bundle: bool,
             model_file: bytes | None) -> list[dict]:
    files = standard_files(assembly_name, assembly)
    files += [
        {"path": "Microsoft.ML.OnnxRuntime.dll", "type": 1, "data": _MANAGED_STUB,
         "compress": True},
        {"path": "MathNet.Numerics.dll", "type": 1, "data": _MANAGED_STUB, "compress": True},
        {"path": "onnxruntime.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
        {"path": "DirectML.dll", "type": 2, "data": _NATIVE_STUB, "compress": True},
    ]
    if license_in_bundle:
        files.append({"path": "Assets/Models/DINOv3-LICENSE.md", "type": 0,
                      "data": b"Meta DINOv3 License", "compress": True})
    if model_file is not None:
        files.append({"path": f"Assets/Models/{MODEL_NAME}", "type": 0,
                      "data": model_file, "compress": True})
    return files


# ---------------------------------------------------------------------------
# 合成 MSIX（自检用；不是真实打包产物）
# ---------------------------------------------------------------------------

def build_msix_manifest(*, identity_name: str, publisher: str, package_version: str,
                        architecture: str, display_name: str, application_id: str,
                        executable: str, entry_point: str, file_association_name: str,
                        file_types=FILE_ASSOCIATION_EXTENSIONS,
                        include_application: bool = True,
                        include_capability: bool = True) -> bytes:
    application = ""
    if include_application:
        supported = "".join(f"<uap:FileType>{extension}</uap:FileType>"
                            for extension in file_types)
        application = f"""
  <Applications>
    <Application Id="{application_id}" Executable="{executable}" EntryPoint="{entry_point}"
                 xmlns:uap10="{UAP10_NAMESPACE}" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="packagedClassicApp">
      <uap:VisualElements DisplayName="{display_name}" Description="{display_name}" BackgroundColor="transparent"
                          Square150x150Logo="Assets\\Square150x150Logo.png"
                          Square44x44Logo="Assets\\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" Square71x71Logo="Assets\\Square71x71Logo.png" />
        <uap:ShowNameOnTiles><uap:ShowOn Tile="square150x150Logo" /></uap:ShowNameOnTiles>
      </uap:VisualElements>
      <Extensions>
        <uap:Extension Category="windows.fileTypeAssociation">
          <uap:FileTypeAssociation Name="{file_association_name}">
            <uap:DisplayName>{display_name}</uap:DisplayName>
            <uap:SupportedFileTypes>{supported}</uap:SupportedFileTypes>
          </uap:FileTypeAssociation>
        </uap:Extension>
      </Extensions>
    </Application>
  </Applications>"""
    capability = ("""
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>"""
                  if include_capability else "")
    manifest = f"""<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="{identity_name}" Publisher="{publisher}" Version="{package_version}" ProcessorArchitecture="{architecture}" />
  <Properties>
    <DisplayName>{display_name}</DisplayName>
    <PublisherDisplayName>xiaojh.cc</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.28100.0" /></Dependencies>
  <Resources><Resource Language="zh-CN" /><Resource Language="en-US" /></Resources>{application}{capability}
</Package>
"""
    return manifest.encode("utf-8")


def msix_payload(assembly_name: str, assembly: bytes, edition: str, *,
                 model_file: bytes | None = None, license_in_bundle: bool = True,
                 include_ort: bool = True) -> dict[str, bytes]:
    files = {
        f"{assembly_name}.exe": _NATIVE_STUB,
        f"{assembly_name}.dll": assembly,
        f"{assembly_name}.deps.json": deps_bytes(assembly_name),
        f"{assembly_name}.runtimeconfig.json": runtimeconfig_bytes(),
        "pvhevc.dll": _NATIVE_STUB,
        "pvhevc.txt": b"pvhevc build note",
        "pvhevc-FFmpeg-LICENSE.txt": b"FFmpeg LGPL license",
        "resources.pri": b"PRI\0fallback",
        f"{assembly_name}.pri": b"PRI\0app",
        "libheif.dll": _NATIVE_STUB,
        "libde265.dll": _NATIVE_STUB,
        "libx265.dll": _NATIVE_STUB,
        "aom.dll": _NATIVE_STUB,
        "msvcp140.dll": _NATIVE_STUB,
        "msvcp140_1.dll": _NATIVE_STUB,
        "vcruntime140.dll": _NATIVE_STUB,
        "vcruntime140_1.dll": _NATIVE_STUB,
        "icon.ico": b"\x00\x00\x01\x00\x01\x00",
        "System.Private.CoreLib.dll": _MANAGED_STUB,
        "DirectNStandard.dll": _MANAGED_STUB,
        "Assets/StoreLogo.png": b"\x89PNG\r\n\x1a\nstore",
        "Assets/Square150x150Logo.png": b"\x89PNG\r\n\x1a\nsquare150",
        "Assets/Square44x44Logo.png": b"\x89PNG\r\n\x1a\nsquare44",
        "Assets/Square71x71Logo.png": b"\x89PNG\r\n\x1a\nsquare71",
        "Assets/Wide310x150Logo.png": b"\x89PNG\r\n\x1a\nwide",
    }
    if edition == "ai":
        if include_ort:
            files["Microsoft.ML.OnnxRuntime.dll"] = _MANAGED_STUB
            files["MathNet.Numerics.dll"] = _MANAGED_STUB
            files["onnxruntime.dll"] = _NATIVE_STUB
            files["DirectML.dll"] = _NATIVE_STUB
        if license_in_bundle:
            files["Assets/Models/DINOv3-LICENSE.md"] = b"Meta DINOv3 License"
        if model_file is not None:
            files[f"Assets/Models/{MODEL_NAME}"] = model_file
    return files


def _msix_blockmap_xml(files: dict[str, bytes]) -> bytes:
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             '<BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap" '
             'HashMethod="http://www.w3.org/2001/04/xmlenc#sha256">']
    for name in sorted(files):
        data = files[name]
        blocks = max(1, (len(data) + (64 * 1024) - 1) // (64 * 1024))
        windows_name = name.replace("/", "\\")
        lines.append(f'  <File Name="{windows_name}" Size="{len(data)}" LfhSize="39">')
        for _ in range(blocks):
            lines.append('    <Block Hash="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" />')
        lines.append("  </File>")
    lines.append("</BlockMap>")
    return "\n".join(lines).encode("utf-8")


def make_msix(target: Path, *, edition: str, assembly_name: str,
              display_name: str | None = None, identity_name: str | None = None,
              application_id: str | None = None, file_association_name: str | None = None,
              package_version: str = "9.9.9.0", version: str = "9.9.9",
              build_number: str = "999", architecture: str = "x64",
              publisher: str = DEFAULT_PUBLISHER,
              executable: str | None = None, entry_point: str = MSIX_ENTRY_POINT,
              assembly_metadata: dict | None = None,
              resources: list[tuple[str, bytes]] | None = None,
              extra_files: dict[str, bytes] | None = None,
              model_file: bytes | None = None, license_in_bundle: bool = True,
              include_ort: bool = True, signed: bool = False,
              blockmap: bool = True, include_content_types: bool = True,
              include_logo: bool = True, include_application: bool = True,
              include_capability: bool = True,
              file_types=FILE_ASSOCIATION_EXTENSIONS) -> Path:
    metadata = {
        "PhotoViewerEdition": edition,
        "AppBuildNumber": build_number,
    }
    metadata.update(assembly_metadata or {})
    assembly = build_managed_assembly(assembly_name, f"{version}.0", metadata,
                                      resources or [])
    files = msix_payload(assembly_name, assembly, edition,
                         model_file=model_file, license_in_bundle=license_in_bundle,
                         include_ort=include_ort)
    if not include_logo:
        for name in MSIX_LOGOS:
            files.pop(name, None)
    if extra_files:
        files.update(extra_files)
    display = display_name or PRODUCT_NAMES[edition]
    manifest = build_msix_manifest(
        identity_name=identity_name or PACKAGE_IDS[edition],
        publisher=publisher,
        package_version=package_version,
        architecture=architecture,
        display_name=display,
        application_id=application_id or APPLICATION_IDS[edition],
        executable=executable or f"{assembly_name}.exe",
        entry_point=entry_point,
        file_association_name=file_association_name or f"{PRODUCT_IDS[edition]}.image",
        file_types=file_types,
        include_application=include_application,
        include_capability=include_capability,
    )
    payload = dict(files)
    payload[MSIX_MANIFEST] = manifest
    target.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
        for name in sorted(payload):
            archive.writestr(name.replace("/", "\\"), payload[name])
        if include_content_types:
            archive.writestr(
                MSIX_CONTENT_TYPES,
                b'<?xml version="1.0" encoding="utf-8"?>'
                b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                b'<Default Extension="xml" ContentType="application/xml" />'
                b'<Default Extension="png" ContentType="image/png" />'
                b'<Default Extension="dll" ContentType="application/octet-stream" />'
                b"</Types>")
        if blockmap:
            archive.writestr(MSIX_BLOCKMAP, _msix_blockmap_xml(payload))
        if signed:
            archive.writestr(MSIX_SIGNATURE, b"PKCX\x03\x00\x00\x00fake-signature")
    return target


def _newest_real_sample() -> Path | None:
    """在仓库内查找可只读复用的真实单文件 EXE（没有就跳过真实样本断言）。"""
    root = Path(__file__).resolve().parent.parent
    candidates: list[Path] = []
    for pattern in ("release/packages/*win-x64*.exe",
                    "PhotoViewer.Windows/bin/*/publish-singlefile/win-x64/*.exe"):
        candidates.extend(path for path in root.glob(pattern) if path.is_file())
    if not candidates:
        return None
    return max(candidates, key=lambda path: path.stat().st_mtime)


def _probe_real_sample(path: Path) -> dict:
    """对既有发布包只做低层结构解析：不套用现行发布策略（旧包缺元数据属正常）。"""
    bundle = SingleFileBundle(path)
    bundle.parse()
    facts: dict = {
        "fileName": path.name,
        "locator": bundle.locator,
        "headerVerified": bundle.header is not None,
        "markerVerified": bool(bundle.marker and bundle.header
                               and bundle.marker["headerOffset"] == bundle.header["headerOffset"]),
        "fileCount": len(bundle.entries),
        "compressedFileCount": sum(1 for entry in bundle.entries if entry.compressed),
        "uncompressedBytes": sum(entry.size for entry in bundle.entries),
        "storedBytes": sum(entry.stored_size for entry in bundle.entries),
        "markerSignature": (bundle.marker or {}).get("signature"),
        "embeddedResources": [],
        "assembly": None,
        "assemblyMetadata": {},
        "appAssemblySha256": None,
        "managedEntryCount": 0,
    }
    for entry in bundle.entries:
        head = bundle.hash_entry(entry, head_size=8192)["head"]
        if entry.name.lower().endswith(".dll") and sniff_head(head).get("kind") == "managed":
            facts["managedEntryCount"] += 1
    deps_entry = next((entry for entry in bundle.entries
                       if entry.type == 3 and entry.path.lower().endswith(".deps.json")), None)
    if deps_entry is not None:
        base = deps_entry.path[:-len(".deps.json")]
        app_entry = next((entry for entry in bundle.entries
                          if entry.type == 1 and entry.path == f"{base}.dll"), None)
        if app_entry is not None:
            payload = bundle.read_entry(app_entry)
            facts["appAssemblySha256"] = sha256_bytes(payload)
            managed = ManagedAssembly(payload, app_entry.path)
            facts["assembly"] = managed.assembly()
            facts["assemblyMetadata"] = managed.assembly_metadata()
            facts["embeddedResources"] = [
                {"name": resource["name"], "bytes": resource["bytes"],
                 "sha256": resource["sha256"]}
                for resource in managed.manifest_resources()
            ]
    return facts


def self_test() -> int:
    """正反自检：合成包为主，真实构建产物只做结构性复用（不依赖固定本机文件）。"""
    failures: list[str] = []
    version, build = "9.9.9", "999"
    tiny_model = b"onnx-self-test-model" * 1024
    tiny_contract = {"name": MODEL_NAME, "bytes": len(tiny_model),
                     "sha256": sha256_bytes(tiny_model)}
    forbidden_contract = {"name": MODEL_NAME, "bytes": len(tiny_model) + 1,
                          "sha256": sha256_bytes(tiny_model + b"x")}
    # Windows 模型契约锁定为已发布 Windows 包与训练审计逐字节相同的唯一模型。
    documented_contract = (
        86_582_598,
        "b1579b92faa070106c613a6a502abe41d507588cab236727396bb2a2505cfc01")
    if (MODEL_BYTES, MODEL_SHA256) != documented_contract:
        failures.append("正式模型契约常量与本轮裁决的单一模型不一致")

    model_resource = f"PhotoViewer.Windows.NativeAssets.Assets.Models.{MODEL_NAME}"

    def expect(edition: str, *, model=tiny_contract, **overrides) -> dict:
        return build_expectations(edition, version, build, model=model, **overrides)

    cases: list[tuple[str, Path, dict, bool, str | None]] = []
    msix_case_count = 0

    with tempfile.TemporaryDirectory(prefix="pv-win-scan-selftest-") as tmp:
        root = Path(tmp)

        def package(name: str, edition: str, *, assembly=None, resources=None,
                    metadata=None, files=None, product_name=None, app_assembly=None,
                    machine=PE_MACHINE_X64, pe32_plus=True, header_count=None,
                    bundle_flags=NETCOREAPP3_COMPAT_FLAG,
                    embed_marker=True, marker_header_offset=None,
                    host_product=None, host_original=None) -> tuple[Path, dict]:
            target = root / name / f"{name}.exe"
            return make_exe(target, edition=edition, version=version, build_number=build,
                            product_name=product_name, app_assembly=app_assembly,
                            metadata=metadata, resources=resources, files=files,
                            machine=machine, pe32_plus=pe32_plus,
                            header_count=header_count, bundle_flags=bundle_flags,
                            embed_marker=embed_marker,
                            marker_header_offset=marker_header_offset,
                            host_product=host_product,
                            host_original=host_original)

        standard_name = f"PhotoViewer-{version}-win-x64"
        ai_name = f"PhotoViewer-AI-{version}-win-x64"

        standard_ok = package("standard-ok", "standard")
        cases.append(("标准版合规包通过", standard_ok[0], expect("standard"), True, None))

        # 改名：文件名与目录含中文与空格，身份仍须来自包内数据
        renamed_target = root / "改名 空格目录" / "照片 查看器.exe"
        renamed_target.parent.mkdir(parents=True, exist_ok=True)
        renamed_target.write_bytes(standard_ok[0].read_bytes())
        cases.append(("EXE 改名后仍可扫描通过", renamed_target, expect("standard"), True, None))

        uncompressed = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        for item in uncompressed:
            item["compress"] = False
        cases.append((
            "未压缩 bundle 通过", package("standard-plain", "standard", files=uncompressed)[0],
            expect("standard"), True, None))

        # 压缩只看条目 compressedSize：flags bit0 是 NetcoreApp3CompatMode，不参与压缩判断
        cases.append((
            "压缩条目 + flags 清零通过",
            package("flags-zero", "standard", header_count=None,
                    bundle_flags=0)[0],
            expect("standard"), True, None))

        # marker 与条目链两条定位路径
        cases.append((
            "无 marker 时条目链反推通过", package("no-marker", "standard",
                                          embed_marker=False)[0],
            expect("standard"), True, None))

        cases.append((
            "marker 头部偏移损坏失败", package("bad-marker", "standard",
                                        marker_header_offset=16)[0],
            expect("standard"), False, "bundle marker"))

        for desktop_name in ("PresentationFramework.dll", "System.Windows.Forms.dll"):
            desktop_files = standard_files(standard_name, build_managed_assembly(
                standard_name, f"{version}.0",
                {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
            desktop_files.append({"path": desktop_name, "type": 1, "data": b"desktop", "compress": False})
            cases.append((
                f"重新引入 {desktop_name} 失败",
                package(f"desktop-{desktop_name}", "standard", files=desktop_files)[0],
                expect("standard"), False, "WPF/WinForms"))

        # 开发期文件混入
        dev_entries = [
            ("NativeSrc/ 原生源码", {"path": "NativeSrc/pvhevc/pvhevc.c", "type": 0,
                                   "data": b"int main(){return 0;}", "compress": True},
             "NativeSrc"),
            ("模块文档 AGENTS.md", {"path": "AGENTS.md", "type": 0,
                                "data": b"# agents", "compress": True}, "AGENTS.md"),
            ("项目清单与 XAML", {"path": "app.manifest", "type": 0,
                             "data": b"<assembly/>", "compress": True}, "app.manifest"),
            ("打包脚本与配置", {"path": "build-native-windows-hevc.ps1", "type": 0,
                            "data": b"Write-Host hi", "compress": True}, "PowerShell"),
            ("单文件打包配置", {"path": "PortableSingleFile.targets", "type": 0,
                            "data": b"<Project/>", "compress": True},
             "PortableSingleFile.targets"),
        ]
        for label, entry, needle in dev_entries:
            files_with_dev = standard_files(standard_name, build_managed_assembly(
                standard_name, f"{version}.0",
                {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
            files_with_dev.append(entry)
            cases.append((
                f"发布包混入{label}失败",
                package(f"dev-{entry['path'].replace('/', '-').replace('.', '-')}",
                        "standard", files=files_with_dev)[0],
                expect("standard"), False, needle))

        app_xaml_files = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        app_xaml_files.append({"path": "App.xaml", "type": 0,
                               "data": b"<Application/>", "compress": True})
        cases.append((
            "发布包混入 App.xaml 失败",
            package("dev-app-xaml", "standard", files=app_xaml_files)[0],
            expect("standard"), False, "App.xaml"))

        # 发行元数据缺失（新 csproj 已声明，缺一即硬错误）
        cases.append((
            "缺少发行元数据失败", package("no-metadata", "standard", metadata={})[0],
            expect("standard"), False, "PhotoViewerEdition"))

        cases.append((
            "缺少构建号元数据失败",
            package("no-build-metadata", "standard",
                    metadata={"PhotoViewerEdition": "standard"})[0],
            expect("standard"), False, "AppBuildNumber"))

        leaked_ort = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        leaked_ort.append({"path": "Microsoft.ML.OnnxRuntime.dll", "type": 1,
                           "data": _MANAGED_STUB, "compress": True})
        cases.append((
            "标准版残留托管 ORT 失败", package("standard-ort", "standard", files=leaked_ort)[0],
            expect("standard"), False, "ONNX Runtime"))

        leaked_native = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        leaked_native.append({"path": "onnxruntime.dll", "type": 2,
                              "data": _NATIVE_STUB, "compress": True})
        cases.append((
            "标准版残留原生 ORT 失败", package("standard-ort-native", "standard",
                                            files=leaked_native)[0],
            expect("standard"), False, "ONNX Runtime"))

        leaked_dml = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        leaked_dml.append({"path": "DirectML.dll", "type": 2,
                           "data": _NATIVE_STUB, "compress": True})
        cases.append((
            "标准版残留 DirectML 失败", package("standard-dml", "standard", files=leaked_dml)[0],
            expect("standard"), False, "DirectML"))

        leaked_mathnet = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        leaked_mathnet.append({"path": "MathNet.Numerics.dll", "type": 1,
                               "data": _MANAGED_STUB, "compress": True})
        cases.append((
            "标准版残留 MathNet 失败", package("standard-mathnet", "standard",
                                          files=leaked_mathnet)[0],
            expect("standard"), False, "MathNet"))

        embedded_model = package("standard-embedded", "standard",
                                 resources=[(model_resource, tiny_model)])
        cases.append(("标准版程序集嵌入模型失败", embedded_model[0], expect("standard"),
                      False, "嵌入"))

        leaked_license = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        leaked_license.append({"path": "Assets/Models/DINOv3-LICENSE.md", "type": 0,
                               "data": b"license", "compress": True})
        cases.append((
            "标准版残留 DINO 许可证失败", package("standard-license", "standard",
                                            files=leaked_license)[0],
            expect("standard"), False, "DINO 模型许可证"))

        ai_embedded_assembly = build_managed_assembly(
            ai_name, f"{version}.0",
            {"PhotoViewerEdition": "ai", "AppBuildNumber": build},
            [(model_resource, tiny_model)])
        ai_embedded = package("ai-ok", "ai", assembly=ai_embedded_assembly,
                              files=ai_files(ai_name, ai_embedded_assembly, True, None))
        cases.append(("AI 版嵌入模型合规通过", ai_embedded[0], expect("ai"), True, None))

        ai_file_assembly = build_managed_assembly(
            ai_name, f"{version}.0",
            {"PhotoViewerEdition": "ai", "AppBuildNumber": build}, [])
        ai_file = package("ai-file-model", "ai", assembly=ai_file_assembly,
                          files=ai_files(ai_name, ai_file_assembly, True, tiny_model))
        cases.append(("AI 版模型作为包内文件通过", ai_file[0], expect("ai"), True, None))

        ai_missing = package("ai-missing", "ai", assembly=ai_embedded_assembly,
                             files=[item for item in ai_files(ai_name, ai_embedded_assembly,
                                                              True, None)
                                    if item["path"] != "Microsoft.ML.OnnxRuntime.dll"])
        cases.append(("AI 版缺托管 ORT 失败", ai_missing[0], expect("ai"), False,
                      "Microsoft.ML.OnnxRuntime.dll"))

        ai_no_license = package("ai-no-license", "ai", assembly=ai_embedded_assembly,
                                files=ai_files(ai_name, ai_embedded_assembly, False, None))
        cases.append(("AI 版缺 DINO 许可证失败", ai_no_license[0], expect("ai"), False,
                      "DINOv3 许可证"))

        ai_license_assembly = build_managed_assembly(
            ai_name, f"{version}.0",
            {"PhotoViewerEdition": "ai", "AppBuildNumber": build},
            [(model_resource, tiny_model),
             ("PhotoViewer.Windows.NativeAssets.Assets.Models.DINOv3-LICENSE.md",
              b"Meta DINOv3 License")])
        ai_embedded_license = package(
            "ai-embedded-license", "ai", assembly=ai_license_assembly,
            files=ai_files(ai_name, ai_license_assembly, False, None))
        cases.append(("AI 版许可证嵌入程序集通过", ai_embedded_license[0], expect("ai"),
                      True, None))

        cases.append(("AI 版模型哈希不符失败", ai_embedded[0],
                      expect("ai", model=forbidden_contract), False, "模型契约不匹配"))

        ai_duplicate = package("ai-duplicate", "ai", assembly=ai_embedded_assembly,
                               files=ai_files(ai_name, ai_embedded_assembly, True,
                                              tiny_model))
        cases.append(("AI 版重复模型失败", ai_duplicate[0], expect("ai"), False,
                      "只应包含一份模型"))

        duplicate_path_files = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        duplicate_path_files.append({"path": "pvhevc.txt", "type": 0,
                                     "data": b"second copy", "compress": True})
        cases.append((
            "重复条目路径失败", package("duplicate", "standard",
                                   files=duplicate_path_files)[0],
            expect("standard"), False, "重复条目"))

        traversal_files = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        traversal_files.append({"path": "../evil.dll", "type": 2,
                                "data": _NATIVE_STUB, "compress": True})
        cases.append((
            "路径穿越条目失败", package("traversal", "standard",
                                   files=traversal_files)[0],
            expect("standard"), False, "无法定位单文件 bundle 清单"))

        nested_hevc_files = standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
        nested_hevc_files.append({"path": "NativeLibs/win-x64/pvhevc.dll", "type": 2,
                                  "data": _NATIVE_STUB, "compress": True})
        cases.append((
            "pvhevc 嵌套副本失败", package("nested-hevc", "standard",
                                     files=nested_hevc_files)[0],
            expect("standard"), False, "嵌套副本"))

        no_pri_files = [item for item in standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
            if item["path"] != "resources.pri"]
        cases.append((
            "缺少 resources.pri 失败", package("no-pri", "standard", files=no_pri_files)[0],
            expect("standard"), False, "resources.pri"))

        no_crt_files = [item for item in standard_files(standard_name, build_managed_assembly(
            standard_name, f"{version}.0",
            {"PhotoViewerEdition": "standard", "AppBuildNumber": build}, []))
            if item["path"] != "vcruntime140_1.dll"]
        cases.append((
            "缺少 VC++ CRT 失败", package("no-crt", "standard", files=no_crt_files)[0],
            expect("standard"), False, "vcruntime140_1.dll"))

        wrong_product = package("wrong-product", "standard", host_product="PhotoViewer AI")
        cases.append(("EXE 产品名不符失败", wrong_product[0], expect("standard"), False,
                      "产品名"))

        wrong_assembly = package("wrong-assembly", "standard",
                                 app_assembly=f"PhotoViewer-{version}-win-x64")
        cases.append(("主程序集名不符失败", wrong_assembly[0],
                      expect("standard", app_assembly="PhotoViewer-0.1.0-win-x64"),
                      False, "主程序集名"))

        wrong_build = package("wrong-build", "standard",
                              metadata={"PhotoViewerEdition": "standard",
                                        "AppBuildNumber": "1"})
        cases.append(("构建号不符失败", wrong_build[0], expect("standard"), False,
                      "构建号"))

        wrong_edition = package("wrong-edition", "standard",
                                metadata={"PhotoViewerEdition": "ai",
                                          "AppBuildNumber": build})
        cases.append(("版本标识不符失败", wrong_edition[0], expect("standard"), False,
                      "版本标识"))

        wrong_package = package("wrong-package-id", "standard",
                                metadata={"PhotoViewerEdition": "standard",
                                          "AppBuildNumber": build,
                                          "PhotoViewerPackageId": PACKAGE_IDS["ai"]})
        cases.append(("标准版程序集误用 AI 包 ID 失败", wrong_package[0],
                      expect("standard"), False, "规范包 ID"))

        ai_wrong_package_assembly = build_managed_assembly(
            ai_name, f"{version}.0",
            {"PhotoViewerEdition": "ai", "AppBuildNumber": build,
             "PhotoViewerPackageId": PACKAGE_IDS["standard"]}, [])
        ai_wrong_package = package(
            "ai-wrong-package-id", "ai", assembly=ai_wrong_package_assembly,
            files=ai_files(ai_name, ai_wrong_package_assembly, True, tiny_model))
        cases.append(("AI 版程序集误用标准版包 ID 失败", ai_wrong_package[0],
                      expect("ai"), False, "规范包 ID"))

        wrong_display = package("wrong-display-name", "standard",
                                metadata={"PhotoViewerEdition": "standard",
                                          "AppBuildNumber": build,
                                          "PhotoViewerDisplayName": PRODUCT_NAMES["ai"]})
        cases.append(("标准版程序集误用 AI 显示名失败", wrong_display[0],
                      expect("standard"), False, "产品显示名"))

        x86_host = package("x86-host", "standard", machine=PE_MACHINE_X86, pe32_plus=False)
        cases.append(("非 x64 宿主失败", x86_host[0], expect("standard"), False, "x64"))

        # 结构性损坏
        truncated = root / "truncated" / "truncated.exe"
        truncated.parent.mkdir(parents=True, exist_ok=True)
        truncated.write_bytes(standard_ok[0].read_bytes()[:-40])
        cases.append(("截断文件失败", truncated, expect("standard"), False,
                      "无法定位单文件 bundle 清单"))

        bad_header = package("bad-header", "standard", header_count=1)
        cases.append(("bundle 头部条目数损坏失败", bad_header[0], expect("standard"), False,
                      "bundle 头部"))

        corrupted_size = root / "bad-entry" / "bad-entry.exe"
        corrupted_size.parent.mkdir(parents=True, exist_ok=True)
        layout = standard_ok[1]
        data = bytearray(standard_ok[0].read_bytes())
        field = layout["records"][f"{standard_name}.dll"]["sizeFieldOffset"]
        struct.pack_into("<q", data, field, 12345)
        corrupted_size.write_bytes(bytes(data))
        cases.append(("压缩条目长度不符失败", corrupted_size, expect("standard"), False,
                      "解压长度"))

        for label, path, expectations, expect_pass, needle in cases:
            try:
                report = scan(path, expectations)
            except Exception as exc:  # 自检自身崩溃也要报出来
                failures.append(f"{label}：扫描抛出异常 {type(exc).__name__}: {exc}")
                continue
            passed = report["passed"]
            if passed != expect_pass:
                detail = "；".join(report["errors"][:3]) or "（无错误）"
                failures.append(
                    f"{label}：期望 {'通过' if expect_pass else '拒绝'}，"
                    f"实际 {'通过' if passed else '拒绝'}（{detail}）")
                continue
            if needle and not any(needle in message for message in report["errors"]):
                detail = "；".join(report["errors"][:4])
                failures.append(f"{label}：错误信息未包含「{needle}」，实际：{detail}")
                continue
            print(f"  [{'OK' if passed else 'OK（已拒绝）'}] {label}")

        # 报告字段自检：分类体积、逐文件大小、压缩信息、嵌入资源哈希
        report = scan(standard_ok[0], expect("standard"))
        if not report["summary"]["categories"]:
            failures.append("报告缺少分类体积")
        if not all(record["sha256"] for record in report["files"]):
            failures.append("报告存在未计算 SHA-256 的文件")
        uncompressed_report = scan(root / "standard-plain" / "standard-plain.exe",
                                   expect("standard"))
        if uncompressed_report["bundle"].get("compressedFileCount") != 0:
            failures.append("未压缩包的压缩条目数不为 0")
        if uncompressed_report["bundle"].get("compressionInUse"):
            failures.append("未压缩包不应报告 compressionInUse")
        if not uncompressed_report["bundle"].get("netcoreApp3CompatMode"):
            failures.append("flags bit0 应解读为 NetcoreApp3CompatMode")
        if not report["bundle"].get("compressedFileCount"):
            failures.append("压缩包未记录压缩条目")
        if not report["bundle"].get("compressionInUse"):
            failures.append("压缩包应报告 compressionInUse")
        flags_zero_report = scan(root / "flags-zero" / "flags-zero.exe", expect("standard"))
        if flags_zero_report["bundle"].get("flags") != 0:
            failures.append("flags 清零包的 flags 字段不为 0")
        if not flags_zero_report["bundle"].get("compressionInUse"):
            failures.append("flags 清零但条目压缩时仍应报告 compressionInUse")
        if flags_zero_report["bundle"].get("netcoreApp3CompatMode"):
            failures.append("flags=0 时不应报告 NetcoreApp3CompatMode")
        ai_report = scan(ai_embedded[0], expect("ai"))
        embedded = [item for item in ai_report["embeddedResources"]
                    if item["name"] == model_resource]
        if not embedded or embedded[0]["sha256"] != tiny_contract["sha256"]:
            failures.append("报告未记录嵌入模型资源的哈希")
        if embedded and embedded[0]["bytes"] != tiny_contract["bytes"]:
            failures.append("报告未记录嵌入模型资源的字节数")
        if report["bundle"].get("locator") != "signature-marker":
            failures.append(
                f"默认合成包应走 marker 定位，实际 {report['bundle'].get('locator')}")
        if not report["bundle"].get("markerVerified"):
            failures.append("marker 定位的包未标记 markerVerified")
        fallback_report = scan(root / "no-marker" / "no-marker.exe", expect("standard"))
        if fallback_report["bundle"].get("locator") != "footer-entries":
            failures.append(
                f"无 marker 的包应回退条目链定位，实际 {fallback_report['bundle'].get('locator')}")
        if fallback_report["bundle"].get("marker") is not None:
            failures.append("无 marker 的包不应记录 marker")
        if not any("marker" in message for message in fallback_report["warnings"]):
            failures.append("无 marker 的包应给出 marker 缺失告警")

        # ------------------------------------------------------------------
        # MSIX：两个独立包的身份、版本、架构、AUMID 与分版资源
        # ------------------------------------------------------------------
        if publisher_id("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, "
                        "S=Washington, C=US") != "8wekyb3d8bbwe":
            failures.append("发布者 ID 算法未复现已知 Microsoft 包族后缀 8wekyb3d8bbwe")

        msix_cases: list[tuple[str, Path, dict, bool, str | None, bool]] = []

        def msix_case(label: str, path: Path, edition: str, expect_pass: bool,
                      needle: str | None = None, require_signature: bool = False,
                      **overrides) -> None:
            msix_cases.append((label, path, expect(edition, **overrides), expect_pass,
                               needle, require_signature))

        def msix_package(name: str, edition: str, **overrides) -> Path:
            assembly = overrides.pop("assembly_name", standard_name if edition == "standard"
                                     else ai_name)
            options = {"version": version, "build_number": build}
            options.update(overrides)
            if edition == "ai" and options.get("model_file") is None:
                options["model_file"] = tiny_model
            return make_msix(root / f"msix-{name}" / f"{name}.msix", edition=edition,
                             assembly_name=assembly, **options)

        standard_msix = msix_package("standard-ok", "standard")
        msix_case("MSIX 标准版合规包通过", standard_msix, "standard", True)

        ai_msix = msix_package("ai-ok", "ai")
        msix_case("MSIX AI 版合规包通过", ai_msix, "ai", True)

        signed_msix = msix_package("ai-signed", "ai", signed=True)
        msix_case("MSIX 已签名包通过签名门禁", signed_msix, "ai", True, require_signature=True)

        msix_case("MSIX 标准版误用 AI 包 ID 失败",
                  msix_package("standard-ai-id", "standard",
                               identity_name=PACKAGE_IDS["ai"]),
                  "standard", False, "Identity.Name")
        msix_case("MSIX AI 版误用标准版包 ID 失败",
                  msix_package("ai-standard-id", "ai",
                               identity_name=PACKAGE_IDS["standard"]),
                  "ai", False, "Identity.Name")
        msix_case("MSIX 包显示名不符失败",
                  msix_package("wrong-display", "standard", display_name="PhotoViewer AI"),
                  "standard", False, "显示名")
        msix_case("MSIX 包版本不符失败",
                  msix_package("wrong-version", "standard", package_version="9.9.9.1"),
                  "standard", False, "包版本")
        msix_case("MSIX 包版本不是四段失败",
                  msix_package("bad-version", "standard", package_version="9.9.9"),
                  "standard", False, "四段")
        msix_case("MSIX 架构不符失败",
                  msix_package("wrong-arch", "standard", architecture="arm64"),
                  "standard", False, "架构")
        msix_case("MSIX Application Id 不符失败",
                  msix_package("wrong-app-id", "standard",
                               application_id=APPLICATION_IDS["ai"]),
                  "standard", False, "Application Id")
        msix_case("MSIX 文件关联名不符失败",
                  msix_package("wrong-association", "standard",
                               file_association_name="photoviewer.ai.image"),
                  "standard", False, "文件关联名")
        msix_case("MSIX 缺少包资源失败",
                  msix_package("no-logo", "standard", include_logo=False),
                  "standard", False, "包资源缺失")
        msix_case("MSIX 未签名在签名门禁下失败",
                  msix_package("unsigned", "standard"), "standard", False, "未签名",
                  require_signature=True)
        msix_case("MSIX 缺少 blockmap 失败",
                  msix_package("no-blockmap", "standard", blockmap=False),
                  "standard", False, "AppxBlockMap.xml")
        msix_case("MSIX 主可执行文件不在包内失败",
                  msix_package("ghost-exe", "standard", executable="Ghost.exe"),
                  "standard", False, "主可执行文件")
        msix_case("MSIX 标准版带模型失败",
                  msix_package("standard-model", "standard",
                               extra_files={f"Assets/Models/{MODEL_NAME}": tiny_model}),
                  "standard", False, "ONNX 模型")
        msix_case("MSIX 标准版带 ORT 失败",
                  msix_package("standard-ort", "standard",
                               extra_files={"Microsoft.ML.OnnxRuntime.dll": _MANAGED_STUB}),
                  "standard", False, "ONNX Runtime")
        msix_case("MSIX AI 版缺 ORT 失败",
                  msix_package("ai-no-ort", "ai", include_ort=False),
                  "ai", False, "Microsoft.ML.OnnxRuntime.dll")
        msix_case("MSIX AI 版缺 DINO 许可证失败",
                  msix_package("ai-no-license", "ai", license_in_bundle=False),
                  "ai", False, "DINOv3 许可证")
        msix_case("MSIX AI 版模型哈希不符失败",
                  msix_package("ai-bad-model", "ai", model_file=tiny_model + b"x"),
                  "ai", False, "模型契约不匹配")
        msix_case("MSIX 标准版包内元数据误用 AI 身份失败",
                  msix_package("standard-ai-metadata", "standard",
                               assembly_metadata={"PhotoViewerEdition": "ai"}),
                  "standard", False, "版本标识")

        msix_case_count = len(msix_cases)
        for label, path, expectations, expect_pass, needle, require_signature in msix_cases:
            try:
                report = scan_msix(path, expectations, require_signature=require_signature)
            except Exception as exc:  # 自检自身崩溃也要报出来
                failures.append(f"{label}：扫描抛出异常 {type(exc).__name__}: {exc}")
                continue
            passed = report["passed"]
            if passed != expect_pass:
                detail = "；".join(report["errors"][:3]) or "（无错误）"
                failures.append(
                    f"{label}：期望 {'通过' if expect_pass else '拒绝'}，"
                    f"实际 {'通过' if passed else '拒绝'}（{detail}）")
                continue
            if needle and not any(needle in message for message in report["errors"]):
                detail = "；".join(report["errors"][:4])
                failures.append(f"{label}：错误信息未包含「{needle}」，实际：{detail}")
                continue
            print(f"  [{'OK' if passed else 'OK（已拒绝）'}] {label}")

        standard_msix_report = scan_msix(standard_msix, expect("standard"))
        ai_msix_report = scan_msix(ai_msix, expect("ai"))
        if not standard_msix_report["identity"].get("packageFamilyName"):
            failures.append("MSIX 报告缺少 Package Family Name")
        if standard_msix_report["identity"].get("aumid") == ai_msix_report["identity"].get("aumid"):
            failures.append("标准版与 AI 版 MSIX 报告了相同 AUMID")
        if standard_msix_report["msix"]["blockMap"]["fileCount"] < 10:
            failures.append("MSIX blockmap 未覆盖包内文件")
        if not all(record["sha256"] for record in standard_msix_report["files"]):
            failures.append("MSIX 报告存在未计算 SHA-256 的文件")
        if not standard_msix_report["summary"]["categories"]:
            failures.append("MSIX 报告缺少分类体积")
        if "签名" not in "".join(standard_msix_report["warnings"]):
            failures.append("未签名 MSIX 应给出签名缺失告警")

        props = read_identity_props()
        for edition in ("standard", "ai"):
            expected = props.get(edition) or {}
            # productId 在根 props 里由 edition 直接拼出，没有逐版本 Condition。
            defaults = {
                "packageId": PACKAGE_IDS[edition],
                "productId": PRODUCT_IDS[edition],
                "displayName": PRODUCT_NAMES[edition],
                "distributionName": PACKAGE_NAMES[edition],
            }
            for key, value in defaults.items():
                actual = expected.get(key)
                if actual != value:
                    failures.append(
                        f"扫描器默认 {key}（{edition}）与 Directory.Build.props 不一致："
                        f"{value} vs {actual}")
            if expected.get("enableDino") != ("true" if edition == "ai" else "false"):
                failures.append(
                    f"Directory.Build.props 的 PhotoViewerEnableDino 未由 edition 派生（{edition}）")


        real_sample = _newest_real_sample()
        if real_sample is not None:
            try:
                facts = _probe_real_sample(real_sample)
            except Exception as exc:
                failures.append(f"真实样本 {real_sample.name} 低层解析异常：{exc}")
            else:
                if not facts["headerVerified"]:
                    failures.append(f"真实样本 {real_sample.name}：bundle 头部未通过校验")
                if facts["locator"] != "signature-marker" or not facts["markerVerified"]:
                    failures.append(
                        f"真实样本 {real_sample.name}：marker 定位未生效"
                        f"（{facts['locator']}）")
                if facts["fileCount"] < 50 or facts["managedEntryCount"] < 10:
                    failures.append(
                        f"真实样本 {real_sample.name}：条目数或托管程序集数异常"
                        f"（{facts['fileCount']}/{facts['managedEntryCount']}）")
                if not facts["appAssemblySha256"]:
                    failures.append(f"真实样本 {real_sample.name}：未记录主程序集哈希")
                if not facts["embeddedResources"]:
                    failures.append(f"真实样本 {real_sample.name}：未解析出嵌入资源")
                print(
                    f"  [OK] 真实样本低层解析（{real_sample.name}：{facts['locator']}，"
                    f"{facts['fileCount']} 条目，{len(facts['embeddedResources'])} 嵌入资源，"
                    f"{facts['uncompressedBytes']} → {facts['storedBytes']} 字节）")
        else:
            print("  [跳过] 真实样本：仓库内没有可只读复用的单文件 EXE")

    if failures:
        print("自检未通过：", file=sys.stderr)
        for message in failures:
            print(f"  - {message}", file=sys.stderr)
        return 1
    print(f"自检通过（EXE {len(cases)} 项 + MSIX {msix_case_count} 项正反用例 + 报告字段 + 真实样本结构）")
    return 0


def main() -> int:
    argv = sys.argv[1:]
    if "--self-test" in argv:
        return self_test()

    parser = argparse.ArgumentParser(
        description="扫描 Windows MSIX 或单文件 EXE，并核对发行版本与产品身份契约")
    parser.add_argument("package", type=Path, help="待扫描的 .msix/.appx 或单文件 .exe")
    parser.add_argument("--edition", required=True, choices=("standard", "ai"), help="发行版本")
    parser.add_argument("--output", required=True, type=Path, help="JSON 报告路径")
    parser.add_argument("--version", help="期望的产品版本；缺省取根 Directory.Build.props")
    parser.add_argument("--build-number", help="期望的构建号；缺省取根 Directory.Build.props")
    parser.add_argument("--product-name", help="期望的产品显示名（默认按 edition 推导）")
    parser.add_argument("--app-assembly", help="期望的主程序集名（默认 <分发名>-<版本>-win-x64）")
    parser.add_argument("--package-id", help="期望的规范包 ID（默认按 edition 推导）")
    parser.add_argument("--product-id", help="期望的内部产品键（默认按 edition 推导）")
    parser.add_argument("--display-name", help="期望的产品显示名（默认按 edition 推导）")
    parser.add_argument("--application-id", help="期望的 MSIX Application Id（AUMID 末段）")
    parser.add_argument("--publisher", help="期望的 MSIX Publisher（默认 CN=xiaojh.cc）")
    parser.add_argument("--require-signature", action="store_true",
                        help="MSIX 必须带 AppxSignature.p7x（正式分发门禁）")
    args = parser.parse_args(argv)

    props = read_root_props()
    version = args.version or props.get("AppVersion")
    build_number = args.build_number or props.get("AppBuildNumber")
    if not version or not build_number:
        print("无法确定期望的版本号/构建号：请显式传 --version 与 --build-number",
              file=sys.stderr)
        return 2

    # 显式期望身份必须与 edition 一致，避免把另一产品的身份当作通过标准放行。
    for option, value, key in (
        ("--package-id", args.package_id, "packageId"),
        ("--product-id", args.product_id, "productId"),
        ("--application-id", args.application_id, "applicationId"),
    ):
        expected = {"packageId": PACKAGE_IDS, "productId": PRODUCT_IDS,
                    "applicationId": APPLICATION_IDS}[key][args.edition]
        if value is not None and value != expected:
            print(f"{option} 与 edition 不符：{args.edition} 期望 {expected}，实际 {value}",
                  file=sys.stderr)
            return 2

    expectations = build_expectations(
        args.edition, version, build_number,
        product_name=args.product_name,
        app_assembly=args.app_assembly,
        package_id=args.package_id,
        product_id=args.product_id,
        display_name=args.display_name,
        application_id=args.application_id,
        publisher=args.publisher,
    )

    suffix = args.package.suffix.lower()
    if suffix in (".msixbundle", ".appxbundle"):
        print("暂不支持 MSIX bundle：请分别扫描两个产品的单包 .msix。", file=sys.stderr)
        return 2
    if suffix in (".msix", ".appx"):
        report = scan_msix(args.package, expectations,
                           require_signature=args.require_signature)
    else:
        if args.require_signature:
            print("--require-signature 只适用于 MSIX；EXE 形态不做 Authenticode 校验。",
                  file=sys.stderr)
        report = scan(args.package, expectations)
    if write_report(report, args.output):
        return 1
    print_report(report, args.output)
    return 0 if report["passed"] else 1


def _configure_streams() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


if __name__ == "__main__":
    _configure_streams()
    raise SystemExit(main())
