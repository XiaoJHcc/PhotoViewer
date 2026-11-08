#!/usr/bin/env python3
"""扫描 Android APK，核对双版本发行契约并生成文件级清单。

用法：
  python3 Tools/check-android-package.py <app.apk> --edition standard|ai --output <报告.json>
        [--version 0.9.0] [--build-number 90]
        [--package-id cc.xiaojh.PhotoViewer] [--display-name PhotoViewer] [--channel direct]
  python3 Tools/check-android-package.py --self-test

只做本地文件检查：不联网、不解密签名凭据、不安装到设备。

身份：AndroidManifest 的 package / android:label / android:versionName /
android:versionCode。包名与显示名默认按 edition 从根 Directory.Build.props 读取，
标准版与 AI 版各用自己的 applicationId；发布脚本会把同一映射显式传给扫描器。
清单是二进制 AXML，内置解析器直接读（不依赖 aapt2）；找到 aapt2 时另做一次
badging 交叉核对，找到 apksigner 时只读取校验结果（无需私钥）。

共存：系统组件 authority 必须位于实际包名的命名空间内（`<package>.…`），
否则另一发行版无法同机安装；Debug 验收 Provider 的 authority 由 ApplicationId 派生。

ABI：只允许 arm64-v8a，出现 x86_64 / armeabi-v7a 一律失败。

standard：不得包含 .onnx 模型、onnxruntime（原生、托管、AOT 或 assembly store 内记录）、
          MathNet（托管或 AOT）、DINOv3 许可证。
ai：      必须包含唯一模型 dinov3_vits16.onnx（字节数与 SHA-256 固定）、
          DINOv3-LICENSE.md、托管 Microsoft.ML.OnnxRuntime 与 MathNet.Numerics
          （普通 assemblies/、Debug 的 lib_*.dll.so、Release 的 libaot-*.so 或
          libassembly-store.so / libxamarin-app.so 内的名字表均算证据），
          以及 lib/arm64-v8a/libonnxruntime*.so。

报告固定写入 edition / productId / package / expectations / identity / components /
abis / summary / files / errors：package 是 APK 文件本身的字节数与 SHA-256，summary
按分类给出文件数、字节数与聚合 SHA-256，files 逐条给出路径、字节数与 SHA-256。
检查失败时仍写报告并以 1 退出。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import sys
import tempfile
import zipfile
from pathlib import Path

EDITIONS = ("standard", "ai")

# 方案 §2 固定的产品身份；根 Directory.Build.props 是唯一来源，这里是读取失败时的兜底。
CANONICAL_IDENTITY = {
    "standard": {
        "PhotoViewerProductId": "photoviewer.standard",
        "PhotoViewerPackageId": "cc.xiaojh.PhotoViewer",
        "PhotoViewerDisplayName": "PhotoViewer",
        "PhotoViewerDistributionName": "PhotoViewer",
        "PhotoViewerEnableDino": "false",
    },
    "ai": {
        "PhotoViewerProductId": "photoviewer.ai",
        "PhotoViewerPackageId": "cc.xiaojh.PhotoViewer.AI",
        "PhotoViewerDisplayName": "PhotoViewer AI",
        "PhotoViewerDistributionName": "PhotoViewer-AI",
        "PhotoViewerEnableDino": "true",
    },
}

MODEL_NAME = "dinov3_vits16.onnx"
MODEL_BYTES = 86_594_677
MODEL_SHA256 = "bcba38490ec2eded4a240751bebac198903daba7263871e8fee3aa39b03352e8"

TARGET_ABI = "arm64-v8a"
FORBIDDEN_ABIS = ("x86_64", "armeabi-v7a", "arm64", "x86")
DINO_LICENSE_NAME = "dinov3-license.md"

ONNX_MANAGED = "microsoft.ml.onnxruntime.dll"
MATHNET_MANAGED = "mathnet.numerics.dll"

ATTR_LABEL = "label"
ATTR_NAME = "name"
ATTR_AUTHORITIES = "authorities"
ATTR_VERSION_CODE = "versionCode"
ATTR_VERSION_NAME = "versionName"

# AXML 常量（frameworks/base/libs/androidfw/include/androidfw/ResourceTypes.h）。
RES_XML_TYPE = 0x0003
RES_STRING_POOL_TYPE = 0x0001
RES_XML_RESOURCE_MAP_TYPE = 0x0180
RES_XML_START_ELEMENT_TYPE = 0x0102
TYPE_STRING = 0x03
TYPE_INT_DEC = 0x10
TYPE_INT_HEX = 0x11
TYPE_REFERENCE = 0x01

NO_INDEX = 0xFFFFFFFF

# android.R.attr 里与本扫描相关的属性 id（name 字符串缺失时按 id 兜底）。
ANDROID_ATTR_IDS = {
    0x01010001: ATTR_LABEL,
    0x01010003: ATTR_NAME,
    0x01010010: "exported",
    0x01010018: ATTR_AUTHORITIES,
    0x0101021B: ATTR_VERSION_CODE,
    0x0101021C: ATTR_VERSION_NAME,
}

MANAGED_SUFFIX = re.compile(rb"[A-Za-z0-9_.\-]{1,120}\.dll")
STORE_BLOBS = ("libassembly-store.so", "libxamarin-app.so", "assemblies.blob")


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def human(size: int) -> str:
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024
    return f"{size} B"


IDENTITY_PROPERTIES = (
    "PhotoViewerProductId",
    "PhotoViewerPackageId",
    "PhotoViewerDisplayName",
    "PhotoViewerDistributionName",
    "PhotoViewerEnableDino",
)

PRODUCT_ID_PROPERTY = re.compile(r"<PhotoViewerProductId[^>]*>([^<]+)</PhotoViewerProductId>")


def read_root_props() -> dict:
    """读取根 Directory.Build.props 的版本号与按 edition 定义的产品身份映射。"""
    props = Path(__file__).resolve().parent.parent / "Directory.Build.props"
    values: dict = {"editions": {}}
    try:
        text = props.read_text(encoding="utf-8-sig")
    except OSError:
        return values
    for key in ("AppVersion", "AppBuildNumber"):
        match = re.search(rf"<{key}>([^<]+)</{key}>", text)
        if match:
            values[key] = match.group(1).strip()
    product = PRODUCT_ID_PROPERTY.search(text)
    for edition in EDITIONS:
        per_edition: dict = {}
        for key in IDENTITY_PROPERTIES:
            match = re.search(
                rf"<{key}\s+Condition=\"'\$\(PhotoViewerEdition\)'\s*==\s*'{edition}'\">([^<]+)</{key}>",
                text,
            )
            if match:
                per_edition[key] = match.group(1).strip()
        if product and "PhotoViewerProductId" not in per_edition:
            per_edition["PhotoViewerProductId"] = product.group(1).strip().replace(
                "$(PhotoViewerEdition)", edition
            )
        values["editions"][edition] = per_edition
    return values


_ROOT_PROPS = read_root_props()


def edition_identity(edition: str) -> dict:
    """edition 的产品身份：根 props 优先，读不到时用方案固定值兜底。"""
    identity = dict(CANONICAL_IDENTITY[edition])
    identity.update(_ROOT_PROPS.get("editions", {}).get(edition, {}))
    return identity


PRODUCT_IDS = {edition: edition_identity(edition)["PhotoViewerProductId"] for edition in EDITIONS}
PACKAGE_IDS = {edition: edition_identity(edition)["PhotoViewerPackageId"] for edition in EDITIONS}
PRODUCT_NAMES = {edition: edition_identity(edition)["PhotoViewerDisplayName"] for edition in EDITIONS}
DISTRIBUTION_NAMES = {
    edition: edition_identity(edition)["PhotoViewerDistributionName"] for edition in EDITIONS
}


# ──────────────────────────────────────────────────────────────
# 二进制 AndroidManifest（AXML）
# ──────────────────────────────────────────────────────────────


def _read_string_pool(data: bytes, offset: int) -> list[str]:
    _type, _header_size, _size = struct.unpack_from("<HHI", data, offset)
    count, _style_count, flags, strings_start, _styles_start = struct.unpack_from(
        "<IIIII", data, offset + 8
    )
    offsets_position = offset + 28
    base = offset + strings_start
    utf8 = bool(flags & 0x100)
    strings: list[str] = []
    for index in range(count):
        (relative,) = struct.unpack_from("<I", data, offsets_position + 4 * index)
        cursor = base + relative
        if utf8:
            length = data[cursor]
            cursor += 1
            if length & 0x80:
                length = ((length & 0x7F) << 8) | data[cursor]
                cursor += 1
            strings.append(data[cursor : cursor + length].decode("utf-8", "replace"))
        else:
            (length,) = struct.unpack_from("<H", data, cursor)
            cursor += 2
            if length & 0x8000:
                low, = struct.unpack_from("<H", data, cursor)
                length = ((length & 0x7FFF) << 16) | low
                cursor += 2
            strings.append(data[cursor : cursor + length * 2].decode("utf-16-le", "replace"))
    return strings


def parse_axml(data: bytes) -> list[dict]:
    """返回按出现顺序排列的起始元素 [{name, attributes:{name: value}}]。"""
    elements: list[dict] = []
    strings: list[str] = []
    resource_ids: list[int] = []
    position = 8
    while position + 8 <= len(data):
        chunk_type, chunk_header, chunk_size = struct.unpack_from("<HHI", data, position)
        if chunk_size < 8:
            break
        if chunk_type == RES_STRING_POOL_TYPE:
            strings = _read_string_pool(data, position)
        elif chunk_type == RES_XML_RESOURCE_MAP_TYPE:
            count = max(0, (chunk_size - chunk_header) // 4)
            resource_ids = list(struct.unpack_from(f"<{count}I", data, position + chunk_header))
        elif chunk_type == RES_XML_START_ELEMENT_TYPE:
            extension = position + chunk_header
            _ns, name_index = struct.unpack_from("<II", data, extension)
            attr_start, attr_size, attr_count = struct.unpack_from("<HHH", data, extension + 8)
            attributes: dict[str, object] = {}
            cursor = extension + attr_start
            for _ in range(attr_count):
                if cursor + 20 > len(data):
                    break
                _attr_ns, attr_name, _raw = struct.unpack_from("<III", data, cursor)
                _typed_size, _res0, data_type, typed_data = struct.unpack_from("<HBBI", data, cursor + 12)
                key = None
                if attr_name < len(strings) and strings[attr_name]:
                    key = strings[attr_name]
                elif attr_name < len(resource_ids) and resource_ids[attr_name] in ANDROID_ATTR_IDS:
                    key = ANDROID_ATTR_IDS[resource_ids[attr_name]]
                if key is not None:
                    if data_type == TYPE_STRING and typed_data < len(strings):
                        attributes[key] = strings[typed_data]
                    elif data_type in (TYPE_INT_DEC, TYPE_INT_HEX):
                        attributes[key] = typed_data
                    elif data_type == TYPE_REFERENCE:
                        attributes[key] = f"@{typed_data:#010x}"
                    else:
                        attributes[key] = typed_data
                cursor += attr_size
            name = strings[name_index] if name_index < len(strings) else ""
            elements.append({"name": name, "attributes": attributes})
        position += chunk_size
    return elements


def manifest_identity(data: bytes) -> dict:
    """解析 AndroidManifest，返回 package / label / versionName / versionCode。"""
    identity = {"source": "axml", "package": None, "label": None, "versionName": None,
                "versionCode": None, "providers": []}
    if len(data) >= 4 and struct.unpack_from("<H", data, 0)[0] == RES_XML_TYPE:
        elements = parse_axml(data)
    else:
        identity["source"] = "xml"
        import xml.etree.ElementTree as element_tree

        root = element_tree.fromstring(data)
        elements = []
        for node in root.iter():
            attributes = {}
            for key, value in node.attrib.items():
                attributes[key.split("}")[-1]] = value
            elements.append({"name": node.tag.split("}")[-1], "attributes": attributes})

    manifest = next((item for item in elements if item["name"] == "manifest"), None)
    if manifest is None:
        return identity
    attributes = manifest["attributes"]
    identity["package"] = attributes.get("package")
    identity["versionName"] = attributes.get(ATTR_VERSION_NAME)
    version_code = attributes.get(ATTR_VERSION_CODE)
    identity["versionCode"] = str(version_code) if version_code is not None else None

    seen_manifest = False
    for item in elements:
        if item["name"] == "manifest":
            seen_manifest = True
            continue
        if seen_manifest and item["name"] == "application":
            identity["label"] = item["attributes"].get(ATTR_LABEL)
            break

    # 系统组件：只取 authority 用于共存检查；多 authority 用分号分隔。
    providers = []
    for item in elements:
        if item["name"] != "provider":
            continue
        attributes = item["attributes"]
        authorities = str(attributes.get(ATTR_AUTHORITIES) or "")
        providers.append({
            "name": attributes.get(ATTR_NAME),
            "authorities": [part for part in authorities.split(";") if part],
            "exported": attributes.get("exported"),
        })
    identity["providers"] = providers
    return identity


# ──────────────────────────────────────────────────────────────
# 可选外部工具
# ──────────────────────────────────────────────────────────────


def find_android_tool(name: str) -> str | None:
    found = shutil.which(name)
    if found:
        return found
    roots = []
    for variable in ("ANDROID_SDK_ROOT", "ANDROID_HOME"):
        value = os.environ.get(variable)
        if value:
            roots.append(Path(value))
    roots.extend([Path.home() / "Library/Android/sdk", Path.home() / "Android/Sdk"])
    candidates: list[tuple[tuple[int, ...], Path]] = []
    for root in roots:
        build_tools = root / "build-tools"
        if not build_tools.is_dir():
            continue
        for entry in build_tools.iterdir():
            for suffix in ("", ".exe", ".bat"):
                tool = entry / f"{name}{suffix}"
                if tool.is_file():
                    version = tuple(int(part) for part in re.findall(r"\d+", entry.name)) or (0,)
                    candidates.append((version, tool))
    if not candidates:
        return None
    candidates.sort(key=lambda item: item[0], reverse=True)
    return str(candidates[0][1])


def run_tool(command: list[str]) -> tuple[int, str]:
    import subprocess

    # 固定英文输出，避免本地化把签名方案行改写成无法解析的文案。
    environment = dict(os.environ, LC_ALL="C", LANG="C")
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=120,
                                   env=environment)
    except (OSError, subprocess.SubprocessError) as exc:
        return 1, str(exc)
    return completed.returncode, (completed.stdout or "") + (completed.stderr or "")


def aapt2_identity(apk: Path) -> dict:
    tool = find_android_tool("aapt2")
    if not tool:
        return {"available": False, "tool": None}
    code, output = run_tool([tool, "dump", "badging", str(apk)])
    if code != 0:
        return {"available": True, "tool": tool, "ok": False, "output": output[-2000:]}
    package = re.search(r"^package: name='([^']*)' versionCode='([^']*)' versionName='([^']*)'",
                        output, re.MULTILINE)
    label = re.search(r"^application-label:'(.*)'$", output, re.MULTILINE)
    native = re.search(r"^native-code:\s*(.*)$", output, re.MULTILINE)
    abis = re.findall(r"'([a-z0-9_-]+)'", native.group(1)) if native else []
    return {
        "available": True,
        "tool": tool,
        "ok": True,
        "package": package.group(1) if package else None,
        "versionCode": package.group(2) if package else None,
        "versionName": package.group(3) if package else None,
        "label": label.group(1) if label else None,
        "nativeCode": abis,
    }


def apksigner_result(apk: Path) -> dict:
    tool = find_android_tool("apksigner")
    if not tool:
        return {"available": False, "tool": None}
    code, output = run_tool([tool, "verify", "--print-certs", "--verbose", str(apk)])
    schemes = {
        "v1": re.search(r"Verified using v1 scheme \(JAR signing\): (true|false)", output),
        "v2": re.search(r"Verified using v2 scheme \(APK Signature Scheme v2\): (true|false)", output),
        "v3": re.search(r"Verified using v3 scheme \(APK Signature Scheme v3\): (true|false)", output),
        "v4": re.search(r"Verified using v4 scheme \(APK Signature Scheme v4\): (true|false)", output),
    }
    certificates = []
    for match in re.finditer(r"^Signer #\d+ certificate DN: (.*)$", output, re.MULTILINE):
        certificates.append({"dn": match.group(1).strip()})
    for index, match in enumerate(re.finditer(r"^Signer #\d+ certificate SHA-256 digest: (.*)$",
                                              output, re.MULTILINE)):
        if index < len(certificates):
            certificates[index]["sha256"] = match.group(1).strip()
    return {
        "available": True,
        "tool": tool,
        "verified": code == 0,
        "schemes": {key: (value.group(1) == "true" if value else None) for key, value in schemes.items()},
        "certificates": certificates,
        "output": output.strip()[-2000:],
    }


# ──────────────────────────────────────────────────────────────
# 包内容
# ──────────────────────────────────────────────────────────────


def classify(path: str) -> str:
    lower = path.lower()
    name = lower.rsplit("/", 1)[-1]
    if path == "AndroidManifest.xml":
        return "manifest"
    if re.fullmatch(r"classes\d*\.dex", lower):
        return "dex"
    if lower.endswith(".onnx"):
        return "model"
    if name == DINO_LICENSE_NAME:
        return "license"
    if lower.startswith("lib/"):
        if "assembly-store" in name:
            return "assembly-store"
        if "onnxruntime" in name:
            managed = name.startswith("libaot-") or name.startswith("lib_")
            return "onnxruntime-managed" if managed else "onnxruntime-native"
        if "mathnet" in name:
            managed = name.startswith("libaot-") or name.startswith("lib_")
            return "mathnet-managed" if managed else "mathnet-native"
        if name.startswith("libaot-"):
            return "native-aot"
        return "native-library"
    if lower.startswith("assemblies/") or lower.endswith(".dll"):
        return "managed-assembly"
    if lower.startswith("meta-inf/"):
        return "meta"
    return "resource"


def collect(archive: zipfile.ZipFile, errors: list[str]) -> list[dict]:
    records: list[dict] = []
    for info in archive.infolist():
        if info.is_dir():
            continue
        try:
            with archive.open(info) as handle:
                digest = hashlib.sha256()
                for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                    digest.update(chunk)
        except (OSError, zipfile.BadZipFile) as exc:
            errors.append(f"{info.filename}：无法读取（{exc}）")
            records.append({"path": info.filename, "bytes": 0, "sha256": None, "category": "unreadable"})
            continue
        records.append({
            "path": info.filename,
            "bytes": info.file_size,
            "sha256": digest.hexdigest(),
            "category": classify(info.filename),
        })
    records.sort(key=lambda item: item["path"])
    return records


def abi_directories(records: list[dict]) -> list[str]:
    abis = set()
    for record in records:
        parts = record["path"].split("/")
        if len(parts) >= 3 and parts[0] == "lib":
            abis.add(parts[1])
    return sorted(abis)


def managed_evidence(archive: zipfile.ZipFile, records: list[dict], errors: list[str]) -> dict:
    """收集托管程序集名与 native 证据。"""
    managed: set[str] = set()
    store_blobs: list[str] = []
    native_onnx: list[str] = []
    for record in records:
        path = record["path"]
        lower = path.lower()
        base = lower.rsplit("/", 1)[-1]
        if lower.startswith("assemblies/") and base.endswith(".dll"):
            managed.add(base)
        elif lower.startswith("lib/") and base.endswith(".dll.so"):
            stem = base
            if stem.startswith("libaot-"):
                stem = stem[len("libaot-"):]
            elif stem.startswith("lib_"):
                stem = stem[len("lib_"):]
            stem = stem[: -len(".so")]
            if stem.endswith(".dll"):
                managed.add(stem)
        if lower.startswith("lib/") and ("onnxruntime" in base) and not (
            base.startswith("libaot-") or base.startswith("lib_")
        ):
            native_onnx.append(path)
        if base in STORE_BLOBS:
            store_blobs.append(path)

    # assembly store / app blob 里以名字表形式记录程序集，按 UTF-8 名字回读。
    for blob in store_blobs:
        try:
            data = archive.read(blob)
        except (OSError, zipfile.BadZipFile) as exc:
            errors.append(f"{blob}：无法读取程序集名字表（{exc}）")
            continue
        for match in MANAGED_SUFFIX.finditer(data):
            managed.add(match.group(0).decode("ascii").lower())

    return {
        "managedAssemblies": sorted(managed),
        "storeBlobs": store_blobs,
        "nativeOnnxRuntime": sorted(native_onnx),
    }


# ──────────────────────────────────────────────────────────────
# 契约检查
# ──────────────────────────────────────────────────────────────


def check_identity(identity: dict, expectations: dict, errors: list[str]) -> dict:
    checks = []
    fields = (
        ("package", "包名", expectations["packageId"]),
        ("label", "显示名", expectations["productName"]),
        ("versionName", "版本号", expectations["version"]),
        ("versionCode", "版本号代码", expectations["buildNumber"]),
    )
    for key, label, expected in fields:
        actual = identity.get(key)
        ok = str(actual) == str(expected) if actual is not None else False
        checks.append({"field": key, "label": label, "expected": expected, "actual": actual, "ok": ok})
        if not ok:
            errors.append(f"AndroidManifest {label}（{key}）不符：期望 {expected}，实际 {actual}")
    identity["checks"] = checks
    identity["verified"] = all(check["ok"] for check in checks)
    return identity


def check_abis(abis: list[str], errors: list[str]) -> dict:
    unexpected = [abi for abi in abis if abi != TARGET_ABI]
    if not abis:
        errors.append("APK 未包含任何 lib/<abi> 目录")
    if unexpected:
        errors.append(
            f"APK 只允许 {TARGET_ABI}，实际包含：{'、'.join(unexpected)}"
        )
    return {"found": abis, "expected": TARGET_ABI, "unexpected": unexpected}


def check_component_authorities(identity: dict, expectations: dict, errors: list[str]) -> dict:
    """系统组件 authority 必须落在实际包名命名空间内，否则两版无法同机安装。"""
    package = str(expectations["packageId"])
    providers = identity.get("providers") or []
    foreign = []
    for provider in providers:
        for authority in provider.get("authorities") or []:
            if authority != package and not authority.startswith(package + "."):
                foreign.append({"provider": provider.get("name"), "authority": authority})
                errors.append(
                    f"组件 authority 不在实际包名 {package} 的命名空间内，无法与另一发行版同机并存："
                    f"{provider.get('name')} → {authority}"
                )
    return {"providers": providers, "foreignAuthorities": foreign}


def check_standard(records: list[dict], evidence: dict, errors: list[str]) -> None:
    for record in records:
        lower = record["path"].lower()
        name = lower.rsplit("/", 1)[-1]
        reasons = []
        if lower.endswith(".onnx"):
            reasons.append("ONNX 模型")
        if "onnxruntime" in lower:
            reasons.append("ONNX Runtime 文件")
        if "mathnet" in lower:
            reasons.append("MathNet 文件")
        if name == DINO_LICENSE_NAME:
            reasons.append("DINO 模型许可证")
        if reasons:
            errors.append(f"标准版不得包含 {'、'.join(reasons)}：{record['path']}")
    for assembly in evidence["managedAssemblies"]:
        if assembly == ONNX_MANAGED:
            errors.append(f"标准版不得包含托管推理组件 Microsoft.ML.OnnxRuntime.dll（{assembly}）")
        elif assembly == MATHNET_MANAGED:
            errors.append(f"标准版不得包含 MathNet.Numerics.dll（{assembly}）")


def check_ai(records: list[dict], evidence: dict, expectations: dict, errors: list[str]) -> dict:
    contract = expectations["model"]
    models = [record for record in records if record["category"] == "model"]
    satisfied = False
    if not models:
        errors.append(
            f"AI 版缺少模型 {contract['name']}（期望 {contract['bytes']} bytes，SHA-256 {contract['sha256']}）"
        )
    else:
        if len(models) > 1:
            listed = "、".join(record["path"] for record in models)
            errors.append(f"AI 版只应包含一个 .onnx 模型，实际 {len(models)} 个：{listed}")
        match = next(
            (
                record
                for record in models
                if record["path"].rsplit("/", 1)[-1] == contract["name"]
                and record["bytes"] == contract["bytes"]
                and record["sha256"] == contract["sha256"]
            ),
            None,
        )
        if match is None:
            actual = "；".join(
                f"{record['path']}：{record['bytes']} bytes，{record['sha256']}" for record in models
            )
            errors.append(
                f"AI 模型契约不匹配（期望 {contract['name']}，{contract['bytes']} bytes，"
                f"SHA-256 {contract['sha256']}）：{actual}"
            )
        else:
            satisfied = len(models) == 1

    licenses = [record for record in records if record["category"] == "license"]
    if len(licenses) != 1:
        errors.append(f"AI 版应恰好包含一份 DINOv3-LICENSE.md，实际 {len(licenses)} 份")

    native_onnx = evidence["nativeOnnxRuntime"]
    if not native_onnx:
        errors.append(f"AI 版缺少 {TARGET_ABI} 的 libonnxruntime*.so")

    for required, label in ((ONNX_MANAGED, "Microsoft.ML.OnnxRuntime.dll"), (MATHNET_MANAGED, "MathNet.Numerics.dll")):
        if required not in evidence["managedAssemblies"]:
            errors.append(
                f"AI 版缺少托管推理组件 {label}（assemblies/、lib_*.dll.so、libaot-*.so 与 "
                f"assembly store 名字表均未发现）"
            )

    return {
        "expected": contract,
        "found": [
            {"path": record["path"], "bytes": record["bytes"], "sha256": record["sha256"]}
            for record in models
        ],
        "satisfied": satisfied,
    }


def build_summary(records: list[dict]) -> dict:
    totals: dict[str, dict] = {}
    for record in records:
        bucket = totals.setdefault(
            record["category"], {"category": record["category"], "fileCount": 0, "bytes": 0, "_digest": hashlib.sha256()}
        )
        bucket["fileCount"] += 1
        bucket["bytes"] += record["bytes"]
        bucket["_digest"].update(f"{record['path']}\0{record['sha256']}\n".encode("utf-8"))
    categories = []
    for bucket in sorted(totals.values(), key=lambda item: (-item["bytes"], item["category"])):
        categories.append({
            "category": bucket["category"],
            "fileCount": bucket["fileCount"],
            "bytes": bucket["bytes"],
            "sha256": bucket["_digest"].hexdigest(),
        })
    return {
        "categories": categories,
        "modelFiles": [
            {"path": record["path"], "bytes": record["bytes"], "sha256": record["sha256"]}
            for record in records
            if record["category"] == "model"
        ],
        "onnxRuntimeFiles": [
            {"path": record["path"], "bytes": record["bytes"], "category": record["category"]}
            for record in records
            if record["category"].startswith("onnxruntime")
        ],
    }


def scan(apk: Path, expectations: dict, external_tools: bool = True) -> dict:
    """扫描单个 APK，返回报告字典（errors 非空即未通过）。

    external_tools=False 时不调用 aapt2 / apksigner（自检用的合成包没有真实签名）。
    """
    errors: list[str] = []
    records: list[dict] = []
    identity: dict = {}
    abis: dict = {}
    components: dict = {"providers": [], "foreignAuthorities": []}
    evidence: dict = {"managedAssemblies": [], "storeBlobs": [], "nativeOnnxRuntime": []}
    model_contract = None
    aapt2: dict = {"available": False}
    signature: dict = {"available": False}
    package = {"bytes": 0, "sha256": None, "entryCount": 0}

    if not apk.is_file():
        errors.append(f"APK 不存在：{apk}")
    else:
        package["bytes"] = apk.stat().st_size
        package["sha256"] = sha256_file(apk)
        try:
            with zipfile.ZipFile(apk) as archive:
                records = collect(archive, errors)
                package["entryCount"] = len(records)
                manifest_entry = next(
                    (record for record in records if record["path"] == "AndroidManifest.xml"), None
                )
                if manifest_entry is None:
                    errors.append("APK 缺少 AndroidManifest.xml")
                    identity = {"source": None, "package": None, "label": None,
                                "versionName": None, "versionCode": None, "providers": []}
                else:
                    identity = check_identity(
                        manifest_identity(archive.read("AndroidManifest.xml")), expectations, errors
                    )
                components = check_component_authorities(identity, expectations, errors)
                abis = check_abis(abi_directories(records), errors)
                evidence = managed_evidence(archive, records, errors)
                if expectations["edition"] == "standard":
                    check_standard(records, evidence, errors)
                else:
                    model_contract = check_ai(records, evidence, expectations, errors)
        except (zipfile.BadZipFile, OSError) as exc:
            errors.append(f"无法打开 APK：{exc}")
        if external_tools:
            aapt2 = aapt2_identity(apk)
        if aapt2.get("available") and aapt2.get("ok"):
            for key, expected in (("package", expectations["packageId"]),
                                  ("versionName", expectations["version"]),
                                  ("versionCode", expectations["buildNumber"]),
                                  ("label", expectations["productName"])):
                actual = aapt2.get(key)
                if actual is not None and str(actual) != str(expected):
                    errors.append(f"aapt2 badging 的 {key} 与期望不符：期望 {expected}，实际 {actual}")
            aapt2_abis = aapt2.get("nativeCode") or []
            if aapt2_abis and sorted(aapt2_abis) != [TARGET_ABI]:
                errors.append(f"aapt2 badging 的 native-code 只允许 {TARGET_ABI}，实际：{'、'.join(aapt2_abis)}")
        if external_tools:
            signature = apksigner_result(apk)
        if signature.get("available") and not signature.get("verified"):
            errors.append("apksigner 校验未通过（APK 未签名或签名无效）")

    summary = build_summary(records)
    report = {
        "edition": expectations["edition"],
        "productId": expectations["productId"],
        "platform": expectations["platform"],
        "channel": expectations["channel"],
        "arch": TARGET_ABI,
        "apkPath": str(apk.resolve()) if apk.exists() else str(apk),
        "apkFile": apk.name,
        "package": package,
        "expectations": expectations,
        "identity": identity,
        "components": components,
        "abis": abis,
        "summary": summary,
        "fileCount": len(records),
        "totalBytes": sum(record["bytes"] for record in records),
        "aapt2": aapt2,
        "signature": signature,
        "errors": errors,
        "files": [
            {
                "path": record["path"],
                "bytes": record["bytes"],
                "sha256": record["sha256"],
                "category": record["category"],
            }
            for record in records
        ],
    }
    if expectations["edition"] == "ai":
        report["modelContract"] = model_contract
    else:
        report["managedEvidence"] = {
            "onnxRuntime": [name for name in evidence["managedAssemblies"] if name == ONNX_MANAGED],
            "mathNet": [name for name in evidence["managedAssemblies"] if name == MATHNET_MANAGED],
        }
    report["managedAssemblies"] = evidence["managedAssemblies"]
    report["nativeOnnxRuntime"] = evidence["nativeOnnxRuntime"]
    report["passed"] = not errors
    return report


def write_report(report: dict, output: Path) -> int:
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except OSError as exc:
        print(f"无法写入报告 {output}：{exc}", file=sys.stderr)
        return 1
    return 0


def print_report(report: dict, output: Path | None) -> None:
    print(f"edition：{report['edition']}")
    print(f"apk：{report['apkPath']}")
    package = report.get("package") or {}
    print(f"包大小：{package.get('bytes')}（{human(package.get('bytes') or 0)}）  SHA-256：{package.get('sha256')}")
    identity = report.get("identity") or {}
    if identity:
        print(
            "身份："
            f"{identity.get('package')} / {identity.get('label')} / "
            f"{identity.get('versionName')} ({identity.get('versionCode')})"
        )
    abis = report.get("abis") or {}
    if abis:
        print(f"ABI：{'、'.join(abis.get('found') or []) or '无'}")
    components = report.get("components") or {}
    providers = components.get("providers") or []
    if providers:
        authorities = [authority for provider in providers for authority in provider.get("authorities") or []]
        print(f"组件：{len(providers)} 个 Provider，authority：{'、'.join(authorities) or '无'}")
    print(f"文件：{report['fileCount']}  解压字节：{report['totalBytes']}（{human(report['totalBytes'])}）")
    for category in report["summary"]["categories"]:
        print(f"  {category['category']:<20} {category['fileCount']:>5}  {human(category['bytes']):>12}")
    aapt2 = report.get("aapt2") or {}
    signature = report.get("signature") or {}
    if aapt2.get("available"):
        print(f"aapt2：{aapt2.get('tool')}（{'已核对' if aapt2.get('ok') else '调用失败'}）")
    if signature.get("available"):
        print(f"apksigner：{'签名有效' if signature.get('verified') else '签名校验失败'}（{signature.get('tool')}）")
    if output is not None:
        print(f"报告：{output}")
    if report["errors"]:
        print("APK 扫描未通过：", file=sys.stderr)
        for message in report["errors"]:
            print(f"  - {message}", file=sys.stderr)
    else:
        print("APK 扫描通过")


# ──────────────────────────────────────────────────────────────
# 自检：合成 AXML + 合成 APK，覆盖正反判定
# ──────────────────────────────────────────────────────────────


def _string_pool(strings: list[str], utf8: bool) -> bytes:
    offsets: list[int] = []
    blob = bytearray()
    for text in strings:
        offsets.append(len(blob))
        if utf8:
            raw = text.encode("utf-8")
            length = len(raw)
            if length > 0x7F:
                blob += bytes([0x80 | (length >> 8), length & 0xFF])
            else:
                blob += bytes([length])
            blob += raw + b"\x00"
        else:
            raw = text.encode("utf-16-le")
            length = len(raw) // 2
            blob += struct.pack("<H", length) + raw + b"\x00\x00"
    while (28 + 4 * len(strings) + len(blob)) % 4:
        blob += b"\x00"
    strings_start = 28 + 4 * len(strings)
    size = strings_start + len(blob)
    header = struct.pack(
        "<HHIIIIII", RES_STRING_POOL_TYPE, 28, size, len(strings), 0, 0x100 if utf8 else 0,
        strings_start, 0,
    )
    return header + b"".join(struct.pack("<I", offset) for offset in offsets) + bytes(blob)


def _start_element(name: str, attributes: list[tuple[str, int, object]],
                   string_index: dict) -> bytes:
    attribute_size = 20
    extension = struct.pack(
        "<IIHHHHHH", NO_INDEX, string_index[name], 20, attribute_size,
        len(attributes), 0, 0, 0,
    )
    body = bytearray()
    for attr_name, data_type, value in attributes:
        raw_value = NO_INDEX
        if data_type == TYPE_STRING:
            raw_value = string_index[str(value)]
            typed = struct.pack("<HBBI", 8, 0, TYPE_STRING, raw_value)
        else:
            typed = struct.pack("<HBBI", 8, 0, data_type, int(value))
        body += struct.pack("<III", NO_INDEX, string_index[attr_name], raw_value) + typed
    size = 16 + len(extension) + len(body)
    header = struct.pack("<HHIII", RES_XML_START_ELEMENT_TYPE, 16, size, 0, NO_INDEX)
    return header + extension + bytes(body)


def _end_element(name: str, string_index: dict) -> bytes:
    extension = struct.pack("<III", NO_INDEX, string_index[name], NO_INDEX)
    return struct.pack("<HHIII", 0x0103, 16, 16 + len(extension), 0, NO_INDEX) + extension


def build_axml(package: str, version_code: int, version_name: str, label: str,
               utf8: bool = True, authorities: list[str] | None = None) -> bytes:
    strings = ["manifest", "application", "package", ATTR_VERSION_CODE, ATTR_VERSION_NAME, ATTR_LABEL]
    if authorities:
        strings.extend(["provider", ATTR_NAME, ATTR_AUTHORITIES])
        provider_name = package + ".SelfTestProvider"
        if provider_name not in strings:
            strings.append(provider_name)
    else:
        provider_name = ""
    for text in (package, label, version_name, *(authorities or [])):
        if text not in strings:
            strings.append(text)
    string_index = {text: index for index, text in enumerate(strings)}
    pool = _string_pool(strings, utf8)
    chunks = [
        _start_element("manifest", [
            ("package", TYPE_STRING, package),
            (ATTR_VERSION_CODE, TYPE_INT_DEC, version_code),
            (ATTR_VERSION_NAME, TYPE_STRING, version_name),
        ], string_index),
        _start_element("application", [(ATTR_LABEL, TYPE_STRING, label)], string_index),
    ]
    if authorities:
        chunks.append(_start_element("provider", [
            (ATTR_NAME, TYPE_STRING, provider_name),
            (ATTR_AUTHORITIES, TYPE_STRING, ";".join(authorities)),
        ], string_index))
        chunks.append(_end_element("provider", string_index))
    chunks.append(_end_element("application", string_index))
    chunks.append(_end_element("manifest", string_index))
    body = pool + b"".join(chunks)
    return struct.pack("<HHI", RES_XML_TYPE, 8, 8 + len(body)) + body


def make_apk(path: Path, package: str, version_code: int, version_name: str, label: str,
             extra: dict[str, bytes] | None = None, abi: str = TARGET_ABI, utf8: bool = True,
             authorities: list[str] | None = None) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("AndroidManifest.xml",
                         build_axml(package, version_code, version_name, label, utf8=utf8,
                                    authorities=authorities))
        archive.writestr("classes.dex", b"dex\n035\0")
        archive.writestr(f"lib/{abi}/libmonodroid.so", b"\x7fELF")
        for name, data in (extra or {}).items():
            archive.writestr(name, data)
    return path


def self_test() -> int:
    """用合成 APK 校验扫描器的正反判定（真实包仍由发布扫描覆盖）。"""
    version, build = "9.9.9", 999
    failures: list[str] = []
    cases: list[tuple[str, dict, bool, str | None]] = []

    # 自检用小模型替换固定契约；仍断言正式常量与根 props 的身份映射一致。
    if MODEL_BYTES != 86_594_677 or len(MODEL_SHA256) != 64:
        failures.append("模型契约常量被意外改动")
    for edition in EDITIONS:
        parsed = _ROOT_PROPS.get("editions", {}).get(edition, {})
        if not parsed:
            continue
        for key, expected in CANONICAL_IDENTITY[edition].items():
            actual = parsed.get(key)
            if actual != expected:
                failures.append(
                    f"Directory.Build.props 的 {edition} {key} 与方案身份不符：期望 {expected}，实际 {actual}"
                )
    if PACKAGE_IDS["standard"] == PACKAGE_IDS["ai"]:
        failures.append("标准版与 AI 版的包名相同，无法同机并存")
    if PRODUCT_NAMES["ai"] != "PhotoViewer AI":
        failures.append(f"AI 版显示名应为 PhotoViewer AI，实际 {PRODUCT_NAMES['ai']}")
    for edition in EDITIONS:
        if PACKAGE_IDS[edition] != CANONICAL_IDENTITY[edition]["PhotoViewerPackageId"]:
            failures.append(f"{edition} 包名与方案身份不符：{PACKAGE_IDS[edition]}")

    tiny_model = b"onnx-self-test" * 64
    tiny_contract = {"name": MODEL_NAME, "bytes": len(tiny_model), "sha256": sha256_bytes(tiny_model)}

    def expectations(edition: str) -> dict:
        return {
            "edition": edition,
            "productId": PRODUCT_IDS[edition],
            "productName": PRODUCT_NAMES[edition],
            "distributionName": DISTRIBUTION_NAMES[edition],
            "version": version,
            "buildNumber": build,
            "packageId": PACKAGE_IDS[edition],
            "platform": "android",
            "channel": "direct",
            "model": tiny_contract,
        }

    with tempfile.TemporaryDirectory(prefix="pv-android-scan-selftest-") as tmp:
        root = Path(tmp)

        def apk(name: str, *, edition: str = "standard", label: str | None = None,
                extra: dict[str, bytes] | None = None,
                abi: str = TARGET_ABI, version_name: str = version, version_code: int = build,
                package_id: str | None = None, utf8: bool = True,
                authorities: list[str] | None = None) -> Path:
            return make_apk(root / f"{name}/{name}.apk", package_id or PACKAGE_IDS[edition],
                            version_code, version_name, label or PRODUCT_NAMES[edition],
                            extra=extra, abi=abi, utf8=utf8, authorities=authorities)

        standard_ok = apk("standard-ok", extra={"assemblies/PhotoViewer.dll": b"MZ"})
        cases.append(("标准版合规包通过", standard_ok, "standard", True, None))

        cases.append((
            "标准版 UTF-16 字符串池通过",
            apk("standard-utf16", utf8=False), "standard", True, None,
        ))
        cases.append((
            "标准版残留模型失败",
            apk("standard-model", extra={f"assets/Models/{MODEL_NAME}": tiny_model}),
            "standard", False, "标准版不得包含 ONNX 模型",
        ))
        cases.append((
            "标准版残留 ORT 原生库失败",
            apk("standard-ort", extra={f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF"}),
            "standard", False, "ONNX Runtime",
        ))
        cases.append((
            "标准版残留 ORT AOT 失败",
            apk("standard-ort-aot", extra={f"lib/{TARGET_ABI}/libaot-Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF"}),
            "standard", False, "ONNX Runtime",
        ))
        cases.append((
            "标准版残留 MathNet AOT 失败",
            apk("standard-mathnet", extra={f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF"}),
            "standard", False, "MathNet",
        ))
        cases.append((
            "标准版 assembly store 内残留 ORT 失败",
            apk("standard-store", extra={
                f"lib/{TARGET_ABI}/libassembly-store.so": b"\x00Microsoft.ML.OnnxRuntime.dll\x00",
            }),
            "standard", False, "托管推理组件",
        ))
        cases.append((
            "标准版残留许可证失败",
            apk("standard-license", extra={"assets/Models/DINOv3-LICENSE.md": b"license"}),
            "standard", False, "DINO 模型许可证",
        ))
        cases.append((
            "标准版带 x86_64 失败",
            apk("standard-x86", abi="x86_64"), "standard", False, "只允许 arm64-v8a",
        ))
        cases.append((
            "标准版误用 AI 显示名失败",
            apk("standard-label", label=PRODUCT_NAMES["ai"]), "standard", False, "显示名",
        ))
        cases.append((
            "标准版误用 AI 包名失败",
            apk("standard-package-ai", package_id=PACKAGE_IDS["ai"]), "standard", False, "包名",
        ))
        cases.append((
            "标准版组件 authority 在同一包名命名空间内通过",
            apk("standard-authority", authorities=[PACKAGE_IDS["standard"] + ".debug.storage"]),
            "standard", True, None,
        ))
        cases.append((
            "版本号不符失败",
            apk("bad-version", version_name="0.0.1"), "standard", False, "版本号",
        ))
        cases.append((
            "版本代码不符失败",
            apk("bad-code", version_code=1), "standard", False, "版本号代码",
        ))
        cases.append((
            "包名不符失败",
            apk("bad-package", package_id="cc.xiaojh.Other"), "standard", False, "包名",
        ))

        ai_extra = {
            "assets/Models/DINOv3-LICENSE.md": b"license",
            f"assets/Models/{MODEL_NAME}": tiny_model,
            f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF",
            f"lib/{TARGET_ABI}/libaot-Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF",
            f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF",
        }
        cases.append((
            "AI 版 AOT 布局合规通过",
            apk("ai-ok-aot", edition="ai", extra=ai_extra), "ai", True, None,
        ))

        store_extra = {
            "assets/Models/DINOv3-LICENSE.md": b"license",
            f"assets/Models/{MODEL_NAME}": tiny_model,
            f"lib/{TARGET_ABI}/libonnxruntime4j_jni.so": b"\x7fELF",
            f"lib/{TARGET_ABI}/libassembly-store.so":
                b"\x00Microsoft.ML.OnnxRuntime.dll\x00MathNet.Numerics.dll\x00PhotoViewer.dll\x00",
        }
        cases.append((
            "AI 版 assembly store 证据通过",
            apk("ai-ok-store", edition="ai", extra=store_extra), "ai", True, None,
        ))

        debug_extra = {
            "assets/Models/DINOv3-LICENSE.md": b"license",
            f"assets/Models/{MODEL_NAME}": tiny_model,
            f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF",
            f"lib/{TARGET_ABI}/lib_Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF",
            f"lib/{TARGET_ABI}/lib_MathNet.Numerics.dll.so": b"\x7fELF",
        }
        cases.append((
            "AI 版 Debug 每程序集布局通过",
            apk("ai-ok-debug", edition="ai", extra=debug_extra), "ai", True, None,
        ))

        cases.append((
            "AI 版缺模型失败",
            apk("ai-no-model", edition="ai", extra={
                "assets/Models/DINOv3-LICENSE.md": b"license",
                f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF",
            }), "ai", False, "缺少模型",
        ))
        cases.append((
            "AI 版模型哈希不符失败",
            apk("ai-bad-hash", edition="ai", extra={
                "assets/Models/DINOv3-LICENSE.md": b"license",
                f"assets/Models/{MODEL_NAME}": tiny_model + b"x",
                f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF",
            }), "ai", False, "模型契约不匹配",
        ))
        cases.append((
            "AI 版缺许可证失败",
            apk("ai-no-license", edition="ai", extra={
                f"assets/Models/{MODEL_NAME}": tiny_model,
                f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF",
            }), "ai", False, "DINOv3-LICENSE.md",
        ))
        cases.append((
            "AI 版缺 ORT 原生库失败",
            apk("ai-no-native", edition="ai", extra={
                "assets/Models/DINOv3-LICENSE.md": b"license",
                f"assets/Models/{MODEL_NAME}": tiny_model,
                f"lib/{TARGET_ABI}/libaot-Microsoft.ML.OnnxRuntime.dll.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF",
            }), "ai", False, "libonnxruntime",
        ))
        cases.append((
            "AI 版缺托管 ORT 失败",
            apk("ai-no-managed", edition="ai", extra={
                "assets/Models/DINOv3-LICENSE.md": b"license",
                f"assets/Models/{MODEL_NAME}": tiny_model,
                f"lib/{TARGET_ABI}/libonnxruntime.so": b"\x7fELF",
                f"lib/{TARGET_ABI}/libaot-MathNet.Numerics.dll.so": b"\x7fELF",
            }), "ai", False, "Microsoft.ML.OnnxRuntime.dll",
        ))
        cases.append((
            "AI 版误用标准版显示名失败",
            apk("ai-label", label=PRODUCT_NAMES["standard"], extra=ai_extra), "ai", False, "显示名",
        ))
        cases.append((
            "AI 版误用标准版包名失败",
            apk("ai-package-standard", package_id=PACKAGE_IDS["standard"], extra=ai_extra),
            "ai", False, "包名",
        ))
        cases.append((
            "AI 版沿用标准版组件 authority 失败",
            apk("ai-foreign-authority", extra=ai_extra,
                authorities=[PACKAGE_IDS["standard"] + ".debug.storage"]),
            "ai", False, "authority",
        ))
        cases.append((
            "AI 版误带 x86_64 失败",
            make_apk(root / "ai-x86/ai-x86.apk", PACKAGE_IDS["ai"], build, version,
                     PRODUCT_NAMES["ai"], extra=ai_extra, abi="x86_64"),
            "ai", False, "只允许 arm64-v8a",
        ))

        for label, apk_path, edition, expect_pass, needle in cases:
            report = scan(apk_path, expectations(edition), external_tools=False)
            passed = report["passed"]
            detail = "；".join(report["errors"][:3])
            if passed != expect_pass:
                failures.append(
                    f"{label}：期望 {'通过' if expect_pass else '失败'}，实际 {'通过' if passed else '失败'}（{detail}）"
                )
            elif needle and not any(needle in message for message in report["errors"]):
                failures.append(f"{label}：错误信息未包含「{needle}」，实际：{detail}")
            else:
                print(f"  [{'OK' if passed else 'OK（已拒绝）'}] {label}")

        # 报告字段完整性与文件级摘要
        report = scan(standard_ok, expectations("standard"), external_tools=False)
        for key in ("productId", "platform", "channel", "package", "identity", "components",
                    "abis", "summary", "files", "errors", "passed"):
            if key not in report:
                failures.append(f"报告缺少字段：{key}")
        if report["identity"].get("package") != PACKAGE_IDS["standard"]:
            failures.append("报告身份未记录实际包名")
        if report["components"].get("foreignAuthorities"):
            failures.append("合规包的组件 authority 被误判为跨包")
        if not report["package"].get("sha256"):
            failures.append("报告缺少 APK 文件 SHA-256")
        if not report["summary"]["categories"]:
            failures.append("报告缺少分类摘要")
        if not report["files"] or not all(item.get("sha256") for item in report["files"]):
            failures.append("报告文件清单缺少 SHA-256")

    if failures:
        print("自检未通过：", file=sys.stderr)
        for message in failures:
            print(f"  - {message}", file=sys.stderr)
        return 1
    print(f"自检通过（{len(cases)} 项）")
    return 0


def main() -> int:
    argv = sys.argv[1:]
    if "--self-test" in argv:
        return self_test()

    parser = argparse.ArgumentParser(description="扫描 Android APK 并核对双版本发行契约")
    parser.add_argument("apk", type=Path, help="待扫描的 APK 路径")
    parser.add_argument("--edition", required=True, choices=EDITIONS, help="发行版本")
    parser.add_argument("--output", required=True, type=Path, help="JSON 报告路径")
    parser.add_argument("--version", help="期望的 versionName；缺省取根 Directory.Build.props")
    parser.add_argument("--build-number", help="期望的 versionCode；缺省取根 Directory.Build.props")
    parser.add_argument("--package-id",
                        help="期望的包名；缺省按 edition 从根 Directory.Build.props 读取")
    parser.add_argument("--display-name",
                        help="期望的显示名；缺省按 edition 从根 Directory.Build.props 读取")
    parser.add_argument("--channel", default="direct", help="发行渠道（默认 direct）")
    args = parser.parse_args(argv)

    props = read_root_props()
    version = args.version or props.get("AppVersion")
    build_number = args.build_number or props.get("AppBuildNumber")
    identity = edition_identity(args.edition)
    package_id = args.package_id or identity["PhotoViewerPackageId"]
    display_name = args.display_name or identity["PhotoViewerDisplayName"]
    if not version or not build_number:
        print("无法确定期望的版本号/构建号：请显式传 --version 与 --build-number", file=sys.stderr)
        return 2
    if not package_id or not display_name:
        print("无法确定期望的包名/显示名：请显式传 --package-id 与 --display-name", file=sys.stderr)
        return 2

    expectations = {
        "edition": args.edition,
        "productId": identity["PhotoViewerProductId"],
        "productName": display_name,
        "distributionName": identity["PhotoViewerDistributionName"],
        "platform": "android",
        "channel": args.channel,
        "version": version,
        "buildNumber": build_number,
        "packageId": package_id,
        "model": {"name": MODEL_NAME, "bytes": MODEL_BYTES, "sha256": MODEL_SHA256},
    }
    report = scan(args.apk, expectations)
    if write_report(report, args.output):
        return 1
    print_report(report, args.output)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
