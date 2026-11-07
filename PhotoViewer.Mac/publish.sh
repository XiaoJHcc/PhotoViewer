#!/bin/bash
# 用法：bash publish.sh [--edition standard|ai|both] [版本号]
#       bash publish.sh --staple-only [--edition standard|ai|both] [版本号]
# --edition 默认 both，版本号缺省取 Directory.Build.props 的 AppVersion。
# 公证已通过但票据附加失败时用 --staple-only 只重试票据附加与验证。
set -euo pipefail

usage() {
    sed -n '2,5p' "$0" | sed 's/^# \{0,1\}//'
}

EDITION_ARG="both"
STAPLE_ONLY=0
VERSION_ARG=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --staple-only)
            STAPLE_ONLY=1
            shift
            ;;
        --edition)
            if [[ $# -lt 2 ]]; then
                echo "--edition 需要 standard、ai 或 both"
                exit 1
            fi
            EDITION_ARG="$2"
            shift 2
            ;;
        --edition=*)
            EDITION_ARG="${1#*=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --*)
            echo "未知参数：$1"
            usage >&2
            exit 1
            ;;
        *)
            if [[ -n "${VERSION_ARG}" ]]; then
                echo "只能指定一个版本号：${VERSION_ARG} / $1"
                exit 1
            fi
            VERSION_ARG="$1"
            shift
            ;;
    esac
done

case "${EDITION_ARG}" in
    standard|ai)
        EDITIONS=("${EDITION_ARG}")
        ;;
    both)
        EDITIONS=(standard ai)
        ;;
    *)
        echo "--edition 只接受 standard、ai 或 both：${EDITION_ARG}"
        exit 1
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEFAULT_VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
VERSION="${VERSION_ARG:-${DEFAULT_VERSION}}"
PROJECT_PATH="${SCRIPT_DIR}/PhotoViewer.Mac.csproj"
OUT_DIR="${SCRIPT_DIR}/bin/Release/net10.0-macos/osx-arm64"
DIST_ROOT="${SCRIPT_DIR}/bin/Release/dist"
RELEASE_DIR="${REPO_ROOT}/release"
PACKAGE_DIR="${RELEASE_DIR}/packages"
REPORT_DIR="${REPO_ROOT}/artifacts/acceptance/mac-editions"
SCANNER="${REPO_ROOT}/Tools/check-mac-package.py"
NOTARY_PROFILE="${MAC_NOTARY_PROFILE:-PhotoViewer-notary}"

if [[ -z "${VERSION}" ]]; then
    echo "未能读取版本号，请显式传入参数，例如：bash publish.sh 0.8.8"
    exit 1
fi

if [[ ! -f "${PROJECT_PATH}" ]]; then
    echo "未找到工程文件：${PROJECT_PATH}"
    exit 1
fi

IDENTITY=""
if [[ "${STAPLE_ONLY}" -eq 0 ]]; then
    IDENTITY="${MAC_DEVELOPER_ID_IDENTITY:-}"
    if [[ -z "${IDENTITY}" ]]; then
        IDENTITY="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:[^"]*\)".*/\1/p' | head -n 1)"
    fi
    if [[ -z "${IDENTITY}" ]]; then
        echo "未找到带私钥的 Developer ID Application 证书。"
        exit 1
    fi
    if ! command -v python3 >/dev/null 2>&1; then
        echo "包扫描需要 python3。"
        exit 1
    fi
    if [[ ! -f "${SCANNER}" ]]; then
        echo "缺少包扫描工具：${SCANNER}"
        exit 1
    fi

    echo "=== 检查发布凭据 ==="
    echo "签名身份：${IDENTITY}"
    echo "公证钥匙串配置：${NOTARY_PROFILE}"
    xcrun notarytool history --keychain-profile "${NOTARY_PROFILE}" --output-format json >/dev/null
fi

