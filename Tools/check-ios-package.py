#!/usr/bin/env python3
"""扫描 iOS .app（或 .ipa）包，核对发行版本契约并生成文件级清单。

用法：
  python3 Tools/check-ios-package.py <App.app|App.ipa> --edition standard|ai
        [--output <报告.json>] [--version 0.9.0] [--build-number 90]
        [--product-name PhotoViewer|"PhotoViewer AI"] [--bundle-id cc.xiaojh.PhotoViewer]
        [--executable PhotoViewer.iOS]
  python3 Tools/check-ios-package.py --self-test

只做本地文件检查：不签名、不安装、不联网。输入可以是解包后的 .app，也可以是 IPA
（扫描时临时解到系统临时目录，Payload 内必须恰好一个 .app；IPA 顶层的 Symbols/
只统计存在性与字节数，不参与版本判定）。

身份：CFBundleName / CFBundleDisplayName / CFBundleShortVersionString / CFBundleVersion /
CFBundleIdentifier / CFBundleExecutable，包目录名 PhotoViewer.iOS.app，以及主执行文件
PhotoViewer.iOS 的 arm64 可执行 Mach-O 头。版本号与构建号缺省取根 Directory.Build.props。

能力边界（用真实 nm / otool 检查主执行文件，不只看文件名）：
standard：不得包含 .onnx 模型、任何文件名含 onnxruntime 的托管/原生文件、MathNet.Numerics、
          DINOv3 许可证；主执行文件不得出现 _Ort* 符号，也不得动态依赖 onnxruntime。
ai：      必须包含精确模型 dinov3_vits16.onnx（字节数与 SHA-256 固定）、DINOv3-LICENSE.md、
          Microsoft.ML.OnnxRuntime.dll 与 MathNet.Numerics.dll，并且主执行文件里有
          ONNX Runtime 静态符号 _OrtGetApiBase（只有同名文件而主执行文件无符号视为未通过）。

报告固定写入 edition / input / expectations / identity / native / fileCount / totalBytes /
files / errors，另附按分类的体积摘要。检查失败时仍写报告并以 1 退出。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

MODEL_NAME = "dinov3_vits16.onnx"
MODEL_BYTES = 86_594_677
MODEL_SHA256 = "bcba38490ec2eded4a240751bebac198903daba7263871e8fee3aa39b03352e8"

# 产品显示名（AI 版含空格）；CFBundleName/DisplayName 与设备主屏名称同值。
# 分发包文件名另用 PhotoViewer-AI，只出现在 publish.sh 的 PACKAGE_PRODUCT 与 release/packages。
PRODUCT_NAMES = {"standard": "PhotoViewer", "ai": "PhotoViewer AI"}
APP_BUNDLE_NAME = "PhotoViewer.iOS.app"
DEFAULT_BUNDLE_ID = "cc.xiaojh.PhotoViewer"
DEFAULT_EXECUTABLE = "PhotoViewer.iOS"
DINO_LICENSE_NAME = "DINOv3-LICENSE.md"

AI_MANAGED_REQUIRED = ("Microsoft.ML.OnnxRuntime.dll", "MathNet.Numerics.dll")
AI_REQUIRED_SYMBOL = "_OrtGetApiBase"
ORT_SYMBOL_PREFIX = "_Ort"

IDENTITY_KEYS = (
    ("CFBundleName", "产品名"),
    ("CFBundleDisplayName", "显示名"),
    ("CFBundleShortVersionString", "版本号"),
    ("CFBundleVersion", "构建号"),
    ("CFBundleIdentifier", "Bundle ID"),
    ("CFBundleExecutable", "可执行文件名"),
)

# Mach-O 头常量（见 <mach-o/loader.h> 与 <mach-o/fat.h>）。
MH_MAGIC = 0xFEEDFACE
MH_MAGIC_64 = 0xFEEDFACF
FAT_MAGIC = 0xCAFEBABE
FAT_MAGIC_64 = 0xCAFEBABF
MH_EXECUTE = 0x2
CPU_TYPE_ARM64 = 0x0100000C


def sha256(path: Path) -> str:
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


def _read_thin_header(handle, offset: int):
    handle.seek(offset)
    raw = handle.read(16)
    if len(raw) < 16:
        return None
    if struct.unpack(">I", raw[:4])[0] in (MH_MAGIC, MH_MAGIC_64):
        endian = ">"
    elif struct.unpack("<I", raw[:4])[0] in (MH_MAGIC, MH_MAGIC_64):
        endian = "<"
    else:
        return None
    _, cputype, _, filetype = struct.unpack(endian + "IIII", raw)
    return cputype, filetype


def macho_architectures(path: Path) -> list[tuple[int, int]]:
    """返回 [(cputype, filetype)]；非 Mach-O 文件返回空列表。"""
    try:
        with path.open("rb") as handle:
            head = handle.read(8)
            if len(head) < 8:
                return []
            magic = struct.unpack(">I", head[:4])[0]
            if magic in (FAT_MAGIC, FAT_MAGIC_64):
                count = struct.unpack(">I", head[4:8])[0]
                entry_size = 32 if magic == FAT_MAGIC_64 else 20
                architectures = []
                handle.seek(8)
                for _ in range(min(count, 32)):
                    entry = handle.read(entry_size)
                    if len(entry) < entry_size:
                        break
                    if magic == FAT_MAGIC_64:
                        offset = struct.unpack(">Q", entry[8:16])[0]
                    else:
                        offset = struct.unpack(">I", entry[8:12])[0]
                    header = _read_thin_header(handle, offset)
                    if header:
                        architectures.append(header)
                return architectures
            header = _read_thin_header(handle, 0)
            return [header] if header else []
    except OSError:
        return []


def classify(rel: str, name: str, architectures: list[tuple[int, int]]) -> str:
    lower = name.lower()
    if lower.endswith(".onnx"):
        return "model"
    if "onnxruntime" in lower:
        if lower.endswith(".dll") and lower.startswith("microsoft.ml.onnxruntime"):
            return "onnxruntime-managed"
        if lower.endswith(".aotdata.arm64"):
            return "onnxruntime-aot"
        if lower.endswith((".dylib", ".so", ".a", ".framework")):
            return "onnxruntime-native"
        return "onnxruntime-other"
    if lower.endswith(".aotdata.arm64"):
        return "aot-data"
    if lower.endswith(".dll"):
        return "managed-assembly"
    if lower.endswith((".pdb", ".mdb")):
        return "debug-symbols"
    if ".framework/" in rel.lower() or lower.endswith(".dylib"):
        return "framework" if architectures else "resource"
    if architectures:
        return "native-binary"
    return "resource"


def is_windows_onnxruntime(name: str) -> bool:
    lower = name.lower()
    return lower.endswith(".dll") and "onnxruntime" in lower and not lower.startswith(
        "microsoft.ml.onnxruntime"
    )


def dir_bytes(path: Path) -> int:
    total = 0
    for root, _dirs, names in os.walk(path, followlinks=False):
        for name in names:
            try:
                total += (Path(root) / name).stat().st_size
            except OSError:
                pass
    return total


def collect(app: Path, errors: list[str]) -> list[dict]:
    records: list[dict] = []
    for root, _dirs, names in os.walk(app, followlinks=False):
        for name in sorted(names):
            path = Path(root) / name
            rel = path.relative_to(app).as_posix()
            link_target = None
            read_path = path
            if path.is_symlink():
                link_target = os.readlink(path)
                try:
                    resolved = path.resolve(strict=True)
                except OSError:
                    errors.append(f"{rel}：符号链接目标不存在（{link_target}）")
                    records.append({"path": rel, "bytes": 0, "sha256": None, "category": "unreadable",
                                    "name": name, "architectures": [], "symlink": link_target})
                    continue
                if not resolved.is_file():
                    errors.append(f"{rel}：符号链接未指向普通文件（{link_target}）")
                    records.append({"path": rel, "bytes": 0, "sha256": None, "category": "unreadable",
                                    "name": name, "architectures": [], "symlink": link_target})
                    continue
                read_path = resolved
            try:
                size = read_path.stat().st_size
                digest = sha256(read_path)
            except OSError as exc:
                errors.append(f"{rel}：无法读取（{exc}）")
                records.append({"path": rel, "bytes": 0, "sha256": None, "category": "unreadable",
                                "name": name, "architectures": [], "symlink": link_target})
                continue
            architectures = macho_architectures(read_path)
            record = {
                "path": rel,
                "bytes": size,
                "sha256": digest,
                "category": classify(rel, name, architectures),
                "name": name,
                "architectures": architectures,
            }
            if link_target is not None:
                record["symlink"] = link_target
            records.append(record)
    records.sort(key=lambda item: item["path"])
    return records


def build_summary(records: list[dict]) -> dict:
    totals: dict[str, dict] = {}
    for record in records:
        bucket = totals.setdefault(record["category"], {"category": record["category"], "fileCount": 0, "bytes": 0})
        bucket["fileCount"] += 1
        bucket["bytes"] += record["bytes"]
    categories = sorted(totals.values(), key=lambda item: (-item["bytes"], item["category"]))
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


def run_tool(argv: list[str]) -> tuple[int, str, str]:
    try:
        completed = subprocess.run(argv, capture_output=True, text=True, check=False)
    except OSError as exc:
        return 127, "", str(exc)
    return completed.returncode, completed.stdout, completed.stderr


def symbol_names(executable: Path, errors: list[str]) -> list[str] | None:
    code, stdout, stderr = run_tool(["nm", "-arch", "arm64", "-j", str(executable)])
    if code != 0:
        errors.append(f"无法读取主执行文件符号（nm -arch arm64）：{stderr.strip() or code}")
        return None
    return [line.strip() for line in stdout.splitlines() if line.strip()]


def dynamic_dependencies(executable: Path, errors: list[str]) -> list[str]:
    code, stdout, stderr = run_tool(["otool", "-L", str(executable)])
    if code != 0:
        errors.append(f"无法读取主执行文件动态依赖（otool -L）：{stderr.strip() or code}")
        return []
    dependencies: list[str] = []
    for line in stdout.splitlines()[1:]:
        candidate = line.strip().split(" (")[0].strip()
        if candidate:
            dependencies.append(candidate)
    return dependencies


def inspect_native(app: Path, expectations: dict, errors: list[str]) -> dict:
    executable = app / expectations["executable"]
    result: dict = {
        "executablePath": str(executable),
        "exists": executable.is_file(),
        "architectures": [],
        "arm64Executable": False,
        "symbolsScanned": False,
        "ortSymbols": [],
        "dynamicLibraries": [],
        "ortDynamicDependencies": [],
        "sha256": None,
    }
    if not executable.is_file():
        errors.append(f"缺少主程序 {expectations['executable']}")
        return result

    try:
        result["sha256"] = sha256(executable)
    except OSError as exc:
        errors.append(f"无法读取主程序：{exc}")
        return result

    architectures = macho_architectures(executable)
    result["architectures"] = [
        {"cputype": hex(cputype), "filetype": filetype} for cputype, filetype in architectures
    ]
    arm64_executable = any(
        cputype == CPU_TYPE_ARM64 and filetype == MH_EXECUTE for cputype, filetype in architectures
    )
    result["arm64Executable"] = arm64_executable
    if not arm64_executable:
        errors.append(f"主程序不是 arm64 可执行 Mach-O：{executable}（{architectures}）")

    symbols = symbol_names(executable, errors)
    if symbols is not None:
        result["symbolsScanned"] = True
        result["ortSymbols"] = sorted(name for name in symbols if name.startswith(ORT_SYMBOL_PREFIX))
    code, defined, stderr = run_tool(["nm", "-arch", "arm64", "-j", "-U", str(executable)])
    if code != 0:
        errors.append(f"无法读取主执行文件已定义符号（nm -U）：{stderr.strip() or code}")
    result["ortDefinedSymbols"] = sorted(
        name.strip() for name in defined.splitlines() if name.strip().startswith(ORT_SYMBOL_PREFIX)
    )

    dependencies = dynamic_dependencies(executable, errors)
    result["dynamicLibraries"] = dependencies
    result["ortDynamicDependencies"] = [dep for dep in dependencies if "onnxruntime" in dep.lower()]
    return result


def check_identity(app: Path, expectations: dict, errors: list[str], native: dict) -> dict:
    app_name = app.name
    if app_name != APP_BUNDLE_NAME:
        errors.append(f"应用包目录名应为 {APP_BUNDLE_NAME}，实际 {app_name}")

    info_plist = app / "Info.plist"
    identity = {
        "appBundle": app_name,
        "infoPlistPath": str(info_plist),
        "infoPlist": {},
        "checks": [],
        "verified": False,
    }
    if not info_plist.is_file():
        errors.append("缺少 Info.plist")
        return identity

    try:
        plist = plistlib.loads(info_plist.read_bytes())
    except (OSError, plistlib.InvalidFileException) as exc:
        errors.append(f"无法解析 Info.plist：{exc}")
        return identity
    if not isinstance(plist, dict):
        errors.append("Info.plist 顶层不是字典")
        return identity

    expected = {
        "CFBundleName": expectations["productName"],
        "CFBundleDisplayName": expectations["productName"],
        "CFBundleShortVersionString": expectations["version"],
        "CFBundleVersion": expectations["buildNumber"],
        "CFBundleIdentifier": expectations["bundleId"],
        "CFBundleExecutable": expectations["executable"],
    }
    identity["infoPlist"] = {key: plist.get(key) for key, _label in IDENTITY_KEYS}
    for key, label in IDENTITY_KEYS:
        actual = plist.get(key)
        want = expected[key]
        ok = actual == want
        identity["checks"].append({"field": key, "label": label, "expected": want, "actual": actual, "ok": ok})
        if not ok:
            errors.append(f"Info.plist {label}（{key}）不符：期望 {want}，实际 {actual}")

    identity["mainExecutable"] = native
    identity["verified"] = (
        all(check["ok"] for check in identity["checks"]) and bool(native.get("arm64Executable"))
    )
    return identity


def check_standard(records: list[dict], native: dict, errors: list[str]) -> None:
    for record in records:
        lower = record["name"].lower()
        reasons = []
        if lower.endswith(".onnx"):
            reasons.append("ONNX 模型")
        if "onnxruntime" in lower:
            reasons.append("ONNX Runtime 文件")
        if "directml" in lower:
            reasons.append("DirectML 文件")
        if record["name"].startswith("MathNet.Numerics"):
            reasons.append("MathNet.Numerics 推理依赖")
        if record["name"] == DINO_LICENSE_NAME:
            reasons.append("DINO 模型许可证")
        if reasons:
            errors.append(f"标准版不得包含 {'、'.join(reasons)}：{record['path']}")

    if native.get("ortSymbols"):
        errors.append(
            "标准版主执行文件含 ONNX Runtime 符号：" + "、".join(native["ortSymbols"])
        )
    if native.get("ortDynamicDependencies"):
        errors.append(
            "标准版主执行文件动态依赖 ONNX Runtime：" + "、".join(native["ortDynamicDependencies"])
        )
    if not native.get("symbolsScanned"):
        errors.append("无法读取主执行文件符号，不能证明标准版没有链接 ONNX Runtime")


def check_ai(records: list[dict], native: dict, expectations: dict, errors: list[str]) -> bool:
    contract = expectations["modelContract"]
    for record in records:
        if is_windows_onnxruntime(record["name"]):
            errors.append(f"AI 版不得包含 Windows ONNX Runtime DLL：{record['path']}")

    licenses = [record for record in records if record["name"] == DINO_LICENSE_NAME]
    if len(licenses) != 1:
        errors.append(f"AI 版应恰好包含一份 {DINO_LICENSE_NAME}，实际 {len(licenses)} 份")

    for required in AI_MANAGED_REQUIRED:
        matches = [record for record in records if record["name"] == required]
        if len(matches) != 1:
            errors.append(f"AI 版应恰好包含一份托管推理组件 {required}，实际 {len(matches)} 份")

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
                if record["name"] == contract["name"]
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

    if not native.get("symbolsScanned"):
        errors.append("无法读取主执行文件符号，不能证明 AI 版静态链接了 ONNX Runtime")
    elif AI_REQUIRED_SYMBOL not in native.get("ortDefinedSymbols", []):
        errors.append(
            f"AI 版主执行文件缺少 ONNX Runtime 已定义静态符号 {AI_REQUIRED_SYMBOL}"
            "（未定义导入、同名文件或托管 dll 不能代替静态链接实现）"
        )
    return satisfied


def materialize(target: Path, errors: list[str]) -> tuple[Path | None, dict, str | None]:
    """返回 (app 目录, 输入信息, 需要清理的临时目录)。"""
    info: dict = {"kind": "app", "path": str(target)}
    if target.is_dir():
        if target.suffix == ".app":
            return target, info, None
        errors.append(f"目标不是 .app 目录：{target}")
        return None, info, None

    if target.is_file() and target.suffix.lower() == ".ipa":
        tempdir = tempfile.mkdtemp(prefix="pv-ios-scan-")
        extraction = Path(tempdir)
        code, _stdout, stderr = run_tool(["ditto", "-x", "-k", str(target), str(extraction)])
        if code != 0:
            code, _stdout, stderr2 = run_tool(["unzip", "-q", str(target), "-d", str(extraction)])
            if code != 0:
                errors.append(f"无法解包 IPA：{stderr.strip() or stderr2.strip()}")
                return None, info, tempdir
        payload = extraction / "Payload"
        apps = sorted(payload.glob("*.app")) if payload.is_dir() else []
        symbols = extraction / "Symbols"
        info = {
            "kind": "ipa",
            "path": str(target),
            "ipaPath": str(target),
            "symbolsPresent": symbols.is_dir(),
            "symbolsBytes": dir_bytes(symbols) if symbols.is_dir() else 0,
            "payloadAppBundles": [app.name for app in apps],
        }
        if len(apps) != 1:
            errors.append(f"IPA 的 Payload/ 内应恰好一个 .app，实际 {len(apps)} 个：{info['payloadAppBundles']}")
            return None, info, tempdir
        return apps[0], info, tempdir
    errors.append(f"目标不存在或不是 .app / .ipa：{target}")
    return None, info, None


def scan(target: Path, expectations: dict) -> dict:
    """扫描单个 .app 或 .ipa，返回报告字典（errors 非空即未通过）。"""
    errors: list[str] = []
    records: list[dict] = []
    model_satisfied = False
    identity: dict = {}
    native: dict = {}
    input_info: dict = {}

    app, input_info, tempdir = materialize(target, errors)
    try:
        if app is not None:
            records = collect(app, errors)
            for record in records:
                if record["architectures"] and not any(
                    cputype == CPU_TYPE_ARM64 for cputype, _filetype in record["architectures"]
                ):
                    errors.append(f"非 arm64 Mach-O：{record['path']}")
            native = inspect_native(app, expectations, errors)
            identity = check_identity(app, expectations, errors, native)
            input_info["appPath"] = str(app.resolve())
            if expectations["edition"] == "standard":
                check_standard(records, native, errors)
            else:
                model_satisfied = check_ai(records, native, expectations, errors)
    finally:
        if tempdir:
            shutil.rmtree(tempdir, ignore_errors=True)

    summary = build_summary(records)
    report = {
        "edition": expectations["edition"],
        "input": input_info,
        "appPath": input_info.get("appPath"),
        "expectations": expectations,
        "identity": identity,
        "native": native,
        "fileCount": len(records),
        "totalBytes": sum(record["bytes"] for record in records),
        "summary": summary,
        "files": [
            {
                "path": record["path"],
                "bytes": record["bytes"],
                "sha256": record["sha256"],
                "category": record["category"],
                **({"symlink": record["symlink"]} if "symlink" in record else {}),
            }
            for record in records
        ],
        "errors": errors,
    }
    if expectations["edition"] == "ai":
        report["modelContract"] = {
            "expected": expectations["modelContract"],
            "found": summary["modelFiles"],
            "satisfied": model_satisfied,
        }
    report["passed"] = not errors
    return report


def write_report(report: dict, output: Path | None) -> int:
    if output is None:
        return 0
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except OSError as exc:
        print(f"无法写入报告 {output}：{exc}", file=sys.stderr)
        return 1
    return 0


def print_report(report: dict, output: Path | None) -> None:
    print(f"edition：{report['edition']}")
    print(f"输入：{report['input'].get('kind')} {report['input'].get('path')}")
    if report["input"].get("kind") == "ipa":
        print(
            "IPA Symbols："
            f"{'有' if report['input'].get('symbolsPresent') else '无'}"
            f"（{human(report['input'].get('symbolsBytes') or 0)}）"
        )
    if report.get("appPath"):
        print(f"app：{report['appPath']}")
    identity = report.get("identity") or {}
    plist = identity.get("infoPlist") or {}
    if plist:
        print(
            "身份："
            f"{plist.get('CFBundleName')} / {plist.get('CFBundleDisplayName')} / "
            f"{plist.get('CFBundleShortVersionString')} ({plist.get('CFBundleVersion')}) / "
            f"{plist.get('CFBundleIdentifier')} / {plist.get('CFBundleExecutable')}"
        )
    native = report.get("native") or {}
    if native:
        symbols = native.get("ortSymbols") or []
        print(f"主执行文件：arm64={'是' if native.get('arm64Executable') else '否'}  Ort 符号={len(symbols)}")
        if symbols:
            print(f"  {'、'.join(symbols[:8])}{' …' if len(symbols) > 8 else ''}")
    print(f"文件：{report['fileCount']}  总字节：{report['totalBytes']}（{human(report['totalBytes'])}）")
    for category in report["summary"]["categories"]:
        print(f"  {category['category']:<20} {category['fileCount']:>5}  {human(category['bytes']):>12}")
    if output is not None:
        print(f"报告：{output}")
    if report["errors"]:
        print("包扫描未通过：", file=sys.stderr)
        for message in report["errors"]:
            print(f"  - {message}", file=sys.stderr)
    else:
        print("包扫描通过")


# ---------------------------------------------------------------------------
# 自检：合成 .app / .ipa，用真实 clang 生成 Mach-O，用真实 nm / otool 判定符号
# ---------------------------------------------------------------------------


def _compile_executable(
    path: Path, ort_symbol: str | None = None, arch: str = "arm64", *, undefined_ort: bool = False
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    source = path.with_suffix(".c")
    argv = ["clang", "-arch", arch, "-o", str(path), str(source)]
    if ort_symbol:
        declaration = f"extern void {ort_symbol}(void);" if undefined_ort else f"void {ort_symbol}(void){{}}"
        source.write_text(
            declaration + f"\nint main(void){{ {ort_symbol}(); return 0; }}\n",
            encoding="utf-8",
        )
        if undefined_ort:
            argv.append("-Wl,-undefined,dynamic_lookup")
    else:
        source.write_text("int main(void){return 0;}\n", encoding="utf-8")
    completed = subprocess.run(argv, capture_output=True, text=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"自检无法生成 Mach-O（{argv}）：{completed.stderr.strip()}")
    source.unlink(missing_ok=True)


def _make_app(
    root: Path,
    product: str,
    version: str,
    build: str,
    *,
    extra_files: dict[str, bytes] | None = None,
    bundle_name: str | None = None,
    executable: str = DEFAULT_EXECUTABLE,
    ort_symbol: str | None = None,
    arch: str = "arm64",
    undefined_ort: bool = False,
) -> Path:
    app = root / APP_BUNDLE_NAME
    app.mkdir(parents=True, exist_ok=True)
    plist = {
        "CFBundleName": bundle_name or product,
        "CFBundleDisplayName": bundle_name or product,
        "CFBundleShortVersionString": version,
        "CFBundleVersion": build,
        "CFBundleIdentifier": DEFAULT_BUNDLE_ID,
        "CFBundleExecutable": executable,
    }
    (app / "Info.plist").write_bytes(plistlib.dumps(plist))
    _compile_executable(app / executable, ort_symbol=ort_symbol, arch=arch, undefined_ort=undefined_ort)
    for rel, data in (extra_files or {}).items():
        target = app / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return app


def _make_ipa(root: Path, app: Path, *, with_symbols: bool = True) -> Path:
    import zipfile

    ipa = root / f"{app.name[:-4]}.ipa"
    with zipfile.ZipFile(ipa, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(app.rglob("*")):
            if path.is_file():
                archive.write(path, Path("Payload") / app.name / path.relative_to(app))
        if with_symbols:
            archive.writestr("Symbols/00000000-0000-0000-0000-000000000000.symbols", b"self-test")
    return ipa


def self_test() -> int:
    """用合成包校验扫描器的正反判定；符号判定走真实 nm / otool。"""
    version, build = "9.9.9", "999"
    cases: list[tuple[str, Path, dict, bool, str | None]] = []

    def expectations(edition: str, model: dict | None = None, **overrides) -> dict:
        base = {
            "edition": edition,
            "productName": PRODUCT_NAMES[edition],
            "version": version,
            "buildNumber": build,
            "bundleId": DEFAULT_BUNDLE_ID,
            "executable": DEFAULT_EXECUTABLE,
        }
        if edition == "ai":
            base["modelContract"] = model or {
                "name": MODEL_NAME,
                "bytes": 0,
                "sha256": "",
            }
        else:
            base["modelContract"] = None
        base.update(overrides)
        return base

    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix="pv-ios-scan-selftest-") as tmp:
        root = Path(tmp)
        model_bytes = b"self-test-model-" * 64
        model_contract = {
            "name": MODEL_NAME,
            "bytes": len(model_bytes),
            "sha256": hashlib.sha256(model_bytes).hexdigest(),
        }
        ai_files = {
            "Assets/Models/" + MODEL_NAME: model_bytes,
            "Assets/Models/" + DINO_LICENSE_NAME: b"license",
            "Microsoft.ML.OnnxRuntime.dll": b"MZ",
            "MathNet.Numerics.dll": b"MZ",
            "PhotoViewer.dll": b"MZ",
        }

        cases.append((
            "标准版合规包通过",
            _make_app(root / "standard-ok", "PhotoViewer", version, build,
                      extra_files={"PhotoViewer.dll": b"MZ"}),
            expectations("standard"),
            True,
            None,
        ))

        cases.append((
            "标准版残留模型失败",
            _make_app(root / "standard-model", "PhotoViewer", version, build,
                      extra_files={"Assets/Models/" + MODEL_NAME: b"model"}),
            expectations("standard"),
            False,
            "标准版不得包含 ONNX 模型",
        ))

        cases.append((
            "标准版残留托管 ORT 失败",
            _make_app(root / "standard-ort-ms", "PhotoViewer", version, build,
                      extra_files={"Microsoft.ML.OnnxRuntime.dll": b"MZ"}),
            expectations("standard"),
            False,
            "标准版不得包含 ONNX Runtime 文件",
        ))

        cases.append((
            "标准版残留 ORT AOT 数据失败",
            _make_app(root / "standard-ort-aot", "PhotoViewer", version, build,
                      extra_files={"Microsoft.ML.OnnxRuntime.aotdata.arm64": b"aot"}),
            expectations("standard"),
            False,
            "标准版不得包含 ONNX Runtime 文件",
        ))

        cases.append((
            "标准版残留 MathNet 失败",
            _make_app(root / "standard-mathnet", "PhotoViewer", version, build,
                      extra_files={"MathNet.Numerics.dll": b"MZ"}),
            expectations("standard"),
            False,
            "标准版不得包含 MathNet.Numerics 推理依赖",
        ))

        cases.append((
            "标准版残留 DINO 许可证失败",
            _make_app(root / "standard-license", "PhotoViewer", version, build,
                      extra_files={"Assets/Models/" + DINO_LICENSE_NAME: b"license"}),
            expectations("standard"),
            False,
            "标准版不得包含 DINO 模型许可证",
        ))

        cases.append((
            "标准版主执行文件带 ORT 符号失败（文件名干净）",
            _make_app(root / "standard-symbol", "PhotoViewer", version, build,
                      ort_symbol="OrtGetApiBase"),
            expectations("standard"),
            False,
            "标准版主执行文件含 ONNX Runtime 符号",
        ))

        cases.append((
            "AI 版合规包通过（含静态符号）",
            _make_app(root / "ai-ok", "PhotoViewer AI", version, build,
                      extra_files=dict(ai_files), ort_symbol="OrtGetApiBase"),
            expectations("ai", model_contract),
            True,
            None,
        ))

        cases.append((
            "AI 版只有同名文件、主执行文件无 ORT 符号失败",
            _make_app(root / "ai-filename-only", "PhotoViewer AI", version, build,
                      extra_files=dict(ai_files)),
            expectations("ai", model_contract),
            False,
            "缺少 ONNX Runtime 已定义静态符号 _OrtGetApiBase",
        ))

        cases.append((
            "AI 版主执行文件只有未定义 ORT 导入失败",
            _make_app(root / "ai-undefined-ort", "PhotoViewer AI", version, build,
                      extra_files=dict(ai_files), ort_symbol="OrtGetApiBase", undefined_ort=True),
            expectations("ai", model_contract),
            False,
            "缺少 ONNX Runtime 已定义静态符号 _OrtGetApiBase",
        ))

        cases.append((
            "AI 版缺少许可证失败",
            _make_app(root / "ai-no-license", "PhotoViewer AI", version, build,
                      extra_files={key: value for key, value in ai_files.items() if "LICENSE" not in key},
                      ort_symbol="OrtGetApiBase"),
            expectations("ai", model_contract),
            False,
            "应恰好包含一份 DINOv3-LICENSE.md",
        ))

        cases.append((
            "AI 版模型字节数不符失败",
            _make_app(root / "ai-bad-model", "PhotoViewer AI", version, build,
                      extra_files={**ai_files, "Assets/Models/" + MODEL_NAME: b"wrong"},
                      ort_symbol="OrtGetApiBase"),
            expectations("ai", model_contract),
            False,
            "AI 模型契约不匹配",
        ))

        cases.append((
            "AI 版缺少托管 ORT 失败",
            _make_app(root / "ai-no-managed", "PhotoViewer AI", version, build,
                      extra_files={key: value for key, value in ai_files.items()
                                   if key != "Microsoft.ML.OnnxRuntime.dll"},
                      ort_symbol="OrtGetApiBase"),
            expectations("ai", model_contract),
            False,
            "托管推理组件 Microsoft.ML.OnnxRuntime.dll",
        ))

        cases.append((
            "版本号不符失败",
            _make_app(root / "bad-version", "PhotoViewer", "0.0.1", build),
            expectations("standard"),
            False,
            "Info.plist 版本号（CFBundleShortVersionString）不符",
        ))

        cases.append((
            "构建号不符失败",
            _make_app(root / "bad-build", "PhotoViewer", version, "1"),
            expectations("standard"),
            False,
            "Info.plist 构建号（CFBundleVersion）不符",
        ))

        cases.append((
            "标准版误带 AI 产品名失败",
            _make_app(root / "bad-product", "PhotoViewer", version, build,
                      bundle_name="PhotoViewer AI"),
            expectations("standard"),
            False,
            "Info.plist 产品名（CFBundleName）不符",
        ))

        cases.append((
            "非 arm64 主执行文件失败",
            _make_app(root / "bad-arch", "PhotoViewer", version, build, arch="x86_64"),
            expectations("standard"),
            False,
            "主程序不是 arm64 可执行 Mach-O",
        ))

        ipa = _make_ipa(root, _make_app(root / "ipa-src", "PhotoViewer", version, build,
                                        extra_files={"PhotoViewer.dll": b"MZ"}))
        cases.append((
            "IPA 输入（标准版）通过并报告 Symbols",
            ipa,
            expectations("standard"),
            True,
            None,
        ))

        for label, target, expect, expect_pass, needle in cases:
            report = scan(target, expect)
            passed = report["passed"]
            detail = "；".join(report["errors"][:3])
            if passed != expect_pass:
                failures.append(f"{label}：期望 {'通过' if expect_pass else '失败'}，实际 {'通过' if passed else '失败'}（{detail}）")
            elif needle and not any(needle in message for message in report["errors"]):
                failures.append(f"{label}：错误信息未包含「{needle}」，实际：{detail}")
            else:
                print(f"  [{'OK' if passed else 'OK（已拒绝）'}] {label}")

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

    parser = argparse.ArgumentParser(description="扫描 iOS .app / .ipa 并核对发行版本契约")
    parser.add_argument("target", type=Path, help="待扫描的 .app 目录或 .ipa")
    parser.add_argument("--edition", required=True, choices=("standard", "ai"), help="发行版本")
    parser.add_argument("--output", type=Path, help="JSON 报告路径")
    parser.add_argument("--version", help="期望的 CFBundleShortVersionString；缺省取根 Directory.Build.props")
    parser.add_argument("--build-number", help="期望的 CFBundleVersion；缺省取根 Directory.Build.props")
    parser.add_argument("--product-name", help="期望的产品名与显示名")
    parser.add_argument("--bundle-id", default=DEFAULT_BUNDLE_ID, help=f"期望的 CFBundleIdentifier（默认 {DEFAULT_BUNDLE_ID}）")
    parser.add_argument("--executable", default=DEFAULT_EXECUTABLE, help=f"期望的 CFBundleExecutable（默认 {DEFAULT_EXECUTABLE}）")
    args = parser.parse_args(argv)

    props = read_root_props()
    version = args.version or props.get("AppVersion")
    build_number = args.build_number or props.get("AppBuildNumber")
    if not version or not build_number:
        print("无法确定期望的版本号/构建号：请显式传 --version 与 --build-number", file=sys.stderr)
        return 2

    expectations = {
        "edition": args.edition,
        "productName": args.product_name or PRODUCT_NAMES[args.edition],
        "version": version,
        "buildNumber": build_number,
        "bundleId": args.bundle_id,
        "executable": args.executable,
        "modelContract": {
            "name": MODEL_NAME,
            "bytes": MODEL_BYTES,
            "sha256": MODEL_SHA256,
        } if args.edition == "ai" else None,
    }
    report = scan(args.target, expectations)
    if write_report(report, args.output):
        return 1
    print_report(report, args.output)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
