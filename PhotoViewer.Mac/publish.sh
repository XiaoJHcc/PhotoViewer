#!/bin/bash
# 用法：bash publish.sh [版本号]
# 公证已通过但票据附加失败时：bash publish.sh --staple-only [版本号]
set -euo pipefail

STAPLE_ONLY=0
if [[ "${1:-}" == "--staple-only" ]]; then
    STAPLE_ONLY=1
    shift
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEFAULT_VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
VERSION="${1:-${DEFAULT_VERSION}}"
PROJECT_NAME="PhotoViewer.Mac"
APP_NAME="PhotoViewer"
APP_PATH="${SCRIPT_DIR}/bin/Release/net10.0-macos/osx-arm64/${APP_NAME}.app"
DIST_DIR="${SCRIPT_DIR}/bin/Release/dist"
STAGING="${DIST_DIR}/_staging"
RELEASE_DIR="${REPO_ROOT}/release"
DMG_PATH="${DIST_DIR}/PhotoViewer-${VERSION}-arm64.dmg"
NOTARY_PROFILE="${MAC_NOTARY_PROFILE:-PhotoViewer-notary}"

if [[ -z "${VERSION}" ]]; then
    echo "未能读取版本号，请显式传入参数，例如：bash publish.sh 0.8.8"
    exit 1
fi

if [[ "${STAPLE_ONLY}" -eq 0 ]]; then
    IDENTITY="${MAC_DEVELOPER_ID_IDENTITY:-}"
    if [[ -z "${IDENTITY}" ]]; then
        IDENTITY="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:[^"]*\)".*/\1/p' | head -n 1)"
    fi
    if [[ -z "${IDENTITY}" ]]; then
        echo "未找到带私钥的 Developer ID Application 证书。"
        exit 1
    fi

    echo "=== 检查发布凭据 ==="
    echo "签名身份：${IDENTITY}"
    echo "公证钥匙串配置：${NOTARY_PROFILE}"
    xcrun notarytool history --keychain-profile "${NOTARY_PROFILE}" --output-format json >/dev/null

    echo ""
    echo "=== 还原 ==="
    dotnet restore "${SCRIPT_DIR}/${PROJECT_NAME}.csproj"

    echo ""
    echo "=== 构建 ==="
    dotnet publish "${SCRIPT_DIR}/${PROJECT_NAME}.csproj" -c Release -r osx-arm64 --no-restore

    if [[ ! -d "${APP_PATH}" ]]; then
        echo "未找到应用包：${APP_PATH}"
        exit 1
    fi

    echo ""
    echo "=== Developer ID + Hardened Runtime 签名 ==="
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
    echo "=== 打包并签名 DMG ==="
    rm -rf "${STAGING}" && mkdir -p "${STAGING}"
    cp -a "${APP_PATH}" "${STAGING}/"
    ln -s /Applications "${STAGING}/Applications"

    hdiutil create \
        -volname "PhotoViewer" \
        -srcfolder "${STAGING}" \
        -ov -format UDZO \
        -imagekey zlib-level=9 \
        "${DMG_PATH}"
    codesign --force --timestamp --sign "${IDENTITY}" "${DMG_PATH}"
    rm -rf "${STAGING}"

    echo ""
    echo "=== Apple 公证 ==="
    xcrun notarytool submit "${DMG_PATH}" \
        --keychain-profile "${NOTARY_PROFILE}" \
        --wait --no-progress
elif [[ ! -f "${DMG_PATH}" ]]; then
    echo "未找到已公证的 DMG：${DMG_PATH}"
    exit 1
fi

echo ""
echo "=== 附加并验证公证票据 ==="
xcrun stapler staple "${DMG_PATH}"
xcrun stapler validate "${DMG_PATH}"
spctl -a -vvv -t open --context context:primary-signature "${DMG_PATH}"

mkdir -p "${RELEASE_DIR}"
cp -f "${DMG_PATH}" "${RELEASE_DIR}/"
echo ""
echo "发布产物：${RELEASE_DIR}/$(basename "${DMG_PATH}")"