for EDITION in "${EDITIONS[@]}"; do
    case "${EDITION}" in
        ai)
            PRODUCT_NAME="PhotoViewer-AI"
            DINO="true"
            ;;
        *)
            PRODUCT_NAME="PhotoViewer"
            DINO="false"
            ;;
    esac

    APP_PATH="${OUT_DIR}/${PRODUCT_NAME}.app"
    DIST_DIR="${DIST_ROOT}/${EDITION}"
    STAGING="${DIST_DIR}/_staging"
    PACKAGE_NAME="${PRODUCT_NAME}-${VERSION}-mac-arm64.dmg"
    DMG_PATH="${DIST_DIR}/${PACKAGE_NAME}"
    REPORT_PATH="${REPORT_DIR}/${EDITION}-package.json"

    echo ""
    echo "=== [${EDITION}] ${PRODUCT_NAME} ${VERSION}（PhotoViewerEnableDino=${DINO}）==="

    if [[ "${STAPLE_ONLY}" -eq 0 ]]; then
        echo ""
        echo "=== [${EDITION}] 还原 ==="
        dotnet restore "${PROJECT_PATH}" -p:PhotoViewerEnableDino="${DINO}"

        echo ""
        echo "=== [${EDITION}] 构建 ==="
        rm -rf "${APP_PATH}"
        if [[ "${DINO}" == "false" ]]; then
            # 共享输出目录仍留着上一轮 AI 构建的 ONNX 文件时，打包阶段会把它们
            # 重新拷进 .app；标准版发布前先清掉。
            rm -f "${OUT_DIR}/onnxruntime.dll" \
                  "${OUT_DIR}/onnxruntime_providers_shared.dll" \
                  "${OUT_DIR}/libonnxruntime.dylib" \
                  "${OUT_DIR}/Microsoft.ML.OnnxRuntime.dll"
        fi
        dotnet publish "${PROJECT_PATH}" -c Release -r osx-arm64 --no-restore \
            -p:PhotoViewerEnableDino="${DINO}" \
            -p:ApplicationTitle="${PRODUCT_NAME}"

        if [[ ! -d "${APP_PATH}" ]]; then
            echo "未找到应用包：${APP_PATH}"
            exit 1
        fi

        echo ""
        echo "=== [${EDITION}] Developer ID + Hardened Runtime 签名 ==="
        while IFS= read -r -d '' FILE_PATH; do
            if [[ "$(file -b "${FILE_PATH}")" == *"Mach-O"* ]]; then
                codesign --force --options runtime --timestamp --sign "${IDENTITY}" "${FILE_PATH}"
            fi
        done < <(find "${APP_PATH}" -type f -print0)

        codesign --force --options runtime --timestamp \
            --entitlements "${SCRIPT_DIR}/Entitlements.plist" \
            --sign "${IDENTITY}" "${APP_PATH}"
        codesign --verify --deep --strict --verbose=2 "${APP_PATH}"

        echo ""
        echo "=== [${EDITION}] 包扫描 ==="
        mkdir -p "${REPORT_DIR}"
        if ! python3 "${SCANNER}" "${APP_PATH}" \
            --edition "${EDITION}" \
            --output "${REPORT_PATH}"; then
            echo "包扫描未通过，终止发布：${REPORT_PATH}"
            exit 1
        fi

        echo ""
        echo "=== [${EDITION}] 打包并签名 DMG ==="
        rm -rf "${STAGING}" && mkdir -p "${STAGING}"
        cp -a "${APP_PATH}" "${STAGING}/"
        ln -s /Applications "${STAGING}/Applications"

        hdiutil create \
            -volname "${PRODUCT_NAME}" \
            -srcfolder "${STAGING}" \
            -ov -format UDZO \
            -imagekey zlib-level=9 \
            "${DMG_PATH}"
        codesign --force --timestamp --sign "${IDENTITY}" "${DMG_PATH}"
        rm -rf "${STAGING}"

        echo ""
        echo "=== [${EDITION}] Apple 公证 ==="
        xcrun notarytool submit "${DMG_PATH}" \
            --keychain-profile "${NOTARY_PROFILE}" \
            --wait --no-progress
    else
        if [[ ! -f "${DMG_PATH}" ]]; then
            DMG_PATH="${PACKAGE_DIR}/${PACKAGE_NAME}"
        fi
        if [[ ! -f "${DMG_PATH}" ]]; then
            echo "未找到已公证的 DMG：${DIST_DIR}/${PACKAGE_NAME} 或 ${PACKAGE_DIR}/${PACKAGE_NAME}"
            exit 1
        fi
    fi

    echo ""
    echo "=== [${EDITION}] 附加并验证公证票据 ==="
    xcrun stapler staple "${DMG_PATH}"
    xcrun stapler validate "${DMG_PATH}"
    spctl -a -vvv -t open --context context:primary-signature "${DMG_PATH}"

    mkdir -p "${PACKAGE_DIR}"
    PACKAGE_PATH="${PACKAGE_DIR}/${PACKAGE_NAME}"
    if [[ "${DMG_PATH}" != "${PACKAGE_PATH}" ]]; then
        cp -f "${DMG_PATH}" "${PACKAGE_PATH}"
    fi
    echo "发布产物：${PACKAGE_PATH}"
done

echo ""
if command -v python3 >/dev/null 2>&1; then
    python3 "${REPO_ROOT}/Tools/check-release.py" --write
fi
