#!/usr/bin/env python3
"""扫描 macOS .app 包，核对发行版本契约并生成文件级清单。

用法：
  python3 Tools/check-mac-package.py <App.app> --edition standard|ai --output <报告.json>
        [--version 0.9.0] [--build-number 90] [--product-name PhotoViewer|PhotoViewer-AI]
        [--bundle-id cc.xiaojh.PhotoViewer.Mac] [--executable PhotoViewer.Mac]
  python3 Tools/check-mac-package.py --self-test

只做本地文件检查：不挂载 DMG、不联网、不调用签名或公证工具。

身份：包目录名、Contents/Info.plist 的 CFBundleName / CFBundleDisplayName /
CFBundleShortVersionString / CFBundleVersion / CFBundleIdentifier / CFBundleExecutable，
以及 Contents/MacOS/<CFBundleExecutable> 的 arm64 可执行 Mach-O 头。版本号与构建号缺省
取根 Directory.Build.props，也可显式传入；报告保存期望值与实际值。

standard：不得包含 .onnx、文件名含 onnxruntime（不区分大小写）、DirectML、
          Microsoft.ML.OnnxRuntime 托管 dll、MathNet.Numerics.dll 或 DINOv3 许可证。
ai：      必须包含 Microsoft.ML.OnnxRuntime.dll、MathNet.Numerics.dll、
          精确模型 dinov3_vits16.onnx（字节数与 SHA-256 固定）、DINOv3-LICENSE.md，
          以及 arm64 Mach-O 的 libonnxruntime*.dylib。
两个版本都拒绝 Windows 的 onnxruntime*.dll。

报告固定写入 edition / appPath / expectations / identity / fileCount / totalBytes /
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
import sys
import tempfile
from pathlib import Path

MODEL_NAME = "dinov3_vits16.onnx"
MODEL_BYTES = 86_594_677
MODEL_SHA256 = "bcba38490ec2eded4a240751bebac198903daba7263871e8fee3aa39b03352e8"

PRODUCT_NAMES = {"standard": "PhotoViewer", "ai": "PhotoViewer-AI"}
DEFAULT_BUNDLE_ID = "cc.xiaojh.PhotoViewer.Mac"
DEFAULT_EXECUTABLE = "PhotoViewer.Mac"

AI_MANAGED_REQUIRED = ("Microsoft.ML.OnnxRuntime.dll", "MathNet.Numerics.dll")
STANDARD_MANAGED_FORBIDDEN = ("Microsoft.ML.OnnxRuntime.dll", "MathNet.Numerics.dll")
DINO_LICENSE_NAME = "DINOv3-LICENSE.md"

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
MH_DYLIB = 0x6
CPU_TYPE_ARM64 = 0x0100000C

CORE_DYLIB_NAME = re.compile(r"^libonnxruntime(\.[0-9][^/]*)?\.dylib$", re.IGNORECASE)


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


def classify(name: str, architectures: list[tuple[int, int]]) -> str:
    lower = name.lower()
    if lower.endswith(".onnx"):
        return "model"
    if "directml" in lower:
        return "directml"
    if "onnxruntime" in lower:
        if lower.endswith(".dylib") or lower.endswith(".so") or lower.endswith(".a"):
            return "onnxruntime-native"
        if lower.endswith(".dll"):
            return "onnxruntime-managed" if lower.startswith("microsoft.ml.onnxruntime") else "onnxruntime-windows"
        return "onnxruntime-other"
    if architectures:
        return "native-binary"
    if lower.endswith(".dll"):
        return "managed-assembly"
    if lower.endswith((".dylib", ".so", ".a", ".o")):
        return "native-library"
    return "resource"


def is_windows_onnxruntime(name: str) -> bool:
    lower = name.lower()
    return (
        lower.endswith(".dll")
        and "onnxruntime" in lower
        and not lower.startswith("microsoft.ml.onnxruntime")
    )


def is_arm64_macho_dylib(architectures: list[tuple[int, int]]) -> bool:
    return any(filetype == MH_DYLIB and cputype == CPU_TYPE_ARM64 for cputype, filetype in architectures)


def is_arm64_macho_executable(architectures: list[tuple[int, int]]) -> bool:
    return any(filetype == MH_EXECUTE and cputype == CPU_TYPE_ARM64 for cputype, filetype in architectures)


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
                "category": classify(name, architectures),
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
    summary = {
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
    return summary


def check_identity(app: Path, expectations: dict, errors: list[str]) -> dict:
    app_name = app.name
    if app_name != f"{expectations['productName']}.app":
        errors.append(
            f"应用包目录名应为 {expectations['productName']}.app，实际 {app_name}"
        )

    info_plist = app / "Contents" / "Info.plist"
    identity = {
        "appBundle": app_name,
        "infoPlistPath": str(info_plist),
        "infoPlist": {},
        "checks": [],
        "verified": False,
    }
    if not info_plist.is_file():
        errors.append("缺少 Contents/Info.plist")
        return identity

    try:
        plist = plistlib.loads(info_plist.read_bytes())
    except (OSError, plistlib.InvalidFileException) as exc:
        errors.append(f"无法解析 Contents/Info.plist：{exc}")
        return identity
    if not isinstance(plist, dict):
        errors.append("Contents/Info.plist 顶层不是字典")
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

    executable_path = app / "Contents" / "MacOS" / str(plist.get("CFBundleExecutable", ""))
    architectures = macho_architectures(executable_path) if executable_path.is_file() else []
    executable_ok = executable_path.is_file() and is_arm64_macho_executable(architectures)
    identity["mainExecutable"] = {
        "path": str(executable_path),
        "exists": executable_path.is_file(),
        "architectures": [
            {"cputype": hex(cputype), "filetype": filetype} for cputype, filetype in architectures
        ],
        "arm64Executable": executable_ok,
    }
    if not executable_path.is_file():
        errors.append(f"缺少主程序 Contents/MacOS/{plist.get('CFBundleExecutable')}")
    elif not executable_ok:
        errors.append(
            f"主程序不是 arm64 可执行 Mach-O：{executable_path}（{architectures}）"
        )

    identity["verified"] = all(check["ok"] for check in identity["checks"]) and executable_ok
    return identity


def check_standard(records: list[dict], errors: list[str]) -> None:
    for record in records:
        lower = record["name"].lower()
        reasons = []
        if lower.endswith(".onnx"):
            reasons.append("ONNX 模型")
        if "onnxruntime" in lower and not is_windows_onnxruntime(record["name"]):
            reasons.append("ONNX Runtime 文件")
        if "directml" in lower:
            reasons.append("DirectML 文件")
        if record["name"] in STANDARD_MANAGED_FORBIDDEN:
            reasons.append("托管推理组件")
        if record["name"] == DINO_LICENSE_NAME:
            reasons.append("DINO 模型许可证")
        if reasons:
            errors.append(f"标准版不得包含 {'、'.join(reasons)}：{record['path']}")
    for record in records:
        if is_windows_onnxruntime(record["name"]):
            errors.append(f"标准版不得包含 Windows ONNX Runtime DLL：{record['path']}")


def check_ai(records: list[dict], errors: list[str]) -> bool:
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
        elif not matches[0]["path"].startswith("Contents/MonoBundle/"):
            errors.append(f"托管推理组件 {required} 不在 Contents/MonoBundle/：{matches[0]['path']}")

    models = [record for record in records if record["category"] == "model"]
    satisfied = False
    if not models:
        errors.append(f"AI 版缺少模型 {MODEL_NAME}（期望 {MODEL_BYTES} bytes，SHA-256 {MODEL_SHA256}）")
    else:
        if len(models) > 1:
            listed = "、".join(record["path"] for record in models)
            errors.append(f"AI 版只应包含一个 .onnx 模型，实际 {len(models)} 个：{listed}")
        match = next(
            (
                record
                for record in models
                if record["name"] == MODEL_NAME
                and record["bytes"] == MODEL_BYTES
                and record["sha256"] == MODEL_SHA256
            ),
            None,
        )
        if match is None:
            actual = "；".join(
                f"{record['path']}：{record['bytes']} bytes，{record['sha256']}" for record in models
            )
            errors.append(
                f"AI 模型契约不匹配（期望 {MODEL_NAME}，{MODEL_BYTES} bytes，"
                f"SHA-256 {MODEL_SHA256}）：{actual}"
            )
        else:
            satisfied = len(models) == 1

    dylibs = [
        record
        for record in records
        if record["category"] == "onnxruntime-native" and CORE_DYLIB_NAME.match(record["name"])
    ]
    genuine = [record for record in dylibs if is_arm64_macho_dylib(record["architectures"])]
    if not dylibs:
        errors.append("AI 版缺少 macOS ONNX Runtime 动态库 libonnxruntime*.dylib")
    elif not genuine:
        listed = "、".join(record["path"] for record in dylibs)
        errors.append(f"libonnxruntime*.dylib 不是 arm64 Mach-O 动态库：{listed}")
    return satisfied


def scan(app: Path, expectations: dict) -> dict:
    """扫描单个 .app，返回报告字典（errors 非空即未通过）。"""
    errors: list[str] = []
    records: list[dict] = []
    model_satisfied = False
    identity: dict = {}

    if not app.is_dir():
        errors.append(f"应用包不存在或不是目录：{app}")
    else:
        records = collect(app, errors)
        identity = check_identity(app, expectations, errors)
        if expectations["edition"] == "standard":
            check_standard(records, errors)
        else:
            model_satisfied = check_ai(records, errors)

    summary = build_summary(records)
    report = {
        "edition": expectations["edition"],
        "appPath": str(app.resolve()),
        "expectations": expectations,
        "identity": identity,
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
            "expected": {"name": MODEL_NAME, "bytes": MODEL_BYTES, "sha256": MODEL_SHA256},
            "found": summary["modelFiles"],
            "satisfied": model_satisfied,
        }
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
    print(f"app：{report['appPath']}")
    identity = report.get("identity") or {}
    plist = identity.get("infoPlist") or {}
    if plist:
        print(
            "身份："
            f"{plist.get('CFBundleName')} / {plist.get('CFBundleShortVersionString')} "
            f"({plist.get('CFBundleVersion')}) / {plist.get('CFBundleIdentifier')} / "
            f"{plist.get('CFBundleExecutable')}"
        )
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


def _write_executable(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    # 小端 arm64 精简 Mach-O 头：magic / cputype / cpusubtype / filetype / ncmds / sizeofcmds / flags / reserved
    path.write_bytes(struct.pack("<IIIIIIII", MH_MAGIC_64, CPU_TYPE_ARM64, 0, MH_EXECUTE, 0, 0, 0, 0))


def _make_app(root: Path, product: str, version: str, build: str, *, extra_files: dict | None = None,
              bundle_name: str | None = None, executable: str = DEFAULT_EXECUTABLE) -> Path:
    app = root / f"{product}.app"
    contents = app / "Contents"
    (contents / "MacOS").mkdir(parents=True, exist_ok=True)
    (contents / "MonoBundle").mkdir(parents=True, exist_ok=True)
    plist = {
        "CFBundleName": bundle_name or product,
        "CFBundleDisplayName": bundle_name or product,
        "CFBundleShortVersionString": version,
        "CFBundleVersion": build,
        "CFBundleIdentifier": DEFAULT_BUNDLE_ID,
        "CFBundleExecutable": executable,
    }
    (contents / "Info.plist").write_bytes(plistlib.dumps(plist))
    _write_executable(contents / "MacOS" / executable)
    for rel, data in (extra_files or {}).items():
        target = contents / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return app


def self_test() -> int:
    """用合成 .app 校验扫描器的正反判定（真实 AI 包仍由发布扫描覆盖）。"""
    version, build = "9.9.9", "999"
    cases: list[tuple[str, dict, bool, str | None]] = []

    def expectations(edition: str, **overrides) -> dict:
        base = {
            "edition": edition,
            "productName": PRODUCT_NAMES[edition],
            "version": version,
            "buildNumber": build,
            "bundleId": DEFAULT_BUNDLE_ID,
            "executable": DEFAULT_EXECUTABLE,
        }
        base.update(overrides)
        return base

    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix="pv-mac-scan-selftest-") as tmp:
        root = Path(tmp)

        app = _make_app(root / "standard-ok", "PhotoViewer", version, build,
                        extra_files={"MonoBundle/PhotoViewer.dll": b"MZ"})
        cases.append(("标准版合规包通过", {"app": app, "expectations": expectations("standard")}, True, None))

        app = _make_app(root / "standard-leak", "PhotoViewer", version, build,
                        extra_files={"MonoBundle/Microsoft.ML.OnnxRuntime.dll": b"MZ"})
        cases.append(("标准版残留托管 ORT 失败", {"app": app, "expectations": expectations("standard")}, False,
                      "托管推理组件"))

        app = _make_app(root / "standard-mathnet", "PhotoViewer", version, build,
                        extra_files={"MonoBundle/MathNet.Numerics.dll": b"MZ"})
        cases.append(("标准版残留 MathNet 失败", {"app": app, "expectations": expectations("standard")}, False,
                      "托管推理组件"))

        app = _make_app(root / "standard-license", "PhotoViewer", version, build,
                        extra_files={"Resources/Assets/Models/DINOv3-LICENSE.md": b"license"})
        cases.append(("标准版残留 DINO 许可证失败", {"app": app, "expectations": expectations("standard")}, False,
                      "标准版不得包含 DINO 模型许可证"))

        app = _make_app(root / "ai-no-managed", "PhotoViewer-AI", version, build)
        cases.append(("AI 版缺少托管 ORT 失败", {"app": app, "expectations": expectations("ai")}, False,
                      "AI 版应恰好包含一份托管推理组件 Microsoft.ML.OnnxRuntime.dll"))

        app = _make_app(root / "bad-version", "PhotoViewer", "0.0.1", build)
        cases.append(("版本号不符失败", {"app": app, "expectations": expectations("standard")}, False,
                      "Info.plist 版本号（CFBundleShortVersionString）不符"))

        app = _make_app(root / "bad-build", "PhotoViewer", version, "1")
        cases.append(("构建号不符失败", {"app": app, "expectations": expectations("standard")}, False,
                      "Info.plist 构建号（CFBundleVersion）不符"))

        app = _make_app(root / "bad-product", "PhotoViewer", version, build, bundle_name="PhotoViewer-AI")
        cases.append(("标准版误带 AI 产品名失败", {"app": app, "expectations": expectations("standard")}, False,
                      "Info.plist 产品名（CFBundleName）不符"))

        app = _make_app(root / "wrong-dir", "PhotoViewer-AI", version, build)
        cases.append(("包目录名与版本不符失败", {"app": app, "expectations": expectations("standard")}, False,
                      "应用包目录名应为 PhotoViewer.app"))

        for label, kwargs, expect_pass, needle in cases:
            report = scan(kwargs["app"], kwargs["expectations"])
            passed = report["passed"]
            detail = "；".join(report["errors"][:3])
            if passed != expect_pass:
                failures.append(f"{label}：期望 {'通过' if expect_pass else '失败'}，实际 {'通过' if passed else '失败'}")
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

    parser = argparse.ArgumentParser(description="扫描 macOS .app 并核对发行版本契约")
    parser.add_argument("app", type=Path, help="待扫描的 .app 路径")
    parser.add_argument("--edition", required=True, choices=("standard", "ai"), help="发行版本")
    parser.add_argument("--output", required=True, type=Path, help="JSON 报告路径")
    parser.add_argument("--version", help="期望的 CFBundleShortVersionString；缺省取根 Directory.Build.props")
    parser.add_argument("--build-number", help="期望的 CFBundleVersion；缺省取根 Directory.Build.props")
    parser.add_argument("--product-name", help="期望的产品名与 .app 目录名")
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
    }
    report = scan(args.app, expectations)
    if write_report(report, args.output):
        return 1
    print_report(report, args.output)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
