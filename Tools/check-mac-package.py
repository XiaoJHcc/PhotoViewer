#!/usr/bin/env python3
"""扫描 macOS .app 包，生成文件清单并核对发行版本契约。

用法：
  python3 Tools/check-mac-package.py <App.app> --edition standard|ai --output <报告.json>

只做本地文件检查：不挂载 DMG、不联网、不调用签名或公证工具。

standard：不得包含 .onnx、文件名含 onnxruntime（不区分大小写）、DirectML，
          或 Microsoft.ML.OnnxRuntime 托管 dll。
ai：      必须包含精确模型 dinov3_vits16.onnx（字节数与 SHA-256 固定），
          以及 arm64 Mach-O 的 libonnxruntime*.dylib。
两个版本都拒绝 Windows 的 onnxruntime*.dll。

报告固定写入 edition / appPath / fileCount / totalBytes / files / errors，
另附按分类的体积摘要。检查失败时仍写报告并以 1 退出。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import struct
import sys
from pathlib import Path

MODEL_NAME = "dinov3_vits16.onnx"
MODEL_BYTES = 86_594_677
MODEL_SHA256 = "bcba38490ec2eded4a240751bebac198903daba7263871e8fee3aa39b03352e8"

# Mach-O 头常量（见 <mach-o/loader.h> 与 <mach-o/fat.h>）。
MH_MAGIC = 0xFEEDFACE
MH_MAGIC_64 = 0xFEEDFACF
FAT_MAGIC = 0xCAFEBABE
FAT_MAGIC_64 = 0xCAFEBABF
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
        if reasons:
            errors.append(f"标准版不得包含 {'、'.join(reasons)}：{record['path']}")
    for record in records:
        if is_windows_onnxruntime(record["name"]):
            errors.append(f"标准版不得包含 Windows ONNX Runtime DLL：{record['path']}")


def check_ai(records: list[dict], errors: list[str]) -> bool:
    for record in records:
        if is_windows_onnxruntime(record["name"]):
            errors.append(f"AI 版不得包含 Windows ONNX Runtime DLL：{record['path']}")
    licenses = [record for record in records if record["name"] == "DINOv3-LICENSE.md"]
    if len(licenses) != 1:
        errors.append(f"AI 版应恰好包含一份 DINOv3-LICENSE.md，实际 {len(licenses)} 份")

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


def main() -> int:
    parser = argparse.ArgumentParser(description="扫描 macOS .app 并核对发行版本契约")
    parser.add_argument("app", type=Path, help="待扫描的 .app 路径")
    parser.add_argument("--edition", required=True, choices=("standard", "ai"), help="发行版本")
    parser.add_argument("--output", required=True, type=Path, help="JSON 报告路径")
    args = parser.parse_args()

    app: Path = args.app
    errors: list[str] = []
    records: list[dict] = []
    model_satisfied = False
    if not app.is_dir():
        errors.append(f"应用包不存在或不是目录：{app}")
    else:
        records = collect(app, errors)
        if args.edition == "standard":
            check_standard(records, errors)
        else:
            model_satisfied = check_ai(records, errors)

    summary = build_summary(records)
    report = {
        "edition": args.edition,
        "appPath": str(app.resolve()),
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
    if args.edition == "ai":
        report["modelContract"] = {
            "expected": {"name": MODEL_NAME, "bytes": MODEL_BYTES, "sha256": MODEL_SHA256},
            "found": summary["modelFiles"],
            "satisfied": model_satisfied,
        }

    output: Path = args.output
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except OSError as exc:
        print(f"无法写入报告 {output}：{exc}", file=sys.stderr)
        return 1

    print(f"edition：{args.edition}")
    print(f"app：{report['appPath']}")
    print(f"文件：{report['fileCount']}  总字节：{report['totalBytes']}（{human(report['totalBytes'])}）")
    for category in summary["categories"]:
        print(f"  {category['category']:<20} {category['fileCount']:>5}  {human(category['bytes']):>12}")
    print(f"报告：{output}")
    if errors:
        print("包扫描未通过：", file=sys.stderr)
        for message in errors:
            print(f"  - {message}", file=sys.stderr)
        return 1
    print("包扫描通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
