#!/usr/bin/env python3
"""校验 release/ 只含分发包与打包过程产物，并生成 SHA256SUMS。

release/ 允许的顶层内容：
  packages/    分发包（历史与当前），文件名 <PhotoViewer|PhotoViewer-AI>-<版本>-<平台后缀>.<dmg|apk|ipa|exe>
  ios-stable/  最新 iOS 稳定安装源（PhotoViewer.iOS.app / PhotoViewer.iOS.ipa）
  testflight/  最近一次 TestFlight 归档（恰好一个 .xcarchive）
  SHA256SUMS   分发包校验清单
  README.md    可选说明

用法：
  python3 Tools/check-release.py            # 只校验
  python3 Tools/check-release.py --write    # 校验并重写 release/SHA256SUMS
"""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RELEASE = ROOT / "release"

PACKAGE_EXTENSIONS = {".dmg", ".apk", ".ipa", ".exe"}
PACKAGE_NAME = re.compile(r"^(PhotoViewer(?:-AI)?)-(\d+\.\d+\.\d+)-(.+)\.(dmg|apk|ipa|exe)$")
ALLOWED_TOP_DIRS = {"packages", "ios-stable", "testflight"}
ALLOWED_TOP_FILES = {"SHA256SUMS", "README.md"}
IOS_STABLE_ALLOWED = {"PhotoViewer.iOS.app"}


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


def main() -> int:
    parser = argparse.ArgumentParser(description="校验 release/ 布局并生成 SHA256SUMS")
    parser.add_argument("--write", action="store_true", help="重写 release/SHA256SUMS")
    args = parser.parse_args()

    errors: list[str] = []
    warnings: list[str] = []

    if not RELEASE.is_dir():
        print("缺少 release/ 目录", file=sys.stderr)
        return 1

    for entry in sorted(RELEASE.iterdir()):
        if entry.name == ".DS_Store":
            continue
        if entry.is_dir():
            if entry.name not in ALLOWED_TOP_DIRS:
                errors.append(f"release/{entry.name}/ 不是分发包或打包过程目录")
        elif entry.name not in ALLOWED_TOP_FILES:
            errors.append(f"release/{entry.name} 应放在 release/packages/ 或不属于 release")

    packages_dir = RELEASE / "packages"
    if not packages_dir.is_dir():
        errors.append("缺少 release/packages/ 目录")
        packages: list[Path] = []
    else:
        packages = []
        for entry in sorted(packages_dir.iterdir()):
            if entry.name == ".DS_Store":
                continue
            if entry.is_dir():
                errors.append(f"release/packages/{entry.name}/ 不允许子目录")
                continue
            if entry.suffix.lower() not in PACKAGE_EXTENSIONS:
                errors.append(f"release/packages/{entry.name} 不是受支持的分发包格式")
                continue
            if not PACKAGE_NAME.match(entry.name):
                errors.append(f"release/packages/{entry.name} 命名不符合 <PhotoViewer|PhotoViewer-AI>-<版本>-<平台后缀>")
                continue
            if entry.name.endswith("-android.apk"):
                errors.append(f"release/packages/{entry.name} 是未签名 APK；只归档 -android-signed.apk")
                continue
            packages.append(entry)

    seen: dict[tuple[str, str, str], str] = {}
    for package in packages:
        match = PACKAGE_NAME.match(package.name)
        if not match:
            continue
        product, version, platform = match.group(1), match.group(2), match.group(3)
        key = (product, version, platform)
        if key in seen:
            errors.append(f"release/packages 中 {product} {version} 的 {platform} 出现多次：{seen[key]} / {package.name}")
        else:
            seen[key] = package.name

    ios_stable = RELEASE / "ios-stable"
    if not ios_stable.is_dir():
        warnings.append("尚无 release/ios-stable/（Publish iOS IPA 后生成）")
    else:
        for entry in sorted(ios_stable.iterdir()):
            if entry.name == ".DS_Store":
                continue
            if entry.name not in IOS_STABLE_ALLOWED:
                errors.append(f"release/ios-stable/{entry.name} 不属于 iOS 稳定安装源")

    testflight = RELEASE / "testflight"
    if not testflight.is_dir():
        warnings.append("尚无 release/testflight/（Upload iOS TestFlight 后生成）")
        archives: list[Path] = []
    else:
        archives = []
        for entry in sorted(testflight.iterdir()):
            if entry.name == ".DS_Store":
                continue
            if entry.is_dir() and entry.suffix == ".xcarchive":
                archives.append(entry)
            else:
                errors.append(f"release/testflight/{entry.name} 不属于最近一次归档；旧归档/崩溃报告应在 artifacts/")
        if len(archives) > 1:
            errors.append("release/testflight/ 有多个归档；只保留最近一次（运行 Tools/clean-artifacts.sh）")

    if packages:
        print("release/packages/")
        for package in packages:
            print(f"  {package.name:<46} {human(package.stat().st_size):>10}")
    else:
        warnings.append("release/packages/ 为空")

    if archives:
        print("release/testflight/")
        for archive in archives:
            print(f"  {archive.name}")

    if errors:
        print("\n校验失败：", file=sys.stderr)
        for message in errors:
            print(f"  - {message}", file=sys.stderr)
        for message in warnings:
            print(f"  提示：{message}", file=sys.stderr)
        return 1

    if args.write:
        lines = [f"{sha256(package)}  packages/{package.name}" for package in packages]
        (RELEASE / "SHA256SUMS").write_text("\n".join(lines) + "\n")
        print(f"\n已写入 release/SHA256SUMS（{len(packages)} 个包）")
    else:
        print(f"\n校验通过（{len(packages)} 个包）；加 --write 重写 SHA256SUMS")

    for message in warnings:
        print(f"提示：{message}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
