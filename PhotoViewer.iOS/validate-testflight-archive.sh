#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
ARCHIVE_PATH="${1:-${REPO_ROOT}/release/testflight/PhotoViewer-${VERSION}-${BUILD}.xcarchive}"
APP_PATH="${ARCHIVE_PATH}/Products/Applications/PhotoViewer.iOS.app"

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

[[ -d "${APP_PATH}" ]] || fail "应用不存在：${APP_PATH}"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${APP_PATH}/Info.plist")" == "${VERSION}" ]] || fail "CFBundleShortVersionString 不是 ${VERSION}"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "${APP_PATH}/Info.plist")" == "${BUILD}" ]] || fail "CFBundleVersion 不是 ${BUILD}"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${APP_PATH}/Info.plist")" == "cc.xiaojh.PhotoViewer" ]] || fail "Bundle ID 不匹配"

codesign --verify --deep --strict --verbose=2 "${APP_PATH}" >/dev/null 2>&1 || fail "代码签名校验失败"
SIGNATURE="$(codesign -dv --verbose=4 "${APP_PATH}" 2>&1)"
[[ "${SIGNATURE}" == *"Authority=Apple Distribution:"* ]] || fail "不是 Apple Distribution 签名"

ENTITLEMENTS="$(codesign -d --entitlements :- "${APP_PATH}" 2>/dev/null)"
[[ "${ENTITLEMENTS}" == *"beta-reports-active"* ]] || fail "缺少 TestFlight 标记"
[[ "${ENTITLEMENTS}" == *"com.apple.developer.kernel.increased-memory-limit"* ]] || fail "缺少正式内存 entitlement"
[[ "${ENTITLEMENTS}" != *"increased-debugging-memory-limit"* ]] || fail "包含调试内存 entitlement"

SYMBOLS="$(nm -arch arm64 "${APP_PATH}/PhotoViewer.iOS")"
for symbol in _main _load_aot_module _OrtGetApiBase; do
    [[ "${SYMBOLS}" == *"${symbol}"* ]] || fail "缺少原生符号 ${symbol}"
done

[[ "$(sips -g hasAlpha "${SCRIPT_DIR}/Resources/Assets.xcassets/AppIcon.appiconset/icon-1024.png")" == *"hasAlpha: no"* ]] || fail "App Store 图标仍含 alpha 通道"

echo "PASS: ${VERSION} (${BUILD}) 归档、签名、entitlements、原生符号与图标检查通过"
