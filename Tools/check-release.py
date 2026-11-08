#!/usr/bin/env python3
"""校验 release/ 只含分发包与打包过程产物，并生成 SHA256SUMS。

release/ 允许的顶层内容：
  packages/    分发包（历史与当前），文件名 <PhotoViewer|PhotoViewer-AI>-<版本>-<平台后缀>.<dmg|apk|aab|ipa|exe|msix>
  ios-stable/  最新 iOS 稳定安装源：<standard|ai>/PhotoViewer.iOS.app（旧布局为顶层 PhotoViewer.iOS.app）
  testflight/  最近一次 TestFlight 归档：<standard|ai>/*.xcarchive（旧布局为顶层单个 .xcarchive）
  SHA256SUMS   分发包校验清单
  README.md    可选说明

双版本发布矩阵（见 Design/spec/editions.md 与 Plans/distribution-package-size-optimization-plan.md）：
  -win-x64 .msix（附加/历史 .exe） · -mac-arm64 .dmg · -android-arm64 .aab（直接分发/历史 .apk） · -ios .ipa（-ios-arm64 同义）
历史包继续按原后缀接受：-arm64 .dmg（macOS 0.8.x）、-android-signed .apk（Android 尚未迁移）。
Android 只发布 arm64-v8a，x86_64/x64/x86/arm 后缀一律拒绝。

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

PACKAGE_EXTENSIONS = {".dmg", ".apk", ".aab", ".ipa", ".exe", ".msix"}
PACKAGE_NAME = re.compile(r"^(PhotoViewer(?:-AI)?)-(\d+\.\d+\.\d+)-(.+)\.(dmg|apk|aab|ipa|exe|msix)$")
ALLOWED_TOP_DIRS = {"packages", "ios-stable", "testflight"}
ALLOWED_TOP_FILES = {"SHA256SUMS", "README.md"}
IOS_STABLE_ALLOWED = {"PhotoViewer.iOS.app"}
IOS_EDITIONS = ("standard", "ai")


def check_ios_stable(ios_stable: Path) -> list[str]:
    """release/ios-stable：顶层旧布局或 <standard|ai>/ 分版本安装源。"""
    errors: list[str] = []
    if not ios_stable.is_dir():
        return errors
    for entry in sorted(ios_stable.iterdir()):
        if entry.name == ".DS_Store":
            continue
        if entry.is_dir() and entry.name in IOS_EDITIONS:
            apps = [item for item in sorted(entry.iterdir()) if item.name != ".DS_Store"]
            if len(apps) != 1 or apps[0].name not in IOS_STABLE_ALLOWED or not apps[0].is_dir():
                listed = "、".join(item.name for item in apps) or "空"
                errors.append(
                    f"release/ios-stable/{entry.name}/ 应只含 PhotoViewer.iOS.app，实际：{listed}"
                )
        elif entry.name not in IOS_STABLE_ALLOWED:
            errors.append(
                f"release/ios-stable/{entry.name} 不属于 iOS 稳定安装源"
                "（应为 PhotoViewer.iOS.app 或 standard/、ai/ 分版本目录）"
            )
    return errors


def check_testflight(testflight: Path) -> tuple[list[Path], list[str], list[str]]:
    """release/testflight：<standard|ai>/ 各一个归档；顶层单个归档为旧布局。"""
    errors: list[str] = []
    warnings: list[str] = []
    archives: list[Path] = []
    legacy_archives: list[Path] = []
    legacy_uploads: list[Path] = []
    edition_dirs = 0
    for entry in sorted(testflight.iterdir()):
        if entry.name == ".DS_Store":
            continue
        if entry.is_dir() and entry.name in IOS_EDITIONS:
            edition_dirs += 1
            entries = [item for item in sorted(entry.iterdir()) if item.name != ".DS_Store"]
            found = [item for item in entries if item.is_dir() and item.suffix == ".xcarchive"]
            for item in entries:
                if item not in found:
                    errors.append(
                        f"release/testflight/{entry.name}/{item.name} 不属于最近一次归档；"
                        "导出目录应在 artifacts/scratch/"
                    )
            if len(found) > 1:
                errors.append(
                    f"release/testflight/{entry.name}/ 有多个归档；只保留最新一份"
                    "（运行 Tools/clean-artifacts.sh）"
                )
            elif not found and not entries:
                warnings.append(f"release/testflight/{entry.name}/ 尚无归档")
            archives.extend(found)
        elif entry.is_dir() and entry.suffix == ".xcarchive":
            legacy_archives.append(entry)
        elif entry.is_dir() and entry.name.startswith("upload-"):
            legacy_uploads.append(entry)
        else:
            errors.append(f"release/testflight/{entry.name} 不属于最近一次归档；旧归档/崩溃报告应在 artifacts/")

    if edition_dirs:
        for entry in legacy_archives + legacy_uploads:
            warnings.append(
                f"release/testflight/{entry.name} 是旧布局归档/导出目录；"
                "新流程把归档放在 standard/、ai/ 下，可在清理时删除"
            )
        archives.extend(legacy_archives)
    else:
        if len(legacy_archives) > 1:
            errors.append("release/testflight/ 有多个归档；只保留最近一次（运行 Tools/clean-artifacts.sh）")
        if legacy_uploads:
            errors.append("release/testflight/ 不应保留导出目录；导出产物应在 artifacts/scratch/")
        archives.extend(legacy_archives)
    return archives, errors, warnings

# 双版本发布矩阵：平台后缀 → 允许的扩展名
MATRIX_SUFFIXES = {
    "win-x64": {".msix", ".exe"},
    "mac-arm64": {".dmg"},
    "android-arm64": {".aab", ".apk"},
    "ios-arm64": {".ipa"},
    "ios": {".ipa"},
}
# 尚未迁移平台的既有命名，继续接受但提示改用矩阵后缀
LEGACY_SUFFIXES = {
    "arm64": {".dmg"},  # macOS 0.8.x 及更早
    "android-signed": {".apk"},  # Android 尚未切换到 -android-arm64
}
LEGACY_HINT = {
    "arm64": "mac-arm64",
    "android-signed": "android-arm64",
}
# Android 只发布 arm64-v8a；这些架构后缀不得出现在分发包名里
REJECTED_ARCH_SUFFIX = re.compile(r"^(?:android|android-[\w.]*)-(?:x86_64|x64|x86|arm|armv7|armeabi|armeabi-v7a)$")


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


def read_app_version() -> str:
    props = ROOT / "Directory.Build.props"
    try:
        text = props.read_text(encoding="utf-8")
    except OSError:
        return ""
    match = re.search(r"<AppVersion>([^<]+)</AppVersion>", text)
    return match.group(1).strip() if match else ""


def main() -> int:
    parser = argparse.ArgumentParser(description="校验 release/ 布局并生成 SHA256SUMS")
    parser.add_argument("--write", action="store_true", help="重写 release/SHA256SUMS")
    args = parser.parse_args()

    errors: list[str] = []
    warnings: list[str] = []
    current_version = read_app_version()

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
            match = PACKAGE_NAME.match(entry.name)
            if not match:
                errors.append(f"release/packages/{entry.name} 命名不符合 <PhotoViewer|PhotoViewer-AI>-<版本>-<平台后缀>")
                continue
            if entry.name.endswith("-android.apk"):
                errors.append(f"release/packages/{entry.name} 是未签名 APK；只归档 -android-signed.apk")
                continue

            suffix = match.group(3)
            extension = f".{match.group(4)}"
            version = match.group(2)
            if suffix in MATRIX_SUFFIXES:
                allowed = MATRIX_SUFFIXES[suffix]
            elif suffix in LEGACY_SUFFIXES:
                allowed = LEGACY_SUFFIXES[suffix]
                if version == current_version:
                    warnings.append(
                        f"release/packages/{entry.name} 使用历史平台后缀 -{suffix}；"
                        f"当前版本新发布请改用 -{LEGACY_HINT[suffix]}"
                    )
            elif REJECTED_ARCH_SUFFIX.match(suffix):
                errors.append(
                    f"release/packages/{entry.name} 使用不受支持的架构后缀 -{suffix}；"
                    "Android 只发布 arm64-v8a 的 -android-arm64.apk"
                )
                continue
            else:
                allowed_suffixes = "、".join(sorted(MATRIX_SUFFIXES))
                errors.append(
                    f"release/packages/{entry.name} 的平台后缀 -{suffix} 不在发布矩阵内；"
                    f"允许 {allowed_suffixes}（历史后缀 -arm64、-android-signed 仍接受）"
                )
                continue
            if extension not in allowed:
                expected = "、".join(sorted(allowed))
                errors.append(
                    f"release/packages/{entry.name} 的平台后缀 -{suffix} 与扩展名 {extension} 不匹配；只能是 {expected}"
                )
                continue
            packages.append(entry)

    seen: dict[tuple[str, str, str], str] = {}
    seen_artifacts: dict[tuple[str, str, str, str], str] = {}
    history: list[Path] = []
    for package in packages:
        match = PACKAGE_NAME.match(package.name)
        if not match:
            continue
        product, version, platform = match.group(1), match.group(2), match.group(3)
        key = (product, version, platform)
        artifact_key = (*key, package.suffix.lower())
        if artifact_key in seen_artifacts:
            errors.append(
                f"release/packages 中 {product} {version} 的 {platform}{package.suffix} 出现多次："
                f"{seen_artifacts[artifact_key]} / {package.name}"
            )
        else:
            seen_artifacts[artifact_key] = package.name
        seen[key] = package.name
        if current_version and version != current_version:
            history.append(package)

    if current_version:
        for product in ("PhotoViewer", "PhotoViewer-AI"):
            if (product, current_version, "mac-arm64") not in seen:
                warnings.append(
                    f"当前版本 {current_version} 尚无 {product}-{current_version}-mac-arm64.dmg"
                )
            if (product, current_version, "ios") not in seen and (
                product,
                current_version,
                "ios-arm64",
            ) not in seen:
                warnings.append(
                    f"当前版本 {current_version} 尚无 {product}-{current_version}-ios.ipa"
                )

    ios_stable = RELEASE / "ios-stable"
    if not ios_stable.is_dir():
        warnings.append("尚无 release/ios-stable/（Publish iOS IPA 后生成）")
    else:
        errors.extend(check_ios_stable(ios_stable))

    testflight = RELEASE / "testflight"
    if not testflight.is_dir():
        warnings.append("尚无 release/testflight/（Upload iOS TestFlight 后生成）")
        archives: list[Path] = []
    else:
        archives, archive_errors, archive_warnings = check_testflight(testflight)
        errors.extend(archive_errors)
        warnings.extend(archive_warnings)

    if packages:
        print("release/packages/")
        for package in packages:
            print(f"  {package.name:<46} {human(package.stat().st_size):>10}")
        if history:
            print(f"  历史版本：{len(history)} 个旧版本包继续保留")
    else:
        warnings.append("release/packages/ 为空")

    if current_version:
        print(f"\n当前版本 {current_version} 发布矩阵")
        variants = ("PhotoViewer", "PhotoViewer-AI")
        for platform in ("win-x64", "mac-arm64", "android-arm64", "ios"):
            cells = []
            for product in variants:
                found = seen.get((product, current_version, platform)) or seen.get(
                    (product, current_version, "ios-arm64" if platform == "ios" else platform)
                )
                cells.append(found or "—")
            print(f"  -{platform:<14} {' | '.join(cells)}")
        if not any(version == current_version for _product, version, _platform in seen):
            warnings.append(f"当前版本 {current_version} 尚无分发包")

    if archives:
        print("\nrelease/testflight/")
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
