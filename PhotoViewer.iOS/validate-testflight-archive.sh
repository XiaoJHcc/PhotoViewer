#!/bin/bash
# 用法：bash validate-testflight-archive.sh [归档路径] [--edition standard|ai]
# 检查 TestFlight 分发归档：版本号/构建号、Bundle ID、Apple Distribution 签名、
# entitlements、dSYM 符号材料、App Store 图标，并调用 Tools/check-ios-package.py
# 按发行版本扫描实际 .app（standard 不得有模型/ORT，ai 必须有固定模型、许可证与
# ONNX Runtime 静态符号）。只读检查，不上传、不签名、不修改任何产物。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUNDLE_ID="cc.xiaojh.PhotoViewer"
SCANNER="${REPO_ROOT}/Tools/check-ios-package.py"
REPORT_DIR="${REPO_ROOT}/artifacts/acceptance/ios-editions"

ARCHIVE_ARG=""
EDITION=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --edition)
            if [[ $# -lt 2 ]]; then
                echo "--edition 需要 standard 或 ai" >&2
                exit 1
            fi
            EDITION="$2"
            shift 2
            ;;
        --edition=*)
            EDITION="${1#*=}"
            shift
            ;;
        -h|--help)
            sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        --*)
            echo "未知参数：$1" >&2
            exit 1
            ;;
        *)
            if [[ -n "${ARCHIVE_ARG}" ]]; then
                echo "只能指定一个归档路径：${ARCHIVE_ARG} / $1" >&2
                exit 1
            fi
            ARCHIVE_ARG="$1"
            shift
            ;;
    esac
done

if [[ -n "${EDITION}" && "${EDITION}" != "standard" && "${EDITION}" != "ai" ]]; then
    echo "--edition 只接受 standard 或 ai：${EDITION}" >&2
    exit 1
fi

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

if [[ -n "${ARCHIVE_ARG}" ]]; then
    ARCHIVE_PATH="${ARCHIVE_ARG}"
elif [[ -n "${EDITION}" ]]; then
    ARCHIVE_PATH="$(find "${REPO_ROOT}/release/testflight/${EDITION}" -maxdepth 1 -name '*.xcarchive' 2>/dev/null | sort | tail -n 1 || true)"
else
    ARCHIVE_PATH="$(find "${REPO_ROOT}/release/testflight" -maxdepth 2 -name '*.xcarchive' 2>/dev/null | sort | tail -n 1 || true)"
fi
[[ -n "${ARCHIVE_PATH}" && -d "${ARCHIVE_PATH}" ]] || fail "未找到归档：${ARCHIVE_ARG:-release/testflight/}"

APP_PATH="${ARCHIVE_PATH}/Products/Applications/PhotoViewer.iOS.app"
[[ -d "${APP_PATH}" ]] || fail "应用不存在：${APP_PATH}"

PRODUCT_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleName' "${APP_PATH}/Info.plist" 2>/dev/null || true)"
case "${PRODUCT_NAME}" in
    PhotoViewer)        INFERRED_EDITION="standard" ;;
    PhotoViewer-AI)     INFERRED_EDITION="ai" ;;
    *)                  fail "归档产品名不是 PhotoViewer / PhotoViewer-AI：${PRODUCT_NAME:-<空>}" ;;
esac
if [[ -n "${EDITION}" && "${EDITION}" != "${INFERRED_EDITION}" ]]; then
    fail "归档产品名为 ${PRODUCT_NAME}（${INFERRED_EDITION}），与 --edition ${EDITION} 不符"
fi
EDITION="${INFERRED_EDITION}"

[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleDisplayName' "${APP_PATH}/Info.plist" 2>/dev/null || true)" == "${PRODUCT_NAME}" ]] \
    || fail "CFBundleDisplayName 与 CFBundleName（${PRODUCT_NAME}）不一致"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${APP_PATH}/Info.plist")" == "${VERSION}" ]] || fail "CFBundleShortVersionString 不是 ${VERSION}"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "${APP_PATH}/Info.plist")" == "${BUILD}" ]] || fail "CFBundleVersion 不是 ${BUILD}"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${APP_PATH}/Info.plist")" == "${BUNDLE_ID}" ]] || fail "Bundle ID 不匹配"

if [[ "${ARCHIVE_PATH}" == *"/release/testflight/"* ]]; then
    EXPECTED_NAME="${PRODUCT_NAME}-${VERSION}-${BUILD}.xcarchive"
    [[ "$(basename "${ARCHIVE_PATH}")" == "${EXPECTED_NAME}" ]] \
        || fail "归档目录名应为 ${EXPECTED_NAME}，实际 $(basename "${ARCHIVE_PATH}")"
fi

codesign --verify --deep --strict --verbose=2 "${APP_PATH}" >/dev/null 2>&1 || fail "代码签名校验失败"
SIGNATURE="$(codesign -dv --verbose=4 "${APP_PATH}" 2>&1)"
[[ "${SIGNATURE}" == *"Authority=Apple Distribution:"* ]] || fail "不是 Apple Distribution 签名"

ENTITLEMENTS="$(codesign -d --entitlements :- "${APP_PATH}" 2>/dev/null)"
[[ "${ENTITLEMENTS}" == *"beta-reports-active"* ]] || fail "缺少 TestFlight 标记"
[[ "${ENTITLEMENTS}" == *"com.apple.developer.kernel.increased-memory-limit"* ]] || fail "缺少正式内存 entitlement"
[[ "${ENTITLEMENTS}" != *"increased-debugging-memory-limit"* ]] || fail "包含调试内存 entitlement"

# AOT 启动符号与版本无关，两个变体都必须保留；ONNX Runtime 符号由扫描器按变体判定。
SYMBOLS="$(nm -arch arm64 "${APP_PATH}/PhotoViewer.iOS")"
for symbol in _main _load_aot_module; do
    [[ "${SYMBOLS}" == *"${symbol}"* ]] || fail "缺少原生符号 ${symbol}"
done

[[ -f "${ARCHIVE_PATH}/dSYMs/PhotoViewer.iOS.app.dSYM/Contents/Info.plist" ]] \
    || fail "归档缺少 dSYMs/PhotoViewer.iOS.app.dSYM（TestFlight 必须保留符号材料）"

mkdir -p "${REPORT_DIR}"
python3 "${SCANNER}" "${APP_PATH}" \
    --edition "${EDITION}" \
    --product-name "${PRODUCT_NAME}" \
    --version "${VERSION}" \
    --build-number "${BUILD}" \
    --output "${REPORT_DIR}/${EDITION}-testflight-scan.json"

[[ "$(sips -g hasAlpha "${SCRIPT_DIR}/Resources/Assets.xcassets/AppIcon.appiconset/icon-1024.png")" == *"hasAlpha: no"* ]] || fail "App Store 图标仍含 alpha 通道"

echo "PASS: ${PRODUCT_NAME} ${VERSION} (${BUILD}) ${EDITION} 归档、签名、entitlements、能力符号、dSYM 与图标检查通过"
